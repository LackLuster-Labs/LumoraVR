// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Helio.UI;
using Lumora.Core.Assets;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Import;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Components.Network;
using Lumora.Core.Components.UI;
using Lumora.Core.Math;
using Lumora.Core.Physics;

namespace Lumora.Core.Components;

// A destination you can pick up. Hand somebody an orb and they are holding the address of a place;
// touch it twice, or press primary while carrying it, and you go there.
//
// Two layers, and the split matters. The ROOT slot is replicated: the collider, the grab, the laser
// target, the menu source and the link fields themselves, because an orb is a real object other
// people can see and take. Everything DRAWN hangs off local slots each peer builds for itself, and
// that is deliberate - "the world you are standing in" and "which way is the viewer" are different
// answers on every machine, so a replicated rim colour would show you somebody else's state and a
// replicated billboard would face one person at a time. Nothing under the visual root is saved or
// sent; it is rebuilt from the replicated fields wherever the orb ends up. -xlinka
[ComponentCategory("World")]
public sealed class WorldOrb : WorldLink, IHeldActivatable
{
    public const float OrbRadius = 0.05f;

    // Above the Grabbable's own interaction priority so the laser resolves the orb to the ray target
    // and a press activates rather than doing nothing. Grabbing is unaffected: the hand walks up the
    // hit slot's chain looking for a grabbable rather than using the interaction target it resolved.
    private const int RayPriority = 2000;

    private const string VisualSlotName = "Orb Visual";
    private const string BodySlotName = "Orb";
    private const string InfoSlotName = "Info";
    private const string FontSlotName = "OrbFont";
    private const string PictureSlotName = "Picture";

    private const float DoubleTapWindow = 0.5f;
    private const float FlashSeconds = 0.5f;

    private Slot? _visual;
    private UnlitMaterial? _body;
    private FresnelMaterial? _rim;
    private ImageProvider? _picture;
    private TextRenderer? _nameText;
    private TextRenderer? _stateText;
    private RayTarget? _ray;
    private Action<float3>? _activated;

    private colorHDR _appliedRim = new colorHDR(0f, 0f, 0f, -1f);
    private colorHDR _appliedBody = new colorHDR(0f, 0f, 0f, -1f);
    private Uri? _appliedThumbnail;
    private string? _appliedName;
    private string? _appliedState;
    private float _flashRemaining;
    private double _lastTap = double.NegativeInfinity;

    // BUILD

    public override void OnAttach()
    {
        base.OnAttach();
        BuildInteraction();
        BuildVisual();
    }

    public override void OnStart()
    {
        base.OnStart();
        // A loaded or replicated orb never ran OnAttach here. The interaction half arrives with the
        // slot (it is replicated and saved), the visual half is local and has to be built per peer.
        if (World?.IsAuthority == true)
            BuildInteraction();
        BuildVisual();

        _ray ??= Slot?.GetComponent<RayTarget>();
        if (_ray != null)
        {
            _activated = OnRayActivated;
            _ray.Activated += _activated;
        }
    }

    public override void OnDestroy()
    {
        if (_ray != null && _activated != null)
            _ray.Activated -= _activated;
        _activated = null;
        base.OnDestroy();
    }

    private void BuildInteraction()
    {
        var slot = Slot;
        if (slot == null || slot.IsDestroyed)
            return;

        var collider = slot.GetComponent<SphereCollider>() ?? slot.AttachComponent<SphereCollider>();
        collider.Radius.Value = OrbRadius;
        // A trigger: the orb is something to point at and pick up, not something to walk into.
        collider.Type.Value = ColliderType.Trigger;

        var grab = slot.GetComponent<Grabbable>() ?? slot.AttachComponent<Grabbable>();
        grab.AllowGrab.Value = true;
        grab.Scalable.Value = false;
        grab.FollowRotation.Value = true;

        _ray = slot.GetComponent<RayTarget>() ?? slot.AttachComponent<RayTarget>();
        _ray.AllowActivation.Value = true;
        _ray.HoverRadius.Value = OrbRadius * 1.6f;
        _ray.InteractionPriority.Value = RayPriority;

        var menu = slot.GetComponent<WorldOrbContextActions>() ?? slot.AttachComponent<WorldOrbContextActions>();
        menu.Orb.Target = this;
    }

