using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VkMusik.Models;

namespace VkMusik.Services;

/// <summary>Сохранение трека на диск. Перекодировать не нужно — просто перекладываем поток в контейнер.</summary>
public static class TrackDownloader
{
    public static async Task<string> SaveAsMp3Async(VkTrack track, string url, CancellationToken ct = default)
    {
        // HLS у ВК — это AAC, его кладём в .m4a; прямые ссылки обычно уже mp3.
        bool isHls = url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
        string extension = isHls ? ".m4a" : ".mp3";

        string name = Sanitize($"{track.Artist} — {track.Title}");
        string path = Path.Combine(AppStorage.DownloadsDirectory, name + extension);

        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(AppStorage.DownloadsDirectory, $"{name} ({i}){extension}");

        var psi = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        void Arg(params string[] items)
        {
            foreach (var item in items) psi.ArgumentList.Add(item);
        }

        Arg("-hide_banner", "-loglevel", "error", "-nostdin", "-y");
        Arg("-user_agent", VkClientApps.Kate.UserAgent);
        Arg("-protocol_whitelist", "file,http,https,tcp,tls,crypto,httpproxy");
        Arg("-i", url);
        Arg("-vn", "-c:a", "copy");
        if (isHls) Arg("-bsf:a", "aac_adtstoasc");
        Arg("-metadata", $"title={track.Title}");
        Arg("-metadata", $"artist={track.Artist}");
        Arg(path);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("не удалось запустить ffmpeg");

        var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            var reason = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
            throw new InvalidOperationException(string.IsNullOrEmpty(reason)
                ? $"ffmpeg завершился с кодом {process.ExitCode}" : reason);
        }

        return path;
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) || c == '/' ? '_' : c).ToArray()).Trim();
        if (cleaned.Length > 150) cleaned = cleaned[..150].Trim();
        return cleaned.Length == 0 ? "Трек" : cleaned;
    }
}
