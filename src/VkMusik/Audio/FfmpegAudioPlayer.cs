using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace VkMusik.Audio;

public enum PlayerState
{
    Idle,
    Buffering,
    Playing,
    Paused,
}

/// <summary>
/// Проигрыватель: ffmpeg декодирует любой источник (mp3, HLS/m3u8 с AES-128 — ровно то,
/// что отдаёт ВК) в сырой float32-PCM, а мы сами кормим им звуковой сервер.
/// Такой подход даёт полный контроль: пауза, перемотка, громкость и точная позиция.
/// </summary>
public sealed class FfmpegAudioPlayer : IDisposable
{
    public const int SampleRate = 44100;
    public const int Channels = 2;
    private const int BytesPerFrame = Channels * sizeof(float);
    private const int ReadChunk = 16384; // ~46 мс

    private const string UserAgent =
        "KateMobileAndroid/56 lite-460 (Android 4.4.2; SDK 19; x86; unknown Android SDK built for x86; en)";

    private readonly BlockingCollection<Action> _commands = new();
    private readonly Thread _commandThread;
    private readonly ManualResetEventSlim _unpaused = new(true);
    private readonly object _stateLock = new();

    private IAudioSink? _sink;
    private Process? _ffmpeg;
    private Thread? _worker;

    /// <summary>
    /// Номер сессии воспроизведения. Растёт на каждый Play/Seek/Stop; поток-насос
    /// сверяется с ним перед каждым шагом, поэтому «опоздавший» поток от прошлого трека
    /// не сможет ни писать в звук, ни объявить о конце трека.
    /// </summary>
    private int _generation;

    private long _framesWritten;
    private double _segmentStart;      // с какой секунды стартовал текущий сегмент ffmpeg
    private double _frozenPosition;    // позиция, замороженная на паузе
    private volatile int _fadeInFrames; // мягкий старт после перемотки, чтобы не щёлкало

    private float _volume = 1f;
    private bool _muted;
    private PlayerState _state = PlayerState.Idle;
    private bool _disposed;

    /// <summary>Событий много, поэтому все они приходят из фонового потока — маршалить в UI обязан подписчик.</summary>
    public event Action<PlayerState>? StateChanged;
    public event Action? TrackEnded;
    public event Action<string>? ErrorOccurred;

    public FfmpegAudioPlayer()
    {
        _commandThread = new Thread(CommandLoop)
        {
            IsBackground = true,
            Name = "vkmusik-player-control",
        };
        _commandThread.Start();
    }

    public PlayerState State
    {
        get { lock (_stateLock) return _state; }
    }

    public string SinkBackend => _sink?.Backend ?? "не инициализирован";

    /// <summary>Громкость 0..1 в «ощущаемой» шкале; в линейное усиление переводим сами.</summary>
    public double Volume
    {
        get => Math.Sqrt(_volume);
        set
        {
            double v = Math.Clamp(value, 0, 1);
            _volume = (float)(v * v);
        }
    }

    public bool Muted
    {
        get => _muted;
        set => _muted = value;
    }

    /// <summary>Текущая позиция в секундах с учётом того, что ещё лежит в буфере звукового сервера.</summary>
    public double Position
    {
        get
        {
            lock (_stateLock)
            {
                if (_state == PlayerState.Paused) return _frozenPosition;
                if (_state == PlayerState.Idle) return 0;
            }

            long frames = Interlocked.Read(ref _framesWritten);
            double latency = 0;
            try { latency = _sink?.LatencySeconds ?? 0; } catch { }
            double pos = _segmentStart + (double)frames / SampleRate - latency;
            return Math.Max(_segmentStart, pos);
        }
    }

    // ---------------------------------------------------------------- команды

    public void Play(string url, double startSeconds = 0)
    {
        if (_disposed) return;
        Enqueue(() => StartPlayback(url, startSeconds));
    }

    public void Seek(double seconds)
    {
        if (_disposed) return;
        string? url = _currentUrl;
        if (url is null) return;
        double target = Math.Max(0, seconds);
        // Отзывчивость: подвинем отображаемую позицию сразу, не дожидаясь рестарта ffmpeg.
        lock (_stateLock)
        {
            _segmentStart = target;
            _frozenPosition = target;
        }
        Interlocked.Exchange(ref _framesWritten, 0);
        Enqueue(() => StartPlayback(url, target, keepPauseState: true));
    }

    public void Stop()
    {
        if (_disposed) return;
        Enqueue(() =>
        {
            _currentUrl = null;
            TeardownSession();
            SetState(PlayerState.Idle);
            lock (_stateLock) { _segmentStart = 0; _frozenPosition = 0; }
            Interlocked.Exchange(ref _framesWritten, 0);
        });
    }

