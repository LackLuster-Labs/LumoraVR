// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Helio.UI;
using Lumora.Core;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.Components;

// marks a UI row as pullable: the grab action on the row's GrabArea (its chip, handle or label)
// spawns a ReferenceProxy card for the element this source points at (the row's sync member, its
// component, or its slot); anywhere else on the row the grab moves the panel as usual. attached by
// the inspector builders on member rows, component headers, and tree rows. also serves the row's
// chip button: pressing it pops the card out in front of the panel for setups where grip is awkward.
[ComponentCategory("Utility/Inspectors")]
public class ReferenceProxySource : Component, IProxySource
{
    public readonly SyncRef<IWorldElement> Target;
    public readonly SyncRef<Slot> GrabArea = new();

    // Every row source of a world, so whatever points at a member can find the row showing it.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<World, System.Collections.Generic.HashSet<ReferenceProxySource>> InWorld = new();

    public ReferenceProxySource()
    {
        Target = new SyncRef<IWorldElement>(this);
    }

    public override void OnAwake()
    {
        base.OnAwake();
        if (World == null)
            return;
        var sources = InWorld.GetValue(World, static _ => new());
        lock (sources)
            sources.Add(this);
    }

    public override void OnDestroy()
    {
        if (World != null && InWorld.TryGetValue(World, out var sources))
        {
            lock (sources)
                sources.Remove(this);
        }
        base.OnDestroy();
    }

    // Where a shown row for an element meets a line: its left edge, halfway down. False when no row shows it,
    // or the row is scrolled out of its panel.
    public static bool TryRowPoint(World? world, IWorldElement element, out float3 point)
    {
        point = default;
        if (world == null || element == null || !InWorld.TryGetValue(world, out var sources))
            return false;
        lock (sources)
        {
            foreach (var source in sources)
            {
                if (ReferenceEquals(source.Target.Target, element) && source.TryRowPoint(out point))
                    return true;
            }
        }
        return false;
    }

    // Every row showing an element now, for whoever looks its points up again each frame.
    public static void RowsOf(World? world, IWorldElement element, System.Collections.Generic.List<ReferenceProxySource> into)
    {
        if (world == null || element == null || !InWorld.TryGetValue(world, out var sources))
            return;
        lock (sources)
        {
            foreach (var source in sources)
            {
                if (!source.IsDestroyed && ReferenceEquals(source.Target.Target, element))
                    into.Add(source);
            }
        }
    }

    public bool TryRowPoint(out float3 point)
    {
        point = default;
        if (IsDestroyed || Slot is not { IsActive: true } row || row.GetComponent<RectTransform>() is not { } rect
            || row.GetComponentInParents<Canvas>() is not { } canvas)
            return false;
        var area = rect.LocalComputeRect;
        var at = new float2(area.x, area.Center.y) + canvas.ScrollOffsetOf(row);
        if (canvas.Slot.GetComponent<RectTransform>() is { } whole && !whole.LocalComputeRect.Contains(at))
            return false;
        point = canvas.Slot.LocalPointToGlobal(canvas.CanvasToLocal(at));
        return true;
    }

    public IGrabbable? TryCreateProxy(Grabber grabber, in float3 spawnPoint)
    {
        var target = Target.Target;
        if (target == null || target.IsDestroyed || World == null || !ContainsPoint(GrabArea.Target, spawnPoint))
            return null;
        return ReferenceProxy.Spawn(World, target, spawnPoint);
    }

    // Only the dedicated reference grip is pullable. A field, checkbox, expander or tab above a
    // row source must never turn an incidental controller squeeze into a reference card.
    internal static bool ContainsPoint(Slot? area, in float3 worldPoint)
    {
        var rect = area?.GetComponent<RectTransform>();
        var canvas = area?.GetComponentInParents<Canvas>();
        if (rect == null || canvas == null || !area!.IsActive) return false;
        var local = canvas.Slot.GlobalPointToLocal(worldPoint);
        var point = canvas.LocalToCanvas(local);
        return rect.LocalComputeRect.Contains(canvas.CanvasPointToLocal(area, in point));
    }

    // button path: spawns the card just off the pressed point, toward the presser's head
    [SyncMethod]
    public void OnPullPressed(Button button, UIInteractionContext context)
    {
        var target = Target.Target;
        if (target == null || target.IsDestroyed || World == null)
            return;
        var head = World.LocalUser?.Root?.HeadSlot;
        float3 point = context.WorldPoint;
        float3 offset = head != null && (head.GlobalPosition - point).LengthSquared > 1e-6f
            ? (head.GlobalPosition - point).Normalized * 0.25f
            : float3.Up * 0.1f;
        ReferenceProxy.Spawn(World, target, point + offset);
    }
}
