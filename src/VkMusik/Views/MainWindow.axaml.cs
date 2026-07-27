using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VkMusik.Services;
using VkMusik.ViewModels;

namespace VkMusik.Views;

public partial class MainWindow : Window
{
    private readonly SavedSession _savedSession;
    private readonly ImageCache _images;
    private readonly AppSettings _settings;
    private VkSession? _session;
    private VkMusicService? _music;
    private MainViewModel? _viewModel;
    private bool _sessionEnded;

    public MainWindow(SavedSession session, ImageCache images, AppSettings settings)
    {
        _savedSession = session;
        _images = images;
        _settings = settings;

        InitializeComponent();

        if (settings.WindowWidth >= MinWidth) Width = settings.WindowWidth;
        if (settings.WindowHeight >= MinHeight) Height = settings.WindowHeight;

        RetryButton.Click += async (_, _) => await ConnectAsync();
        RelogButton.Click += (_, _) => EndSession(clearSaved: true);

        // Событие прокрутки всплывает из внутреннего ScrollViewer списка — ловим его здесь,
        // чтобы не искать этот ScrollViewer в дереве после применения шаблона.
        AddHandler(ScrollViewer.ScrollChangedEvent, OnScrollChanged, RoutingStrategies.Bubble);

        Opened += async (_, _) => await ConnectAsync();
        KeyDown += OnWindowKeyDown;
        Closing += OnWindowClosing;
    }

    /// <summary>Сессия закончилась. Параметр — надо ли забыть сохранённый токен.</summary>
    public event Action<bool>? SessionEnded;

    // ------------------------------------------------------------------ запуск

    private async Task ConnectAsync()
    {
        if (_viewModel is not null) return;

        LoadingActions.IsVisible = false;
        LoadingProgress.IsVisible = true;
        LoadingText.Text = "Подключаемся к ВКонтакте…";

        try
        {
            // Браузерный режим достаёт сессию из браузера здесь же — она могла успеть протухнуть.
            _session ??= VkSession.Create(_savedSession);
            _session.Api.CaptchaHandler = url => CaptchaDialog.ShowAsync(this, url);
            _music ??= new VkMusicService(_session.Api);

            var user = await _music.GetCurrentUserAsync();

            var viewModel = new MainViewModel(_session.Api, _music, _images, _settings, user);
            viewModel.LogoutRequested += () => EndSession(clearSaved: true);
            _viewModel = viewModel;

            AsyncRelayCommand.UnhandledError += OnCommandError;

            DataContext = viewModel;
            ProgressBar.DragStarted += OnSeekStarted;
            ProgressBar.DragCompleted += OnSeekCompleted;

            LoadingOverlay.IsVisible = false;
            RootUi.IsVisible = true;

            // Профиль мог измениться — обновим сохранённую сессию.
            _savedSession.UserName = user.FullName;
            _savedSession.UserPhoto = user.Photo;
            _savedSession.UserId = user.Id;
            AppStorage.SaveSession(_savedSession);

            await viewModel.InitializeAsync();
        }
        catch (VkApiException ex) when (ex.IsAuthFailure)
        {
            ShowConnectionProblem("Сессия ВКонтакте истекла. Нужно войти заново.", allowRetry: false);
        }
        catch (VkWebAuthException ex)
        {
            DropSession();
            ShowConnectionProblem(ex.Message, allowRetry: true);
        }
        catch (Exception ex)
        {
            DropSession();
            ShowConnectionProblem("Не удалось подключиться к ВКонтакте.\n" + ex.Message, allowRetry: true);
        }
    }

    private void ShowConnectionProblem(string message, bool allowRetry)
    {
        LoadingText.Text = message;
        LoadingProgress.IsVisible = false;
        LoadingActions.IsVisible = true;
        RetryButton.IsVisible = allowRetry;
    }

    /// <summary>Сбрасывает подключение, чтобы следующая попытка началась с чистого листа.</summary>
    private void DropSession()
    {
        _music = null;
        _session?.Dispose();
        _session = null;
    }

    private void EndSession(bool clearSaved)
    {
        if (_sessionEnded) return;
        _sessionEnded = true;
        SessionEnded?.Invoke(clearSaved);
    }

    private void OnCommandError(Exception ex) =>
        Dispatcher.UIThread.Post(() => _viewModel?.ShowStatus("Ошибка: " + ex.Message));

    // ------------------------------------------------------------------ список треков

    private void OnTrackRowLoaded(object? sender, RoutedEventArgs e)
    {
        // Обложку грузим только когда строка реально показалась — список виртуализирован.
        if (sender is Control { DataContext: TrackViewModel track }) track.EnsureCover();
    }

    private void OnPlaylistItemLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: PlaylistViewModel playlist }) playlist.EnsureCover();
    }

    private void OnTrackRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel is null) return;
        if (sender is Control { DataContext: TrackViewModel track })
        {
            _ = _viewModel.PlayTrackAsync(track);
            e.Handled = true;
        }
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_viewModel is null || !_viewModel.HasMoreTracks) return;
        if (e.Source is not ScrollViewer scroll) return;

        // Начинаем подгружать заранее, чтобы прокрутка не упиралась в конец списка.
        if (scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 600)
            _viewModel.LoadMoreCommand.Execute(null);
    }

    // ------------------------------------------------------------------ перемотка

    private void OnSeekStarted(object? sender, EventArgs e)
    {
        if (_viewModel is not null) _viewModel.IsSeeking = true;
    }

    private void OnSeekCompleted(object? sender, double value)
        => _viewModel?.CommitSeek(value);

    // ------------------------------------------------------------------ клавиатура

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null) return;

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        if (ctrl && e.Key == Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        // Медиаклавиши работают всегда, даже если курсор стоит в поиске.
        switch (e.Key)
        {
            case Key.MediaPlayPause: _viewModel.HandleShortcut("playpause"); e.Handled = true; return;
            case Key.MediaNextTrack: _viewModel.HandleShortcut("next"); e.Handled = true; return;
            case Key.MediaPreviousTrack: _viewModel.HandleShortcut("prev"); e.Handled = true; return;
        }

        if (e.Key == Key.Escape)
        {
            if (_viewModel.IsLyricsOpen) _viewModel.CloseLyricsCommand.Execute(null);
            else if (_viewModel.HasSearchQuery) _viewModel.ClearSearchCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Внутри поля ввода обычные клавиши должны печататься, а не управлять плеером.
        if (e.Source is TextBox) return;

        switch (e.Key)
        {
            case Key.Space: _viewModel.HandleShortcut("playpause"); break;
            case Key.Right when ctrl: _viewModel.HandleShortcut("next"); break;
            case Key.Left when ctrl: _viewModel.HandleShortcut("prev"); break;
            case Key.Right: _viewModel.HandleShortcut("seek-forward"); break;
            case Key.Left: _viewModel.HandleShortcut("seek-back"); break;
            case Key.Up: _viewModel.HandleShortcut("volume-up"); break;
            case Key.Down: _viewModel.HandleShortcut("volume-down"); break;
            case Key.M: _viewModel.HandleShortcut("mute"); break;
            default: return;
        }

        e.Handled = true;
    }

    // ------------------------------------------------------------------ закрытие

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        AsyncRelayCommand.UnhandledError -= OnCommandError;

        if (Width > 0 && Height > 0)
        {
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
            AppStorage.SaveSettings(_settings);
        }

        _viewModel?.Dispose();
        _viewModel = null;
        DropSession();
    }
}
