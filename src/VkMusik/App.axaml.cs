using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using VkMusik.Services;
using VkMusik.Views;

namespace VkMusik;

public partial class App : Application
{
    private ImageCache? _images;
    private AppSettings _settings = new();
    private IClassicDesktopStyleApplicationLifetime? _desktop;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            _settings = AppStorage.LoadSettings();
            _images = new ImageCache();

            RequestedThemeVariant = string.Equals(_settings.Theme, "Light", StringComparison.OrdinalIgnoreCase)
                ? ThemeVariant.Light
                : ThemeVariant.Dark;

            desktop.Exit += (_, _) => _images?.Dispose();

            var session = AppStorage.LoadSession();
            if (session is not null) ShowMain(session);
            else ShowLogin();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ShowLogin()
    {
        var window = new LoginWindow();
        window.LoggedIn += session =>
        {
            AppStorage.SaveSession(session);
            ShowMain(session);
        };
        Swap(window);
    }

    private void ShowMain(SavedSession session)
    {
        var window = new MainWindow(session, _images!, _settings);
        window.SessionEnded += clearSaved =>
        {
            if (clearSaved) AppStorage.ClearSession();
            ShowLogin();
        };
        Swap(window);
    }

    /// <summary>
    /// Новое окно открываем до закрытия старого — иначе Avalonia решит,
    /// что окон не осталось, и завершит приложение.
    /// </summary>
    private void Swap(Avalonia.Controls.Window window)
    {
        if (_desktop is null) return;

        var previous = _desktop.MainWindow;
        _desktop.MainWindow = window;
        window.Show();
        previous?.Close();
    }
}
