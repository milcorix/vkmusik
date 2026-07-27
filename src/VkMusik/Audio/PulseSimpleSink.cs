using System;
using System.Runtime.InteropServices;

namespace VkMusik.Audio;

/// <summary>
/// Прямой вывод в PulseAudio/PipeWire через libpulse-simple.
/// Это основной бэкенд: даёт точную задержку (нужна для позиции трека) и мгновенный flush.
/// </summary>
public sealed unsafe class PulseSimpleSink : IAudioSink
{
    private const string LibSimple = "libpulse-simple.so.0";
    private const string LibPulse = "libpulse.so.0";

    private const int PA_STREAM_PLAYBACK = 1;
    private const int PA_SAMPLE_FLOAT32LE = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct SampleSpec
    {
        public int Format;
        public uint Rate;
        public byte Channels;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BufferAttr
    {
        public uint MaxLength;
        public uint TLength;
        public uint PreBuf;
        public uint MinReq;
        public uint FragSize;
    }

    [DllImport(LibSimple, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr pa_simple_new(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? server,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int dir,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? dev,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string streamName,
        in SampleSpec ss,
        IntPtr map,
        in BufferAttr attr,
        out int error);

    [DllImport(LibSimple, CallingConvention = CallingConvention.Cdecl)]
    private static extern int pa_simple_write(IntPtr s, byte* data, nuint bytes, out int error);

    [DllImport(LibSimple, CallingConvention = CallingConvention.Cdecl)]
    private static extern int pa_simple_drain(IntPtr s, out int error);

    [DllImport(LibSimple, CallingConvention = CallingConvention.Cdecl)]
    private static extern int pa_simple_flush(IntPtr s, out int error);

    [DllImport(LibSimple, CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong pa_simple_get_latency(IntPtr s, out int error);

    [DllImport(LibSimple, CallingConvention = CallingConvention.Cdecl)]
    private static extern void pa_simple_free(IntPtr s);

    [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr pa_strerror(int error);

    private IntPtr _handle;
    private readonly object _gate = new();

    public int SampleRate { get; }
    public int Channels { get; }
    public string Backend => "PulseAudio (libpulse-simple)";

    /// <param name="bufferMs">
    /// Целевой размер буфера сервера. Держим небольшим: именно он определяет,
    /// сколько ещё будет звучать после паузы.
    /// </param>
    public PulseSimpleSink(int sampleRate, int channels, int bufferMs = 200)
    {
        SampleRate = sampleRate;
        Channels = channels;

        var ss = new SampleSpec
        {
            Format = PA_SAMPLE_FLOAT32LE,
            Rate = (uint)sampleRate,
            Channels = (byte)channels,
        };

        int bytesPerSecond = sampleRate * channels * sizeof(float);
        uint tlength = (uint)(bytesPerSecond * bufferMs / 1000);

        var attr = new BufferAttr
        {
            MaxLength = uint.MaxValue,
            TLength = tlength,
            // Копим примерно 3/4 буфера прежде чем поехать — сглаживает старт по сети.
            PreBuf = (uint)(tlength * 3 / 4),
            MinReq = uint.MaxValue,
            FragSize = uint.MaxValue,
        };

        _handle = pa_simple_new(
            null,
            "VK Музыка",
            PA_STREAM_PLAYBACK,
            null,
            "Музыка",
            in ss,
            IntPtr.Zero,
            in attr,
            out int error);

        if (_handle == IntPtr.Zero)
            throw new AudioSinkException($"pa_simple_new: {StrError(error)}");
    }

    private static string StrError(int error)
    {
        var p = pa_strerror(error);
        return p == IntPtr.Zero ? $"код {error}" : Marshal.PtrToStringUTF8(p) ?? $"код {error}";
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return;
        lock (_gate)
        {
            if (_handle == IntPtr.Zero) return;
            fixed (byte* p = data)
            {
                if (pa_simple_write(_handle, p, (nuint)data.Length, out int error) < 0)
                    throw new AudioSinkException($"pa_simple_write: {StrError(error)}");
            }
        }
    }

    public double LatencySeconds
    {
        get
        {
            lock (_gate)
            {
                if (_handle == IntPtr.Zero) return 0;
                ulong usec = pa_simple_get_latency(_handle, out int error);
                if (error != 0) return 0;
                return usec / 1_000_000.0;
            }
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            if (_handle == IntPtr.Zero) return;
            pa_simple_flush(_handle, out _);
        }
    }

    public void Drain()
    {
        lock (_gate)
        {
            if (_handle == IntPtr.Zero) return;
            pa_simple_drain(_handle, out _);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_handle == IntPtr.Zero) return;
            var h = _handle;
            _handle = IntPtr.Zero;
            pa_simple_free(h);
        }
    }
}

public sealed class AudioSinkException : Exception
{
    public AudioSinkException(string message) : base(message) { }
    public AudioSinkException(string message, Exception inner) : base(message, inner) { }
}
