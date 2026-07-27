using System;

namespace VkMusik.Audio;

/// <summary>
/// Приёмник готового PCM-потока (float32 little-endian, interleaved).
/// </summary>
public interface IAudioSink : IDisposable
{
    int SampleRate { get; }
    int Channels { get; }

    /// <summary>Название бэкенда — для диагностики.</summary>
    string Backend { get; }

    /// <summary>Блокирующая запись. Именно она задаёт темп воспроизведения.</summary>
    void Write(ReadOnlySpan<byte> data);

    /// <summary>Сколько секунд аудио уже отдано, но ещё не прозвучало.</summary>
    double LatencySeconds { get; }

    /// <summary>Выбросить всё, что стоит в очереди (перемотка/стоп).</summary>
    void Flush();

    /// <summary>Дождаться, пока доиграет всё записанное.</summary>
    void Drain();
}
