using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DynamicIsland.Views;

/// <summary>
/// The island surface in the Windows 11 style. Floating (<see cref="TopOffset"/> = <see cref="FloatingOffset"/>) it is a
/// flyout: a rounded rectangle with a 1px stroke and a soft shadow. Docked (<see cref="TopOffset"/> = 0) it tucks into the
/// screen's top edge like the collapsed Snap bar: square top corners, no top stroke, rounded bottom. The offset is
/// animatable, and the top corners round in step with it, so docking and floating blend continuously.
/// Shape size is animated independently of the content, which keeps its own fixed layout and is centred and clipped.
/// </summary>
public sealed class IslandSurface : Decorator
{
    public const double CornerRadius = 8;
    public const double FloatingOffset = 8;
    private const int ShadowSteps = 6;

    public static readonly DependencyProperty ShapeWidthProperty = Layout(nameof(ShapeWidth), 200.0);
    public static readonly DependencyProperty ShapeHeightProperty = Layout(nameof(ShapeHeight), 36.0);
    public static readonly DependencyProperty TopOffsetProperty = Layout(nameof(TopOffset), 0.0);

    public static readonly DependencyProperty SurfaceProperty = RegisterBrush(nameof(Surface));
    public static readonly DependencyProperty TintProperty = RegisterBrush(nameof(Tint));
    public static readonly DependencyProperty StrokeProperty = RegisterBrush(nameof(Stroke));

    public static readonly DependencyProperty ShadowColorProperty = DependencyProperty.Register(
        nameof(ShadowColor), typeof(Color), typeof(IslandSurface),
        new FrameworkPropertyMetadata(Color.FromArgb(0x30, 0, 0, 0), FrameworkPropertyMetadataOptions.AffectsRender, OnShadowChanged));

    public static readonly DependencyProperty ArtworkProperty = DependencyProperty.Register(
        nameof(Artwork), typeof(ImageSource), typeof(IslandSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnArtworkChanged));

