using System.Threading.Tasks;
using Lumora.Core.Math;
using Lumora.Core.HelioUI.Rendering;
using Lumora.Core.Assets;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Base class for HelioUI graphical elements (images, text, raw graphics).
/// Provides rendering and hit testing capabilities.
/// </summary>
[ComponentCategory("HelioUI")]
public abstract class HelioGraphic : HelioUIComputeComponent, IHelioUIRenderer
{
    private bool _renderDirty = true;

    // ===== VISUAL PROPERTIES =====

    /// <summary>
    /// Tint color applied to the graphic.
    /// </summary>
    public Sync<color> Tint { get; private set; }

    /// <summary>
    /// Whether this graphic should raycast (receive pointer events).
    /// </summary>
    public Sync<bool> Raycast { get; private set; }

    /// <summary>
    /// Reference to the material used for rendering.
    /// </summary>
    public AssetRef<Material> Material { get; private set; }

    // ===== IHelioUIRenderer =====

    public bool IsDirty => _renderDirty;

    public void SetDirty()
    {
        _renderDirty = true;
        MarkComputeDirty();
    }

    /// <summary>
    /// Generate mesh data for rendering.
    /// </summary>
    public abstract void Generate(HelioUIMesh mesh);

    // ===== ABSTRACT PROPERTIES =====

    /// <summary>
    /// Whether this graphic requires pre-graphics computation (e.g., text layout).
    /// </summary>
    public abstract bool RequiresPreGraphicsCompute { get; }

    /// <summary>
    /// Whether this graphic requires precise same-level sorting.
    /// </summary>
    public virtual bool RequirePreciseSameLevelSorting => false;

    // ===== COMPUTATION =====

    /// <summary>
    /// Compute graphic data. Called during layout pass.
    /// </summary>
    public abstract void ComputeGraphic();

    /// <summary>
    /// Pre-graphics computation (async). Used for text layout, etc.
    /// </summary>
    public virtual Task PreGraphicsCompute()
    {
        return Task.CompletedTask;
    }

    // ===== HIT TESTING =====

    /// <summary>
    /// Check if a point (in local UI space) is inside this graphic.
    /// </summary>
    public abstract bool IsPointInside(float2 point);

    /// <summary>
    /// Default rectangular hit test.
    /// </summary>
    protected bool RectContainsPoint(float2 point)
    {
        var rect = RectTransform?.Rect ?? default;
        return rect.Contains(point);
    }

    // ===== INITIALIZATION =====

    public override void OnAwake()
    {
        base.OnAwake();

        Tint = new Sync<color>(this, color.White);
        Raycast = new Sync<bool>(this, true);
        Material = new AssetRef<Material>(this);

        Tint.OnChanged += _ => SetDirty();
    }

    public override void OnLateUpdate(float delta)
    {
        base.OnLateUpdate(delta);

        if (_renderDirty)
        {
            ComputeGraphic();
            _renderDirty = false;
        }
    }

    // ===== CANVAS REGISTRATION =====

    /// <summary>
    /// Get the canvas this graphic belongs to.
    /// </summary>
    public HelioCanvas GetCanvas()
    {
        // Walk up the hierarchy to find a canvas
        var current = Slot;
        while (current != null)
        {
            var canvas = current.GetComponent<HelioCanvas>();
            if (canvas != null)
                return canvas;
            current = current.Parent;
        }
        return null;
    }
}
