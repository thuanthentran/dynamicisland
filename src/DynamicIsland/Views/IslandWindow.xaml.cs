using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynamicIsland.Platform;
using DynamicIsland.Services;
using DynamicIsland.ViewModels;
using static DynamicIsland.Platform.NativeMethods;

namespace DynamicIsland.Views;

/// <summary>
/// Host of the island HWND: a mostly transparent window pinned to the top centre of the primary monitor, with the island
/// floating just below the edge like a Windows 11 flyout. It owns the size state (<see cref="IslandModeController"/>),
/// shape interpolation, content slides, the artwork backdrop, banner timing and fullscreen hiding. Media, lyrics and
/// notification state live in <see cref="IslandViewModel"/>.
/// </summary>
public partial class IslandWindow : Window
{
    private const double SmallHeight = 32;
    // Stowed, nothing is drawn; a strip this tall along the top edge, as wide as the small island was, brings it back.
    private const double StowedHotZoneDip = 3;
    // Widths follow measured lyric lines (1036 lines, 13 tracks): the large card fits the longest line at 16px
    // semibold, the medium card the longest at 12px.
    private const double LargeWidth = 840;
    private const double MediumWidth = 480;
    private const double BannerWidth = 420;
    private const double BannerHeight = 56;
    private const double SlideDistance = 24;
    private const double LiftDistance = 12;
    private const double HoverPaddingDip = 6;
    private const double FullWindowHeight = 640;
    // Room below the shape for its shadow.
    private const double WindowSlackDip = 14;
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan SpectrumInterval = TimeSpan.FromMilliseconds(33);
    private static readonly TimeSpan TopmostInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TrimAfterIdle = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TrimMinimumInterval = TimeSpan.FromSeconds(60);
    private static readonly Duration ResizeDuration = new(TimeSpan.FromMilliseconds(520));
    private static readonly Duration ExitDuration = new(TimeSpan.FromMilliseconds(160));
    private static readonly Duration EnterDuration = new(TimeSpan.FromMilliseconds(300));
    private static readonly TimeSpan EnterDelay = TimeSpan.FromMilliseconds(60);
    // Into the resize: the spring has covered most of the distance, so dropped-in content is not clipped for long.
    private static readonly TimeSpan DropDelay = TimeSpan.FromMilliseconds(180);
    private static readonly Duration WindowFade = new(TimeSpan.FromMilliseconds(200));

    private readonly IslandViewModel _viewModel;
    private readonly IslandModeController _modes = new();
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _spectrumTimer;
    private readonly SpringEase _spring = new();
    private readonly float[] _bands = new float[AudioSpectrumService.BandCount];
    private readonly float[] _smoothedBands = new float[AudioSpectrumService.BandCount];
    private readonly FrameworkElement[] _panels;
    // Indexed by IslandTab.
    private readonly FrameworkElement[] _tabs;
    private IntPtr _hwnd;
    private ScreenInfo _screen;
    private FrameworkElement? _currentPanel;
    private IslandMode _shownMode = IslandMode.Small;
    private IslandTab _shownTab;
    private bool _headerShown;
    private int _wheelDelta;
    private DateTime? _bannerUntil;
    private double _mediumHeight = 72;
    private double _lastSmallWidth = 200;
    private Size _targetSize;
    private DateTime _smallSince = DateTime.UtcNow;
    private DateTime _lastTrim = DateTime.MinValue;
    private DateTime _lastTopmost = DateTime.MinValue;
    private bool _trimmedThisRest;
    private bool _placementQueued;
    private bool _stopped;

