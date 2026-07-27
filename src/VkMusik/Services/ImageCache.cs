using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace VkMusik.Services;

/// <summary>
/// Обложки: сначала память, потом диск, и только потом сеть.
/// Декодируем сразу под нужную ширину — незачем держать в памяти картинки 600×600 для списка.
/// </summary>
public sealed class ImageCache : IDisposable
{
    private const int MemoryCacheLimit = 400;

    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, Bitmap> _memory = new();
    private readonly ConcurrentQueue<string> _insertionOrder = new();
    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _inFlight = new();
    private readonly SemaphoreSlim _networkLimit = new(6, 6);

    public ImageCache()
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(VkClientApps.Kate.UserAgent);
    }

    public Task<Bitmap?> GetAsync(string? url, int decodeWidth, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return Task.FromResult<Bitmap?>(null);

        string key = $"{decodeWidth}|{url}";
        if (_memory.TryGetValue(key, out var cached)) return Task.FromResult<Bitmap?>(cached);

        return _inFlight.GetOrAdd(key, _ => LoadAsync(url!, decodeWidth, key, ct));
    }

    private async Task<Bitmap?> LoadAsync(string url, int decodeWidth, string key, CancellationToken ct)
    {
        try
        {
            string file = Path.Combine(AppStorage.CoversDirectory, Hash(url));

            byte[]? bytes = null;
            if (File.Exists(file))
            {
                try { bytes = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false); }
                catch { bytes = null; }
            }

            if (bytes is null || bytes.Length == 0)
            {
                await _networkLimit.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    bytes = await _http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
                }
                finally
                {
                    _networkLimit.Release();
                }

                try { await File.WriteAllBytesAsync(file, bytes, ct).ConfigureAwait(false); }
                catch { /* кэш на диске — приятный бонус, не более */ }
            }

            using var stream = new MemoryStream(bytes);
            var bitmap = Bitmap.DecodeToWidth(stream, decodeWidth, BitmapInterpolationMode.HighQuality);

            Remember(key, bitmap);
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    private void Remember(string key, Bitmap bitmap)
    {
        if (!_memory.TryAdd(key, bitmap)) return;
        _insertionOrder.Enqueue(key);

        while (_insertionOrder.Count > MemoryCacheLimit && _insertionOrder.TryDequeue(out var oldest))
        {
            if (_memory.TryRemove(oldest, out var old))
            {
                try { old.Dispose(); } catch { }
            }
        }
    }

    private static string Hash(string url)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(bytes, 0, 16).ToLowerInvariant();
    }

    public void Dispose()
    {
        _http.Dispose();
        _networkLimit.Dispose();
        foreach (var bitmap in _memory.Values)
        {
            try { bitmap.Dispose(); } catch { }
        }
        _memory.Clear();
    }
}
