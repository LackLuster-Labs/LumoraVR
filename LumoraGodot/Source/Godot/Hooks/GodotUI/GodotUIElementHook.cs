using Godot;
using Lumora.Core;
using Lumora.Core.GodotUI;

namespace Aquamarine.Godot.Hooks.GodotUI;

#nullable enable

/// <summary>
/// Base hook for Godot UI elements.
/// Can either create a new Control or adopt an existing one from a parent panel's scene.
/// </summary>
public abstract class GodotUIElementHook<T> : Hook<T> where T : GodotUIElement
{
    protected Control? _control;
    protected GodotUICanvasHook? _canvasHook;
    protected bool _isAdoptedNode;

    /// <summary>
    /// The Godot Control node for this UI element.
    /// </summary>
    public Control? ControlNode => _control;

    public override void Initialize()
    {
        // Check if this element should adopt an existing node from a panel
        var nodePath = Owner.SceneNodePath.Value;
        var parentPanel = Owner.ParentPanel.Target;

        if (!string.IsNullOrEmpty(nodePath) && parentPanel != null)
        {
            // Find the panel hook and get the existing node
            var panelHook = GodotUIPanelHook.FindHookForPanel(parentPanel);
            if (panelHook != null)
            {
                _control = panelHook.GetNodeByPath(nodePath);
                if (_control != null)
                {
                    _isAdoptedNode = true;
                    // Don't create new node, just apply our properties
                    ApplyChanges();
                    return;
                }
            }
        }

        // No existing node - create one (original behavior)
        _canvasHook = GodotUICanvasHook.FindCanvasForElement(Owner);
        if (_canvasHook?.RootControl == null)
        {
            return;
        }

        _control = CreateControl();
        if (_control == null) return;

        _control.Name = Owner.GetType().Name;
        _canvasHook.RootControl.AddChild(_control);
        _isAdoptedNode = false;

        ApplyAnchorAndOffset();
        ApplyBaseProperties();
    }

    /// <summary>
    /// Create the specific Control node for this element type.
    /// Only called when not adopting an existing node.
    /// </summary>
    protected abstract Control CreateControl();

    public override void ApplyChanges()
    {
        if (_control == null) return;

        // For adopted nodes, sync properties back to Godot
        ApplyBaseProperties();

        // Only apply anchor/offset for created nodes, not adopted ones
        if (!_isAdoptedNode)
        {
            ApplyAnchorAndOffset();
        }
    }

    protected void ApplyAnchorAndOffset()
    {
        if (_control == null) return;

        var anchor = Owner.Anchor.Value;
        var anchorRect = Owner.AnchorRect.Value;
        var offsetRect = Owner.OffsetRect.Value;

        switch (anchor)
        {
            case AnchorPreset.TopLeft:
                _control.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
                break;
            case AnchorPreset.TopCenter:
                _control.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
                break;
            case AnchorPreset.TopRight:
                _control.SetAnchorsPreset(Control.LayoutPreset.TopRight);
                break;
            case AnchorPreset.CenterLeft:
                _control.SetAnchorsPreset(Control.LayoutPreset.CenterLeft);
                break;
            case AnchorPreset.Center:
                _control.SetAnchorsPreset(Control.LayoutPreset.Center);
                break;
            case AnchorPreset.CenterRight:
                _control.SetAnchorsPreset(Control.LayoutPreset.CenterRight);
                break;
            case AnchorPreset.BottomLeft:
                _control.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
                break;
            case AnchorPreset.BottomCenter:
                _control.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
                break;
            case AnchorPreset.BottomRight:
                _control.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
                break;
            case AnchorPreset.FullRect:
                _control.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                break;
            case AnchorPreset.TopWide:
                _control.SetAnchorsPreset(Control.LayoutPreset.TopWide);
                break;
            case AnchorPreset.BottomWide:
                _control.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
                break;
            case AnchorPreset.LeftWide:
                _control.SetAnchorsPreset(Control.LayoutPreset.LeftWide);
                break;
            case AnchorPreset.RightWide:
                _control.SetAnchorsPreset(Control.LayoutPreset.RightWide);
                break;
            case AnchorPreset.Custom:
                _control.AnchorLeft = anchorRect.x;
                _control.AnchorTop = anchorRect.y;
                _control.AnchorRight = anchorRect.z;
                _control.AnchorBottom = anchorRect.w;
                break;
        }

        _control.OffsetLeft = offsetRect.x;
        _control.OffsetTop = offsetRect.y;
        _control.OffsetRight = offsetRect.z;
        _control.OffsetBottom = offsetRect.w;

        var minSize = Owner.MinSize.Value;
        _control.CustomMinimumSize = new Vector2(minSize.x, minSize.y);

        _control.SizeFlagsHorizontal = (Control.SizeFlags)Owner.SizeFlagsHorizontal.Value;
        _control.SizeFlagsVertical = (Control.SizeFlags)Owner.SizeFlagsVertical.Value;
    }

    protected void ApplyBaseProperties()
    {
        if (_control == null) return;

        _control.Visible = Owner.Visible.Value;

        var modulate = Owner.Modulate.Value;
        _control.Modulate = new Color(modulate.r, modulate.g, modulate.b, modulate.a);
    }

    public override void Destroy(bool destroyingWorld)
    {
        // Only free the control if we created it (not adopted)
        if (!destroyingWorld && _control != null && !_isAdoptedNode && GodotObject.IsInstanceValid(_control))
        {
            _control.QueueFree();
        }
        _control = null;
        _canvasHook = null;
    }
}
