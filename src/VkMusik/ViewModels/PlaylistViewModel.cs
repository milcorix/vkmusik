using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using VkMusik.Models;

namespace VkMusik.ViewModels;

public sealed class PlaylistViewModel : ViewModelBase
{
    private Bitmap? _cover;
    private bool _coverRequested;

    public PlaylistViewModel(VkPlaylist playlist, MainViewModel owner)
    {
        Playlist = playlist;
        Owner = owner;
    }

    public VkPlaylist Playlist { get; }
    public MainViewModel Owner { get; }

    public string Title => Playlist.Title;
    public string SubtitleText => Playlist.SubtitleText;

    public Bitmap? Cover
    {
        get => _cover;
        private set
        {
            if (SetField(ref _cover, value)) OnPropertyChanged(nameof(HasCover));
        }
    }

    public bool HasCover => _cover is not null;

    public void EnsureCover()
    {
        if (_coverRequested) return;
        _coverRequested = true;
        if (string.IsNullOrWhiteSpace(Playlist.Cover)) return;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var bitmap = await Owner.Images.GetAsync(Playlist.Cover, 320).ConfigureAwait(false);
            if (bitmap is not null) OnUiThread(() => Cover = bitmap);
        }
        catch { }
    }
}

public sealed class NavItemViewModel : ViewModelBase
{
    private bool _isSelected;

    public NavItemViewModel(SectionKind kind, string title, string iconKey, MainViewModel owner)
    {
        Kind = kind;
        Title = title;
        IconKey = iconKey;
        Owner = owner;
    }

    public SectionKind Kind { get; }
    public string Title { get; }
    public MainViewModel Owner { get; }

    /// <summary>Имя ресурса с геометрией иконки.</summary>
    public string IconKey { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }
}

public enum SectionKind
{
    MyMusic,
    Recommendations,
    Popular,
    Playlists,
    Search,
    Playlist,
}

public enum RepeatMode
{
    Off,
    All,
    One,
}
