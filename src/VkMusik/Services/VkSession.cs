using System;
using System.Linq;

namespace VkMusik.Services;

/// <summary>Способ, которым приложение ходит в ВКонтакте.</summary>
public static class SessionModes
{
    /// <summary>Через сессию браузера — единственный режим, где ВК отдаёт музыку.</summary>
    public const string Browser = "browser";

    /// <summary>По сохранённому токену приложения.</summary>
    public const string Token = "token";
}

/// <summary>
/// Готовый к работе доступ к ВКонтакте: клиент API плюс то, что нужно держать живым
/// (для браузерного режима — перевыпуск веб-токена).
/// </summary>
public sealed class VkSession : IDisposable
{
    private readonly VkWebAuth? _web;

    private VkSession(VkApiClient api, VkWebAuth? web)
    {
        Api = api;
        _web = web;
    }

    public VkApiClient Api { get; }

    /// <summary>Браузерный режим требует живой сессии в браузере и обновляется сам.</summary>
    public bool IsBrowserMode => _web is not null;

    public static VkSession Create(SavedSession saved)
    {
        if (saved.Mode != SessionModes.Browser)
        {
            var app = VkClientApps.ByKey(saved.ClientApp);
            return new VkSession(new VkApiClient(app) { AccessToken = saved.AccessToken }, null);
        }

        var cookies = ReadCookies(saved.BrowserProfilePath)
            ?? throw new VkWebAuthException(
                "Не нашёл сессию ВКонтакте в браузере. Откройте vk.ru в браузере, войдите и повторите вход.");

        var web = new VkWebAuth(cookies);
        return new VkSession(web.CreateApiClient(), web);
    }

    private static VkWebCookies? ReadCookies(string? preferredProfile)
    {
        var profiles = BrowserCookies.FindProfiles();

        // Сначала тот профиль, которым уже входили.
        if (!string.IsNullOrEmpty(preferredProfile))
        {
            var saved = profiles.FirstOrDefault(p => p.CookiesPath == preferredProfile);
            if (saved is not null)
            {
                var cookies = BrowserCookies.Read(saved);
                if (cookies is not null) return cookies;
            }
        }

        // Иначе — любой, где нашлась живая сессия.
        foreach (var profile in profiles)
        {
            var cookies = BrowserCookies.Read(profile);
            if (cookies is not null) return cookies;
        }

        return null;
    }

    public void Dispose()
    {
        Api.Dispose();
        _web?.Dispose();
    }
}