    public static readonly DependencyProperty ArtworkOpacityProperty = DependencyProperty.Register(
        nameof(ArtworkOpacity), typeof(double), typeof(IslandSurface),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private ImageBrush? _artworkBrush;
    private Brush[] _shadowBrushes = CreateShadow(Color.FromArgb(0x30, 0, 0, 0));

    public double ShapeWidth
    {
        get => (double)GetValue(ShapeWidthProperty);
        set => SetValue(ShapeWidthProperty, value);
    }

    public double ShapeHeight
    {
        get => (double)GetValue(ShapeHeightProperty);
        set => SetValue(ShapeHeightProperty, value);
    }

    /// <summary>Gap between the screen's top edge and the shape: 0 docked, <see cref="FloatingOffset"/> floating.</summary>
    public double TopOffset
    {
        get => (double)GetValue(TopOffsetProperty);
        set => SetValue(TopOffsetProperty, value);
    }

    public Brush? Surface
    {
        get => (Brush?)GetValue(SurfaceProperty);
        set => SetValue(SurfaceProperty, value);
    }

    /// <summary>Semi-transparent surface colour laid over the artwork so it only tints the flyout.</summary>
    public Brush? Tint
    {
        get => (Brush?)GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public Color ShadowColor
    {
        get => (Color)GetValue(ShadowColorProperty);
        set => SetValue(ShadowColorProperty, value);
    }

    public ImageSource? Artwork
    {
        get => (ImageSource?)GetValue(ArtworkProperty);
        set => SetValue(ArtworkProperty, value);
    }

    public double ArtworkOpacity
    {
        get => (double)GetValue(ArtworkOpacityProperty);
        set => SetValue(ArtworkOpacityProperty, value);
    }

    private double BottomRadius => Math.Max(0, Math.Min(CornerRadius, Math.Min(ShapeWidth, ShapeHeight) / 2));

    /// <summary>Top corners round in proportion to how far the shape has left the edge.</summary>
    private double TopRadius => BottomRadius * Math.Clamp(TopOffset / FloatingOffset, 0, 1);

    private bool IsDocked => TopOffset < 0.5;

    protected override Size MeasureOverride(Size constraint)
    {
        // Content keeps its natural size; the shape, not the content, decides what is visible.
        Child?.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Footprint;
    }

    /// <summary>The spring overshoots slightly past a zero-height target (stowing), so the layout size is clamped.</summary>
    private Size Footprint => new(Math.Max(0, ShapeWidth), Math.Max(0, ShapeHeight + TopOffset));

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        if (Child is { } child)
        {
            var size = child.DesiredSize;
            var offset = (ShapeWidth - size.Width) / 2;
            child.Arrange(new Rect(offset, TopOffset, size.Width, size.Height));
            // Clip in the child's coordinates: content outside the shape is neither drawn nor clickable.
            var clip = BuildOutline(-offset, 0, ShapeWidth, ShapeHeight, TopRadius, BottomRadius, closed: true);
            child.Clip = clip;
        }
        return Footprint;
    }

    protected override void OnRender(DrawingContext context)
    {
        if (ShapeWidth < 1 || ShapeHeight < 1)
            return;
        var bounds = new Rect(0, TopOffset, ShapeWidth, ShapeHeight);
        var bottomRadius = BottomRadius;

        // Layered soft shadow around and below the shape; above the screen edge it simply falls outside the window.
        for (var step = ShadowSteps; step >= 1; step--)
        {
            var spread = step * 1.6;
            var shadow = new Rect(bounds.X - spread, bounds.Y - spread * 0.4 + 2, bounds.Width + spread * 2, bounds.Height + spread * 1.6);
            context.DrawRoundedRectangle(_shadowBrushes[step - 1], null, shadow, bottomRadius + spread, bottomRadius + spread);
        }

        var shape = BuildOutline(0, TopOffset, ShapeWidth, ShapeHeight, TopRadius, bottomRadius, closed: true);
        context.DrawGeometry(Surface, null, shape);
        if (_artworkBrush is not null && ArtworkOpacity > 0.001)
        {
            context.PushClip(shape);
            context.PushOpacity(ArtworkOpacity);
            context.DrawRectangle(_artworkBrush, null, bounds);
            context.Pop();
            context.DrawRectangle(Tint, null, bounds);
            context.Pop();
        }

        // Inset by half the pen so the hairline stays inside; docked, the top edge has no stroke, like the Snap bar.
        var outline = BuildOutline(0.5, TopOffset + (IsDocked ? 0 : 0.5), ShapeWidth - 1, ShapeHeight - (IsDocked ? 0.5 : 1),
            Math.Max(0, TopRadius - 0.5), Math.Max(0, bottomRadius - 0.5), closed: !IsDocked);
        context.DrawGeometry(null, new Pen(Stroke, 1), outline);
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters)
    {
        // The shadow is decoration: only the shape takes the mouse.
        var shape = BuildOutline(0, TopOffset, ShapeWidth, ShapeHeight, TopRadius, BottomRadius, closed: true);
        return shape.FillContains(hitTestParameters.HitPoint) ? new PointHitTestResult(this, hitTestParameters.HitPoint) : null;
    }

    /// <summary>
    /// Rectangle with separate top and bottom corner radii. Open outlines start and end at the top corners and skip the
    /// top edge, which is how the docked stroke leaves the screen edge clean.
    /// </summary>
    private static Geometry BuildOutline(double x, double y, double width, double height, double topRadius, double bottomRadius, bool closed)
    {
        if (width <= 0 || height <= 0)
            return Geometry.Empty;
        topRadius = Math.Min(topRadius, Math.Min(width, height) / 2);
        bottomRadius = Math.Min(bottomRadius, Math.Min(width, height) / 2);
        double right = x + width, bottom = y + height;

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(x, y + topRadius), isFilled: true, isClosed: closed);
            context.LineTo(new Point(x, bottom - bottomRadius), true, true);
            Arc(context, new Point(x + bottomRadius, bottom), bottomRadius);
            context.LineTo(new Point(right - bottomRadius, bottom), true, true);
            Arc(context, new Point(right, bottom - bottomRadius), bottomRadius);
            context.LineTo(new Point(right, y + topRadius), true, true);
            if (closed)
            {
                Arc(context, new Point(right - topRadius, y), topRadius);
                context.LineTo(new Point(x + topRadius, y), true, true);
                Arc(context, new Point(x, y + topRadius), topRadius);
            }
        }
        geometry.Freeze();
        return geometry;

        static void Arc(StreamGeometryContext context, Point to, double radius)
        {
            if (radius > 0.01)
                context.ArcTo(to, new Size(radius, radius), 0, false, SweepDirection.Counterclockwise, true, true);
            else
                context.LineTo(to, true, true);
        }
    }

    private static DependencyProperty Layout(string name, double defaultValue) => DependencyProperty.Register(
        name, typeof(double), typeof(IslandSurface),
        new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    private static DependencyProperty RegisterBrush(string name) => DependencyProperty.Register(
        name, typeof(Brush), typeof(IslandSurface), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static Brush[] CreateShadow(Color color)
    {
        var brushes = new Brush[ShadowSteps];
        for (var step = 1; step <= ShadowSteps; step++)
        {
            // Outer rings fainter; the stacked alpha forms a soft falloff.
            var alpha = (byte)(color.A * (ShadowSteps + 1 - step) / (ShadowSteps * 3.0));
            var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
            brush.Freeze();
            brushes[step - 1] = brush;
        }
        return brushes;
    }

    private static void OnShadowChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((IslandSurface)sender)._shadowBrushes = CreateShadow((Color)e.NewValue);

    private static void OnArtworkChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var surface = (IslandSurface)sender;
        if (e.NewValue is not ImageSource image)
        {
            surface._artworkBrush = null;
            return;
        }
        var brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
        brush.Freeze();
        surface._artworkBrush = brush;
    }
}
