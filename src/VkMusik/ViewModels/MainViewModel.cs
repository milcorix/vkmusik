using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using VkMusik.Audio;
using VkMusik.Models;
using VkMusik.Services;

namespace VkMusik.ViewModels;

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private const int PageSize = 100;

    private readonly VkApiClient _api;
    private readonly VkMusicService _music;
    private readonly FfmpegAudioPlayer _player;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _positionTimer;
    private readonly DispatcherTimer _searchDebounce;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _settingsSaveDebounce;

    private CancellationTokenSource? _loadCts;
    private int _playGeneration;

    // очередь воспроизведения
    private List<TrackViewModel> _queue = new();
    private List<int> _order = new();
    private int _orderPos = -1;
    private readonly Random _random = new();

    public MainViewModel(VkApiClient api, VkMusicService music, ImageCache images,
                         AppSettings settings, VkUser user)
    {
        _api = api;
        _music = music;
        Images = images;
        _settings = settings;
        User = user;

        _player = new FfmpegAudioPlayer
        {
            Volume = settings.Volume,
            Muted = settings.Muted,
        };
        _player.StateChanged += OnPlayerStateChanged;
        _player.TrackEnded += OnTrackEnded;
        _player.ErrorOccurred += OnPlayerError;

        _volume = settings.Volume;
        _muted = settings.Muted;
        _shuffle = settings.Shuffle;
        _repeat = Enum.TryParse<RepeatMode>(settings.Repeat, out var r) ? r : RepeatMode.Off;
        _isDarkTheme = !string.Equals(settings.Theme, "Light", StringComparison.OrdinalIgnoreCase);

        NavItems =
        [
            new NavItemViewModel(SectionKind.MyMusic, "Моя музыка", "IconMusicNote", this),
            new NavItemViewModel(SectionKind.Playlists, "Плейлисты", "IconPlaylist", this),
            new NavItemViewModel(SectionKind.Recommendations, "Рекомендации", "IconStar", this),
            new NavItemViewModel(SectionKind.Popular, "Популярное", "IconFire", this),
        ];

        PlayPauseCommand = new RelayCommand(TogglePlayPause);
        NextCommand = new AsyncRelayCommand(() => NextAsync(auto: false));
        PreviousCommand = new AsyncRelayCommand(PreviousAsync);
        ToggleShuffleCommand = new RelayCommand(() => Shuffle = !Shuffle);
        CycleRepeatCommand = new RelayCommand(CycleRepeat);
        ToggleMuteCommand = new RelayCommand(() => Muted = !Muted);
        ToggleThemeCommand = new RelayCommand(() => IsDarkTheme = !IsDarkTheme);

        SelectNavCommand = new AsyncRelayCommand(p => p is NavItemViewModel nav
            ? SelectSectionAsync(nav.Kind) : Task.CompletedTask);
        OpenPlaylistCommand = new AsyncRelayCommand(p => p is PlaylistViewModel pl
            ? OpenPlaylistAsync(pl.Playlist) : Task.CompletedTask);
        PlayTrackCommand = new AsyncRelayCommand(p => p is TrackViewModel t
            ? PlayTrackAsync(t) : Task.CompletedTask);
        ToggleLikeCommand = new AsyncRelayCommand(p => p is TrackViewModel t
            ? ToggleLikeAsync(t) : Task.CompletedTask);
        DownloadTrackCommand = new AsyncRelayCommand(p => p is TrackViewModel t
            ? DownloadTrackAsync(t) : Task.CompletedTask);
        ShowLyricsCommand = new AsyncRelayCommand(p => p is TrackViewModel t
            ? ShowLyricsAsync(t) : Task.CompletedTask);
        CloseLyricsCommand = new RelayCommand(() => IsLyricsOpen = false);
        RefreshCommand = new AsyncRelayCommand(() => ReloadCurrentSectionAsync());
        LoadMoreCommand = new AsyncRelayCommand(LoadMoreAsync);
        ClearSearchCommand = new RelayCommand(() => SearchQuery = "");
        PlayAllCommand = new AsyncRelayCommand(PlayAllAsync);
        ShufflePlayCommand = new AsyncRelayCommand(ShufflePlayAsync);
        BackToPlaylistsCommand = new AsyncRelayCommand(() => SelectSectionAsync(SectionKind.Playlists));

        _positionTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, OnPositionTick);
        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _searchDebounce.Tick += OnSearchDebounceTick;
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); StatusMessage = null; };

        // Ползунок громкости шлёт десятки изменений в секунду — на диск пишем один раз, когда всё утихло.
        _settingsSaveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _settingsSaveDebounce.Tick += (_, _) =>
        {
            _settingsSaveDebounce.Stop();
            AppStorage.SaveSettings(_settings);
        };

        _positionTimer.Start();
    }

    public ImageCache Images { get; }
    public VkUser User { get; }

    public ObservableCollection<NavItemViewModel> NavItems { get; }
    public ObservableCollection<TrackViewModel> Tracks { get; } = new();
    public ObservableCollection<PlaylistViewModel> Playlists { get; } = new();
    public ObservableCollection<PlaylistViewModel> SidebarPlaylists { get; } = new();

    public bool HasSidebarPlaylists => SidebarPlaylists.Count > 0;

    // ------------------------------------------------------------- команды

    public RelayCommand PlayPauseCommand { get; }
    public AsyncRelayCommand NextCommand { get; }
    public AsyncRelayCommand PreviousCommand { get; }
    public RelayCommand ToggleShuffleCommand { get; }
    public RelayCommand CycleRepeatCommand { get; }
    public RelayCommand ToggleMuteCommand { get; }
    public RelayCommand ToggleThemeCommand { get; }
    public AsyncRelayCommand SelectNavCommand { get; }
    public AsyncRelayCommand OpenPlaylistCommand { get; }
    public AsyncRelayCommand PlayTrackCommand { get; }
    public AsyncRelayCommand ToggleLikeCommand { get; }
    public AsyncRelayCommand DownloadTrackCommand { get; }
    public AsyncRelayCommand ShowLyricsCommand { get; }
    public RelayCommand CloseLyricsCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand LoadMoreCommand { get; }
    public RelayCommand ClearSearchCommand { get; }
    public AsyncRelayCommand PlayAllCommand { get; }
    public AsyncRelayCommand ShufflePlayCommand { get; }
    public AsyncRelayCommand BackToPlaylistsCommand { get; }

    /// <summary>Просьба к окну выйти из аккаунта — обрабатывается в code-behind.</summary>
    public event Action? LogoutRequested;

    public RelayCommand LogoutCommand => _logoutCommand ??= new RelayCommand(() => LogoutRequested?.Invoke());
    private RelayCommand? _logoutCommand;

    // ------------------------------------------------------------- состояние раздела

    private SectionKind _section = SectionKind.MyMusic;
    private VkPlaylist? _openPlaylist;
    private int _totalCount;
    private bool _isLoading;
    private bool _isLoadingMore;
    private string _sectionTitle = "Моя музыка";
    private string? _sectionSubtitle;
    private string? _errorMessage;
    private string? _statusMessage;
    private string _searchQuery = "";

    public SectionKind Section
    {
        get => _section;
        private set
        {
            if (!SetField(ref _section, value)) return;
            OnPropertyChanged(nameof(ShowPlaylistsGrid));
            OnPropertyChanged(nameof(ShowTrackList));
            OnPropertyChanged(nameof(IsInsidePlaylist));
        }
    }

    public bool ShowPlaylistsGrid => Section == SectionKind.Playlists;
    public bool ShowTrackList => Section != SectionKind.Playlists;
    public bool IsInsidePlaylist => Section == SectionKind.Playlist;

    public string SectionTitle
    {
        get => _sectionTitle;
        private set => SetField(ref _sectionTitle, value);
    }

    public string? SectionSubtitle
    {
        get => _sectionSubtitle;
        private set
        {
            if (SetField(ref _sectionSubtitle, value)) OnPropertyChanged(nameof(HasSectionSubtitle));
        }
    }

    public bool HasSectionSubtitle => !string.IsNullOrWhiteSpace(_sectionSubtitle);

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetField(ref _isLoading, value)) OnPropertyChanged(nameof(IsEmptyState));
        }
    }

    public bool IsLoadingMore
    {
        get => _isLoadingMore;
        private set => SetField(ref _isLoadingMore, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetField(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(IsEmptyState));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(_errorMessage);

    /// <summary>Показывать «здесь пусто», когда загрузка кончилась, а показывать нечего.</summary>
    public bool IsEmptyState => !IsLoading && !HasError
        && (ShowPlaylistsGrid ? Playlists.Count == 0 : Tracks.Count == 0);

    public string? StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetField(ref _statusMessage, value)) OnPropertyChanged(nameof(HasStatus));
        }
    }

    public bool HasStatus => !string.IsNullOrWhiteSpace(_statusMessage);

    public void ShowStatus(string message)
    {
        OnUiThread(() =>
        {
            StatusMessage = message;
            _statusTimer.Stop();
            _statusTimer.Start();
        });
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (!SetField(ref _searchQuery, value)) return;
            OnPropertyChanged(nameof(HasSearchQuery));
            _searchDebounce.Stop();
            _searchDebounce.Start();
        }
    }

    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(_searchQuery);

    private async void OnSearchDebounceTick(object? sender, EventArgs e)
    {
        _searchDebounce.Stop();
        try
        {
            if (string.IsNullOrWhiteSpace(_searchQuery))
            {
                if (Section == SectionKind.Search) await SelectSectionAsync(SectionKind.MyMusic);
            }
            else
            {
                await SelectSectionAsync(SectionKind.Search);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    // ------------------------------------------------------------- состояние плеера

    private TrackViewModel? _currentTrack;
    private Bitmap? _currentCover;
    private bool _isPlaying;
    private bool _isBuffering;
    private double _positionSeconds;
    private double _durationSeconds;
    private bool _isSeeking;
    private double _volume;
    private bool _muted;
    private bool _shuffle;
    private RepeatMode _repeat;
    private bool _isDarkTheme;
    private bool _isLyricsOpen;
    private string? _lyricsText;

    public TrackViewModel? CurrentTrack
    {
        get => _currentTrack;
        private set
        {
            if (!SetField(ref _currentTrack, value)) return;
            OnPropertyChanged(nameof(HasCurrentTrack));
            OnPropertyChanged(nameof(CurrentTitle));
            OnPropertyChanged(nameof(CurrentArtist));
            OnPropertyChanged(nameof(CurrentHasLyrics));
        }
    }

    public bool HasCurrentTrack => _currentTrack is not null;
    public string CurrentTitle => _currentTrack?.Title ?? "Ничего не играет";
    public string CurrentArtist => _currentTrack?.Artist ?? "Выберите трек";
    public bool CurrentHasLyrics => _currentTrack?.HasLyrics ?? false;

    public Bitmap? CurrentCover
    {
        get => _currentCover;
        private set
        {
            if (SetField(ref _currentCover, value)) OnPropertyChanged(nameof(HasCurrentCover));
        }
    }

    public bool HasCurrentCover => _currentCover is not null;

    public bool IsPlaying
    {
        get => _isPlaying;
        private set => SetField(ref _isPlaying, value);
    }

    public bool IsBuffering
    {
        get => _isBuffering;
        private set => SetField(ref _isBuffering, value);
    }

    public double PositionSeconds
    {
        get => _positionSeconds;
        set
        {
            if (!SetField(ref _positionSeconds, value)) return;
            OnPropertyChanged(nameof(PositionText));
        }
    }

    public double DurationSeconds
    {
        get => _durationSeconds;
        private set
        {
            if (!SetField(ref _durationSeconds, value)) return;
            OnPropertyChanged(nameof(DurationText));
        }
    }

    public string PositionText => VkTrack.FormatDuration(_positionSeconds);
    public string DurationText => VkTrack.FormatDuration(_durationSeconds);

    /// <summary>Пока пользователь тащит ползунок, таймер позицию не трогает.</summary>
    public bool IsSeeking
    {
        get => _isSeeking;
        set => SetField(ref _isSeeking, value);
    }

    public void CommitSeek(double seconds)
    {
        IsSeeking = false;
        if (_currentTrack is null) return;
        PositionSeconds = seconds;
        _player.Seek(seconds);
    }

    public double Volume
    {
        get => _volume;
        set
        {
            if (!SetField(ref _volume, value)) return;
            _player.Volume = value;
            if (value > 0 && _muted) Muted = false;
            _settings.Volume = value;
            SaveSettingsSoon();
            OnPropertyChanged(nameof(VolumeIconKey));
        }
    }

    public bool Muted
    {
        get => _muted;
        set
        {
            if (!SetField(ref _muted, value)) return;
            _player.Muted = value;
            _settings.Muted = value;
            SaveSettingsSoon();
            OnPropertyChanged(nameof(VolumeIconKey));
        }
    }

    public string VolumeIconKey => _muted || _volume <= 0.001
        ? "IconVolumeOff"
        : _volume < 0.5 ? "IconVolumeLow" : "IconVolumeHigh";

    public bool Shuffle
    {
        get => _shuffle;
        set
        {
            if (!SetField(ref _shuffle, value)) return;
            _settings.Shuffle = value;
            SaveSettingsSoon();
            if (_queue.Count > 0 && _orderPos >= 0 && _orderPos < _order.Count)
                RebuildOrder(_order[_orderPos]);
            ShowStatus(value ? "Перемешивание включено" : "Перемешивание выключено");
        }
    }

    public RepeatMode Repeat
    {
        get => _repeat;
        private set
        {
            if (!SetField(ref _repeat, value)) return;
            _settings.Repeat = value.ToString();
            SaveSettingsSoon();
            OnPropertyChanged(nameof(RepeatIconKey));
            OnPropertyChanged(nameof(IsRepeatActive));
        }
    }

    public string RepeatIconKey => _repeat == RepeatMode.One ? "IconRepeatOne" : "IconRepeat";
    public bool IsRepeatActive => _repeat != RepeatMode.Off;

    private void CycleRepeat()
    {
        Repeat = _repeat switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.Off,
        };
        ShowStatus(_repeat switch
        {
            RepeatMode.All => "Повтор списка",
            RepeatMode.One => "Повтор одного трека",
            _ => "Повтор выключен",
        });
    }

    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        set
        {
            if (!SetField(ref _isDarkTheme, value)) return;
            ApplyTheme();
            _settings.Theme = value ? "Dark" : "Light";
            SaveSettingsSoon();
        }
    }

    public void ApplyTheme()
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = _isDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    public bool IsLyricsOpen
    {
        get => _isLyricsOpen;
        private set => SetField(ref _isLyricsOpen, value);
    }

    public string? LyricsText
    {
        get => _lyricsText;
        private set => SetField(ref _lyricsText, value);
    }

    // ------------------------------------------------------------- загрузка разделов

    public async Task InitializeAsync()
    {
        ApplyTheme();
        _ = LoadUserAvatarAsync();
        _ = LoadSidebarPlaylistsAsync();
        await SelectSectionAsync(SectionKind.MyMusic);
    }

    private Bitmap? _userAvatar;
    public Bitmap? UserAvatar
    {
        get => _userAvatar;
        private set
        {
            if (SetField(ref _userAvatar, value)) OnPropertyChanged(nameof(HasUserAvatar));
        }
    }
    public bool HasUserAvatar => _userAvatar is not null;

    private async Task LoadUserAvatarAsync()
    {
        try
        {
            var bitmap = await Images.GetAsync(User.Photo, 96);
            if (bitmap is not null) UserAvatar = bitmap;
        }
        catch { }
    }

    private async Task LoadSidebarPlaylistsAsync()
    {
        try
        {
            var page = await _music.GetPlaylistsAsync(_music.CurrentUserId, 0, 30);
            SidebarPlaylists.Clear();
            foreach (var playlist in page.Items)
                SidebarPlaylists.Add(new PlaylistViewModel(playlist, this));
            OnPropertyChanged(nameof(HasSidebarPlaylists));
        }
        catch (Exception)
        {
            // Плейлисты в боковой панели — украшение, без них жить можно.
        }
    }

    public async Task SelectSectionAsync(SectionKind kind, VkPlaylist? playlist = null)
    {
        foreach (var nav in NavItems) nav.IsSelected = nav.Kind == kind;

        Section = kind;
        _openPlaylist = playlist;

        (SectionTitle, SectionSubtitle) = kind switch
        {
            SectionKind.MyMusic => ("Моя музыка", null),
            SectionKind.Playlists => ("Плейлисты", null),
            SectionKind.Recommendations => ("Рекомендации", "Подобрано ВКонтакте лично для вас"),
            SectionKind.Popular => ("Популярное", "Что сейчас слушают"),
            SectionKind.Search => ($"Поиск: {SearchQuery}", null),
            SectionKind.Playlist => (playlist?.Title ?? "Плейлист", playlist?.Description),
            _ => ("Музыка", null),
        };

        await ReloadCurrentSectionAsync();
    }

    private Task OpenPlaylistAsync(VkPlaylist playlist) => SelectSectionAsync(SectionKind.Playlist, playlist);

    public async Task ReloadCurrentSectionAsync()
    {
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var ct = cts.Token;

        Tracks.Clear();
        Playlists.Clear();
        _totalCount = 0;
        ErrorMessage = null;
        IsLoading = true;
        OnPropertyChanged(nameof(IsEmptyState));

        try
        {
            if (Section == SectionKind.Playlists)
            {
                var page = await _music.GetPlaylistsAsync(_music.CurrentUserId, 0, 100, ct);
                if (ct.IsCancellationRequested) return;
                _totalCount = page.TotalCount;
                foreach (var playlist in page.Items)
                    Playlists.Add(new PlaylistViewModel(playlist, this));
                SectionSubtitle = page.TotalCount > 0
                    ? $"{page.TotalCount} {VkPlaylist.Plural(page.TotalCount, "плейлист", "плейлиста", "плейлистов")}"
                    : null;
            }
            else
            {
                var page = await FetchTracksAsync(0, ct);
                if (ct.IsCancellationRequested) return;
                _totalCount = page.TotalCount;
                AppendTracks(page.Items);
                UpdateSectionCountSubtitle();
            }
        }
        catch (OperationCanceledException) { }
        catch (VkApiException ex)
        {
            ErrorMessage = DescribeSectionError(ex);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                IsLoading = false;
                OnPropertyChanged(nameof(IsEmptyState));
            }
        }
    }

    private void UpdateSectionCountSubtitle()
    {
        if (Section is SectionKind.MyMusic or SectionKind.Playlist && _totalCount > 0)
            SectionSubtitle = $"{_totalCount} {VkPlaylist.Plural(_totalCount, "трек", "трека", "треков")}";
        else if (Section == SectionKind.Search && _totalCount > 0)
            SectionSubtitle = $"Найдено {_totalCount} {VkPlaylist.Plural(_totalCount, "трек", "трека", "треков")}";
    }

    private string DescribeSectionError(VkApiException ex)
    {
        if (ex.IsAudioForbidden)
            return "ВКонтакте не даёт этому токену доступ к аудио. "
                 + "Войдите заново — токен должен быть выдан с правом «Аудио».";

        if (Section == SectionKind.Popular)
            return "ВКонтакте больше не отдаёт этот раздел через API. Попробуйте «Рекомендации» или поиск.";

        if (Section == SectionKind.Recommendations)
            return "Рекомендации пока недоступны — ВКонтакте не вернул подборку. "
                 + "Послушайте немного музыки и загляните снова.";

        return ex.FriendlyMessage;
    }

    private Task<VkPage<VkTrack>> FetchTracksAsync(int offset, CancellationToken ct) => Section switch
    {
        SectionKind.MyMusic => _music.GetAudioAsync(_music.CurrentUserId, null, null, offset, PageSize, ct),
        SectionKind.Playlist => _music.GetAudioAsync(
            _openPlaylist?.OwnerId ?? _music.CurrentUserId, _openPlaylist?.Id,
            _openPlaylist?.AccessKey, offset, PageSize, ct),
        SectionKind.Recommendations => _music.GetRecommendationsAsync(offset, PageSize, ct),
        SectionKind.Popular => _music.GetPopularAsync(offset, PageSize, ct),
        SectionKind.Search => _music.SearchAsync(SearchQuery, offset, PageSize, ct),
        _ => Task.FromResult(new VkPage<VkTrack>()),
    };

    private void AppendTracks(IReadOnlyList<VkTrack> items)
    {
        bool mine = Section == SectionKind.MyMusic;
        foreach (var track in items)
        {
            var vm = new TrackViewModel(track, this, Tracks.Count + 1, mine);
            if (_currentTrack is not null
                && vm.Track.Id == _currentTrack.Track.Id
                && vm.Track.OwnerId == _currentTrack.Track.OwnerId)
            {
                vm.IsCurrent = true;
                vm.IsPlaying = IsPlaying;
            }
            Tracks.Add(vm);
        }
    }

    public bool HasMoreTracks => Tracks.Count < _totalCount && Section != SectionKind.Playlists;

    private async Task LoadMoreAsync()
    {
        if (IsLoading || IsLoadingMore || !HasMoreTracks) return;

        var ct = _loadCts?.Token ?? CancellationToken.None;
        IsLoadingMore = true;
        try
        {
            var page = await FetchTracksAsync(Tracks.Count, ct);
            if (ct.IsCancellationRequested) return;
            if (page.TotalCount > 0) _totalCount = page.TotalCount;
            AppendTracks(page.Items);

            // ВК иногда рапортует больше, чем реально отдаёт — иначе будем грузить вечно.
            if (page.Items.Count == 0) _totalCount = Tracks.Count;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ShowStatus("Не удалось догрузить: " + ex.Message);
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    // ------------------------------------------------------------- воспроизведение

    public async Task PlayTrackAsync(TrackViewModel track)
    {
        // Уже играет этот трек — просто пауза/продолжить.
        if (ReferenceEquals(_currentTrack, track))
        {
            TogglePlayPause();
            return;
        }

        _queue = Tracks.ToList();
        int index = _queue.IndexOf(track);
        if (index < 0)
        {
            _queue = [track];
            index = 0;
        }
        RebuildOrder(index);
        await PlayQueueItemAsync(index);
    }

    private async Task PlayAllAsync()
    {
        if (Tracks.Count == 0) return;
        _queue = Tracks.ToList();
        RebuildOrder(0);
        await PlayQueueItemAsync(_order[0]);
    }

    private async Task ShufflePlayAsync()
    {
        if (Tracks.Count == 0) return;
        Shuffle = true;
        _queue = Tracks.ToList();
        int start = _random.Next(_queue.Count);
        RebuildOrder(start);
        await PlayQueueItemAsync(start);
    }

    private void RebuildOrder(int currentIndex)
    {
        _order = Enumerable.Range(0, _queue.Count).ToList();

        if (_shuffle)
        {
            for (int i = _order.Count - 1; i > 0; i--)
            {
                int j = _random.Next(i + 1);
                (_order[i], _order[j]) = (_order[j], _order[i]);
            }
            // Текущий трек всегда первый — дальше идёт перемешанный хвост.
            int at = _order.IndexOf(currentIndex);
            if (at > 0) (_order[0], _order[at]) = (_order[at], _order[0]);
        }

        _orderPos = _order.IndexOf(currentIndex);
    }

    private async Task PlayQueueItemAsync(int queueIndex)
    {
        if (queueIndex < 0 || queueIndex >= _queue.Count) return;

        _orderPos = _order.IndexOf(queueIndex);
        var vm = _queue[queueIndex];
        int generation = ++_playGeneration;

        SetCurrentTrack(vm);
        DurationSeconds = vm.Track.Duration;
        PositionSeconds = 0;
        IsBuffering = true;
        _ = LoadCurrentCoverAsync(vm);

        string? url;
        try
        {
            // Ссылки ВК живут около часа и привязаны к IP — берём свежую прямо перед стартом.
            url = await _music.ResolveUrlAsync(vm.Track) ?? vm.Track.Url;
        }
        catch (VkApiException ex)
        {
            url = vm.Track.Url;
            if (url is null)
            {
                if (generation == _playGeneration)
                {
                    IsBuffering = false;
                    ShowStatus("Не удалось получить ссылку: " + ex.FriendlyMessage);
                }
                return;
            }
        }
        catch (Exception)
        {
            url = vm.Track.Url;
        }

        if (generation != _playGeneration) return;

        if (string.IsNullOrWhiteSpace(url))
        {
            IsBuffering = false;
            ShowStatus($"ВКонтакте не отдаёт файл трека «{vm.Title}»");
            await NextAsync(auto: true);
            return;
        }

        vm.Track.Url = url;
        _player.Play(url);
    }

    private async Task LoadCurrentCoverAsync(TrackViewModel vm)
    {
        var source = vm.Track.CoverLarge ?? vm.Track.CoverSmall;
        if (string.IsNullOrWhiteSpace(source))
        {
            CurrentCover = null;
            return;
        }

        try
        {
            var bitmap = await Images.GetAsync(source, 160);
            if (ReferenceEquals(_currentTrack, vm)) CurrentCover = bitmap;
        }
        catch { }
    }

    private void SetCurrentTrack(TrackViewModel? vm)
    {
        if (_currentTrack is not null)
        {
            _currentTrack.IsCurrent = false;
            _currentTrack.IsPlaying = false;
        }

        CurrentTrack = vm;
        if (vm is not null) vm.IsCurrent = true;

        // Тот же трек может присутствовать и в открытом списке — подсветим и его.
        foreach (var row in Tracks)
        {
            bool same = vm is not null
                && row.Track.Id == vm.Track.Id
                && row.Track.OwnerId == vm.Track.OwnerId;
            row.IsCurrent = same;
            if (!same) row.IsPlaying = false;
        }

        IsLyricsOpen = false;
        LyricsText = null;
    }

    private void TogglePlayPause()
    {
        if (_currentTrack is null)
        {
            if (Tracks.Count > 0) _ = PlayAllAsync();
            return;
        }
        _player.TogglePause();
    }

    private async Task NextAsync(bool auto)
    {
        if (_queue.Count == 0 || _order.Count == 0) return;

        int next = _orderPos + 1;
        if (next >= _order.Count)
        {
            if (_repeat == RepeatMode.All)
            {
                next = 0;
            }
            else
            {
                _player.Stop();
                if (auto) ShowStatus("Список закончился");
                return;
            }
        }

        await PlayQueueItemAsync(_order[next]);
    }

    private async Task PreviousAsync()
    {
        if (_queue.Count == 0 || _order.Count == 0) return;

        // Как в ВК: в начале трека — к предыдущему, дальше — к его началу.
        if (_player.Position > 3)
        {
            CommitSeek(0);
            return;
        }

        int prev = _orderPos - 1;
        if (prev < 0)
        {
            if (_repeat == RepeatMode.All) prev = _order.Count - 1;
            else { CommitSeek(0); return; }
        }

        await PlayQueueItemAsync(_order[prev]);
    }

    private void OnPlayerStateChanged(PlayerState state) => OnUiThread(() =>
    {
        IsBuffering = state == PlayerState.Buffering;
        IsPlaying = state == PlayerState.Playing;

        if (_currentTrack is not null) _currentTrack.IsPlaying = IsPlaying;
        foreach (var row in Tracks)
            if (row.IsCurrent) row.IsPlaying = IsPlaying;
    });

    private void OnTrackEnded() => OnUiThread(async () =>
    {
        if (_repeat == RepeatMode.One && _orderPos >= 0 && _orderPos < _order.Count)
        {
            await PlayQueueItemAsync(_order[_orderPos]);
            return;
        }
        await NextAsync(auto: true);
    });

    private void OnPlayerError(string message) => OnUiThread(() =>
    {
        IsBuffering = false;
        ShowStatus(message);
    });

    private void OnPositionTick(object? sender, EventArgs e)
    {
        if (_currentTrack is null || IsSeeking) return;

        double position = _player.Position;
        if (Math.Abs(position - _positionSeconds) > 0.05) PositionSeconds = position;

        if (_durationSeconds <= 0 && _currentTrack.Track.Duration > 0)
            DurationSeconds = _currentTrack.Track.Duration;
    }

    // ------------------------------------------------------------- действия с треком

    private async Task ToggleLikeAsync(TrackViewModel vm)
    {
        try
        {
            if (vm.IsInMyMusic)
            {
                if (await _music.DeleteAsync(vm.Track))
                {
                    vm.IsInMyMusic = false;
                    ShowStatus("Удалено из моей музыки");
                    if (Section == SectionKind.MyMusic)
                    {
                        Tracks.Remove(vm);
                        Renumber();
                        if (_totalCount > 0) _totalCount--;
                        UpdateSectionCountSubtitle();
                        OnPropertyChanged(nameof(IsEmptyState));
                    }
                }
            }
            else
            {
                await _music.AddAsync(vm.Track);
                vm.IsInMyMusic = true;
                ShowStatus("Добавлено в мою музыку");
            }
        }
        catch (VkApiException ex)
        {
            ShowStatus(ex.FriendlyMessage);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message);
        }
    }

    private void Renumber()
    {
        for (int i = 0; i < Tracks.Count; i++) Tracks[i].Number = i + 1;
    }

    private async Task DownloadTrackAsync(TrackViewModel vm)
    {
        ShowStatus($"Скачиваю «{vm.Title}»…");
        try
        {
            string? url = await _music.ResolveUrlAsync(vm.Track) ?? vm.Track.Url;
            if (string.IsNullOrWhiteSpace(url))
            {
                ShowStatus("ВКонтакте не отдал файл этого трека");
                return;
            }

            string path = await TrackDownloader.SaveAsMp3Async(vm.Track, url);
            ShowStatus($"Сохранено: {path}");
        }
        catch (Exception ex)
        {
            ShowStatus("Не удалось скачать: " + ex.Message);
        }
    }

    private async Task ShowLyricsAsync(TrackViewModel vm)
    {
        if (vm.Track.LyricsId is not > 0)
        {
            ShowStatus("У этого трека нет текста");
            return;
        }

        IsLyricsOpen = true;
        LyricsText = null;
        try
        {
            LyricsText = await _music.GetLyricsAsync(vm.Track.LyricsId.Value)
                         ?? "ВКонтакте не вернул текст песни.";
        }
        catch (Exception ex)
        {
            LyricsText = "Не удалось загрузить текст: " + ex.Message;
        }
    }

    /// <summary>Пробел, стрелки и прочие горячие клавиши.</summary>
    public void HandleShortcut(string action)
    {
        switch (action)
        {
            case "playpause": TogglePlayPause(); break;
            case "next": _ = NextAsync(auto: false); break;
            case "prev": _ = PreviousAsync(); break;
            case "seek-forward": CommitSeek(Math.Min(_durationSeconds, _player.Position + 10)); break;
            case "seek-back": CommitSeek(Math.Max(0, _player.Position - 10)); break;
            case "volume-up": Volume = Math.Min(1, _volume + 0.05); break;
            case "volume-down": Volume = Math.Max(0, _volume - 0.05); break;
            case "mute": Muted = !Muted; break;
        }
    }

    /// <summary>Откладываем запись настроек, чтобы не дёргать диск на каждое изменение.</summary>
    private void SaveSettingsSoon()
    {
        _settingsSaveDebounce.Stop();
        _settingsSaveDebounce.Start();
    }

    public void Dispose()
    {
        _positionTimer.Stop();
        _searchDebounce.Stop();
        _statusTimer.Stop();
        if (_settingsSaveDebounce.IsEnabled)
        {
            _settingsSaveDebounce.Stop();
            AppStorage.SaveSettings(_settings);
        }
        _loadCts?.Cancel();
        _player.Dispose();
    }
}
