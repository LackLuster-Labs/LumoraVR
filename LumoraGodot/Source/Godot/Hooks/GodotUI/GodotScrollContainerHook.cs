using Godot;
using Lumora.Core;
using Lumora.Core.GodotUI;

namespace Aquamarine.Godot.Hooks.GodotUI;

#nullable enable

/// <summary>
/// Godot hook for GodotScrollContainer.
/// Creates a ScrollContainer control.
/// </summary>
public class GodotScrollContainerHook : GodotUIElementHook<GodotScrollContainer>
{
    private ScrollContainer? _scrollContainer;

    public static IHook<GodotScrollContainer> Constructor()
    {
        return new GodotScrollContainerHook();
    }

    protected override Control CreateControl()
    {
        _scrollContainer = new ScrollContainer();

        // Connect scroll signals to sync back to component
        _scrollContainer.ScrollStarted += OnScrollStarted;
        _scrollContainer.ScrollEnded += OnScrollEnded;

        ApplyScrollProperties();

        return _scrollContainer;
    }

    private void OnScrollStarted()
    {
        // Could trigger events here
    }

    private void OnScrollEnded()
    {
        // Sync scroll position back to component
        SyncScrollPosition();
    }

    private void SyncScrollPosition()
    {
        if (_scrollContainer == null) return;

        var hBar = _scrollContainer.GetHScrollBar();
        var vBar = _scrollContainer.GetVScrollBar();

        if (hBar != null && hBar.MaxValue > 0)
        {
            Owner.ScrollHorizontal.Value = (float)(hBar.Value / hBar.MaxValue);
        }

        if (vBar != null && vBar.MaxValue > 0)
        {
            Owner.ScrollVertical.Value = (float)(vBar.Value / vBar.MaxValue);
        }
    }

    public override void ApplyChanges()
    {
        base.ApplyChanges();
        ApplyScrollProperties();
    }

    private void ApplyScrollProperties()
    {
        if (_scrollContainer == null) return;

        // Scroll mode - Godot 4 uses ScrollBar directly
        // HorizontalScrollMode and VerticalScrollMode control visibility
        var hBar = _scrollContainer.GetHScrollBar();
        var vBar = _scrollContainer.GetVScrollBar();

        // Configure horizontal scrollbar
        if (hBar != null)
        {
            switch (Owner.HorizontalScrollMode.Value)
            {
                case ScrollMode.Disabled:
                    hBar.Visible = false;
                    break;
                case ScrollMode.Auto:
                    // Auto is default behavior
                    break;
                case ScrollMode.AlwaysShow:
                    hBar.Visible = true;
                    break;
                case ScrollMode.AlwaysHide:
                    hBar.Visible = false;
                    break;
            }
        }

        // Configure vertical scrollbar
        if (vBar != null)
        {
            switch (Owner.VerticalScrollMode.Value)
            {
                case ScrollMode.Disabled:
                    vBar.Visible = false;
                    break;
                case ScrollMode.Auto:
                    // Auto is default behavior
                    break;
                case ScrollMode.AlwaysShow:
                    vBar.Visible = true;
                    break;
                case ScrollMode.AlwaysHide:
                    vBar.Visible = false;
                    break;
            }
        }

        _scrollContainer.FollowFocus = Owner.FollowFocus.Value;
        _scrollContainer.ScrollDeadzone = Owner.ScrollDeadzone.Value;

        // Apply scroll position (reuse hBar/vBar from above)
        if (hBar != null && hBar.MaxValue > 0)
        {
            hBar.Value = Owner.ScrollHorizontal.Value * hBar.MaxValue;
        }

        if (vBar != null && vBar.MaxValue > 0)
        {
            vBar.Value = Owner.ScrollVertical.Value * vBar.MaxValue;
        }
    }

    public override void Destroy(bool destroyingWorld)
    {
        if (_scrollContainer != null)
        {
            _scrollContainer.ScrollStarted -= OnScrollStarted;
            _scrollContainer.ScrollEnded -= OnScrollEnded;
        }

        _scrollContainer = null;
        base.Destroy(destroyingWorld);
    }
}