    private void BuildVisual()
    {
        var slot = Slot;
        if (slot == null || slot.IsDestroyed)
            return;

        if (_visual == null || _visual.IsDestroyed)
        {
            _visual = slot.FindChild(VisualSlotName, recursive: false);
            if (_visual == null || _visual.IsDestroyed)
            {
                _visual = slot.AddLocalSlot(VisualSlotName);
                _visual.Persistent.Value = false;
            }
        }

        var font = EnsureFont(_visual);

        var body = _visual.FindChild(BodySlotName, recursive: false) ?? _visual.AddSlot(BodySlotName);
        var mesh = body.GetComponent<SphereMesh>() ?? body.AttachComponent<SphereMesh>();
        mesh.Radius.Value = OrbRadius;
        mesh.Segments.Value = 24;
        mesh.Rings.Value = 16;

        _body = body.GetComponent<UnlitMaterial>() ?? body.AttachComponent<UnlitMaterial>();
        _body.TintColor.Value = ModeColor();

        _rim = body.GetComponent<FresnelMaterial>() ?? body.AttachComponent<FresnelMaterial>();
        _rim.BlendMode.Value = BlendMode.Alpha;
        _rim.NearColor.Value = Clear;
        _rim.FarColor.Value = Clear;
        _rim.Exponent.Value = 2f;

        // Two renderers over the same sphere: the solid body underneath, the fresnel shell over it, so
        // the state colour reads as a rim rather than repainting the picture. Taken in collection
        // order so a rebuild binds the same two rather than stacking a third.
        MeshRenderer? bodyRenderer = null;
        MeshRenderer? rimRenderer = null;
        foreach (var existing in body.GetComponents<MeshRenderer>())
        {
            if (bodyRenderer == null)
                bodyRenderer = existing;
            else if (rimRenderer == null)
                rimRenderer = existing;
        }
        bodyRenderer ??= body.AttachComponent<MeshRenderer>();
        rimRenderer ??= body.AttachComponent<MeshRenderer>();
        bodyRenderer.Mesh.Target = mesh;
        bodyRenderer.Material.Target = _body;
        rimRenderer.Mesh.Target = mesh;
        rimRenderer.Material.Target = _rim;

        var info = _visual.FindChild(InfoSlotName, recursive: false) ?? _visual.AddSlot(InfoSlotName);
        info.GetOrAttachComponent<FaceLocalUser>();

        _nameText = EnsureLabel(info, "Name", new float3(0f, OrbRadius + 0.055f, 0f), 0.03f, font);
        _stateText = EnsureLabel(info, "State", new float3(0f, OrbRadius + 0.020f, 0f), 0.02f, font);
        _stateText.Color.Value = new color(0.78f, 0.80f, 0.86f, 1f);

        ApplyThumbnail();
        ApplyLabels();
    }

    // Text with no font renders NOTHING, so the orb brings its own rather than hoping a canvas is
    // nearby. Same shape WidgetPanel uses, on the local visual root so it is never saved.
    private static FontProvider EnsureFont(Slot visual)
    {
        var fontSlot = visual.FindChild(FontSlotName, recursive: false) ?? visual.AddSlot(FontSlotName);
        var provider = fontSlot.GetComponent<FontProvider>() ?? fontSlot.AttachComponent<FontProvider>();
        if (provider.URL.Value == null)
        {
            var url = ImportDialog.ResolveFontUrl(visual.World);
            if (url != null)
            {
                provider.URL.Value = url;
                provider.FallbackURLs.Add(url);
            }
        }
        return provider;
    }

    private static TextRenderer EnsureLabel(Slot info, string name, in float3 offset, float size,
        FontProvider? font)
    {
        var slot = info.FindChild(name, recursive: false) ?? info.AddSlot(name);
        slot.LocalPosition.Value = offset;
        var text = slot.GetComponent<TextRenderer>() ?? slot.AttachComponent<TextRenderer>();
        if (font != null)
            text.Font.Target = font;
        text.Size.Value = size;
        text.HorizontalAlign.Value = TextHorizontalAlignment.Center;
        text.VerticalAlign.Value = TextVerticalAlignment.Middle;
        text.OutlineThickness.Value = 1.2f;
        return text;
    }

    // LIVE

    public override void OnCommonUpdate()
    {
        base.OnCommonUpdate();

        if (_visual == null || _visual.IsDestroyed)
            return;

        if (_flashRemaining > 0f)
            _flashRemaining -= World?.Time.Delta ?? 0f;

        ApplyThumbnail();
        ApplyLabels();
        ApplyRim();
    }

    private void ApplyRim()
    {
        if (_rim == null || _rim.IsDestroyed)
            return;
        var wanted = _flashRemaining > 0f ? Yellow : StateColor();
        if (_appliedRim.r == wanted.r && _appliedRim.g == wanted.g
            && _appliedRim.b == wanted.b && _appliedRim.a == wanted.a)
            return;
        _appliedRim = wanted;
        _rim.FarColor.Value = wanted;
    }

    private void ApplyLabels()
    {
        string name = DisplayName.Value ?? string.Empty;
        if (_nameText != null && !_nameText.IsDestroyed && !string.Equals(_appliedName, name, StringComparison.Ordinal))
        {
            _appliedName = name;
            _nameText.Text.Value = name;
        }

        string state = DescribeState();
        if (_stateText != null && !_stateText.IsDestroyed && !string.Equals(_appliedState, state, StringComparison.Ordinal))
        {
            _appliedState = state;
            _stateText.Text.Value = state;
        }
    }

