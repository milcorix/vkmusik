using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VkMusik.Models;

namespace VkMusik.Services;

/// <summary>Все музыкальные методы ВК в одном месте.</summary>
public sealed class VkMusicService
{
    private readonly VkApiClient _api;

    public VkMusicService(VkApiClient api) => _api = api;

    public long CurrentUserId { get; private set; }

    private static string N(long v) => v.ToString(CultureInfo.InvariantCulture);

    public async Task<VkUser> GetCurrentUserAsync(CancellationToken ct = default)
    {
        var resp = await _api.CallAsync("users.get",
            new() { ["fields"] = "photo_200,photo_100" }, ct).ConfigureAwait(false);

        if (resp.ValueKind != JsonValueKind.Array || resp.GetArrayLength() == 0)
            throw new VkApiException(-1, "ВКонтакте не вернул профиль пользователя");

        var user = VkUser.Parse(resp[0]);
        CurrentUserId = user.Id;
        return user;
    }

    private static VkPage<VkTrack> ParseTrackPage(JsonElement resp)
    {
        var items = new List<VkTrack>();
        int total = 0;

        if (resp.ValueKind == JsonValueKind.Object)
        {
            if (resp.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number)
                total = c.GetInt32();
            if (resp.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var e in arr.EnumerateArray()) items.Add(VkTrack.Parse(e));
        }
        else if (resp.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in resp.EnumerateArray()) items.Add(VkTrack.Parse(e));
            total = items.Count;
        }

        return new VkPage<VkTrack> { Items = items, TotalCount = Math.Max(total, items.Count) };
    }

    /// <summary>Аудиозаписи пользователя или конкретного плейлиста.</summary>
    public async Task<VkPage<VkTrack>> GetAudioAsync(
        long ownerId, long? playlistId = null, string? accessKey = null,
        int offset = 0, int count = 100, CancellationToken ct = default)
    {
        var pars = new Dictionary<string, string?>
        {
            ["owner_id"] = N(ownerId),
            ["offset"] = N(offset),
            ["count"] = N(count),
        };
        if (playlistId is not null) pars["album_id"] = N(playlistId.Value);
        if (!string.IsNullOrEmpty(accessKey)) pars["access_key"] = accessKey;

        var resp = await _api.CallAsync("audio.get", pars, ct).ConfigureAwait(false);
        return ParseTrackPage(resp);
    }

    public async Task<VkPage<VkTrack>> SearchAsync(
        string query, int offset = 0, int count = 100, CancellationToken ct = default)
    {
        var resp = await _api.CallAsync("audio.search", new()
        {
            ["q"] = query,
            ["auto_complete"] = "1",
            ["search_own"] = "0",
            ["offset"] = N(offset),
            ["count"] = N(count),
        }, ct).ConfigureAwait(false);
        return ParseTrackPage(resp);
    }

    public async Task<VkPage<VkTrack>> GetRecommendationsAsync(
        int offset = 0, int count = 100, CancellationToken ct = default)
    {
        var resp = await _api.CallAsync("audio.getRecommendations", new()
        {
            ["offset"] = N(offset),
            ["count"] = N(count),
        }, ct).ConfigureAwait(false);
        return ParseTrackPage(resp);
    }

    public async Task<VkPage<VkTrack>> GetPopularAsync(
        int offset = 0, int count = 100, CancellationToken ct = default)
    {
        var resp = await _api.CallAsync("audio.getPopular", new()
        {
            ["only_eng"] = "0",
            ["offset"] = N(offset),
            ["count"] = N(count),
        }, ct).ConfigureAwait(false);
        return ParseTrackPage(resp);
    }

    public async Task<VkPage<VkPlaylist>> GetPlaylistsAsync(
        long ownerId, int offset = 0, int count = 50, CancellationToken ct = default)
    {
        var resp = await _api.CallAsync("audio.getPlaylists", new()
        {
            ["owner_id"] = N(ownerId),
            ["offset"] = N(offset),
            ["count"] = N(count),
        }, ct).ConfigureAwait(false);

        var items = new List<VkPlaylist>();
        int total = 0;
        if (resp.ValueKind == JsonValueKind.Object)
        {
            if (resp.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number)
                total = c.GetInt32();
            if (resp.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var e in arr.EnumerateArray()) items.Add(VkPlaylist.Parse(e));
        }
        return new VkPage<VkPlaylist> { Items = items, TotalCount = Math.Max(total, items.Count) };
    }

    /// <summary>
    /// Обновляет ссылки на файлы. Ссылки ВК живут около часа и привязаны к IP,
    /// поэтому перед проигрыванием их надо перезапрашивать.
    /// </summary>
    public async Task<IReadOnlyList<VkTrack>> GetByIdAsync(
        IEnumerable<string> fullIds, CancellationToken ct = default)
    {
        var ids = fullIds.Take(100).ToList();
        if (ids.Count == 0) return Array.Empty<VkTrack>();

        var resp = await _api.CallAsync("audio.getById", new()
        {
            ["audios"] = string.Join(",", ids),
        }, ct).ConfigureAwait(false);

        return ParseTrackPage(resp).Items;
    }

    /// <summary>Свежая ссылка на конкретный трек (null, если ВК её не отдал).</summary>
    public async Task<string?> ResolveUrlAsync(VkTrack track, CancellationToken ct = default)
    {
        var fresh = await GetByIdAsync([track.FullId], ct).ConfigureAwait(false);
        var found = fresh.FirstOrDefault(t => t.Id == track.Id && t.OwnerId == track.OwnerId)
                    ?? fresh.FirstOrDefault();
        return string.IsNullOrWhiteSpace(found?.Url) ? null : found!.Url;
    }

    public async Task<long?> AddAsync(VkTrack track, CancellationToken ct = default)
    {
        var pars = new Dictionary<string, string?>
        {
            ["audio_id"] = N(track.Id),
            ["owner_id"] = N(track.OwnerId),
        };
        if (!string.IsNullOrEmpty(track.AccessKey)) pars["access_key"] = track.AccessKey;

        var resp = await _api.CallAsync("audio.add", pars, ct).ConfigureAwait(false);
        return resp.ValueKind == JsonValueKind.Number ? resp.GetInt64() : null;
    }

    public async Task<bool> DeleteAsync(VkTrack track, CancellationToken ct = default)
    {
        var resp = await _api.CallAsync("audio.delete", new()
        {
            ["audio_id"] = N(track.Id),
            ["owner_id"] = N(track.OwnerId),
        }, ct).ConfigureAwait(false);
        return resp.ValueKind == JsonValueKind.Number && resp.GetInt32() == 1;
    }

    /// <summary>Текст песни. ВК за годы менял формат ответа — поддерживаем оба.</summary>
    public async Task<string?> GetLyricsAsync(long lyricsId, CancellationToken ct = default)
    {
        var resp = await _api.CallAsync("audio.getLyrics", new()
        {
            ["lyrics_id"] = N(lyricsId),
        }, ct).ConfigureAwait(false);

        if (resp.ValueKind != JsonValueKind.Object) return null;

        if (resp.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            return text.GetString();

        if (resp.TryGetProperty("lyrics", out var lyrics) && lyrics.ValueKind == JsonValueKind.Object
            && lyrics.TryGetProperty("timestamps", out var stamps) && stamps.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var stamp in stamps.EnumerateArray())
            {
                if (stamp.TryGetProperty("line", out var line) && line.ValueKind == JsonValueKind.String)
                    sb.AppendLine(line.GetString());
                else
                    sb.AppendLine();
            }
            return sb.ToString().Trim();
        }

        return null;
    }
}
