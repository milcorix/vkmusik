using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VkMusik.Services;

public enum AuthOutcome
{
    Success,
    NeedTwoFactor,
    NeedCaptcha,
    Failed,
}

/// <summary>Почему вход не удался — от этого зависит, есть ли смысл пробовать другое приложение.</summary>
public enum AuthFailureKind
{
    None,
    /// <summary>Логин или пароль неверные — перебирать приложения бессмысленно.</summary>
    WrongCredentials,
    /// <summary>ВКонтакте временно закрыл парольный вход этому приложению.</summary>
    FloodControl,
    /// <summary>ВКонтакте требует подтвердить вход в браузере.</summary>
    NeedsBrowser,
    Other,
}

public sealed class AuthResult
{
    public AuthOutcome Outcome { get; init; }
    public string? AccessToken { get; init; }
    public long UserId { get; init; }

    /// <summary>Под каким приложением шла попытка.</summary>
    public VkClientApp App { get; init; } = VkClientApps.Kate;

    public AuthFailureKind FailureKind { get; init; }
    public string? ErrorMessage { get; init; }

    // Двухфакторка
    public string? PhoneMask { get; init; }
    public bool CodeBySms { get; init; }
    public string? ValidationSid { get; init; }

    // Капча
    public string? CaptchaSid { get; init; }
    public string? CaptchaImage { get; init; }
}

/// <summary>
/// Прямая авторизация (grant_type=password). Только так ВКонтакте выдаёт токен,
/// которому разрешены методы audio.* — токен из браузерной авторизации музыку не открывает.
///
/// Приложение перебираем: ВК периодически включает «Flood control» на отдельные клиенты,
/// и тогда вход возможен только под другим.
/// </summary>
public sealed class VkAuthService : IDisposable
{
    private readonly HttpClient _http;

