using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using VkMusik.Services;

namespace VkMusik.ViewModels;

public enum AuthMode
{
    /// <summary>Через сессию браузера — единственный способ, при котором ВК отдаёт музыку.</summary>
    Browser,
    Password,
    Token,
}

public sealed class LoginViewModel : ViewModelBase, IDisposable
{
    /// <summary>
    /// Страница ручной авторизации. Токен отсюда годится только как запасной вариант:
    /// ВКонтакте не даёт таким токенам методы audio.get и audio.search.
    /// </summary>
    public static readonly string AuthorizeUrl =
        "https://oauth.vk.com/authorize"
        + "?client_id=" + VkClientApps.Kate.ClientId
        + "&scope=audio,offline"
        + "&redirect_uri=https://oauth.vk.com/blank.html"
        + "&display=page&response_type=token&revoke=1&v=" + VkApiClient.DefaultApiVersion;

    private readonly VkAuthService _auth = new();

    private AuthMode _mode = AuthMode.Browser;
    private string _login = "";
    private string _password = "";
    private string _twoFactorCode = "";
    private string _captchaKey = "";
    private string _tokenInput = "";
    private string? _errorMessage;
    private string? _audioWarning;
    private string? _phoneMask;
    private string? _validationSid;
    private string? _captchaSid;
    private Bitmap? _captchaImage;
    private bool _isBusy;
    private bool _needTwoFactor;
    private bool _needCaptcha;
    private bool _codeBySms;

    private BrowserProfile? _selectedProfile;

    // Код из SMS и капча привязаны к тому приложению, которое их запросило.
    private VkClientApp _app = VkClientApps.Kate;

    // Вход прошёл, но музыка недоступна — держим сессию наготове на случай «всё равно войти».
    private SavedSession? _pending;

    public LoginViewModel()
    {
        SubmitCommand = new AsyncRelayCommand(SubmitAsync, () => !IsBusy);
        UseBrowserModeCommand = new RelayCommand(() => Mode = AuthMode.Browser);
        UsePasswordModeCommand = new RelayCommand(() => Mode = AuthMode.Password);
        UseTokenModeCommand = new RelayCommand(() => Mode = AuthMode.Token);
        OpenAuthPageCommand = new RelayCommand(() => OpenUrl(AuthorizeUrl));
        OpenVkSiteCommand = new RelayCommand(() => OpenUrl("https://vk.ru/feed"));
        RefreshProfilesCommand = new RelayCommand(DetectProfiles);
        RequestSmsCommand = new AsyncRelayCommand(RequestSmsAsync);
        CancelTwoFactorCommand = new RelayCommand(ResetChallenges);
        ContinueAnywayCommand = new RelayCommand(ContinueAnyway);
    }

    public AsyncRelayCommand SubmitCommand { get; }
    public RelayCommand UseBrowserModeCommand { get; }
    public RelayCommand UsePasswordModeCommand { get; }
    public RelayCommand UseTokenModeCommand { get; }
    public RelayCommand OpenAuthPageCommand { get; }
    public RelayCommand OpenVkSiteCommand { get; }
    public RelayCommand RefreshProfilesCommand { get; }
    public AsyncRelayCommand RequestSmsCommand { get; }
    public RelayCommand CancelTwoFactorCommand { get; }
    public RelayCommand ContinueAnywayCommand { get; }

    /// <summary>Вход удался — окно логина отдаёт сессию приложению.</summary>
    public event Action<SavedSession>? LoggedIn;

