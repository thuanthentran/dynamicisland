using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DynamicIsland.Views;

/// <summary>Five rounded bars whose heights the host pushes from the spectrum at ~30 fps.</summary>
public sealed class EqualizerBars : FrameworkElement
{
    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(EqualizerBars),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double BarWidth = 3;
    private const double Gap = 2;
    private const double MinimumHeight = 3;
    // Room above and below the tallest bar so device-pixel snapping never shaves off its rounded end.
    private const double VerticalInset = 1.5;
    private readonly float[] _levels = new float[5];

    public Brush BarBrush
    {
        get => (Brush)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public void SetLevels(ReadOnlySpan<float> levels)
    {
        levels[..Math.Min(levels.Length, _levels.Length)].CopyTo(_levels);
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(_levels.Length * BarWidth + (_levels.Length - 1) * Gap, double.IsInfinity(availableSize.Height) ? 14 : availableSize.Height);

    protected override void OnRender(DrawingContext context)
    {
        var height = ActualHeight;
        var range = Math.Max(MinimumHeight, height - 2 * VerticalInset);
        for (var index = 0; index < _levels.Length; index++)
        {
            var bar = Math.Max(MinimumHeight, _levels[index] * range);
            var x = index * (BarWidth + Gap);
            context.DrawRoundedRectangle(BarBrush, null, new Rect(x, (height - bar) / 2, BarWidth, bar), BarWidth / 2, BarWidth / 2);
        }
    }
}

/// <summary>
/// Progress track drawn like the Windows 11 Slider: 4px rail, accent fill, a round thumb with an accent centre that grows
/// on hover. Click or drag to scrub; the position is committed through <see cref="Command"/> (fraction 0–1) on release,
/// so a drag issues one seek instead of many.
/// </summary>
public sealed class SeekBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(SeekBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(SeekBar),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(SeekBar),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThumbBrushProperty = DependencyProperty.Register(
        nameof(ThumbBrush), typeof(Brush), typeof(SeekBar),
        new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(
        nameof(Command), typeof(ICommand), typeof(SeekBar));

    private const double TrackHeight = 4;
    private const double ThumbRadius = 9;
    private const double CenterRadius = 5;
    private const double CenterHoverRadius = 6.5;
    private bool _dragging;
    private double _dragValue;

    public SeekBar()
    {
        Height = 20;
    }

    public Brush ThumbBrush
    {
        get => (Brush)GetValue(ThumbBrushProperty);
        set => SetValue(ThumbBrushProperty, value);
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush FillBrush
    {
        get => (Brush)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!IsEnabled)
            return;
        _dragging = true;
        CaptureMouse();
        UpdateDrag(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging)
            UpdateDrag(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging)
            return;
        _dragging = false;
        ReleaseMouseCapture();
        if (Command?.CanExecute(_dragValue) == true)
            Command.Execute(_dragValue);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _dragging = false;
        InvalidateVisual();
    }

    protected override void OnMouseEnter(MouseEventArgs e) => InvalidateVisual();

    protected override void OnMouseLeave(MouseEventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext context)
    {
        var middle = ActualHeight / 2;
        // The thumb travels inside the control so it is never clipped at either end.
        var left = ThumbRadius;
        var length = Math.Max(0, ActualWidth - 2 * ThumbRadius);
        var value = Math.Clamp(_dragging ? _dragValue : Value, 0, 1);
        var x = left + length * value;

        // Transparent full-height hit area makes the thin rail easy to grab.
        context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        context.DrawRoundedRectangle(TrackBrush, null, new Rect(left, middle - TrackHeight / 2, length, TrackHeight), 2, 2);
        context.DrawRoundedRectangle(FillBrush, null, new Rect(left, middle - TrackHeight / 2, x - left, TrackHeight), 2, 2);
        if (!IsEnabled)
            return;
        context.DrawEllipse(ThumbBrush, null, new Point(x, middle), ThumbRadius, ThumbRadius);
        var center = _dragging || IsMouseOver ? CenterHoverRadius : CenterRadius;
        context.DrawEllipse(FillBrush, null, new Point(x, middle), center, center);
    }

    private void UpdateDrag(Point point)
    {
        var length = ActualWidth - 2 * ThumbRadius;
        _dragValue = length > 0 ? Math.Clamp((point.X - ThumbRadius) / length, 0, 1) : 0;
        InvalidateVisual();
    }
}

/// <summary>
/// Slightly under-damped spring (~1.5% overshoot) used for every resize: fast start, soft settle, no visible bounce.
/// </summary>
public sealed class SpringEase : EasingFunctionBase
{
    private const double Damping = 0.8;
    private const double Frequency = 13.0; // rad/s at the nominal duration below
    private const double NominalSeconds = 0.55;

    public SpringEase() => EasingMode = EasingMode.EaseIn; // EaseIn passes t through unchanged to EaseInCore.

    protected override double EaseInCore(double normalizedTime)
    {
        if (normalizedTime >= 1)
            return 1;
        var t = normalizedTime * NominalSeconds;
        var damped = Frequency * Math.Sqrt(1 - Damping * Damping);
        var envelope = Math.Exp(-Damping * Frequency * t);
        return 1 - envelope * (Math.Cos(damped * t) + Damping * Frequency / damped * Math.Sin(damped * t));
    }

    protected override Freezable CreateInstanceCore() => new SpringEase();
}