    // A picture if the link has one, a flat mode colour if it does not. The provider is built the
    // moment a URL turns up, which for a session link is a couple of frames after the spawn.
    private void ApplyThumbnail()
    {
        if (_body == null || _body.IsDestroyed || _visual == null || _visual.IsDestroyed)
            return;

        var url = ThumbnailUrl.Value;
        if (url != null && !ReferenceEquals(url, _appliedThumbnail))
        {
            _appliedThumbnail = url;
            var slot = _visual.FindChild(PictureSlotName, recursive: false) ?? _visual.AddSlot(PictureSlotName);
            _picture = slot.GetComponent<ImageProvider>() ?? slot.AttachComponent<ImageProvider>();
            _picture.URL.Value = url;
            _picture.GenerateMipmaps.Value = false;
            _body.Texture.Target = _picture;
        }

        var wanted = url != null ? colorHDR.White : ModeColor();
        if (_appliedBody.r == wanted.r && _appliedBody.g == wanted.g
            && _appliedBody.b == wanted.b && _appliedBody.a == wanted.a)
            return;
        _appliedBody = wanted;
        _body.TintColor.Value = wanted;
    }

    // ACTIVATION

    // Pointing at a resting orb and pressing primary. One press flashes so you know the orb heard
    // you; the second inside half a second takes you there. A single press cannot open a world by
    // accident, which is the whole reason for the two-tap.
    private void OnRayActivated(float3 hitPoint)
    {
        double now = World?.Time.TotalTime ?? 0d;
        if (now - _lastTap <= DoubleTapWindow)
        {
            _lastTap = double.NegativeInfinity;
            _flashRemaining = 0f;
            Open();
            return;
        }
        _lastTap = now;
        _flashRemaining = FlashSeconds;
    }

    // Primary press while carrying the orb. The hand offers the press here before its own align
    // gesture, so holding an orb and clicking goes to the place rather than snapping it to an axis.
    public bool OnHeldActivate(Grabber grabber)
    {
        Open();
        return true;
    }

    // SPAWNERS

    public static bool SpawnForSession(World into, SessionListEntry entry, out string? reason)
    {
        if (entry == null)
        {
            reason = "no session";
            return false;
        }
        string name = string.IsNullOrEmpty(entry.Name) ? (entry.JoinUrl?.Host ?? "Session") : entry.Name;
        return Spawn(into, name, orb => orb.FillFrom(entry), out reason);
    }

    // A link to a world that is already open on this machine. Sessions are addressed by their
    // published URL, so an unshareable world produces no orb rather than a dead one.
    public static bool SpawnForWorld(World into, World target, out string? reason)
    {
        if (!CanShareSession(target, out reason))
            return false;
        string name = string.IsNullOrEmpty(target.WorldName?.Value) ? target.Name : target.WorldName!.Value;
        return Spawn(into, name, orb => orb.FillFrom(target), out reason);
    }

    public static bool SpawnForSavedWorld(World into, string path, out string? reason)
    {
        string name = string.IsNullOrEmpty(path) ? "Saved world" : System.IO.Path.GetFileNameWithoutExtension(path);
        return Spawn(into, name, orb => orb.FillSaved(path), out reason);
    }

    // The gates answer now; the orb itself is made inside the target world's own update. A dash button
    // fires from the userspace world's update, and writing straight into a session world from there
    // races that world's sync thread, which holds the world's lock while it builds each delta. True
    // means it is queued, not that it exists yet. -xlinka
    private static bool Spawn(World into, string name, Action<WorldOrb> fill, out string? reason)
    {
        if (!CanSpawnIn(into, out reason))
            return false;

        var root = into.RootSlot;
        if (root == null || root.IsDestroyed)
        {
            reason = "no world";
            return false;
        }

        into.RunSynchronously(() =>
        {
            if (root.IsDestroyed)
                return;
            using var own = into.EnterSpawnAllocation();
            var slot = root.AddSlot("World Orb: " + name);
            slot.GlobalPosition = InFrontOfLocalHead(into);
            // The user's favourite orb when they have one, pointed at this place.
            FavouriteSpawn.Fill<WorldOrb>(slot, FavouriteKind.WorldOrb, favourite =>
            {
                if (slot.IsDestroyed)
                    return;
                using var mine = into.EnterSpawnAllocation();
                var orb = favourite ?? slot.AttachComponent<WorldOrb>();
                orb.DisplayName.Value = name;
                fill(orb);
            });
        });
        return true;
    }

    // Arm's length, a little under the eyeline so it does not sit in the middle of the view. The view
    // direction here is the head's MINUS Z; +Z points out the back of the head.
    private static float3 InFrontOfLocalHead(World into)
    {
        var userRoot = into.LocalUser?.Root;
        if (userRoot?.HeadSlot == null)
            return new float3(0f, 1f, 0f);
        var forward = userRoot.HeadRotation * float3.Backward;
        return userRoot.HeadPosition + forward * 0.6f - float3.Up * 0.1f;
    }
}
