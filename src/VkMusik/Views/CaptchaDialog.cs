using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;

namespace VkMusik.Views;

/// <summary>
/// ВКонтакте изредка просит капчу прямо посреди обычных запросов.
/// Окно маленькое и живёт только этот момент, поэтому собрано кодом, без отдельного XAML.
/// </summary>
internal static class CaptchaDialog
{
    public static async Task<string?> ShowAsync(Window owner, string imageUrl)
    {
        var image = new Image
        {
            Height = 64,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        try
        {
            using var http = new HttpClient();
            var bytes = await http.GetByteArrayAsync(imageUrl);
            using var stream = new MemoryStream(bytes);
            image.Source = new Bitmap(stream);
        }
        catch
        {
            // Картинка не загрузилась — оставим пустое место, поле ввода всё равно покажем.
        }

        var input = new TextBox
        {
            PlaceholderText = "Символы с картинки",
            MinHeight = 40,
            CornerRadius = new CornerRadius(10),
        };
        input.Classes.Add("vk");

        var dialog = new Window
        {
            Title = "Проверка ВКонтакте",
            Width = 340,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var ok = new Button { Content = "Отправить", IsDefault = true };
        var cancel = new Button { Content = "Отмена", IsCancel = true };

        if (Application.Current?.TryGetResource("VkPrimaryButton", null, out var primary) == true
            && primary is ControlTheme primaryTheme)
            ok.Theme = primaryTheme;
        if (Application.Current?.TryGetResource("VkSecondaryButton", null, out var secondary) == true
            && secondary is ControlTheme secondaryTheme)
            cancel.Theme = secondaryTheme;

        ok.Click += (_, _) => dialog.Close(input.Text);
        cancel.Click += (_, _) => dialog.Close(null);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = "ВКонтакте просит подтвердить, что вы не робот.",
                    TextWrapping = TextWrapping.Wrap,
                },
                image,
                input,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, ok },
                },
            },
        };

        dialog.Opened += (_, _) => input.Focus();
        return await dialog.ShowDialog<string?>(owner);
    }
}
