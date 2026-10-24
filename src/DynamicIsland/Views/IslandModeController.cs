namespace DynamicIsland.Views;

public enum IslandMode
{
    Hidden,
    /// <summary>Stowed by the user: nothing is drawn; resting the pointer on the top edge where it was brings it back.</summary>
    Stowed,
    Small,
    Medium,
    Large
}

/// <summary>One tick of cursor and desktop state, already resolved by the host.</summary>
/// <param name="OverControl">The pointer is on a button of the small island; resting there must not expand it.</param>
public readonly record struct ModeInput(
    DateTime Now,
    bool OverIsland,
    bool OverControl,
    bool MouseButtonDown,
    bool KeepOpen,
    bool IsPlaying,
    bool Fullscreen);

/// <summary>
/// Pure size-state policy; owns no window or timer.
/// <list type="bullet">
/// <item>Small → Large after the cursor rests on the island for <see cref="OpenDelay"/> with no button held. A button held
/// over it (window drag, Snap) or a quick close blocks reopening until the cursor leaves.</item>
/// <item>Large → Medium (media playing) or Small after the cursor has been away for <see cref="LeaveDelay"/>.</item>
/// <item>Medium → Large on a short hover; → Small after <see cref="MediumTimeout"/> away.</item>
/// <item>Any → Hidden while a fullscreen app runs, debounced both ways; it comes back as it was (stowed or small).</item>
/// <item>Small → Stowed only through <see cref="Stow"/>. Stowed opens like Small (the host reports its top-edge hot zone as
/// <see cref="ModeInput.OverIsland"/>), and once expanded the island returns to Small, not Stowed: stowing is a one-off
/// choice the user repeats with the button.</item>
/// </list>
/// </summary>
public sealed class IslandModeController
{
    public static readonly TimeSpan LeaveDelay = TimeSpan.FromMilliseconds(450);
    public static readonly TimeSpan ReopenFromMediumDelay = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan FullscreenDebounce = TimeSpan.FromMilliseconds(400);

    private DateTime? _overSince;
    private DateTime? _awaySince;
    private DateTime? _fullscreenSince;
    private DateTime? _windowedSince;
    private bool _blockedUntilLeave;
    private bool _stowed;

    public IslandMode Mode { get; private set; } = IslandMode.Small;
    public TimeSpan OpenDelay { get; set; } = TimeSpan.FromMilliseconds(300);
    public TimeSpan MediumTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Returns the new mode when it changed.</summary>
    public IslandMode? Update(in ModeInput input)
    {
        if (UpdateFullscreen(input) is { } fullscreenMode)
            return fullscreenMode;
        if (Mode == IslandMode.Hidden)
            return null;

        if (!input.OverIsland)
        {
            _overSince = null;
            _blockedUntilLeave = false;
        }

        switch (Mode)
        {
            case IslandMode.Small or IslandMode.Stowed:
                if (!input.OverIsland)
                    return null;
                if (input.OverControl)
                {
                    _overSince = null;
                    return null;
                }
                if (input.MouseButtonDown)
                    _blockedUntilLeave = true;
                if (_blockedUntilLeave)
                {
                    _overSince = null;
                    return null;
                }
                _overSince ??= input.Now;
                return input.Now - _overSince >= OpenDelay ? Set(IslandMode.Large) : null;

            case IslandMode.Medium:
                if (input.OverIsland)
                {
                    _awaySince = null;
                    if (input.MouseButtonDown)
                        return null;
                    _overSince ??= input.Now;
                    return input.Now - _overSince >= ReopenFromMediumDelay ? Set(IslandMode.Large) : null;
                }
                _awaySince ??= input.Now;
                return input.Now - _awaySince >= MediumTimeout ? Set(IslandMode.Small) : null;

            case IslandMode.Large:
                if (input.OverIsland || input.KeepOpen)
                {
                    _awaySince = null;
                    return null;
                }
                _awaySince ??= input.Now;
                return input.Now - _awaySince >= LeaveDelay
                    ? Set(input.IsPlaying ? IslandMode.Medium : IslandMode.Small)
                    : null;
        }
        return null;
    }

    /// <summary>A click on the small or medium island opens it at once.</summary>
    public IslandMode? Open() => Mode is IslandMode.Small or IslandMode.Medium ? Set(IslandMode.Large) : null;

    /// <summary>The "^" button: hide the small island until the user opens it again from the top edge.</summary>
    public IslandMode? Stow()
    {
        if (Mode != IslandMode.Small)
            return null;
        _stowed = true;
        var mode = Set(IslandMode.Stowed);
        // The pointer is still at the top edge; do not reopen until it has left.
        _blockedUntilLeave = true;
        return mode;
    }

    /// <summary>The quick-close handle: straight to small, and stay small until the cursor leaves.</summary>
    public IslandMode? QuickClose()
    {
        if (Mode != IslandMode.Large)
            return null;
        var mode = Set(IslandMode.Small);
        _blockedUntilLeave = true;
        return mode;
    }

    private IslandMode? UpdateFullscreen(in ModeInput input)
    {
        if (input.Fullscreen)
        {
            _windowedSince = null;
            _fullscreenSince ??= input.Now;
            return Mode != IslandMode.Hidden && input.Now - _fullscreenSince >= FullscreenDebounce
                ? Set(IslandMode.Hidden)
                : null;
        }

        _fullscreenSince = null;
        if (Mode != IslandMode.Hidden)
            return null;
        _windowedSince ??= input.Now;
        return input.Now - _windowedSince >= FullscreenDebounce ? Set(_stowed ? IslandMode.Stowed : IslandMode.Small) : null;
    }

    private IslandMode Set(IslandMode mode)
    {
        if (mode is IslandMode.Medium or IslandMode.Large)
            _stowed = false;
        Mode = mode;
        _overSince = null;
        _awaySince = null;
        return mode;
    }
}
