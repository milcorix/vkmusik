using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace VkMusik.Controls;

/// <summary>
/// Полоса прогресса/громкости в духе ВК: тонкая дорожка, заливка акцентом
/// и кружок-ползунок, который появляется при наведении.
/// Написана вручную, потому что нужны точные события «начал тянуть» и «отпустил»:
/// пока пользователь тащит, позицию нельзя перебивать таймером.
/// </summary>
public sealed class VkSeekBar : Control
{
    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<VkSeekBar, double>(nameof(Minimum));

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<VkSeekBar, double>(nameof(Maximum), 100.0);

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<VkSeekBar, double>(
            nameof(Value), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<VkSeekBar, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> FillBrushProperty =
        AvaloniaProperty.Register<VkSeekBar, IBrush?>(nameof(FillBrush));

    public static readonly StyledProperty<IBrush?> ThumbBrushProperty =
        AvaloniaProperty.Register<VkSeekBar, IBrush?>(nameof(ThumbBrush));

    public static readonly StyledProperty<double> TrackHeightProperty =
        AvaloniaProperty.Register<VkSeekBar, double>(nameof(TrackHeight), 4.0);

    public static readonly StyledProperty<double> ThumbRadiusProperty =
        AvaloniaProperty.Register<VkSeekBar, double>(nameof(ThumbRadius), 6.0);

    /// <summary>Всегда показывать ползунок, а не только при наведении.</summary>
    public static readonly StyledProperty<bool> AlwaysShowThumbProperty =
        AvaloniaProperty.Register<VkSeekBar, bool>(nameof(AlwaysShowThumb));

    private bool _dragging;
    private bool _hovered;

    static VkSeekBar()
    {
        AffectsRender<VkSeekBar>(
            MinimumProperty, MaximumProperty, ValueProperty,
            TrackBrushProperty, FillBrushProperty, ThumbBrushProperty,
            TrackHeightProperty, ThumbRadiusProperty, AlwaysShowThumbProperty);
    }

    public VkSeekBar()
    {
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public IBrush? FillBrush { get => GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }
    public IBrush? ThumbBrush { get => GetValue(ThumbBrushProperty); set => SetValue(ThumbBrushProperty, value); }
    public double TrackHeight { get => GetValue(TrackHeightProperty); set => SetValue(TrackHeightProperty, value); }
    public double ThumbRadius { get => GetValue(ThumbRadiusProperty); set => SetValue(ThumbRadiusProperty, value); }
    public bool AlwaysShowThumb { get => GetValue(AlwaysShowThumbProperty); set => SetValue(AlwaysShowThumbProperty, value); }

    /// <summary>Пользователь взялся за ползунок.</summary>
    public event EventHandler? DragStarted;

    /// <summary>Пользователь отпустил ползунок — вот итоговое значение.</summary>
    public event EventHandler<double>? DragCompleted;

    /// <summary>Значение поменяли мышью или клавишами (в том числе в процессе перетаскивания).</summary>
    public event EventHandler<double>? UserValueChanged;

    public bool IsDragging => _dragging;

    protected override Size MeasureOverride(Size availableSize)
    {
        double height = Math.Max(TrackHeight, ThumbRadius * 2) + 8;
        return new Size(double.IsInfinity(availableSize.Width) ? 120 : availableSize.Width, height);
    }

    private double Range => Math.Max(0.0001, Maximum - Minimum);

    private double Fraction => Math.Clamp((Value - Minimum) / Range, 0, 1);

    private double UsableWidth => Math.Max(1, Bounds.Width - ThumbRadius * 2);

    private double ValueFromPoint(Point point)
    {
        double fraction = Math.Clamp((point.X - ThumbRadius) / UsableWidth, 0, 1);
        return Minimum + fraction * Range;
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _hovered = true;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hovered = false;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        _dragging = true;
        e.Pointer.Capture(this);
        DragStarted?.Invoke(this, EventArgs.Empty);

        SetUserValue(ValueFromPoint(e.GetPosition(this)));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        SetUserValue(ValueFromPoint(e.GetPosition(this)));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;

        _dragging = false;
        e.Pointer.Capture(null);
        double value = ValueFromPoint(e.GetPosition(this));
        SetUserValue(value);
        DragCompleted?.Invoke(this, value);
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        double step = Range / 20;
        double value = Math.Clamp(Value + Math.Sign(e.Delta.Y) * step, Minimum, Maximum);
        SetUserValue(value);
        DragCompleted?.Invoke(this, value);
        e.Handled = true;
    }

    private void SetUserValue(double value)
    {
        value = Math.Clamp(value, Minimum, Maximum);
        if (Math.Abs(value - Value) < double.Epsilon) return;
        Value = value;
        UserValueChanged?.Invoke(this, value);
    }

    public override void Render(DrawingContext context)
    {
        double centerY = Bounds.Height / 2;
        double left = ThumbRadius;
        double width = UsableWidth;
        double radius = TrackHeight / 2;

        // Дорожка целиком.
        if (TrackBrush is { } track)
        {
            var rect = new RoundedRect(
                new Rect(left, centerY - TrackHeight / 2, width, TrackHeight),
                radius);
            context.DrawRectangle(track, null, rect);
        }

        // Пройденная часть.
        double filled = width * Fraction;
        if (FillBrush is { } fill && filled > 0.5)
        {
            var rect = new RoundedRect(
                new Rect(left, centerY - TrackHeight / 2, filled, TrackHeight),
                radius);
            context.DrawRectangle(fill, null, rect);
        }

        // Ползунок — только когда он нужен, чтобы полоса не выглядела перегруженной.
        if ((AlwaysShowThumb || _hovered || _dragging) && ThumbBrush is { } thumb)
        {
            var center = new Point(left + filled, centerY);
            context.DrawEllipse(thumb, null, center, ThumbRadius, ThumbRadius);
        }
    }
}
