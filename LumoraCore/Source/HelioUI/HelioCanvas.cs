using System;
using Lumora.Core.Math;
using Lumora.Core.Components;
using Lumora.Core.HelioUI.Rendering;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Helio UI root component. Rebuilds rect transforms and layout groups each frame when dirty.
/// Uses mesh-based rendering for 3D display.
/// Creates a child slot with HelioUIMesh for visualization.
/// </summary>
public class HelioCanvas : Component
{
    private bool _layoutDirty = true;
    private bool _visualsDirty = true;
    private bool _rendererInitialized = false;
    private Slot? _rendererSlot;
    private HelioUIMesh? _canvasMesh;
    private HelioRaycaster? _raycaster;
    private bool _rebuildInProgress = false;
    private float _rebuildCooldown = 0f;

    // Minimum interval between rebuilds to avoid spamming mesh uploads
    private const float MIN_REBUILD_INTERVAL = 1f / 30f;

    /// <summary>
    /// Logical reference size for the canvas (UI units).
    /// </summary>
    public Sync<float2> ReferenceSize { get; private set; }

    /// <summary>
    /// Pixels-per-unit equivalent for render backends.
    /// </summary>
    public Sync<float> PixelScale { get; private set; }

    /// <summary>
    /// Background color of the canvas.
    /// </summary>
    public Sync<color> BackgroundColor { get; private set; }

    /// <summary>
    /// Fired when the canvas finishes a rebuild so platform hooks can sync layout.
    /// </summary>
    public event Action CanvasRebuilt;

    /// <summary>
    /// The rendering slot containing the UI mesh.
    /// </summary>
    public Slot? RendererSlot => _rendererSlot;

    /// <summary>
    /// The canvas mesh component.
    /// </summary>
    public HelioUIMesh? CanvasMesh => _canvasMesh;

    public override void OnAwake()
    {
        base.OnAwake();
        ReferenceSize = new Sync<float2>(this, new float2(400f, 600f));
        PixelScale = new Sync<float>(this, 100f);
        BackgroundColor = new Sync<color>(this, HelioUITheme.CanvasBackground);

        ReferenceSize.OnChanged += _ => OnReferenceSizeChanged();
        PixelScale.OnChanged += _ => OnPixelScaleChanged();
        BackgroundColor.OnChanged += _ => OnBackgroundColorChanged();

        // Ensure a RectTransform exists on the same slot so children have a parent rect
        var rect = Slot.GetComponent<HelioRectTransform>() ?? Slot.AttachComponent<HelioRectTransform>();
        rect.AnchorMin.Value = float2.Zero;
        rect.AnchorMax.Value = float2.Zero;
        rect.OffsetMin.Value = float2.Zero;
        rect.OffsetMax.Value = ReferenceSize.Value;
    }

    public override void OnStart()
    {
        base.OnStart();

        _raycaster = Slot.GetComponent<HelioRaycaster>() ?? Slot.AttachComponent<HelioRaycaster>();
        if (_raycaster != null)
        {
            _raycaster.TargetCanvas.Target = this;
        }

        // Ensure first rebuild is not delayed by the throttle
        _rebuildCooldown = MIN_REBUILD_INTERVAL;

        Logging.Logger.Log($"[HelioCanvas] OnStart -> RequestRebuild for '{Slot.SlotName.Value}'");
        // Force an initial rebuild to compute layout
        RequestRebuild();
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        // Throttle rebuild frequency to reduce mesh churn on rapidly-changing UI
        if (_layoutDirty || _visualsDirty)
        {
            _rebuildCooldown += delta;
            if (!_rebuildInProgress && _rebuildCooldown >= MIN_REBUILD_INTERVAL)
            {
                Rebuild();
            }
        }
    }

    /// <summary>
    /// Mark the canvas layout for rebuild on next update.
    /// </summary>
    public void RequestRebuild()
    {
        _layoutDirty = true;
    }

    /// <summary>
    /// Mark the canvas visuals for rebuild (triggers mesh regeneration).
    /// </summary>
    public void RequestVisualRebuild()
    {
        _visualsDirty = true;
    }

