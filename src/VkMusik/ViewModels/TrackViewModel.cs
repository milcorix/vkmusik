using System;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using VkMusik.Models;

namespace VkMusik.ViewModels;

public sealed class TrackViewModel : ViewModelBase
{
    private Bitmap? _cover;
    private bool _coverRequested;
    private bool _isCurrent;
    private bool _isPlaying;
    private bool _isInMyMusic;
    private int _number;

    public TrackViewModel(VkTrack track, MainViewModel owner, int number, bool isInMyMusic)
    {
        Track = track;
        Owner = owner;
        _number = number;
        _isInMyMusic = isInMyMusic;
    }

    public VkTrack Track { get; }

    /// <summary>Ссылка на главную модель — из шаблона строки нужны её команды.</summary>
    public MainViewModel Owner { get; }

    public string Title => Track.Title;
    public string Artist => Track.Artist;
    public string? Subtitle => Track.Subtitle;
    public bool HasSubtitle => !string.IsNullOrWhiteSpace(Track.Subtitle);
    public string DurationText => Track.DurationText;
    public bool IsExplicit => Track.IsExplicit;
    public bool HasLyrics => Track.LyricsId is > 0;

    public int Number
    {
        get => _number;
        set { if (SetField(ref _number, value)) OnPropertyChanged(nameof(NumberText)); }
    }

    public string NumberText => _number.ToString();

    public Bitmap? Cover
    {
        get => _cover;
        private set
        {
            if (SetField(ref _cover, value)) OnPropertyChanged(nameof(HasCover));
        }
    }

    public bool HasCover => _cover is not null;

    /// <summary>Трек, который сейчас в плеере (не обязательно звучит — может стоять на паузе).</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetField(ref _isCurrent, value);
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set => SetField(ref _isPlaying, value);
    }

    public bool IsInMyMusic
    {
        get => _isInMyMusic;
        set
        {
            if (SetField(ref _isInMyMusic, value)) OnPropertyChanged(nameof(LikeTooltip));
        }
    }

    public string LikeTooltip => _isInMyMusic ? "Удалить из моей музыки" : "Добавить в мою музыку";

    /// <summary>Обложку тянем только когда строка реально появилась на экране.</summary>
    public void EnsureCover()
    {
        if (_coverRequested) return;
        _coverRequested = true;

        if (string.IsNullOrWhiteSpace(Track.CoverSmall)) return;

        _ = LoadCoverAsync();
    }

    private async Task LoadCoverAsync()
    {
        try
        {
            var bitmap = await Owner.Images.GetAsync(Track.CoverSmall, 96).ConfigureAwait(false);
            if (bitmap is not null) OnUiThread(() => Cover = bitmap);
        }
        catch
        {
            // Нет обложки — покажем заглушку с нотой.
        }
    }
}