    public VkAuthService()
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9");
    }

    /// <summary>Идентификатор устройства должен быть стабильным между попытками входа.</summary>
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N")[..16];

    /// <summary>
    /// Пробует войти, перебирая приложения, пока какое-нибудь не ответит осмысленно.
    /// Перебор прекращается, как только ВК сказал что-то содержательное про сам аккаунт
    /// (неверный пароль, нужен код, нужна капча) — тогда другое приложение не поможет.
    /// </summary>
    public async Task<AuthResult> LoginAsync(
        string login,
        string password,
        string? twoFactorCode = null,
        string? captchaSid = null,
        string? captchaKey = null,
        VkClientApp? only = null,
        CancellationToken ct = default)
    {
        var apps = only is not null ? new[] { only } : [.. VkClientApps.All];

        AuthResult? last = null;
        foreach (var app in apps)
        {
            var result = await LoginWithAppAsync(app, login, password, twoFactorCode,
                captchaSid, captchaKey, ct).ConfigureAwait(false);

            if (result.Outcome != AuthOutcome.Failed) return result;

            last = result;
            // Смысл пробовать соседнее приложение есть только когда упёрлись в его ограничения.
            if (result.FailureKind is not (AuthFailureKind.FloodControl or AuthFailureKind.Other))
                return result;
        }

        return last ?? new AuthResult
        {
            Outcome = AuthOutcome.Failed,
            FailureKind = AuthFailureKind.Other,
            ErrorMessage = "Не удалось войти.",
        };
    }

    private async Task<AuthResult> LoginWithAppAsync(
        VkClientApp app,
        string login,
        string password,
        string? twoFactorCode,
        string? captchaSid,
        string? captchaKey,
        CancellationToken ct)
    {
        var query = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = app.ClientId,
            ["client_secret"] = app.ClientSecret,
            ["username"] = login,
            ["password"] = password,
            ["scope"] = "audio,offline,friends,status,groups,wall",
            ["v"] = VkApiClient.DefaultApiVersion,
            ["2fa_supported"] = "1",
            ["libverify_support"] = "1",
            ["lang"] = "ru",
            ["device_id"] = DeviceId,
        };

        if (!string.IsNullOrWhiteSpace(twoFactorCode)) query["code"] = twoFactorCode.Trim();
        if (!string.IsNullOrWhiteSpace(captchaSid)) query["captcha_sid"] = captchaSid;
        if (!string.IsNullOrWhiteSpace(captchaKey)) query["captcha_key"] = captchaKey.Trim();

        var url = "https://oauth.vk.com/token?" + await new FormUrlEncodedContent(query)
            .ReadAsStringAsync(ct).ConfigureAwait(false);

        string body;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(app.UserAgent);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new AuthResult
            {
                Outcome = AuthOutcome.Failed,
                App = app,
                FailureKind = AuthFailureKind.Other,
                ErrorMessage = "Нет связи с ВКонтакте: " + ex.Message,
            };
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(body);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new AuthResult
            {
                Outcome = AuthOutcome.Failed,
                App = app,
                FailureKind = AuthFailureKind.Other,
                ErrorMessage = "ВКонтакте вернул неожиданный ответ.",
            };
        }

        if (root.TryGetProperty("access_token", out var tokenEl))
        {
            return new AuthResult
            {
                Outcome = AuthOutcome.Success,
                App = app,
                AccessToken = tokenEl.GetString(),
                UserId = root.TryGetProperty("user_id", out var uid) && uid.ValueKind == JsonValueKind.Number
                    ? uid.GetInt64() : 0,
            };
        }

        string error = root.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
        string description = root.TryGetProperty("error_description", out var d) ? d.GetString() ?? "" : "";
        string errorType = root.TryGetProperty("error_type", out var t) ? t.GetString() ?? "" : "";

        if (error == "need_validation")
        {
            string type = root.TryGetProperty("validation_type", out var vt) ? vt.GetString() ?? "" : "";

            // Иногда ВК вместо кода требует пройти проверку в браузере — код тут не поможет.
            if (type.Length == 0 && root.TryGetProperty("redirect_uri", out _))
            {
                return new AuthResult
                {
                    Outcome = AuthOutcome.Failed,
                    App = app,
                    FailureKind = AuthFailureKind.NeedsBrowser,
                    ErrorMessage = "ВКонтакте требует подтвердить вход в браузере. "
                                 + "Подтвердите вход на vk.com с этого же компьютера и попробуйте снова.",
                };
            }

            return new AuthResult
            {
                Outcome = AuthOutcome.NeedTwoFactor,
                App = app,
                CodeBySms = type == "2fa_sms",
                PhoneMask = root.TryGetProperty("phone_mask", out var pm) ? pm.GetString() : null,
                ValidationSid = root.TryGetProperty("validation_sid", out var vs) ? vs.GetString() : null,
            };
        }

        if (error == "need_captcha")
        {
            return new AuthResult
            {
                Outcome = AuthOutcome.NeedCaptcha,
                App = app,
                CaptchaSid = root.TryGetProperty("captcha_sid", out var cs) ? cs.ToString() : null,
                CaptchaImage = root.TryGetProperty("captcha_img", out var ci) ? ci.GetString() : null,
            };
        }

        return Classify(app, error, errorType, description);
    }

    private static AuthResult Classify(VkClientApp app, string error, string errorType, string description)
    {
        // ВК присылает «9;Flood control» — на конкретное приложение временно закрыт парольный вход.
        bool flood = error.Contains("Flood", StringComparison.OrdinalIgnoreCase)
                  || description.Contains("слишком много попыток", StringComparison.OrdinalIgnoreCase)
                  || description.Contains("too many", StringComparison.OrdinalIgnoreCase);

        if (flood)
        {
            return new AuthResult
            {
                Outcome = AuthOutcome.Failed,
                App = app,
                FailureKind = AuthFailureKind.FloodControl,
                ErrorMessage = $"ВКонтакте временно закрыл вход по паролю для «{app.Name}». "
                             + "Попробуйте позже или войдите по токену.",
            };
        }

        bool wrong = errorType == "username_or_password_is_incorrect"
                  || description.Contains("Неправильный логин или пароль", StringComparison.OrdinalIgnoreCase)
                  || description.Contains("username or password is incorrect", StringComparison.OrdinalIgnoreCase);

        if (wrong)
        {
            return new AuthResult
            {
                Outcome = AuthOutcome.Failed,
                App = app,
                FailureKind = AuthFailureKind.WrongCredentials,
                ErrorMessage = "Неверный логин или пароль.",
            };
        }

        string message = !string.IsNullOrWhiteSpace(description) ? description
                       : !string.IsNullOrWhiteSpace(error) ? error
                       : "Не удалось войти.";

        return new AuthResult
        {
            Outcome = AuthOutcome.Failed,
            App = app,
            FailureKind = AuthFailureKind.Other,
            ErrorMessage = message,
        };
    }

    /// <summary>Попросить ВК прислать код в SMS вместо приложения-аутентификатора.</summary>
    public async Task<bool> RequestSmsCodeAsync(string validationSid, VkClientApp app,
                                                CancellationToken ct = default)
    {
        try
        {
            var url = "https://api.vk.com/method/auth.validatePhone"
                    + $"?sid={Uri.EscapeDataString(validationSid)}"
                    + $"&client_id={app.ClientId}"
                    + $"&client_secret={app.ClientSecret}"
                    + $"&v={VkApiClient.DefaultApiVersion}&lang=ru";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(app.UserAgent);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return body.Contains("\"response\"", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Достаёт токен из того, что вставил пользователь: это может быть и сам токен,
    /// и целиком адрес страницы с #access_token=... после ручной авторизации.
    /// </summary>
    public static string? ExtractToken(string input)
    {
        input = input.Trim();
        if (input.Length == 0) return null;

        var match = Regex.Match(input, @"access_token=([0-9a-zA-Z._\-]+)");
        if (match.Success) return match.Groups[1].Value;

        // «Голый» токен ВК — длинная строка без пробелов.
        if (Regex.IsMatch(input, @"^[0-9a-zA-Z._\-]{40,}$")) return input;

        return null;
    }

    /// <summary>Из того же адреса вытащим и user_id, если он там есть.</summary>
    public static long ExtractUserId(string input)
    {
        var match = Regex.Match(input, @"user_id=(\d+)");
        return match.Success && long.TryParse(match.Groups[1].Value, out var id) ? id : 0;
    }

    public void Dispose() => _http.Dispose();
}
