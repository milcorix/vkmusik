using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VkMusik.Services;

public sealed class SavedSession
{
    public string AccessToken { get; set; } = "";
    public long UserId { get; set; }
    public string? UserName { get; set; }
    public string? UserPhoto { get; set; }

    /// <summary>
    /// Под каким приложением выдан токен. Запросы к ВК обязаны идти с его же User-Agent,
    /// иначе музыка не откроется.
    /// </summary>
    public string ClientApp { get; set; } = "kate";

    /// <summary>Режим доступа: через браузерную сессию или по сохранённому токену.</summary>
    public string Mode { get; set; } = SessionModes.Browser;

    /// <summary>
    /// Путь к базе куков браузера. Сами куки не храним — берём их из браузера каждый раз,
    /// так и свежее, и в файлах приложения не лежит лишняя копия сессии.
    /// </summary>
    public string? BrowserProfilePath { get; set; }

    public DateTimeOffset SavedAt { get; set; }
}

public sealed class AppSettings
{
    public double Volume { get; set; } = 0.7;
    public bool Muted { get; set; }
    public string Theme { get; set; } = "Dark";
    public bool Shuffle { get; set; }
    public string Repeat { get; set; } = "Off";
    public double WindowWidth { get; set; } = 1180;
    public double WindowHeight { get; set; } = 760;
}

/// <summary>Файлы приложения: сессия в ~/.config/vkmusik, обложки в ~/.cache/vkmusik.</summary>
public static class AppStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string ConfigDirectory { get; } = Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "vkmusik");

    public static string CacheDirectory { get; } = Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CACHE_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"),
        "vkmusik");

    private static string SessionPath => Path.Combine(ConfigDirectory, "session.json");
    private static string SettingsPath => Path.Combine(ConfigDirectory, "settings.json");

    private static void EnsureConfigDirectory()
    {
        Directory.CreateDirectory(ConfigDirectory);
        TryRestrictPermissions(ConfigDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void TryRestrictPermissions(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, mode); } catch { /* не критично */ }
    }

    public static SavedSession? LoadSession()
    {
        try
        {
            if (!File.Exists(SessionPath)) return null;
            var session = JsonSerializer.Deserialize<SavedSession>(File.ReadAllText(SessionPath), JsonOptions);
            return string.IsNullOrWhiteSpace(session?.AccessToken) ? null : session;
        }
        catch
        {
            return null;
        }
    }

    public static void SaveSession(SavedSession session)
    {
        try
        {
            EnsureConfigDirectory();
            session.SavedAt = DateTimeOffset.UtcNow;
            File.WriteAllText(SessionPath, JsonSerializer.Serialize(session, JsonOptions));
            // В файле лежит токен доступа — читать его должен только владелец.
            TryRestrictPermissions(SessionPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch { /* не смогли сохранить — просто попросим войти в следующий раз */ }
    }

    public static void ClearSession()
    {
        try { if (File.Exists(SessionPath)) File.Delete(SessionPath); } catch { }
    }

    public static AppSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions)
                   ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void SaveSettings(AppSettings settings)
    {
        try
        {
            EnsureConfigDirectory();
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch { }
    }

    public static string CoversDirectory
    {
        get
        {
            var dir = Path.Combine(CacheDirectory, "covers");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string DownloadsDirectory
    {
        get
        {
            var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            if (string.IsNullOrWhiteSpace(music))
                music = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music");
            var dir = Path.Combine(music, "VK Музыка");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
