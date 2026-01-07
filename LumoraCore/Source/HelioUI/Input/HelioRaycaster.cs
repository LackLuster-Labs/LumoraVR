using System.Collections.Generic;
using Lumora.Core.Math;
using Lumora.Core.Components;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Result of a Helio UI raycast.
/// </summary>
public struct HelioRaycastResult
{
    /// <summary>
    /// Whether a hit was found.
    /// </summary>
    public bool IsValid;

    /// <summary>
    /// The slot that was hit.
    /// </summary>
    public Slot HitSlot;

    /// <summary>
    /// The interactable component on the hit slot (if any).
    /// </summary>
    public IHelioInteractable Interactable;

    /// <summary>
    /// Position on the canvas in canvas-space coordinates.
    /// </summary>
    public float2 CanvasPosition;

    /// <summary>
    /// World-space position of the hit point.
    /// </summary>
    public float3 WorldPosition;

    /// <summary>
    /// Distance from ray origin to hit point.
    /// </summary>
    public float Distance;

    /// <summary>
    /// Normal of the hit surface.
    /// </summary>
    public float3 Normal;

    public static HelioRaycastResult Empty => new HelioRaycastResult { IsValid = false };
}

/// <summary>
/// Helio raycaster component.
/// Performs raycasts against Helio UI canvases for VR laser and desktop mouse input.
/// </summary>
[ComponentCategory("HelioUI")]
public class HelioRaycaster : Component
{
    private static readonly List<HelioRaycaster> _activeRaycasters = new();

    /// <summary>
    /// Currently active raycasters (one per canvas).
    /// </summary>
    public static IReadOnlyList<HelioRaycaster> ActiveRaycasters => _activeRaycasters;

    // ===== CONFIGURATION =====

    /// <summary>
    /// The canvas to raycast against.
    /// </summary>
    public SyncRef<HelioCanvas> TargetCanvas { get; private set; }

    /// <summary>
    /// Maximum raycast distance.
    /// </summary>
    public Sync<float> MaxDistance { get; private set; }

    /// <summary>
    /// Whether to block raycasts on non-interactable areas.
    /// </summary>
    public Sync<bool> BlocksRaycasts { get; private set; }

    // ===== INITIALIZATION =====

    public override void OnAwake()
    {
        base.OnAwake();

        TargetCanvas = new SyncRef<HelioCanvas>(this);
        MaxDistance = new Sync<float>(this, 100f);
        BlocksRaycasts = new Sync<bool>(this, true);
    }

    public override void OnStart()
    {
        base.OnStart();
        _activeRaycasters.Add(this);
    }

    public override void OnDestroy()
    {
        _activeRaycasters.Remove(this);
        base.OnDestroy();
    }

    // ===== RAYCASTING =====

    /// <summary>
    /// Raycast from a world-space ray against the canvas.
    /// Used for VR laser pointers.
    /// </summary>
    public bool Raycast(float3 origin, float3 direction, out HelioRaycastResult result)
    {
        result = HelioRaycastResult.Empty;

        var canvas = TargetCanvas?.Target;
        if (canvas == null) return false;

        float2 referenceSize = canvas.ReferenceSize.Value;
        float pixelScale = canvas.PixelScale?.Value ?? 100f;
        if (pixelScale <= 0.0001f)
        {
            pixelScale = 100f;
        }

        // World size of the canvas quad (respect pixel scale)
        float2 worldSize = referenceSize / pixelScale;

        // Get canvas slot's world transform
        var canvasSlot = canvas.Slot;
        float3 canvasPosition = canvasSlot.GlobalPosition;
        floatQ canvasRotation = canvasSlot.GlobalRotation;
        float3 canvasScale = canvasSlot.GlobalScale;

        // Canvas normal (assuming canvas faces -Z in local space)
        float3 canvasNormal = canvasRotation * new float3(0, 0, -1);

        // Ray-plane intersection
        float denom = float3.Dot(canvasNormal, direction);
        if (System.Math.Abs(denom) < 0.0001f)
            return false; // Ray parallel to canvas

        float3 toCanvas = canvasPosition - origin;
        float t = float3.Dot(toCanvas, canvasNormal) / denom;

        if (t < 0 || t > MaxDistance.Value)
            return false; // Behind ray or too far

        // Hit point in world space
        float3 worldHit = origin + direction * t;

        // Transform to canvas local space
        float3 localHit = canvasRotation.Inverse * (worldHit - canvasPosition);
        localHit = new float3(localHit.x / canvasScale.x, localHit.y / canvasScale.y, localHit.z / canvasScale.z);

        // Convert to canvas UV (0-1 range based on world quad size)
        if (worldSize.x <= 0.0001f || worldSize.y <= 0.0001f)
            return false;

        float2 canvasPos = new float2(
            localHit.x / worldSize.x + 0.5f,
            0.5f - localHit.y / worldSize.y // Invert Y so top of canvas is 0
        );

        // Check if within canvas bounds
        if (canvasPos.x < 0 || canvasPos.x > 1 || canvasPos.y < 0 || canvasPos.y > 1)
            return false;

        // Convert to canvas-space coordinates
        float2 canvasCoord = new float2(canvasPos.x * referenceSize.x, canvasPos.y * referenceSize.y);

        // Find hit element
        var hitSlot = FindHitSlot(canvas.Slot, canvasCoord);

        result = new HelioRaycastResult
        {
            IsValid = hitSlot != null || BlocksRaycasts.Value,
            HitSlot = hitSlot,
            Interactable = hitSlot?.GetComponent<HelioButton>() as IHelioInteractable
                ?? hitSlot?.GetComponent<HelioToggle>() as IHelioInteractable
                ?? hitSlot?.GetComponent<HelioSlider>() as IHelioInteractable
                ?? hitSlot?.GetComponent<HelioTextField>() as IHelioInteractable
                ?? hitSlot?.GetComponent<HelioDropdown>() as IHelioInteractable
                ?? hitSlot?.GetComponent<HelioScrollView>() as IHelioInteractable
                ?? hitSlot?.GetComponent<HelioModalOverlay>() as IHelioInteractable,
            CanvasPosition = canvasCoord,
            WorldPosition = worldHit,
            Distance = t,
            Normal = canvasNormal
        };

        return result.IsValid;
    }

