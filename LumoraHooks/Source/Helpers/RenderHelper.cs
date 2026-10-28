// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Godot;
using Lumora.Core;

namespace Lumora.Godot.Helpers;

public static class RenderHelper
{
    public static Action<Camera3D> RegisterCamera = null!;

    // THE CAMERA THE LOCAL PERSON IS LOOKING THROUGH RIGHT NOW. In VR that is the XRCamera3D, which lives
    // in the XR viewport, not the one the world root sits under - so a hook that asks its own viewport
    // for the current camera gets the desktop mirror camera in VR and builds its picture from the wrong
    // eye. The bootstrap owns the mode switch and sets this; a mirror asks here. Null until the
    // bootstrap has a camera to offer. -xlinka
    public static Func<Camera3D?>? ViewerCamera;

    // Render layer bit assignments. Bit 0 (layer 1) is the default "public" layer.
    // Cameras can mask layers via VisualInstance3D.Layers / Camera3D.CullMask.
    public const int PUBLIC_LAYER = 1 << 0;
    public const int PRIVATE_LAYER = 1 << 1;
    public const int TEMP_LAYER = 1 << 2;
    // Drawn by no eye: the dash's offscreen interface, and the cubemap boxes that probes showing a stored
    // cubemap capture (ReflectionProbeHook.SyncSeed), so no eye mask and no scene-capturing probe takes it.
    public const int HIDDEN_LAYER = 1 << 3;
    public const int OVERLAY_LAYER = 1 << 4;
    // Free-cam position marker (sphere + label). Rendered for spectators/others but culled from the
    // local view so you don't see your own marker sitting on the camera. - xlinka
    public const int FREECAM_INDICATOR_LAYER = 1 << 19;
    // The local user's own head geometry. Culled from the eye cameras (main + XR) and from nothing else,
    // so a mirror, a camera and every other peer still see a head while you don't see the inside of
    // yours. Godot draws a caster into a shadow only for cameras that see its layer, so it casts for
    // those other cameras and not for the eyes; while none of them films it, ShadowBudget keeps the
    // shadowed lights off it altogether. -xlinka
    public const int LOCAL_HEAD_LAYER = 1 << 18;
    // The opposite band: drawn by the eye cameras and by NOTHING else. A skinned body whose head bone
    // is hidden from the wearer gets a second instance here with that bone collapsed, while the real
    // instance moves onto LOCAL_HEAD_LAYER, so the wearer sees the headless twin and everything else
    // (mirrors, in-world cameras, probes, other peers) sees the real one. Every camera built anywhere
    // but the two eyes has to mask this bit off; the two render masks below already do. -xlinka
    public const int EYES_ONLY_LAYER = 1 << 17;
    public const int MIRROR_LAYER = Lumora.Core.Components.RenderLayerOverride.MirrorLayer;
    // Name plates and laser pointers, on bands of their own so a camera can leave them out by mask.
    public const int NAMEPLATE_LAYER = Lumora.Core.Components.RenderLayerOverride.NameplateLayer;
    public const int POINTER_LAYER = Lumora.Core.Components.RenderLayerOverride.PointerLayer;
    public const int OWN_NAMEPLATE_LAYER = Lumora.Core.Components.RenderLayerOverride.OwnNameplateLayer;

    public const int PUBLIC_RENDER_MASK = ~(PRIVATE_LAYER | TEMP_LAYER | HIDDEN_LAYER | OVERLAY_LAYER | EYES_ONLY_LAYER);
    public const int PRIVATE_RENDER_MASK = ~(TEMP_LAYER | HIDDEN_LAYER | OVERLAY_LAYER | EYES_ONLY_LAYER);

    // Bits 5..10 are unclaimed. 11 and 12 are the shadow bands, 13 is your own name plate, 14 laser
    // pointers, 15 name plates, 16 mirrors, 17 the eyes-only twin, 18 the local head and 19 the free-cam
    // indicator, so a camera doing selective or excluded rendering borrows one of the six in between and
    // gives it back when it stops. Godot only has twenty visual layers,
    // so this is a hard ceiling on how many cameras can be doing it at once - past that the camera
    // renders normally rather than wrongly. -xlinka
    private const int SELECTIVE_FIRST_BIT = 5;
    private const int SELECTIVE_LAST_BIT = 10;

    // The shadow-only stand-ins of moving objects (MovingShadowCasters) sit on these, one copy per band,
    // and each shadowed light takes one band (ShadowBudget). Every camera draws both bands; the stand-ins
    // only ever show up as shadows.
    public const int SHADOW_BAND_0 = 1 << 11;
    public const int SHADOW_BAND_1 = 1 << 12;
    public const int SHADOW_BANDS = SHADOW_BAND_0 | SHADOW_BAND_1;

