using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace VkMusik.Audio;

/// <summary>
/// Запасной бэкенд на случай, если libpulse-simple недоступна: пишем PCM в stdin
/// внешней утилите (pw-cat / paplay / aplay). Задержку точно не узнать, поэтому
/// оцениваем её по размеру буфера утилиты.
/// </summary>
public sealed class ProcessAudioSink : IAudioSink
{
    private readonly Process _process;
    private readonly Stream _stdin;
    private readonly object _gate = new();
    private bool _disposed;

    public int SampleRate { get; }
    public int Channels { get; }
    public string Backend { get; }
    public double LatencySeconds => 0.25;

    private ProcessAudioSink(Process process, string backend, int sampleRate, int channels)
    {
        _process = process;
        _stdin = process.StandardInput.BaseStream;
        Backend = backend;
        SampleRate = sampleRate;
        Channels = channels;
    }

    public static ProcessAudioSink Create(int sampleRate, int channels)
    {
        var candidates = new List<(string exe, string[] args)>
        {
            ("pw-cat", ["--playback", "--format", "f32", "--rate", sampleRate.ToString(), "--channels", channels.ToString(), "-"]),
            ("paplay", ["--raw", "--format=float32le", $"--rate={sampleRate}", $"--channels={channels}", "-"]),
            ("aplay", ["-q", "-t", "raw", "-f", "FLOAT_LE", "-r", sampleRate.ToString(), "-c", channels.ToString(), "-"]),
        };

        var errors = new List<string>();
        foreach (var (exe, args) in candidates)
        {
            try
            {
                var psi = new ProcessStartInfo(exe)
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                foreach (var a in args) psi.ArgumentList.Add(a);

                var proc = Process.Start(psi);
                if (proc is null) { errors.Add($"{exe}: не запустился"); continue; }

                // Утилита может умереть сразу, если не поддерживает формат.
                if (proc.WaitForExit(150) && proc.ExitCode != 0)
                {
                    errors.Add($"{exe}: код выхода {proc.ExitCode}");
                    continue;
                }

                return new ProcessAudioSink(proc, exe, sampleRate, channels);
            }
            catch (Exception ex)
            {
                errors.Add($"{exe}: {ex.Message}");
            }
        }

        throw new AudioSinkException("Не найден звуковой бэкенд. " + string.Join("; ", errors));
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _stdin.Write(data);
            _stdin.Flush();
        }
    }

    // У внешнего процесса очередь не сбросить — просто ничего не делаем.
    public void Flush() { }

    public void Drain()
    {
        lock (_gate)
        {
            if (_disposed) return;
            try { _stdin.Flush(); } catch { /* процесс мог умереть */ }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _stdin.Close(); } catch { }
            try
            {
                if (!_process.WaitForExit(500)) _process.Kill(true);
            }
            catch { }
            _process.Dispose();
        }
    }
}