    public void Pause()
    {
        lock (_stateLock)
        {
            if (_state != PlayerState.Playing && _state != PlayerState.Buffering) return;
            _frozenPosition = PositionUnlocked();
            _state = PlayerState.Paused;
        }
        _unpaused.Reset();
        StateChanged?.Invoke(PlayerState.Paused);
    }

    public void Resume()
    {
        lock (_stateLock)
        {
            if (_state != PlayerState.Paused) return;
            _state = PlayerState.Playing;
        }
        _unpaused.Set();
        StateChanged?.Invoke(PlayerState.Playing);
    }

    public void TogglePause()
    {
        if (State == PlayerState.Paused) Resume();
        else Pause();
    }

    private volatile string? _currentUrl;

    // ---------------------------------------------------------------- внутреннее

    private double PositionUnlocked()
    {
        long frames = Interlocked.Read(ref _framesWritten);
        double latency = 0;
        try { latency = _sink?.LatencySeconds ?? 0; } catch { }
        return Math.Max(_segmentStart, _segmentStart + (double)frames / SampleRate - latency);
    }

    private void Enqueue(Action action)
    {
        try { _commands.Add(action); }
        catch (InvalidOperationException) { /* очередь закрыта при Dispose */ }
    }

    private void CommandLoop()
    {
        foreach (var cmd in _commands.GetConsumingEnumerable())
        {
            try { cmd(); }
            catch (Exception ex) { ErrorOccurred?.Invoke(ex.Message); }
        }
    }

    private void SetState(PlayerState state)
    {
        bool changed;
        lock (_stateLock)
        {
            changed = _state != state;
            _state = state;
        }
        if (changed) StateChanged?.Invoke(state);
    }

    private IAudioSink EnsureSink()
    {
        if (_sink is not null) return _sink;

        try
        {
            _sink = new PulseSimpleSink(SampleRate, Channels);
        }
        catch (Exception ex) when (ex is AudioSinkException or DllNotFoundException or EntryPointNotFoundException)
        {
            _sink = ProcessAudioSink.Create(SampleRate, Channels);
        }
        return _sink;
    }

    private void StartPlayback(string url, double startSeconds, bool keepPauseState = false)
    {
        bool wasPaused = keepPauseState && State == PlayerState.Paused;

        TeardownSession();

        _currentUrl = url;
        lock (_stateLock)
        {
            _segmentStart = startSeconds;
            _frozenPosition = startSeconds;
        }
        Interlocked.Exchange(ref _framesWritten, 0);
        _fadeInFrames = SampleRate / 50; // 20 мс

        IAudioSink sink;
        try
        {
            sink = EnsureSink();
            sink.Flush();
        }
        catch (Exception ex)
        {
            SetState(PlayerState.Idle);
            ErrorOccurred?.Invoke("Не удалось открыть звуковое устройство: " + ex.Message);
            return;
        }

        Process proc;
        try
        {
            proc = StartFfmpeg(url, startSeconds);
        }
        catch (Exception ex)
        {
            SetState(PlayerState.Idle);
            ErrorOccurred?.Invoke("Не удалось запустить ffmpeg: " + ex.Message);
            return;
        }

        _ffmpeg = proc;
        int generation = Volatile.Read(ref _generation);

        if (wasPaused)
        {
            _unpaused.Reset();
            SetState(PlayerState.Paused);
        }
        else
        {
            _unpaused.Set();
            SetState(PlayerState.Buffering);
        }

        var worker = new Thread(() => PumpAudio(proc, sink, generation))
        {
            IsBackground = true,
            Name = "vkmusik-player-pump",
        };
        _worker = worker;
        worker.Start();
    }

    private static Process StartFfmpeg(string url, double startSeconds)
    {
        var psi = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };

        void Arg(params string[] items)
        {
            foreach (var i in items) psi.ArgumentList.Add(i);
        }

        Arg("-hide_banner", "-loglevel", "error", "-nostdin");

        // Опции ниже существуют только у http-протокола: для локального файла
        // ffmpeg на них ругается и падает, поэтому включаем их выборочно.
        bool isHttp = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                   || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (isHttp)
        {
            // Сеть у ВК бывает капризная: пусть ffmpeg сам переподключается.
            Arg("-reconnect", "1", "-reconnect_streamed", "1", "-reconnect_on_network_error", "1",
                "-reconnect_delay_max", "5");
            Arg("-rw_timeout", "20000000");
            Arg("-user_agent", UserAgent);
            // Ссылки ВК — это HLS-плейлисты с AES-128; ключ лежит на том же хосте.
            Arg("-allowed_extensions", "ALL");
            Arg("-protocol_whitelist", "file,http,https,tcp,tls,crypto,httpproxy");
        }