    public IslandWindow(IslandViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TickInterval };
        _timer.Tick += OnTick;
        _spectrumTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = SpectrumInterval };
        _spectrumTimer.Tick += OnSpectrumTick;

        _tabs = [HomeTab, SettingsTab];
        _panels = [StowedPanel, SmallIdlePanel, SmallMediaPanel, BannerPanel, MediumPanel, .. _tabs];
        foreach (var panel in _panels)
        {
            panel.RenderTransform = new TranslateTransform();
            panel.SizeChanged += OnPanelSizeChanged;
        }
        LargeHeader.RenderTransform = new TranslateTransform();

        Island.MouseLeftButtonUp += OnIslandClicked;
        Island.PreviewMouseWheel += OnIslandWheel;
        QuickCloseButton.Click += (_, _) => ApplyModeChange(_modes.QuickClose());
        StowIdleButton.Click += (_, _) => ApplyModeChange(_modes.Stow());
        StowMediaButton.Click += (_, _) => ApplyModeChange(_modes.Stow());
        ApplyPalette(viewModel.Palette);
    }

    public void Start()
    {
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        // Tool window: not in Alt+Tab. No-activate: clicks never steal focus from the app the user is in.
        var exStyle = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.BannerRequested += OnBannerRequested;
        _viewModel.SettingsChanged += ApplySettings;
        ApplySettings(_viewModel.CurrentSettings);

        Place();
        Show();
        Refresh(direction: 1, animate: false);
        SetWindowShown(true);
        _timer.Start();
    }

    public void Stop()
    {
        if (_stopped)
            return;
        _stopped = true;
        _timer.Stop();
        _spectrumTimer.Stop();
        _viewModel.SetSpectrumVisible(false);
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.BannerRequested -= OnBannerRequested;
        _viewModel.SettingsChanged -= ApplySettings;
    }

    private void ApplySettings(IslandSettings settings)
    {
        _modes.OpenDelay = TimeSpan.FromMilliseconds(settings.OpenDelayMs);
        _modes.MediumTimeout = TimeSpan.FromSeconds(settings.MediumTimeoutSeconds);
    }

    // ---- Placement -------------------------------------------------------------------------------------------------

    private void Place()
    {
        _placementQueued = false;
        _screen = ScreenProbe.Primary();
        var bounds = _screen.Bounds;
        // Enter the monitor first: WPF handles WM_DPICHANGED inside this call, so the size read afterwards is final.
        SetWindowPos(_hwnd, HWND_TOPMOST, bounds.Left, bounds.Top, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        if (GetWindowRect(_hwnd, out var rect))
            SetWindowPos(_hwnd, HWND_TOPMOST, bounds.Left + (bounds.Width - rect.Width) / 2, bounds.Top, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private void QueuePlacement()
    {
        if (_placementQueued || _stopped)
            return;
        _placementQueued = true;
        Dispatcher.InvokeAsync(() =>
        {
            Place();
            Refresh(direction: 1, animate: true);
        }, DispatcherPriority.Background);
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case WM_SETTINGCHANGE when lParam != IntPtr.Zero && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet":
                _viewModel.RefreshTheme();
                break;
            case WM_SETTINGCHANGE when wParam == SPI_SETWORKAREA:
            case WM_DISPLAYCHANGE:
            case WM_DPICHANGED:
                QueuePlacement();
                break;
        }
        return IntPtr.Zero;
    }

    // ---- Tick ------------------------------------------------------------------------------------------------------

    private void OnTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        _viewModel.Tick(DateTimeOffset.Now);

        var over = GetCursorPos(out var cursor) && IsOverIsland(cursor);
        var input = new ModeInput(
            now,
            over,
            OverControl: StowIdleButton.IsMouseOver || StowMediaButton.IsMouseOver,
            ScreenProbe.IsMouseButtonDown(),
            KeepOpen: Island.ContextMenu?.IsOpen == true || Mouse.Captured is not null,
            _viewModel.IsPlaying,
            ScreenProbe.IsFullscreenAppOn(_screen));
        ApplyModeChange(_modes.Update(input));

        if (_bannerUntil is { } until && now >= until)
        {
            _bannerUntil = null;
            Refresh(direction: 1, animate: true);
        }

        if (_modes.Mode != IslandMode.Hidden && now - _lastTopmost >= TopmostInterval)
        {
            _lastTopmost = now;
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        if (_modes.Mode is IslandMode.Small or IslandMode.Stowed && !_trimmedThisRest && now - _smallSince >= TrimAfterIdle &&
            now - _lastTrim >= TrimMinimumInterval)
        {
            _trimmedThisRest = true;
            _lastTrim = now;
            MemoryTrimmer.Trim();
        }
    }

    /// <summary>
    /// The current (animated) shape plus a little padding. Stowed, only a thin strip on the top edge where the small
    /// island was: a pointer pushed against the edge, not one passing by underneath.
    /// </summary>
    private bool IsOverIsland(POINT cursor)
    {
        if (_modes.Mode == IslandMode.Hidden || !GetWindowRect(_hwnd, out var window))
            return false;
        var scale = _screen.Scale;
        var centerX = window.Left + window.Width / 2.0;
        if (_modes.Mode == IslandMode.Stowed)
        {
            var halfZone = _lastSmallWidth / 2 * scale;
            return Math.Abs(cursor.X - centerX) <= halfZone && cursor.Y >= _screen.Bounds.Top &&
                   cursor.Y < _screen.Bounds.Top + StowedHotZoneDip * scale;
        }

        var halfWidth = (Island.ShapeWidth / 2 + HoverPaddingDip) * scale;
        return cursor.X >= centerX - halfWidth && cursor.X <= centerX + halfWidth &&
               cursor.Y >= _screen.Bounds.Top &&
               cursor.Y <= window.Top + (Island.TopOffset + Island.ShapeHeight + HoverPaddingDip) * scale;
    }

    private void OnIslandClicked(object sender, MouseButtonEventArgs e) => ApplyModeChange(_modes.Open());

    /// <summary>
    /// Scrolling on the large island's header strip steps through the tabs: down for the next, up for the previous,
    /// stopping at either end. Precision touchpads send small deltas, so they add up to one notch first.
    /// </summary>
    private void OnIslandWheel(object sender, MouseWheelEventArgs e)
    {
        if (_modes.Mode != IslandMode.Large || !_headerShown)
            return;
        var y = e.GetPosition(LargeHeader).Y;
        if (y < 0 || y > LargeHeader.ActualHeight)
            return;
        e.Handled = true;

        _wheelDelta += e.Delta;
        if (Math.Abs(_wheelDelta) < Mouse.MouseWheelDeltaForOneLine)
            return;
        var step = _wheelDelta < 0 ? 1 : -1;
        _wheelDelta = 0;
        _viewModel.SelectedTab = (IslandTab)Math.Clamp((int)_viewModel.SelectedTab + step, 0, _tabs.Length - 1);
    }

    // ---- Modes and content -----------------------------------------------------------------------------------------

    private void ApplyModeChange(IslandMode? change)
    {
        if (change is not { } mode)
            return;

        SetWindowShown(mode != IslandMode.Hidden);
        if (mode != IslandMode.Large)
            _viewModel.SelectedTab = IslandTab.Home; // every opening starts on Home
        if (mode is IslandMode.Large or IslandMode.Hidden)
            _bannerUntil = null; // the large island lists notifications itself
        if (mode is IslandMode.Stowed)
            _bannerUntil = null; // stowed means quiet
        if (mode is IslandMode.Small or IslandMode.Stowed)
        {
            _smallSince = DateTime.UtcNow;
            _trimmedThisRest = false;
        }
        if (mode == IslandMode.Large)
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        Refresh(direction: 1, animate: true);
    }

    private FrameworkElement ResolvePanel() => _modes.Mode switch
    {
        IslandMode.Large => _tabs[(int)_viewModel.SelectedTab],
        IslandMode.Medium => _bannerUntil.HasValue ? BannerPanel : MediumPanel,
        IslandMode.Stowed => StowedPanel,
        _ => _bannerUntil.HasValue ? BannerPanel : _viewModel.HasMedia ? SmallMediaPanel : SmallIdlePanel
    };

    private void Refresh(int direction, bool animate)
    {
        // Into or out of the small island (or nothing at all) is a vertical move in three beats: the old content lifts
        // away, then the shape resizes, then the new content drops in. Medium ↔ large and swaps within one size stay
        // horizontal and resize at once.
        var mode = _modes.Mode;
        var vertical = animate && mode != _shownMode && !(IsExpanded(mode) && IsExpanded(_shownMode));
        _shownMode = mode;
        _shownTab = _viewModel.SelectedTab;

        var next = ResolvePanel();
        PreparePanel(next);
        // With nothing on screen to lift away (stowed), the shape starts at once.
        var resizeDelay = vertical && _currentPanel is { } shown && shown != StowedPanel ? ExitDuration.TimeSpan : TimeSpan.Zero;
        var motion = vertical
            ? new Motion(TranslateTransform.YProperty, -LiftDistance, -LiftDistance, resizeDelay + DropDelay)
            : new Motion(TranslateTransform.XProperty, -SlideDistance * direction, SlideDistance * direction, EnterDelay);
        if (next != _currentPanel)
            SwapContent(next, animate, motion);
        UpdateHeader(animate, motion);
        ApplyLayout(animate, resizeDelay);

        var equalizerOn = IsEqualizerPanel(next) && _modes.Mode != IslandMode.Hidden;
        _viewModel.SetSpectrumVisible(equalizerOn);
        if (equalizerOn)
            _spectrumTimer.Start();
    }

    private void PreparePanel(FrameworkElement panel)
    {
        if (panel == SmallIdlePanel || panel == SmallMediaPanel)
        {
            panel.Height = SmallHeight;
        }
        else if (panel == StowedPanel)
        {
            (panel.Width, panel.Height) = (_lastSmallWidth, 0);
        }
        else if (panel == BannerPanel)
        {
            var size = BannerSize();
            (panel.Width, panel.Height) = (size.Width, size.Height);
        }
    }

    private Size BannerSize() => _modes.Mode == IslandMode.Medium
        ? new Size(MediumWidth, _mediumHeight)
        : new Size(BannerWidth, BannerHeight);

    private Size TargetSize(FrameworkElement panel)
    {
        if (panel == BannerPanel)
            return BannerSize();
        if (panel == StowedPanel)
            return new Size(_lastSmallWidth, 0); // the small island, drawn up into the edge until nothing is left

        panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = panel.DesiredSize;
        if (panel == SmallIdlePanel || panel == SmallMediaPanel)
        {
            // Sized by its content, like a taskbar flyout.
            _lastSmallWidth = Math.Ceiling(desired.Width);
            return new Size(_lastSmallWidth, SmallHeight);
        }

        var height = Math.Min(Math.Ceiling(desired.Height), FullWindowHeight - WindowSlackDip);
        if (panel == MediumPanel)
            _mediumHeight = height;
        return new Size(panel == MediumPanel ? MediumWidth : LargeWidth, height);
    }

    private static bool IsExpanded(IslandMode mode) => mode is IslandMode.Medium or IslandMode.Large;

    private void ApplyLayout(bool animate, TimeSpan delay = default)
    {
        if (_currentPanel is null)
            return;
        // Every size tucks into the screen edge like the collapsed Snap bar, so the pointer at the very top reaches it.
        var target = TargetSize(_currentPanel);
        if (Math.Abs(target.Width - _targetSize.Width) < 0.5 && Math.Abs(target.Height - _targetSize.Height) < 0.5)
            return;
        _targetSize = target;
        // Grow the window before the shape needs the room; shrink it only once the shape has settled.
        if (target.Height + WindowSlackDip > Height)
            Height = FullWindowHeight;

        if (!animate)
        {
            Island.BeginAnimation(IslandSurface.ShapeWidthProperty, null);
            Island.BeginAnimation(IslandSurface.ShapeHeightProperty, null);
            Island.ShapeWidth = target.Width;
            Island.ShapeHeight = target.Height;
            CompactWindow(target);
            return;
        }
        // From the current animated value: an interrupted resize continues smoothly toward the new target.
        Island.BeginAnimation(IslandSurface.ShapeWidthProperty,
            ResizeAnimation(Island.ShapeWidth, target.Width, delay), HandoffBehavior.SnapshotAndReplace);
        var height = ResizeAnimation(Island.ShapeHeight, target.Height, delay);
        height.Completed += (_, _) => CompactWindow(target);
        Island.BeginAnimation(IslandSurface.ShapeHeightProperty, height, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>
    /// The spring toward <paramref name="to"/>; with a delay the shape first holds its current size (key frames, so the
    /// held value never falls back to the property's base value).
    /// </summary>
    private AnimationTimeline ResizeAnimation(double from, double to, TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
            return new DoubleAnimation(to, ResizeDuration) { EasingFunction = _spring };
        var frames = new DoubleAnimationUsingKeyFrames();
        frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(delay)));
        frames.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(delay + ResizeDuration.TimeSpan), _spring));
        return frames;
    }

    /// <summary>
    /// A layered window repaints its whole surface every frame, so while the island rests the window is cut down to the
    /// shape's height (width and top edge stay put, so nothing moves on screen). Matters for the 30 fps equalizer.
    /// </summary>
    private void CompactWindow(Size settled)
    {
        if (_stopped || settled != _targetSize)
            return; // a newer resize is under way
        Height = Math.Ceiling(settled.Height + WindowSlackDip);
    }

    /// <summary>
    /// How content leaves and arrives. Horizontal: out toward the leading side, in from the trailing side (direction 1
    /// moves content leftward, -1 rightward). Vertical: out by lifting into the edge, in by dropping down from it once the
    /// shape has started resizing.
    /// </summary>
    private readonly record struct Motion(DependencyProperty Axis, double ExitOffset, double EnterOffset, TimeSpan EnterDelay);

    private void SwapContent(FrameworkElement next, bool animate, Motion motion)
    {
        var previous = _currentPanel;
        _currentPanel = next;
        if (!animate || previous is null)
        {
            Settle(next);
            if (previous is not null)
                previous.Visibility = Visibility.Collapsed;
            return;
        }
        SlideOut(previous, motion, () => previous != _currentPanel);
        SlideIn(next, motion);
    }

    /// <summary>The large island's header comes and goes with the large size, and stays put while its tabs swap.</summary>
    private void UpdateHeader(bool animate, Motion motion)
    {
        var show = _modes.Mode == IslandMode.Large;
        if (show == _headerShown)
            return;
        _headerShown = show;
        if (!animate)
        {
            if (show)
                Settle(LargeHeader);
            else
                LargeHeader.Visibility = Visibility.Collapsed;
        }
        else if (show)
        {
            SlideIn(LargeHeader, motion);
        }
        else
        {
            SlideOut(LargeHeader, motion, () => !_headerShown);
        }
    }

    private static void Settle(FrameworkElement element)
    {
        var shift = ResetMotion(element);
        shift.X = shift.Y = 0;
        element.Opacity = 1;
    }

    private void SlideIn(FrameworkElement element, Motion motion)
    {
        var shift = ResetMotion(element);
        shift.X = shift.Y = 0;
        // Base values cover the enter delay, so the new content never flashes at full opacity.
        element.Opacity = 0;
        shift.SetValue(motion.Axis, motion.EnterOffset);
        shift.BeginAnimation(motion.Axis,
            new DoubleAnimation(0, EnterDuration) { BeginTime = motion.EnterDelay, EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } });
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(1, EnterDuration) { BeginTime = motion.EnterDelay });
    }

    private static void SlideOut(FrameworkElement element, Motion motion, Func<bool> collapseWhenDone)
    {
        var fadeOut = new DoubleAnimation(0, ExitDuration);
        fadeOut.Completed += (_, _) =>
        {
            if (collapseWhenDone())
                element.Visibility = Visibility.Collapsed;
        };
        ((TranslateTransform)element.RenderTransform).BeginAnimation(motion.Axis,
            new DoubleAnimation(motion.ExitOffset, ExitDuration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
        element.BeginAnimation(OpacityProperty, fadeOut);
    }

    private static TranslateTransform ResetMotion(FrameworkElement element)
    {
        var shift = (TranslateTransform)element.RenderTransform;
        element.Visibility = Visibility.Visible;
        element.BeginAnimation(OpacityProperty, null);
        shift.BeginAnimation(TranslateTransform.XProperty, null);
        shift.BeginAnimation(TranslateTransform.YProperty, null);
        return shift;
    }

    private void OnPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender == _currentPanel)
            ApplyLayout(animate: true);
    }

    private void OnBannerRequested(TimeSpan duration)
    {
        if (_modes.Mode is IslandMode.Hidden or IslandMode.Stowed or IslandMode.Large || ScreenProbe.IsNotificationQuiet(_screen))
            return;
        _bannerUntil = DateTime.UtcNow + duration;
        Refresh(direction: 1, animate: true);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IslandViewModel.HasMedia):
                Refresh(direction: 1, animate: true);
                break;
            case nameof(IslandViewModel.SelectedTab) when _modes.Mode == IslandMode.Large && _viewModel.SelectedTab != _shownTab:
                Refresh(direction: _viewModel.SelectedTab > _shownTab ? 1 : -1, animate: true);
                break;
            case nameof(IslandViewModel.Palette):
                ApplyPalette(_viewModel.Palette);
                break;
            case nameof(IslandViewModel.BlurredArtwork):
                CrossfadeArtwork(_viewModel.BlurredArtwork);
                break;
            case nameof(IslandViewModel.CurrentLyric):
                AnimateLyricShift();
                break;
        }
    }

    // ---- Visual details --------------------------------------------------------------------------------------------

    private void ApplyPalette(IslandPalette palette)
    {
        Island.Surface = palette.Surface;
        Island.Tint = palette.Tint;
        Island.Stroke = palette.Stroke;
        Island.ShadowColor = palette.Shadow;
        Resources["Island.Foreground"] = palette.Foreground;
        Resources["Island.Secondary"] = palette.Secondary;
        Resources["Island.Tertiary"] = palette.Tertiary;
        Resources["Island.Card"] = palette.Card;
        Resources["Island.CardStroke"] = palette.CardStroke;
        Resources["Island.Hover"] = palette.Hover;
        Resources["Island.Pressed"] = palette.Pressed;
        Resources["Island.ControlFill"] = palette.ControlFill;
        Resources["Island.ControlStrong"] = palette.ControlStrong;
        Resources["Island.Track"] = palette.Track;
        Resources["Island.Thumb"] = palette.Thumb;
        Resources["Island.Accent"] = palette.Accent;
        Resources["Island.OnAccent"] = palette.OnAccent;
    }

    /// <summary>Dips the backdrop out and back in on track changes so two artworks never hard-cut.</summary>
    private void CrossfadeArtwork(ImageSource? next)
    {
        if (next is null)
        {
            Island.BeginAnimation(IslandSurface.ArtworkOpacityProperty, new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(300))));
            return;
        }
        if (Island.Artwork is null || Island.ArtworkOpacity < 0.01)
        {
            Island.Artwork = next;
            Island.BeginAnimation(IslandSurface.ArtworkOpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(420))));
            return;
        }

        var fadeOut = new DoubleAnimation(0.15, new Duration(TimeSpan.FromMilliseconds(180)));
        fadeOut.Completed += (_, _) =>
        {
            if (_viewModel.BlurredArtwork != next)
                return;
            Island.Artwork = next;
            Island.BeginAnimation(IslandSurface.ArtworkOpacityProperty, new DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(380))));
        };
        Island.BeginAnimation(IslandSurface.ArtworkOpacityProperty, fadeOut);
    }

    /// <summary>New lyric line: the visible lyric text slides up a step and settles, like a scrolling lyric sheet.</summary>
    private void AnimateLyricShift()
    {
        if (_currentPanel == HomeTab)
            SlideUp(LyricsStack, LyricsShift, 10);
        else if (_currentPanel == SmallMediaPanel)
            SlideUp(SmallLyric, SmallLyricShift, 6);
        else if (_currentPanel == MediumPanel)
            SlideUp(MediumLyric, EnsureShift(MediumLyric), 6);

        static void SlideUp(UIElement text, TranslateTransform shift, double distance)
        {
            var duration = new Duration(TimeSpan.FromMilliseconds(300));
            shift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(distance, 0, duration) { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } });
            text.BeginAnimation(OpacityProperty, new DoubleAnimation(0.4, 1, duration));
        }

        static TranslateTransform EnsureShift(UIElement text)
        {
            if (text.RenderTransform is not TranslateTransform shift)
                text.RenderTransform = shift = new TranslateTransform();
            return shift;
        }
    }

    private void SetWindowShown(bool shown)
    {
        if (shown)
        {
            if (!IsVisible)
                Show();
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, WindowFade));
            return;
        }

        var fade = new DoubleAnimation(0, WindowFade);
        fade.Completed += (_, _) =>
        {
            if (_modes.Mode == IslandMode.Hidden)
                Hide();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    private void OnSpectrumTick(object? sender, EventArgs e)
    {
        var active = _currentPanel is { } panel && IsEqualizerPanel(panel) && _modes.Mode != IslandMode.Hidden;
        if (!active || !_viewModel.ReadSpectrum(_bands) || !_viewModel.IsPlaying)
            Array.Clear(_bands);

        var resting = true;
        for (var i = 0; i < _bands.Length; i++)
        {
            var current = _smoothedBands[i];
            var target = _bands[i];
            // Fast attack, slow release: bars jump with the beat and fall back gently.
            _smoothedBands[i] = target > current ? current + (target - current) * 0.6f : current + (target - current) * 0.16f;
            resting &= _smoothedBands[i] < 0.01f;
        }
        // Only the visible bars repaint; the other set keeps its last frame while collapsed.
        (_currentPanel == MediumPanel ? MediumEqualizer : Equalizer).SetLevels(_smoothedBands);

        if (!active && resting)
            _spectrumTimer.Stop();
    }

    private bool IsEqualizerPanel(FrameworkElement panel) => panel == SmallMediaPanel || panel == MediumPanel;
}
