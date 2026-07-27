using System.Collections.Generic;

namespace VkMusik.Services;

/// <summary>
/// Приложение, под которым мы представляемся ВКонтакте.
/// Методы <c>audio.*</c> обычным приложениям недоступны — их открывают только
/// «свои» клиенты, поэтому идентификатор, секрет и User-Agent обязаны совпадать между собой.
/// </summary>
public sealed record VkClientApp(
    string Key,
    string Name,
    string ClientId,
    string ClientSecret,
    string UserAgent)
{
    public override string ToString() => Name;
}

public static class VkClientApps
{
    public static readonly VkClientApp Kate = new(
        "kate",
        "Kate Mobile",
        "2685278",
        "lxhD8OD7dMsqtXIm5IUY",
        "KateMobileAndroid/56 lite-460 (Android 4.4.2; SDK 19; x86; unknown Android SDK built for x86; en)");

    public static readonly VkClientApp Android = new(
        "android",
        "VK для Android",
        "2274003",
        "hHbZxrka2uZ6jB1inYsH",
        "VKAndroidApp/7.7-10445 (Android 10; SDK 29; arm64-v8a; unknown Android SDK built for arm64; ru; 2340x1080)");

    public static readonly VkClientApp VkMe = new(
        "vkme",
        "VK Me",
        "6146827",
        "qVxWRF1CwHERuIrKBnqe",
        "VKAndroidApp/5.52-4543 (Android 5.1.1; SDK 22; x86_64; unknown Android SDK built for x86_64; ru)");

    /// <summary>
    /// Веб-плеер ВКонтакте. Единственное приложение, которому ВК сейчас открывает
    /// audio.get, audio.search и audio.getById — но только по токену из браузерной сессии.
    /// </summary>
    public static readonly VkClientApp Web = new(
        "web",
        "Веб-версия ВКонтакте",
        VkWebAuth.WebAppId,
        "",
        VkWebAuth.BrowserUserAgent);

    /// <summary>
    /// Порядок перебора при входе по паролю. ВКонтакте периодически включает «Flood control»
    /// для отдельных приложений, поэтому запасные варианты нужны обязательно.
    /// </summary>
    public static IReadOnlyList<VkClientApp> All { get; } = [Kate, Android, VkMe];

    public static VkClientApp ByKey(string? key)
    {
        foreach (var app in All)
            if (app.Key == key) return app;
        return Kate;
    }
}