        if (startSeconds > 0.05)
            Arg("-ss", startSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        Arg("-i", url);
        Arg("-vn", "-sn", "-dn");
        Arg("-f", "f32le", "-acodec", "pcm_f32le", "-ar", SampleRate.ToString(), "-ac", Channels.ToString());
        Arg("-");

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start вернул null");
        return proc;
    }

    private void PumpAudio(Process proc, IAudioSink sink, int generation)
    {
        bool Cancelled() => Volatile.Read(ref _generation) != generation;

        var stderr = new StringBuilder();
        var stderrThread = new Thread(() =>
        {
            try
            {
                string? line;
                while ((line = proc.StandardError.ReadLine()) is not null)
                {
                    lock (stderr)
                    {
                        if (stderr.Length < 4000) stderr.AppendLine(line);
                    }
                }
            }
            catch { }
        })
        { IsBackground = true, Name = "vkmusik-ffmpeg-stderr" };
        stderrThread.Start();

        var buffer = new byte[ReadChunk];
        bool producedAudio = false;
        bool faulted = false;

        try
        {
            var stdout = proc.StandardOutput.BaseStream;
            int carry = 0; // хвост неполного фрейма между чтениями

            while (!Cancelled())
            {
                int read = stdout.Read(buffer, carry, buffer.Length - carry);
                if (read <= 0) break;

                int available = carry + read;
                int usable = available - (available % BytesPerFrame);
                carry = available - usable;

                if (usable > 0)
                {
                    ApplyGain(buffer.AsSpan(0, usable));

                    _unpaused.Wait();
                    if (Cancelled()) break;

                    sink.Write(buffer.AsSpan(0, usable));
                    Interlocked.Add(ref _framesWritten, usable / BytesPerFrame);

                    if (!producedAudio)
                    {
                        producedAudio = true;
                        if (State == PlayerState.Buffering) SetState(PlayerState.Playing);
                    }
                }

                if (carry > 0)
                    buffer.AsSpan(usable, carry).CopyTo(buffer.AsSpan(0, carry));
            }
        }
        catch (Exception ex)
        {
            if (!Cancelled())
            {
                faulted = true;
                ErrorOccurred?.Invoke("Ошибка воспроизведения: " + ex.Message);
            }
        }

        if (Cancelled()) return;

        int exitCode = -1;
        try
        {
            proc.WaitForExit(3000);
            if (proc.HasExited) exitCode = proc.ExitCode;
        }
        catch { }

        if (!producedAudio && !faulted)
        {
            string detail;
            lock (stderr) detail = stderr.ToString().Trim();
            if (string.IsNullOrEmpty(detail))
                detail = exitCode == 0 ? "источник не содержит звука" : $"ffmpeg завершился с кодом {exitCode}";
            ErrorOccurred?.Invoke("Не удалось прочитать поток: " + detail);
            SetState(PlayerState.Idle);
            return;
        }

        if (producedAudio)
        {
            try { sink.Drain(); } catch { }
        }

        if (Cancelled()) return;
        SetState(PlayerState.Idle);
        TrackEnded?.Invoke();
    }

    private void ApplyGain(Span<byte> pcm)
    {
        var samples = MemoryMarshal.Cast<byte, float>(pcm);
        float gain = _muted ? 0f : _volume;

        int fade = _fadeInFrames;
        if (fade > 0)
        {
            int frames = samples.Length / Channels;
            int fadeFrames = Math.Min(fade, frames);
            int total = SampleRate / 50;
            for (int f = 0; f < fadeFrames; f++)
            {
                float k = gain * (total - fade + f) / total;
                for (int c = 0; c < Channels; c++)
                    samples[f * Channels + c] *= k;
            }
            _fadeInFrames = fade - fadeFrames;

            for (int i = fadeFrames * Channels; i < samples.Length; i++)
                samples[i] *= gain;
            return;
        }

        if (gain == 1f) return;
        for (int i = 0; i < samples.Length; i++)
            samples[i] *= gain;
    }

    private void TeardownSession()
    {
        Interlocked.Increment(ref _generation);
        _unpaused.Set(); // чтобы поток-насос не завис на паузе

        var proc = _ffmpeg;
        _ffmpeg = null;
        if (proc is not null)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            try { proc.WaitForExit(1000); } catch { }
            try { proc.Dispose(); } catch { }
        }

        var worker = _worker;
        _worker = null;
        if (worker is not null && worker.IsAlive)
        {
            try { worker.Join(1500); } catch { }
        }

        try { _sink?.Flush(); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _commands.CompleteAdding(); } catch { }
        TeardownSession();
        try { _commandThread.Join(500); } catch { }

        try { _sink?.Dispose(); } catch { }
        _sink = null;
        _unpaused.Dispose();
        _commands.Dispose();
    }
}
