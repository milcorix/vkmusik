using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace VkMusik.Services;

/// <summary>Профиль браузера, в котором может быть открыта сессия ВКонтакте.</summary>
public sealed record BrowserProfile(string Browser, string Name, string CookiesPath)
{
    public string Title => $"{Browser} — {Name}";
}

/// <summary>Куки, которых достаточно, чтобы получить веб-токен ВКонтакте.</summary>
public sealed record VkWebCookies(string RemixSid, string P)
{
    public string HeaderValue => $"remixsid={RemixSid}; p={P}";
}

/// <summary>
/// Достаёт сессию ВКонтакте из браузера. Это единственный способ получить токен,
/// которому ВК разрешает методы audio.get и audio.search: токены обычных приложений
/// к музыке не пускают, а веб-плеер работает именно на такой сессии.
/// </summary>
public static class BrowserCookies
{
    /// <summary>Находит профили Firefox, в которых есть база с куками.</summary>
    public static IReadOnlyList<BrowserProfile> FindProfiles()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new (string Browser, string Path)[]
        {
            ("Firefox", Path.Combine(home, ".mozilla", "firefox")),
            ("Firefox", Path.Combine(home, ".config", "mozilla", "firefox")),
            ("Firefox", Path.Combine(home, "snap", "firefox", "common", ".mozilla", "firefox")),
            ("LibreWolf", Path.Combine(home, ".librewolf")),
            ("Floorp", Path.Combine(home, ".floorp")),
            ("Zen", Path.Combine(home, ".zen")),
        };

        var found = new List<BrowserProfile>();

        foreach (var (browser, root) in roots)
        {
            if (!Directory.Exists(root)) continue;

            foreach (var dir in SafeEnumerateDirectories(root))
            {
                var cookies = Path.Combine(dir, "cookies.sqlite");
                if (!File.Exists(cookies)) continue;

                found.Add(new BrowserProfile(browser, Path.GetFileName(dir), cookies));
            }
        }

        // Свежие профили вероятнее содержат живую сессию.
        return found
            .OrderByDescending(p => SafeLastWrite(p.CookiesPath))
            .ToList();
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root)
    {
        try { return Directory.EnumerateDirectories(root); }
        catch { return []; }
    }

    private static DateTime SafeLastWrite(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    /// <summary>
    /// Читает из профиля куки сессии ВК. Возвращает null, если пользователь
    /// в этом браузере во ВКонтакте не входил.
    /// </summary>
    public static VkWebCookies? Read(BrowserProfile profile)
    {
        // Работающий браузер держит базу заблокированной, поэтому читаем копию.
        string temp = Path.Combine(Path.GetTempPath(),
            $"vkmusik-cookies-{Environment.ProcessId}-{Guid.NewGuid():N}.sqlite");

        try
        {
            File.Copy(profile.CookiesPath, temp, overwrite: true);
            CopyIfExists(profile.CookiesPath + "-wal", temp + "-wal");
            CopyIfExists(profile.CookiesPath + "-shm", temp + "-shm");

            using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = temp,
                    Mode = SqliteOpenMode.ReadOnly,
                }.ToString());
            connection.Open();

            string? remixsid = ReadCookie(connection, "remixsid", "vk.ru") ?? ReadCookie(connection, "remixsid", "vk.com");
            string? p = ReadCookie(connection, "p", "login.vk.ru") ?? ReadCookie(connection, "p", "login.vk.com");

            if (string.IsNullOrEmpty(remixsid) || string.IsNullOrEmpty(p)) return null;
            return new VkWebCookies(remixsid, p);
        }
        catch
        {
            return null;
        }
        finally
        {
            TryDelete(temp);
            TryDelete(temp + "-wal");
            TryDelete(temp + "-shm");
        }
    }

    private static string? ReadCookie(SqliteConnection connection, string name, string hostSuffix)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT value FROM moz_cookies
            WHERE name = $name AND (host = $host OR host = '.' || $host)
            ORDER BY length(value) DESC
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$host", hostSuffix);

        var value = command.ExecuteScalar() as string;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static void CopyIfExists(string from, string to)
    {
        try { if (File.Exists(from)) File.Copy(from, to, overwrite: true); } catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