    // What a left-out subtree gives up for a camera's bit: every band the other cameras draw it on. A person
    // left out takes their name plate and their beam with them, and those are not on the public band.
    private const uint SWAPPED_BANDS = (uint)(PUBLIC_LAYER | NAMEPLATE_LAYER | OWN_NAMEPLATE_LAYER | POINTER_LAYER);
    private static readonly StringName SwappedBandsMeta = "lumora_swapped_bands";

    private static readonly object _selectiveLock = new();
    private static int _selectiveBitsInUse;

    // The lent bits a camera films some objects alone on (Camera.SelectiveRender). Such a bit is drawn by that
    // camera and by no other view, eyes included: the listed objects keep their own bands for everyone else, and
    // one on a band no eye draws (the hidden band, a camera's own stage) shows in that camera's picture alone.
    // A bit lent for leaving objects out is the opposite and stays in every other view. -xlinka
    private static int _filmedAloneBits;

    public static uint FilmedAloneBits => unchecked((uint)System.Threading.Volatile.Read(ref _filmedAloneBits));

    public static void SetFilmedAlone(int mask, bool alone)
    {
        if (mask == 0)
            return;
        lock (_selectiveLock)
        {
            _filmedAloneBits = alone ? _filmedAloneBits | mask : _filmedAloneBits & ~mask;
        }
    }

    // A view's mask without the bits other cameras film alone; own is the bit this view films alone, if any.
    public static uint WithoutFilmedAlone(uint mask, int own = 0) => mask & ~(FilmedAloneBits & ~unchecked((uint)own));

    // Returns a single-bit mask, or 0 when every bit is taken.
    public static int AllocateSelectiveLayer()
    {
        lock (_selectiveLock)
        {
            for (int bit = SELECTIVE_FIRST_BIT; bit <= SELECTIVE_LAST_BIT; bit++)
            {
                int mask = 1 << bit;
                if ((_selectiveBitsInUse & mask) != 0)
                    continue;
                _selectiveBitsInUse |= mask;
                return mask;
            }
        }
        return 0;
    }

    public static void ReleaseSelectiveLayer(int mask)
    {
        if (mask == 0)
            return;
        lock (_selectiveLock)
        {
            _selectiveBitsInUse &= ~mask;
            _filmedAloneBits &= ~mask;
        }
    }

    // Additive: the subtree keeps every layer it already had and gains one more, so adding a camera's
    // selective layer never changes what anything else sees.
    public static void AddHierarchyLayerBit(Node3D root, int bit)
    {
        if (root == null || !GodotObject.IsInstanceValid(root) || bit == 0)
            return;

        if (root is VisualInstance3D visual)
            visual.Layers |= (uint)bit;

        foreach (Node child in root.GetChildren())
        {
            if (child is Node3D child3D)
                AddHierarchyLayerBit(child3D, bit);
        }
    }

    public static void RemoveHierarchyLayerBit(Node3D root, int bit)
    {
        if (root == null || !GodotObject.IsInstanceValid(root) || bit == 0)
            return;

        if (root is VisualInstance3D visual)
            visual.Layers &= ~(uint)bit;

        foreach (Node child in root.GetChildren())
        {
            if (child is Node3D child3D)
                RemoveHierarchyLayerBit(child3D, bit);
        }
    }

    // Move a subtree off the bands everyone draws and onto a camera's own bit, so that camera can mask it out.
    // The private/temp/hidden/overlay bits are left alone - they carry meaning nothing here is entitled
    // to throw away - and every other camera's mask already includes bits 5..16 and 18, so the subtree
    // stays visible everywhere else.
    public static void SwapPublicLayerForBit(Node3D root, int bit)
    {
        if (root == null || !GodotObject.IsInstanceValid(root) || bit == 0)
            return;

        // Only nodes that were ACTUALLY on a band everyone draws get swapped, and which bands they wore
        // stays on the node. Without that a node on the private band alone would come back from
        // RestorePublicLayerFromBit wearing a public bit it never had, which is a visibility leak dressed up
        // as a restore, and a name plate would come back public instead of on its own band.
        if (root is VisualInstance3D visual)
        {
            uint bands = visual.Layers & SWAPPED_BANDS;
            if (bands != 0)
            {
                visual.SetMeta(SwappedBandsMeta, (int)bands);
                visual.Layers = (visual.Layers & ~bands) | (uint)bit;
            }
        }

        foreach (Node child in root.GetChildren())
        {
            if (child is Node3D child3D)
                SwapPublicLayerForBit(child3D, bit);
        }
    }

