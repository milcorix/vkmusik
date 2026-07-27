using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace VkMusik.Models;

public sealed class VkTrack
{
    public long Id { get; init; }
    public long OwnerId { get; init; }
    public string Artist { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Subtitle { get; init; }
    public int Duration { get; init; }
    public string? Url { get; set; }
    public string? AccessKey { get; init; }
    public string? CoverSmall { get; init; }
    public string? CoverLarge { get; init; }
    public long? LyricsId { get; init; }
    public bool IsExplicit { get; init; }
    public DateTimeOffset? AddedAt { get; init; }

    /// <summary>Идентификатор в формате ВК: owner_id + "_" + id (+ ключ доступа).</summary>
    public string FullId => string.IsNullOrEmpty(AccessKey)
        ? $"{OwnerId}_{Id}"
        : $"{OwnerId}_{Id}_{AccessKey}";

    public string DurationText => FormatDuration(Duration);

    public static string FormatDuration(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var ts = TimeSpan.FromSeconds(Math.Floor(seconds));
        return ts.TotalHours >= 1
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", (int)ts.TotalHours, ts.Minutes, ts.Seconds)
            : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", (int)ts.TotalMinutes, ts.Seconds);
    }

    public static VkTrack Parse(JsonElement e)
    {
        string? coverSmall = null, coverLarge = null;
        if (e.TryGetProperty("album", out var album) && album.ValueKind == JsonValueKind.Object
            && album.TryGetProperty("thumb", out var thumb) && thumb.ValueKind == JsonValueKind.Object)
        {
            coverSmall = Str(thumb, "photo_135") ?? Str(thumb, "photo_270") ?? Str(thumb, "photo_68");
            coverLarge = Str(thumb, "photo_600") ?? Str(thumb, "photo_300") ?? Str(thumb, "photo_270") ?? coverSmall;
        }

        long? lyricsId = null;
        if (e.TryGetProperty("lyrics_id", out var lid) && lid.ValueKind == JsonValueKind.Number)
            lyricsId = lid.GetInt64();

        DateTimeOffset? added = null;
        if (e.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.Number && d.GetInt64() > 0)
            added = DateTimeOffset.FromUnixTimeSeconds(d.GetInt64());

        string? url = Str(e, "url");
        if (string.IsNullOrWhiteSpace(url)) url = null;

        return new VkTrack
        {
            Id = Num(e, "id"),
            OwnerId = Num(e, "owner_id"),
            Artist = Str(e, "artist") ?? "Неизвестный исполнитель",
            Title = Str(e, "title") ?? "Без названия",
            Subtitle = string.IsNullOrWhiteSpace(Str(e, "subtitle")) ? null : Str(e, "subtitle"),
            Duration = (int)Num(e, "duration"),
            Url = url,
            AccessKey = Str(e, "access_key"),
            CoverSmall = coverSmall,
            CoverLarge = coverLarge,
            LyricsId = lyricsId,
            IsExplicit = Bool(e, "is_explicit"),
            AddedAt = added,
        };
    }

    internal static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static long Num(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    internal static bool Bool(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => v.GetInt32() != 0,
            _ => false,
        };
    }
}

public sealed class VkPlaylist
{
    public long Id { get; init; }
    public long OwnerId { get; init; }
    public string Title { get; init; } = "";
    public string? Description { get; init; }
    public int Count { get; init; }
    public string? AccessKey { get; init; }
    public string? Cover { get; init; }
    public bool IsFollowed { get; init; }

    public string SubtitleText => Count == 0 ? "Пусто" : $"{Count} {Plural(Count, "трек", "трека", "треков")}";

    public static string Plural(int n, string one, string few, string many)
    {
        int mod100 = n % 100;
        if (mod100 is >= 11 and <= 14) return many;
        return (n % 10) switch { 1 => one, 2 or 3 or 4 => few, _ => many };
    }

    public static VkPlaylist Parse(JsonElement e)
    {
        string? cover = null;
        if (e.TryGetProperty("photo", out var photo) && photo.ValueKind == JsonValueKind.Object)
            cover = VkTrack.Str(photo, "photo_300") ?? VkTrack.Str(photo, "photo_270") ?? VkTrack.Str(photo, "photo_600");
        if (cover is null && e.TryGetProperty("thumbs", out var thumbs)
            && thumbs.ValueKind == JsonValueKind.Array && thumbs.GetArrayLength() > 0)
        {
            var t = thumbs[0];
            cover = VkTrack.Str(t, "photo_300") ?? VkTrack.Str(t, "photo_270") ?? VkTrack.Str(t, "photo_600");
        }

        return new VkPlaylist
        {
            Id = VkTrack.Num(e, "id"),
            OwnerId = VkTrack.Num(e, "owner_id"),
            Title = VkTrack.Str(e, "title") ?? "Плейлист",
            Description = VkTrack.Str(e, "description"),
            Count = (int)VkTrack.Num(e, "count"),
            AccessKey = VkTrack.Str(e, "access_key"),
            Cover = cover,
            IsFollowed = VkTrack.Bool(e, "is_following"),
        };
    }
}

public sealed class VkUser
{
    public long Id { get; init; }
    public string FirstName { get; init; } = "";
    public string LastName { get; init; } = "";
    public string? Photo { get; init; }

    public string FullName => $"{FirstName} {LastName}".Trim();

    public static VkUser Parse(JsonElement e) => new()
    {
        Id = VkTrack.Num(e, "id"),
        FirstName = VkTrack.Str(e, "first_name") ?? "",
        LastName = VkTrack.Str(e, "last_name") ?? "",
        Photo = VkTrack.Str(e, "photo_200") ?? VkTrack.Str(e, "photo_100") ?? VkTrack.Str(e, "photo_50"),
    };
}

/// <summary>Страница результата: сами элементы плюс сколько их всего у ВК.</summary>
public sealed class VkPage<T>
{
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();
    public int TotalCount { get; init; }
}