    public Task InitializeAsync()
    {
        DetectProfiles();
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ режим

    public AuthMode Mode
    {
        get => _mode;
        private set
        {
            if (!SetField(ref _mode, value)) return;
            OnPropertyChanged(nameof(IsBrowserMode));
            OnPropertyChanged(nameof(IsPasswordMode));
            OnPropertyChanged(nameof(IsTokenMode));
            OnPropertyChanged(nameof(SubmitCaption));
            ErrorMessage = null;
            AudioWarning = null;
            if (value == AuthMode.Browser) DetectProfiles();
        }
    }

    public bool IsBrowserMode => _mode == AuthMode.Browser;
    public bool IsPasswordMode => _mode == AuthMode.Password;
    public bool IsTokenMode => _mode == AuthMode.Token;

    public string SubmitCaption => _needTwoFactor ? "Подтвердить"
        : _mode switch
        {
            AuthMode.Browser => "Войти через браузер",
            AuthMode.Token => "Войти по токену",
            _ => "Войти",
        };

    // ------------------------------------------------------------------ браузер

    public ObservableCollection<BrowserProfile> Profiles { get; } = new();

    public BrowserProfile? SelectedProfile
    {
        get => _selectedProfile;
        set => SetField(ref _selectedProfile, value);
    }

    public bool HasProfiles => Profiles.Count > 0;
    public bool HasNoProfiles => Profiles.Count == 0;

    /// <summary>Выбор профиля показываем, только если их несколько.</summary>
    public bool ShowProfilePicker => Profiles.Count > 1;

    private void DetectProfiles()
    {
        Profiles.Clear();
        foreach (var profile in BrowserCookies.FindProfiles()) Profiles.Add(profile);

        SelectedProfile = Profiles.Count > 0 ? Profiles[0] : null;

        OnPropertyChanged(nameof(HasProfiles));
        OnPropertyChanged(nameof(HasNoProfiles));
        OnPropertyChanged(nameof(ShowProfilePicker));
    }

    private async Task SubmitBrowserAsync()
    {
        var profile = SelectedProfile ?? (Profiles.Count > 0 ? Profiles[0] : null);
        if (profile is null)
        {
            ErrorMessage = "Не нашёл ни одного профиля браузера. "
                         + "Откройте vk.ru в Firefox, войдите и нажмите «Обновить список».";
            return;
        }

        var cookies = BrowserCookies.Read(profile);
        if (cookies is null)
        {
            ErrorMessage = $"В профиле «{profile.Title}» нет сессии ВКонтакте. "
                         + "Откройте vk.ru в этом браузере, войдите и попробуйте снова.";
            return;
        }

        using var web = new VkWebAuth(cookies);
        try
        {
            var token = await web.RequestTokenAsync();

            var session = new SavedSession
            {
                Mode = SessionModes.Browser,
                ClientApp = VkClientApps.Web.Key,
                BrowserProfilePath = profile.CookiesPath,
                UserId = token.UserId,
            };

            using var api = web.CreateApiClient();
            await CheckAndFinishAsync(api, session, VkClientApps.Web);
        }
        catch (VkWebAuthException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            ErrorMessage = "Не удалось получить доступ через браузер: " + ex.Message;
        }
    }

    // ------------------------------------------------------------------ поля

    public string Login
    {
        get => _login;
        set => SetField(ref _login, value);
    }

    public string Password
    {
        get => _password;
        set => SetField(ref _password, value);
    }

    public string TwoFactorCode
    {
        get => _twoFactorCode;
        set => SetField(ref _twoFactorCode, value);
    }

    public string CaptchaKey
    {
        get => _captchaKey;
        set => SetField(ref _captchaKey, value);
    }

    public string TokenInput
    {
        get => _tokenInput;
        set => SetField(ref _tokenInput, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetField(ref _errorMessage, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(_errorMessage);

    /// <summary>Вход прошёл, но музыка этому доступу закрыта.</summary>
    public string? AudioWarning
    {
        get => _audioWarning;
        private set
        {
            if (SetField(ref _audioWarning, value)) OnPropertyChanged(nameof(HasAudioWarning));
        }
    }

    public bool HasAudioWarning => !string.IsNullOrWhiteSpace(_audioWarning);

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetField(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(IsNotBusy));
            SubmitCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsNotBusy => !_isBusy;

    public bool NeedTwoFactor
    {
        get => _needTwoFactor;
        private set
        {
            if (SetField(ref _needTwoFactor, value)) OnPropertyChanged(nameof(SubmitCaption));
        }
    }

    public bool NeedCaptcha
    {
        get => _needCaptcha;
        private set => SetField(ref _needCaptcha, value);
    }

    public bool CodeBySms
    {
        get => _codeBySms;
        private set
        {
            if (SetField(ref _codeBySms, value)) OnPropertyChanged(nameof(TwoFactorHint));
        }
    }

    public string? PhoneMask
    {
        get => _phoneMask;
        private set
        {
            if (SetField(ref _phoneMask, value)) OnPropertyChanged(nameof(TwoFactorHint));
        }
    }

    public string TwoFactorHint => _codeBySms
        ? $"Код отправлен в SMS на номер {_phoneMask ?? "вашего телефона"}"
        : "Введите код из приложения-аутентификатора";

    public bool CanRequestSms => !_codeBySms && !string.IsNullOrEmpty(_validationSid);

    public Bitmap? CaptchaImage
    {
        get => _captchaImage;
        private set => SetField(ref _captchaImage, value);
    }

    // ------------------------------------------------------------------ действия

    private void ResetChallenges()
    {
        NeedTwoFactor = false;
        NeedCaptcha = false;
        TwoFactorCode = "";
        CaptchaKey = "";
        _captchaSid = null;
        _validationSid = null;
        CaptchaImage = null;
        ErrorMessage = null;
    }

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            try { Process.Start("xdg-open", url); }
            catch { ErrorMessage = "Не удалось открыть браузер. Откройте вручную:\n" + url; }
        }
    }

    private async Task RequestSmsAsync()
    {
        if (string.IsNullOrEmpty(_validationSid)) return;
        IsBusy = true;
        try
        {
            bool ok = await _auth.RequestSmsCodeAsync(_validationSid, _app);
            if (ok)
            {
                CodeBySms = true;
                ErrorMessage = null;
            }
            else
            {
                ErrorMessage = "ВКонтакте не отправил SMS. Используйте код из приложения.";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SubmitAsync()
    {
        ErrorMessage = null;
        AudioWarning = null;
        IsBusy = true;
        try
        {
            switch (_mode)
            {
                case AuthMode.Browser: await SubmitBrowserAsync(); break;
                case AuthMode.Token: await SubmitTokenAsync(); break;
                default: await SubmitPasswordAsync(); break;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SubmitTokenAsync()
    {
        string? token = VkAuthService.ExtractToken(TokenInput);
        if (token is null)
        {
            ErrorMessage = "Не вижу токена. Вставьте адрес страницы целиком или сам токен.";
            return;
        }

        var session = new SavedSession
        {
            Mode = SessionModes.Token,
            ClientApp = VkClientApps.Kate.Key,
            AccessToken = token,
            UserId = VkAuthService.ExtractUserId(TokenInput),
        };

        using var api = new VkApiClient(VkClientApps.Kate) { AccessToken = token };
        await CheckAndFinishAsync(api, session, VkClientApps.Kate);
    }

    private async Task SubmitPasswordAsync()
    {
        if (string.IsNullOrWhiteSpace(Login) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Введите логин и пароль.";
            return;
        }

        // Продолжение начатой проверки должно уйти тому же приложению, что её запросило.
        bool continuing = NeedTwoFactor || NeedCaptcha;

        var result = await _auth.LoginAsync(
            Login.Trim(), Password,
            NeedTwoFactor ? TwoFactorCode : null,
            NeedCaptcha ? _captchaSid : null,
            NeedCaptcha ? CaptchaKey : null,
            only: continuing ? _app : null);

        _app = result.App;

        switch (result.Outcome)
        {
            case AuthOutcome.Success:
            {
                var session = new SavedSession
                {
                    Mode = SessionModes.Token,
                    ClientApp = result.App.Key,
                    AccessToken = result.AccessToken!,
                    UserId = result.UserId,
                };
                using var api = new VkApiClient(result.App) { AccessToken = result.AccessToken };
                await CheckAndFinishAsync(api, session, result.App);
                break;
            }

            case AuthOutcome.NeedTwoFactor:
                NeedCaptcha = false;
                NeedTwoFactor = true;
                CodeBySms = result.CodeBySms;
                PhoneMask = result.PhoneMask;
                _validationSid = result.ValidationSid;
                TwoFactorCode = "";
                OnPropertyChanged(nameof(CanRequestSms));
                break;

            case AuthOutcome.NeedCaptcha:
                NeedCaptcha = true;
                _captchaSid = result.CaptchaSid;
                CaptchaKey = "";
                CaptchaImage = await LoadCaptchaAsync(result.CaptchaImage);
                ErrorMessage = "Введите символы с картинки.";
                break;

            default:
                ErrorMessage = result.FailureKind == AuthFailureKind.FloodControl
                    ? "ВКонтакте временно закрыл вход по паролю — слишком много попыток. "
                      + "Войдите через браузер: первая вкладка."
                    : result.ErrorMessage ?? "Не удалось войти.";
                if (NeedTwoFactor) TwoFactorCode = "";
                break;
        }
    }

    private static async Task<Bitmap?> LoadCaptchaAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            using var http = new HttpClient();
            var bytes = await http.GetByteArrayAsync(url);
            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Узнаём, кто вошёл, и сразу проверяем, открыта ли музыка.</summary>
    private async Task CheckAndFinishAsync(VkApiClient api, SavedSession session, VkClientApp app)
    {
        var music = new VkMusicService(api);

        try
        {
            var user = await music.GetCurrentUserAsync();
            session.UserId = user.Id != 0 ? user.Id : session.UserId;
            session.UserName = user.FullName;
            session.UserPhoto = user.Photo;
            _pending = session;

            try
            {
                await music.GetAudioAsync(user.Id, null, null, 0, 1);
            }
            catch (VkApiException ex) when (ex.IsAudioForbidden)
            {
                AudioWarning = $"Вход выполнен ({user.FullName}), но ВКонтакте не открыл музыку "
                             + $"для «{app.Name}». Музыку отдаёт только вход через браузер.";
                return;
            }

            Complete();
        }
        catch (VkApiException ex) when (ex.IsAuthFailure)
        {
            ErrorMessage = "Доступ недействителен или отозван.";
        }
        catch (VkApiException ex)
        {
            ErrorMessage = ex.FriendlyMessage;
        }
    }

    private void ContinueAnyway()
    {
        if (_pending is not null) Complete();
    }

    private void Complete()
    {
        if (_pending is null) return;
        LoggedIn?.Invoke(_pending);
    }

    public void Dispose() => _auth.Dispose();
}