    public static void RestorePublicLayerFromBit(Node3D root, int bit)
    {
        if (root == null || !GodotObject.IsInstanceValid(root) || bit == 0)
            return;

        if (root is VisualInstance3D visual && (visual.Layers & (uint)bit) != 0)
        {
            uint bands = (uint)PUBLIC_LAYER;
            if (visual.HasMeta(SwappedBandsMeta))
            {
                bands = (uint)visual.GetMeta(SwappedBandsMeta).AsInt32();
                visual.RemoveMeta(SwappedBandsMeta);
            }
            visual.Layers = (visual.Layers & ~(uint)bit) | bands;
        }

        foreach (Node child in root.GetChildren())
        {
            if (child is Node3D child3D)
                RestorePublicLayerFromBit(child3D, bit);
        }
    }

    public static void SetHierarchyLayer(List<Slot> slots, int layer, Dictionary<Node3D, int> previous)
    {
        var nodes = new List<Node3D>();
        GodotHelper.ConvertSlots(slots, nodes);
        SetHierarchyLayer(nodes, layer, previous);
    }

    public static void SetHierarchyLayer(List<Node3D> nodes, int layer, Dictionary<Node3D, int> previous)
    {
        if (nodes == null)
            return;

        foreach (var node in nodes)
        {
            if (node != null && GodotObject.IsInstanceValid(node))
            {
                SetHierarchyLayer(node, layer, previous);
            }
        }
    }

    public static void RestoreHierarchyLayer(List<Node3D> nodes, Dictionary<Node3D, int> previous)
    {
        if (nodes == null)
            return;

        foreach (var node in nodes)
        {
            if (node != null && GodotObject.IsInstanceValid(node))
            {
                RestoreHierarchyLayer(node, previous);
            }
        }
    }

    // A world that is this machine's alone (userspace: the dash, its pointer, the keyboard, every panel
    // that follows you) draws for this machine's eyes and nothing else. Whatever band its things ask for,
    // they wear the private band, which only the eyes and a camera told to film the private interface draw.
    // Bands drawn by nobody but special cameras (hidden, temp, overlay, eyes only) stay as they are. This is
    // asked every time a layer is written, so a thing made in userspace long after it was set up can never
    // sit on a band everyone films. -xlinka
    public static uint ForWorld(Lumora.Core.World? world, uint layers)
    {
        if (world == null || world.Focus != Lumora.Core.World.WorldFocus.PrivateOverlay)
            return layers;
        return (layers & unchecked((uint)PRIVATE_RENDER_MASK)) == 0 ? layers : (uint)PRIVATE_LAYER;
    }

    // Every node under a world that just became private, through ForWorld; what each wore is kept so the
    // world can give it back if it ever stops being private.
    public static void MakePrivate(Node3D root, Lumora.Core.World world, Dictionary<Node3D, int> previous)
    {
        if (root is VisualInstance3D visual)
        {
            uint layers = ForWorld(world, visual.Layers);
            if (layers != visual.Layers)
            {
                if (!previous.ContainsKey(root))
                    previous.Add(root, (int)visual.Layers);
                visual.Layers = layers;
            }
        }

        foreach (Node child in root.GetChildren())
        {
            if (child is Node3D child3D)
                MakePrivate(child3D, world, previous);
        }
    }

    public static void SetHierarchyLayer(Node3D root, int layer, Dictionary<Node3D, int> previous)
    {
        if (root is VisualInstance3D visual && visual.Layers != (uint)layer)
        {
            if (!previous.ContainsKey(root))
            {
                previous.Add(root, (int)visual.Layers);
            }
            visual.Layers = (uint)layer;
        }

        foreach (Node child in root.GetChildren())
        {
            if (child is Node3D child3D)
            {
                SetHierarchyLayer(child3D, layer, previous);
            }
        }
    }

    public static void RestoreHierarchyLayer(Node3D root, Dictionary<Node3D, int> previous)
    {
        if (root is VisualInstance3D visual && previous.TryGetValue(root, out int previousLayer))
        {
            visual.Layers = (uint)previousLayer;
        }

        foreach (Node child in root.GetChildren())
        {
            if (child is Node3D child3D)
            {
                RestoreHierarchyLayer(child3D, previous);
            }
        }
    }

    public static void RestoreLayers(Dictionary<Node3D, int> previous)
    {
        foreach (var pair in previous)
        {
            if (pair.Key != null && GodotObject.IsInstanceValid(pair.Key) && pair.Key is VisualInstance3D visual)
            {
                visual.Layers = (uint)pair.Value;
            }
        }
    }
}

