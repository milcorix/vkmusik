using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VkMusik.Services;

public sealed record WebToken(string AccessToken, long UserId, DateTimeOffset ExpiresAt)
{
    /// <summary>Токен живёт минуты, поэтому обновляем заранее.</summary>
    public bool NeedsRefresh => DateTimeOffset.UtcNow >= ExpiresAt - TimeSpan.FromMinutes(1);
}

/// <summary>
/// Веб-токен ВКонтакте: тот самый, на котором работает музыка на сайте.
/// Берётся из живой сессии браузера и живёт около 15 минут, поэтому автоматически
/// перевыпускается — куки при этом остаются те же.
/// </summary>
public sealed class VkWebAuth : IDisposable
{
    /// <summary>Идентификатор веб-приложения ВКонтакте — только ему открыты методы audio.*.</summary>
    public const string WebAppId = "6287487";

    public const string BrowserUserAgent =
        "Mozilla/5.0 (X11; Linux x86_64; rv:128.0) Gecko/20100101 Firefox/128.0";

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private VkWebCookies _cookies;
    private WebToken? _token;

    public VkWebAuth(VkWebCookies cookies)
    {
        _cookies = cookies;
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            UseCookies = false,
        })
        {
            Timeout = TimeSpan.FromSeconds(25),
        };
    }

    public VkWebCookies Cookies
    {
        get => _cookies;
        set => _cookies = value;
    }

    /// <summary>Отдаёт действующий токен, перевыпуская его при необходимости.</summary>
    public async Task<string> GetTokenAsync(CancellationToken ct = default)
    {
        var current = _token;
        if (current is not null && !current.NeedsRefresh) return current.AccessToken;

        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Пока ждали блокировку, токен мог обновить кто-то другой.
            current = _token;
            if (current is not null && !current.NeedsRefresh) return current.AccessToken;

            var fresh = await RequestTokenAsync(ct).ConfigureAwait(false);
            _token = fresh;
            return fresh.AccessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public long UserId => _token?.UserId ?? 0;

    public async Task<WebToken> RequestTokenAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://login.vk.ru/?act=web_token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["version"] = "1",
                ["app_id"] = WebAppId,
            }),
        };

        request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        request.Headers.TryAddWithoutValidation("Cookie", _cookies.HeaderValue);
        request.Headers.TryAddWithoutValidation("Origin", "https://vk.ru");
        request.Headers.TryAddWithoutValidation("Referer", "https://vk.ru/");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(body);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new VkWebAuthException("ВКонтакте вернул неожиданный ответ на запрос веб-токена.");
        }

        string type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
        if (type != "okay")
        {
            string info = root.TryGetProperty("error_info", out var e) ? e.GetString() ?? "" : "";
            throw new VkWebAuthException(info.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
                ? "Сессия ВКонтакте в браузере истекла. Откройте vk.ru в браузере, войдите заново и повторите."
                : $"ВКонтакте отказал в выдаче веб-токена: {(info.Length > 0 ? info : type)}");
        }

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new VkWebAuthException("В ответе ВКонтакте нет данных токена.");

        string token = data.TryGetProperty("access_token", out var a) ? a.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(token))
            throw new VkWebAuthException("ВКонтакте не вернул веб-токен.");

        long userId = data.TryGetProperty("user_id", out var u) && u.ValueKind == JsonValueKind.Number
            ? u.GetInt64() : 0;

        // expires — метка времени, а не длительность.
        var expiresAt = data.TryGetProperty("expires", out var ex) && ex.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeSeconds(ex.GetInt64())
            : DateTimeOffset.UtcNow.AddMinutes(10);

        return new WebToken(token, userId, expiresAt);
    }

    /// <summary>
    /// Собирает клиента API для веб-режима: другой домен, свежая версия API
    /// и обязательный client_id в каждом запросе — без него ВК музыку не отдаёт.
    /// </summary>
    public VkApiClient CreateApiClient()
    {
        var api = new VkApiClient(VkClientApps.Web)
        {
            ApiBaseUrl = "https://api.vk.ru/method/",
            ApiVersion = "5.207",
            TokenProvider = GetTokenAsync,
        };
        api.ExtraParameters["client_id"] = WebAppId;
        return api;
    }

    public void Dispose()
    {
        _http.Dispose();
        _refreshLock.Dispose();
    }
}

public sealed class VkWebAuthException : Exception
{
    public VkWebAuthException(string message) : base(message) { }
}
