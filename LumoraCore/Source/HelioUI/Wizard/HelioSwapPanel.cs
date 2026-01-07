using Lumora.Core.Math;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Direction for panel swap animations.
/// </summary>
public enum SwapDirection
{
    None,
    Left,
    Right,
    Up,
    Down,
    Fade
}

/// <summary>
/// Helio swap panel component.
/// Manages animated transitions between UI panels for wizard flows.
/// </summary>
[ComponentCategory("HelioUI/Wizard")]
public class HelioSwapPanel : Component
{
    // ===== CONFIGURATION =====

    /// <summary>
    /// Duration of transition animations in seconds.
    /// </summary>
    public Sync<float> TransitionDuration { get; private set; }

    /// <summary>
    /// Default direction for swap animations.
    /// </summary>
    public Sync<SwapDirection> DefaultDirection { get; private set; }

    // ===== STATE =====

    /// <summary>
    /// Reference to the currently displayed panel.
    /// </summary>
    public SyncRef<Slot> CurrentPanel { get; private set; }

    /// <summary>
    /// Reference to the previous panel (during transition).
    /// </summary>
    public SyncRef<Slot> PreviousPanel { get; private set; }

    /// <summary>
    /// Current animation progress (0-1).
    /// </summary>
    public Sync<float> AnimationProgress { get; private set; }

    /// <summary>
    /// Whether a transition is currently playing.
    /// </summary>
    public Sync<bool> IsAnimating { get; private set; }

    // ===== PRIVATE STATE =====

    private SwapDirection _currentDirection;
    private float2 _startOffset;
    private float2 _endOffset;

    // ===== INITIALIZATION =====

    public override void OnAwake()
    {
        base.OnAwake();

        TransitionDuration = new Sync<float>(this, 0.25f);
        DefaultDirection = new Sync<SwapDirection>(this, SwapDirection.Left);

        CurrentPanel = new SyncRef<Slot>(this);
        PreviousPanel = new SyncRef<Slot>(this);
        AnimationProgress = new Sync<float>(this, 0f);
        IsAnimating = new Sync<bool>(this, false);
    }

    // ===== UPDATE =====

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        if (!IsAnimating.Value) return;

        float duration = TransitionDuration.Value;
        if (duration <= 0f) duration = 0.001f;

        AnimationProgress.Value += delta / duration;

        if (AnimationProgress.Value >= 1f)
        {
            // Animation complete
            AnimationProgress.Value = 1f;
            CompleteTransition();
        }
        else
        {
            // Update panel positions
            UpdateTransition();
        }
    }

    // ===== SWAP OPERATIONS =====

    /// <summary>
    /// Swap to a new panel with animation.
    /// </summary>
    public void SwapTo(Slot newPanel, SwapDirection direction)
    {
        if (newPanel == null) return;
        // Temporarily skip animations; just swap visibility to avoid overlapping panels
        SetPanelImmediate(newPanel);
    }

    /// <summary>
    /// Swap to a new panel using the default direction.
    /// </summary>
    public void SwapTo(Slot newPanel)
    {
        SwapTo(newPanel, DefaultDirection.Value);
    }

    /// <summary>
    /// Swap back (reverse direction).
    /// </summary>
    public void SwapBack(Slot newPanel)
    {
        SwapDirection reverseDir = _currentDirection switch
        {
            SwapDirection.Left => SwapDirection.Right,
            SwapDirection.Right => SwapDirection.Left,
            SwapDirection.Up => SwapDirection.Down,
            SwapDirection.Down => SwapDirection.Up,
            _ => SwapDirection.Fade
        };
        SwapTo(newPanel, reverseDir);
    }

    /// <summary>
    /// Instantly set the current panel without animation.
    /// </summary>
    public void SetPanelImmediate(Slot panel)
    {
        // Hide previous
        if (CurrentPanel.Target != null)
            CurrentPanel.Target.ActiveSelf.Value = false;

        CurrentPanel.Target = panel;
        PreviousPanel.Target = null;

        // Show new
        if (panel != null)
            panel.ActiveSelf.Value = true;

        IsAnimating.Value = false;
        AnimationProgress.Value = 0f;
    }

    // ===== ANIMATION =====

    private void CalculateOffsets()
    {
        // Get canvas size for offset calculations
        var rect = Slot.GetComponent<HelioRectTransform>();
        float2 size = rect?.Rect.Size ?? new float2(400f, 600f);

        switch (_currentDirection)
        {
            case SwapDirection.Left:
                _startOffset = new float2(size.x, 0);
                _endOffset = new float2(-size.x, 0);
                break;
            case SwapDirection.Right:
                _startOffset = new float2(-size.x, 0);
                _endOffset = new float2(size.x, 0);
                break;
            case SwapDirection.Up:
                _startOffset = new float2(0, -size.y);
                _endOffset = new float2(0, size.y);
                break;
            case SwapDirection.Down:
                _startOffset = new float2(0, size.y);
                _endOffset = new float2(0, -size.y);
                break;
            default:
                _startOffset = float2.Zero;
                _endOffset = float2.Zero;
                break;
        }
    }

    private void UpdateTransition()
    {
        float t = EaseOutCubic(AnimationProgress.Value);

        // Update new panel position (slide in from startOffset to zero)
        var currentRect = CurrentPanel.Target?.GetComponent<HelioRectTransform>();
        if (currentRect != null)
        {
            float2 offset = float2.Lerp(_startOffset, float2.Zero, t);
            // Apply offset via rect transform offset
            currentRect.OffsetMin.Value = offset;
            currentRect.OffsetMax.Value = offset + currentRect.Rect.Size;
        }

        // Update previous panel position (slide out from zero to endOffset)
        var prevRect = PreviousPanel.Target?.GetComponent<HelioRectTransform>();
        if (prevRect != null)
        {
            float2 offset = float2.Lerp(float2.Zero, _endOffset, t);
            prevRect.OffsetMin.Value = offset;
            prevRect.OffsetMax.Value = offset + prevRect.Rect.Size;

            // Fade out for fade transition
            if (_currentDirection == SwapDirection.Fade)
            {
                // Would need alpha support in panel
            }
        }
    }

    private void CompleteTransition()
    {
        IsAnimating.Value = false;

        // Hide previous panel
        if (PreviousPanel.Target != null)
        {
            PreviousPanel.Target.ActiveSelf.Value = false;
        }

        // Reset current panel position
        var currentRect = CurrentPanel.Target?.GetComponent<HelioRectTransform>();
        if (currentRect != null)
        {
            currentRect.OffsetMin.Value = float2.Zero;
            // Reset max based on parent
        }

        PreviousPanel.Target = null;
    }

    /// <summary>
    /// Ease out cubic interpolation.
    /// </summary>
    private float EaseOutCubic(float t)
    {
        return 1f - (1f - t) * (1f - t) * (1f - t);
    }
}
