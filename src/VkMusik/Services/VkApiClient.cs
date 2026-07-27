using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VkMusik.Services;

public sealed class VkApiException : Exception
{
    public int Code { get; }
    public string? Method { get; }
    public string? CaptchaSid { get; }
    public string? CaptchaImg { get; }

    public VkApiException(int code, string message, string? method = null,
                          string? captchaSid = null, string? captchaImg = null)
        : base(message)
    {
        Code = code;
        Method = method;
        CaptchaSid = captchaSid;
        CaptchaImg = captchaImg;
    }

    /// <summary>Токен протух или отозван — надо заново логиниться.</summary>
    public bool IsAuthFailure => Code is 5;

    private bool IsAudioMethod => Method?.StartsWith("audio.", StringComparison.Ordinal) == true;

    /// <summary>
    /// Аудио закрыто для этого токена. Код 3 («Unknown method passed») — не опечатка в имени
    /// метода: так ВКонтакте прячет методы audio.* от токенов, которым не положен доступ к музыке.
    /// </summary>
    public bool IsAudioForbidden => Code is 15 or 200 or 201 || (Code == 3 && IsAudioMethod);

    public string FriendlyMessage
    {
        get
        {
            if (IsAudioForbidden)
                return "ВКонтакте не даёт этому токену доступ к музыке. "
                     + "Нужен вход по логину и паролю — токен, полученный через браузер, музыку не открывает.";

            return Code switch
            {
                5 => "Сессия ВКонтакте истекла. Войдите заново.",
                6 => "Слишком много запросов. Попробуйте ещё раз.",
                _ => Message,
            };
        }
    }
}

/// <summary>
/// Тонкая обёртка над api.vk.com: троттлинг 3 запроса/сек, разбор ошибок, капча.
/// </summary>
public sealed class VkApiClient : IDisposable
{
    /// <summary>Версия API по умолчанию.</summary>
    public const string DefaultApiVersion = "5.131";

    /// <summary>Версия API этого клиента. Веб-режим работает на более свежей.</summary>
    public string ApiVersion { get; set; } = DefaultApiVersion;

    private static readonly TimeSpan MinRequestGap = TimeSpan.FromMilliseconds(340);

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _throttle = new(1, 1);
    private DateTime _lastCall = DateTime.MinValue;

    public string? AccessToken { get; set; }

    /// <summary>Адрес API. Веб-режим ходит на api.vk.ru.</summary>
    public string ApiBaseUrl { get; set; } = "https://api.vk.com/method/";

    /// <summary>
    /// Источник действующего токена. Веб-токен живёт минуты, поэтому берём его
    /// перед каждым запросом — обновлением занимается сам источник.
    /// </summary>
    public Func<CancellationToken, Task<string>>? TokenProvider { get; set; }

    /// <summary>Параметры, которые ВК требует в каждом запросе (например client_id веб-приложения).</summary>
    public Dictionary<string, string> ExtraParameters { get; } = new();

    /// <summary>
    /// Вызывается, когда ВК просит капчу. Должен вернуть введённый пользователем текст
    /// (или null, чтобы отменить). Запрос после этого повторяется автоматически.
    /// </summary>
    public Func<string, Task<string?>>? CaptchaHandler { get; set; }

    /// <param name="app">
    /// Приложение, под которым выдан токен. User-Agent обязан ему соответствовать —
    /// иначе ВКонтакте не отдаст музыку.
    /// </param>
    public VkApiClient(VkClientApp? app = null)
    {
        App = app ?? VkClientApps.Kate;

        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(App.UserAgent);
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9");
    }

    public VkClientApp App { get; }

    public async Task<JsonElement> CallAsync(
        string method,
        Dictionary<string, string?> parameters,
        CancellationToken ct = default)
    {
        string? captchaSid = null, captchaKey = null;

        for (int attempt = 0; ; attempt++)
        {
            var form = new Dictionary<string, string>();
            foreach (var (k, v) in parameters)
                if (v is not null) form[k] = v;

            form["v"] = ApiVersion;
            form["lang"] = "ru";
            foreach (var (k, v) in ExtraParameters) form[k] = v;

            string? token = AccessToken;
            if (TokenProvider is not null) token = await TokenProvider(ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(token)) form["access_token"] = token!;
            if (captchaSid is not null && captchaKey is not null)
            {
                form["captcha_sid"] = captchaSid;
                form["captcha_key"] = captchaKey;
            }

            string body;
            await _throttle.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var gap = MinRequestGap - (DateTime.UtcNow - _lastCall);
                if (gap > TimeSpan.Zero) await Task.Delay(gap, ct).ConfigureAwait(false);

                using var content = new FormUrlEncodedContent(form);
                using var response = await _http
                    .PostAsync(ApiBaseUrl + method, content, ct)
                    .ConfigureAwait(false);
                body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _lastCall = DateTime.UtcNow;
                _throttle.Release();
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                int code = error.TryGetProperty("error_code", out var c) ? c.GetInt32() : -1;
                string message = error.TryGetProperty("error_msg", out var m)
                    ? m.GetString() ?? "Неизвестная ошибка" : "Неизвестная ошибка";

                // 6 — «слишком часто». Подождём и повторим.
                if (code == 6 && attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(700 * (attempt + 1)), ct).ConfigureAwait(false);
                    continue;
                }

                if (code == 14 && attempt < 3 && CaptchaHandler is not null)
                {
                    string? sid = error.TryGetProperty("captcha_sid", out var s) ? s.ToString() : null;
                    string? img = error.TryGetProperty("captcha_img", out var i) ? i.GetString() : null;
                    if (sid is not null && img is not null)
                    {
                        string? key = await CaptchaHandler(img).ConfigureAwait(false);
                        if (key is not null)
                        {
                            captchaSid = sid;
                            captchaKey = key;
                            continue;
                        }
                    }
                    throw new VkApiException(code, message, method, sid, img);
                }

                throw new VkApiException(code, message, method,
                    error.TryGetProperty("captcha_sid", out var cs) ? cs.ToString() : null,
                    error.TryGetProperty("captcha_img", out var ci) ? ci.GetString() : null);
            }

            if (!root.TryGetProperty("response", out var resp))
                throw new VkApiException(-1, "ВКонтакте вернул ответ без поля response", method);

            // JsonDocument умрёт вместе с using — отдаём независимую копию.
            return resp.Clone();
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _throttle.Dispose();
    }
}