    /// <summary>
    /// Raycast from screen-space position (for desktop mouse).
    /// Requires camera reference to convert screen to world ray.
    /// </summary>
    public bool RaycastScreen(float2 screenPosition, Camera camera, out HelioRaycastResult result)
    {
        result = HelioRaycastResult.Empty;

        if (camera == null) return false;

        // Convert screen position to world ray
        // This is simplified - real implementation needs proper camera projection
        float3 origin = camera.Slot.GlobalPosition;
        float3 direction = camera.Slot.GlobalRotation * new float3(0, 0, -1);

        // Would need proper screen-to-world conversion here
        // For now, use simple forward ray
        return Raycast(origin, direction, out result);
    }

    /// <summary>
    /// Raycast using canvas-space coordinates directly.
    /// Useful when you already have the canvas position.
    /// </summary>
    public bool RaycastCanvasSpace(float2 canvasPosition, out HelioRaycastResult result)
    {
        result = HelioRaycastResult.Empty;

        var canvas = TargetCanvas?.Target;
        if (canvas == null) return false;

        var hitSlot = FindHitSlot(canvas.Slot, canvasPosition);

        result = new HelioRaycastResult
        {
            IsValid = hitSlot != null || BlocksRaycasts.Value,
            HitSlot = hitSlot,
            Interactable = hitSlot?.GetComponent<HelioButton>() as IHelioInteractable
                ?? hitSlot?.GetComponent<HelioToggle>() as IHelioInteractable
                ?? hitSlot?.GetComponent<HelioSlider>() as IHelioInteractable
                ?? hitSlot?.GetComponent<HelioTextField>() as IHelioInteractable
                ?? hitSlot?.GetComponent<HelioDropdown>() as IHelioInteractable
                ?? hitSlot?.GetComponent<HelioScrollView>() as IHelioInteractable
                ?? hitSlot?.GetComponent<HelioModalOverlay>() as IHelioInteractable,
            CanvasPosition = canvasPosition,
            Distance = 0f
        };

        return result.IsValid;
    }

    // ===== INTERNAL =====

    private Slot FindHitSlot(Slot slot, float2 position)
    {
        if (!slot.ActiveSelf.Value) return null;

        // Check children first (reverse order for z-ordering)
        var children = slot.Children;
        for (int i = children.Count - 1; i >= 0; i--)
        {
            var hit = FindHitSlot(children[i], position);
            if (hit != null) return hit;
        }

        // Check this slot
        var rect = slot.GetComponent<HelioRectTransform>();
        if (rect == null) return null;

        if (!rect.Rect.Contains(position)) return null;

        // Check if this slot has any interactable or visual component
        if (slot.GetComponent<HelioButton>() != null ||
            slot.GetComponent<HelioToggle>() != null ||
            slot.GetComponent<HelioSlider>() != null ||
            slot.GetComponent<HelioTextField>() != null ||
            slot.GetComponent<HelioDropdown>() != null ||
            slot.GetComponent<HelioScrollView>() != null ||
            slot.GetComponent<HelioModalOverlay>() != null ||
            slot.GetComponent<HelioPanel>() != null ||
            slot.GetComponent<HelioImage>() != null)
        {
            return slot;
        }

        return null;
    }
}
