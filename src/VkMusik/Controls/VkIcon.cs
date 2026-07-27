using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace VkMusik.Controls;

/// <summary>
/// Иконка из готовой геометрии. Все пути нарисованы в квадрате 24×24, поэтому
/// масштабируем строго по этому квадрату, а не по границам конкретного пути —
/// иначе иконки разной «плотности» выглядели бы разного размера.
/// </summary>
public sealed class VkIcon : Control
{
    private const double DesignSize = 24.0;

    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<VkIcon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<VkIcon>();

    static VkIcon()
    {
        AffectsRender<VkIcon>(DataProperty, ForegroundProperty);
        AffectsMeasure<VkIcon>(DataProperty);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? DesignSize : availableSize.Width;
        double h = double.IsInfinity(availableSize.Height) ? DesignSize : availableSize.Height;
        return new Size(Math.Min(w, DesignSize), Math.Min(h, DesignSize));
    }

    public override void Render(DrawingContext context)
    {
        var data = Data;
        var brush = Foreground;
        if (data is null || brush is null) return;

        double scale = Math.Min(Bounds.Width, Bounds.Height) / DesignSize;
        if (scale <= 0) return;

        double drawn = DesignSize * scale;
        var matrix = Matrix.CreateScale(scale, scale)
                   * Matrix.CreateTranslation((Bounds.Width - drawn) / 2, (Bounds.Height - drawn) / 2);

        using (context.PushTransform(matrix))
            context.DrawGeometry(brush, null, data);
    }
}