    /// <summary>
    /// Rebuild the full UI tree: set root rect, then run layout on descendants.
    /// </summary>
    public void Rebuild()
    {
        if (_rebuildInProgress)
            return;

        _rebuildInProgress = true;

        try
        {
            // Logging.Logger.Log($"[HelioCanvas] Rebuild start for '{Slot.SlotName.Value}'");
            var rootRect = Slot.GetComponent<HelioRectTransform>();
            if (rootRect == null)
            {
                Logging.Logger.Warn($"[HelioCanvas] No HelioRectTransform on root slot '{Slot.SlotName.Value}'");
                return;
            }

            // Root rect spans the reference size
            rootRect.SetLayoutRect(new HelioRect(float2.Zero, ReferenceSize.Value), rewriteOffsets: false);

            RebuildSlotRecursive(Slot);

            // Initialize renderer after first layout pass
            if (!_rendererInitialized)
            {
                InitializeRenderer();
                _visualsDirty = true; // Force visual rebuild after init
            }

            // Only regenerate mesh when visuals actually changed
            if (_visualsDirty)
            {
                _canvasMesh?.RegenerateMesh();
                _visualsDirty = false;
            }

            _layoutDirty = false;
            CanvasRebuilt?.Invoke();
        }
        catch (Exception ex)
        {
            Logging.Logger.Error($"[HelioCanvas] Rebuild failed on '{Slot.SlotName.Value}': {ex}");
        }
        finally
        {
            _rebuildInProgress = false;
            _rebuildCooldown = 0f;
        }
    }

    /// <summary>
    /// Initialize the mesh-based renderer.
    /// Creates a child slot with HelioCanvasMesh + MeshRenderer.
    /// </summary>
    private void InitializeRenderer()
    {
        // If we already have a renderer slot that's alive, do nothing
        if (_rendererInitialized && _rendererSlot != null && !_rendererSlot.IsDestroyed)
            return;

        try
        {
            // Reuse existing renderer slot if present, otherwise create it
            if (_rendererSlot == null || _rendererSlot.IsDestroyed)
            {
                _rendererSlot = Slot.AddSlot("CanvasRenderer");
            }

            // Create canvas mesh
            _canvasMesh = _rendererSlot.GetComponent<HelioUIMesh>() ?? _rendererSlot.AttachComponent<HelioUIMesh>();
            _canvasMesh.SetCanvas(this);
            _canvasMesh.SetRendererSlot(_rendererSlot);
            _canvasMesh.CanvasSize.Value = ReferenceSize.Value;
            _canvasMesh.PixelScale.Value = PixelScale.Value;
            _canvasMesh.BackgroundColor.Value = BackgroundColor.Value;

            _rendererInitialized = true;

            Logging.Logger.Log($"[HelioCanvas] Renderer initialized for: {Slot.SlotName.Value}");
        }
        catch (Exception ex)
        {
            // Cleanup partially created renderer slot to avoid duplicates
            if (_rendererSlot != null && !_rendererSlot.IsDestroyed)
            {
                _rendererSlot.Destroy();
            }
            _rendererSlot = null;
            _canvasMesh = null;
            _rendererInitialized = false;
            Logging.Logger.Error($"[HelioCanvas] Failed to initialize renderer: {ex}");
        }
    }

    private void OnReferenceSizeChanged()
    {
        RequestRebuild();

        // Update root rect
        var rect = Slot.GetComponent<HelioRectTransform>();
        if (rect != null)
        {
            rect.OffsetMax.Value = ReferenceSize.Value;
        }

        // Update canvas mesh if initialized
        if (_canvasMesh != null)
        {
            _canvasMesh.CanvasSize.Value = ReferenceSize.Value;
        }

    }

    private void OnPixelScaleChanged()
    {
        RequestVisualRebuild();

        if (_canvasMesh != null)
        {
            _canvasMesh.PixelScale.Value = PixelScale.Value;
        }
    }

    private void OnBackgroundColorChanged()
    {
        RequestVisualRebuild();

        if (_canvasMesh != null)
        {
            _canvasMesh.BackgroundColor.Value = BackgroundColor.Value;
        }
    }

    public override void OnDestroy()
    {
        // Clean up renderer slot
        if (_rendererSlot != null && !_rendererSlot.IsDestroyed)
        {
            _rendererSlot.Destroy();
        }

        _rendererSlot = null;
        _canvasMesh = null;
        _rendererInitialized = false;

        base.OnDestroy();
    }

    private void RebuildSlotRecursive(Slot slot)
    {
        // Skip the renderer slot
        if (slot == _rendererSlot) return;

        var rect = slot.GetComponent<HelioRectTransform>();
        if (rect == null)
            return;

        // Layout group on this slot arranges its children within this rect
        var layoutGroup = slot.GetComponent<HelioLayoutGroup>();
        if (layoutGroup != null)
        {
            layoutGroup.RebuildLayout(rect);
        }
        else
        {
            // No layout group: just ensure anchors/offsets are applied
            rect.Recalculate(propagateToChildren: false);
        }

        // Recurse into children
        foreach (var child in slot.Children)
        {
            RebuildSlotRecursive(child);
        }
    }
}
