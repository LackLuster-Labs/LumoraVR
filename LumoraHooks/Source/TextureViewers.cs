// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Godot;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Components;
using Lumora.Core.Components.Assets;

namespace Lumora.Godot.Hooks;

// WHO SHOWS A PICTURE, AND CAN ANYBODY SEE THEM.
//
// A texture made every frame (a camera's, a playing video's) is only worth making while it is on screen. Its
// users are the materials that take it and the renderers wearing those materials; each frame the question is
// whether any of those renderers is in front of the viewer and big enough to be more than a few pixels. The
// users are found again every half second. Anything holding the texture that is not a material on a renderer
// (a script, a picture on a panel, a feed to the desktop) leaves the users unknown, and unknown always counts as
// seen, so nothing that worked before goes dark. Main thread. -xlinka
internal sealed class TextureViewers
{
    private const double RescanSeconds = 0.5;
    // Past this many radii away a screen is a few pixels.
    private const float SmallestSeen = 0.004f;
    private const float AlwaysNearMetres = 2f;
    private const float ViewMarginDegrees = 12f;

    private readonly List<MeshRenderer> _renderers = new();
    private bool _unknown = true;
    private double _scannedAt = double.NegativeInfinity;

    // The biggest share of the view's height any user takes, from the last InView that saw one.
    public float LargestShare { get; private set; }

    public void Forget()
    {
        _renderers.Clear();
        _unknown = true;
        _scannedAt = double.NegativeInfinity;
    }

    // Holders that only fill the texture or play it, and never show it.
    public System.Func<Component, bool>? IgnoreHolder;

    public bool InView(IAsset? asset)
    {
        double now = global::Godot.Time.GetTicksMsec() / 1000.0;
        if (now >= _scannedAt + RescanSeconds)
        {
            _scannedAt = now;
            Find(asset);
        }
        LargestShare = 1f;
        if (_unknown)
            return true;

        var viewer = Helpers.RenderHelper.ViewerCamera?.Invoke();
        if (viewer == null || !GodotObject.IsInstanceValid(viewer) || !viewer.IsInsideTree())
            viewer = (global::Godot.Engine.GetMainLoop() as SceneTree)?.Root?.GetCamera3D();
        if (viewer == null || !GodotObject.IsInstanceValid(viewer) || !viewer.IsInsideTree())
            return true;

        var eye = viewer.GlobalTransform;
        var forward = -eye.Basis.Z.Normalized();
        var size = viewer.GetViewport()?.GetVisibleRect().Size ?? new Vector2(16f, 9f);
        float aspect = size.Y > 0f ? size.X / size.Y : 16f / 9f;
        float halfV = Mathf.DegToRad(Mathf.Clamp(viewer.Fov, 1f, 179f) * 0.5f);
        float halfH = Mathf.Atan(Mathf.Tan(halfV) * Mathf.Max(aspect, 1f));
        float halfView = Mathf.Max(halfV, halfH) + Mathf.DegToRad(ViewMarginDegrees);
        float tanHalfV = Mathf.Tan(halfV);

        bool seen = false;
        float largest = 0f;
        foreach (var renderer in _renderers)
        {
            if (renderer.IsDestroyed || !renderer.Enabled || renderer.Slot is not { IsActive: true })
                continue;
            var world = renderer.World;
            if (world == null || world.Focus == World.WorldFocus.Background)
                continue;
            // A dash or an overlay is drawn by a camera of its own.
            if (world.IsUserspace || world.Focus != World.WorldFocus.Focused)
            {
                LargestShare = 1f;
                return true;
            }
            if (renderer.Hook is not IDrawnInstance drawn || drawn.DrawnInstance is not { } instance)
            {
                LargestShare = 1f;
                return true;
            }
            if (!instance.IsVisibleInTree())
                continue;
            var box = instance.GlobalTransform * instance.GetAabb();
            var centre = box.GetCenter();
            float radius = box.Size.Length() * 0.5f;
            var toward = centre - eye.Origin;
            float distance = toward.Length();
            // Inside the screen's own bounds it can be anywhere around the eye. Close by it still has to be in
            // front: someone standing with their back to a screen was counted as watching it.
            if (distance <= radius)
            {
                LargestShare = 1f;
                return true;
            }
            bool near = distance <= radius + AlwaysNearMetres;
            if (!near && radius / distance < SmallestSeen)
                continue;
            float off = Mathf.Acos(Mathf.Clamp(toward.Dot(forward) / distance, -1f, 1f));
            if (off - Mathf.Asin(Mathf.Min(1f, radius / distance)) > halfView)
                continue;
            if (near)
            {
                LargestShare = 1f;
                return true;
            }
            seen = true;
            largest = Mathf.Max(largest, radius / (distance * tanHalfV));
        }
        LargestShare = seen ? Mathf.Min(1f, largest) : 0f;
        return seen;
    }

    // The renderers wearing a material that takes this texture.
    private void Find(IAsset? asset)
    {
        _renderers.Clear();
        _unknown = true;
        if ((asset as Asset)?.Owner is not IAssetProvider provider)
            return;
        bool any = false;
        bool kept = false;
        // On a guest the references change on the sync thread; a set caught changing is read again next time.
        try
        {
            foreach (var reference in provider.References)
            {
                var holder = OwnerOf(reference);
                if (holder != null && IgnoreHolder?.Invoke(holder) == true)
                    continue;
                if (holder != null && OnlyKeeps(holder))
                {
                    kept = true;
                    continue;
                }
                if (holder is not IAssetProvider material)
                    return;
                foreach (var materialReference in material.References)
                {
                    var wearer = OwnerOf(materialReference);
                    // A UI graphic is drawn by its canvas's chunk, and the chunk's renderer wears the material too.
                    if (wearer is Helio.UI.Graphic)
                        continue;
                    if (wearer != null && OnlyKeeps(wearer))
                    {
                        kept = true;
                        continue;
                    }
                    if (wearer is not MeshRenderer renderer)
                        return;
                    if (!_renderers.Contains(renderer))
                        _renderers.Add(renderer);
                    any = true;
                }
            }
        }
        catch (System.InvalidOperationException)
        {
            _renderers.Clear();
            return;
        }
        // Only kept loaded or measured and worn by nobody: nobody sees it. Held by nothing at all stays unknown.
        _unknown = !any && !kept;
    }

    // Holders that keep a texture loaded or read its size, and never show it.
    private static bool OnlyKeeps(Component holder)
    {
        if (holder is TextureSizeValue)
            return true;
        var type = holder.GetType();
        if (!type.IsGenericType)
            return false;
        var open = type.GetGenericTypeDefinition();
        return open == typeof(AssetHolder<>) || open == typeof(AssetLoadTally<>);
    }

    public static Component? OwnerOf(IAssetRef reference)
    {
        IWorldElement? at = reference as IWorldElement;
        for (int i = 0; i < 4 && at != null; i++)
        {
            if (at is Component component)
                return component;
            at = (at as Lumora.Core.Networking.Sync.SyncElement)?.Parent ?? (at as Worker)?.Parent;
        }
        return null;
    }
}
