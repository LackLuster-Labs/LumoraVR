// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Helio.UI;
using Lumora.Core.Assets;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Import;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Components.Network;
using Lumora.Core.Components.Utility;
using Lumora.Core.Math;
using Lumora.Simulation.Particles.Emitters;

namespace Lumora.Core.Components;

// A door somebody opened for the room. Where the orb is a thing you hand to one person, a portal
// stands on the floor and anybody who walks through it goes where it points, then it closes itself.
//
// It closes itself because the alternative is a room full of other people's doors. The deadline is
// replicated so everyone counts down to the same instant, and only the authority does the destroying;
// every peer just draws the number.
//
// The walk-through test is per peer and runs against the LOCAL user only. A portal that could send
// somebody else somewhere would be a shove, not a door, and Open() is local for exactly that reason.
// Same split as the orb: the replicated half is the anchor and the link fields, the drawn half is
// local per peer. The face is one quad on PortalMaterial (picture, crystal rim, glow, sparks in the
// shader) with a spark spray from the particle system floating in front of it. -xlinka
[ComponentCategory("World")]
public sealed class WorldPortal : WorldLink
{
    // Seconds the door stays open.
    public readonly Sync<float> Lifetime;

    // Session seconds, not world seconds. World.Time starts at zero when each peer opens the world,
    // so a replicated absolute deadline on that clock means a different real moment on every machine
    // and a late joiner reads it as long past. The session clock is the one every peer agrees on. -xlinka
    public readonly Sync<double> ExpiresAt;

    public readonly Sync<string> DroppedBy;

    // The ellipse: a touch narrower than tall, like a doorway, standing on the floor point.
    public const float FaceWidth = 1.9f;
    public const float FaceHeight = 2.5f;
    public const float FaceCentreHeight = 1.35f;

    private const float EnterRadiusXZ = 0.7f;
    private const float EnterHeight = 1.6f;
    private const float RearmDistance = 1.3f;
    private const float Cooldown = 3f;
    private const float ArmDelay = 1.5f;

    private const string VisualSlotName = "Portal Visual";
    private const string FaceSlotName = "Face";
    private const string SparksSlotName = "Sparks";
    private const string LabelsSlotName = "Labels";
    private const string FontSlotName = "PortalFont";
    private const string PictureSlotName = "Picture";

    private Slot? _visual;
    private PortalMaterial? _face;
    private ParticleSystem? _sparks;
    private ImageProvider? _picture;
    private TextRenderer? _nameText;
    private TextRenderer? _infoText;
    private TextRenderer? _statusText;

    private Uri? _appliedThumbnail;
    private string? _appliedName;
    private string? _appliedInfo;
    private string? _appliedStatus;
    private colorHDR _appliedRim = new colorHDR(0f, 0f, 0f, -1f);
    private float _armCountdown = ArmDelay;
    private float _cooldownRemaining;
    private float _statusCountdown;
    private bool _inside;

    public WorldPortal()
    {
        Lifetime = new Sync<float>(this, 60f);
        ExpiresAt = new Sync<double>(this, 0d);
        DroppedBy = new Sync<string>(this, string.Empty);
    }

    // Seconds left before the door closes, floored at zero.
    public float SecondsLeft
    {
        get
        {
            double now = World?.SessionSeconds ?? 0d;
            double left = ExpiresAt.Value - now;
            return left <= 0d ? 0f : (float)left;
        }
    }

    // BUILD

    public override void OnAttach()
    {
        base.OnAttach();
        BuildVisual();
    }

    public override void OnStart()
    {
        base.OnStart();
        BuildVisual();
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

        // The face: one upright quad, the ellipse and everything on it drawn by the shader. Single
        // sided geometry on purpose: the shader draws both faces itself, and a second coplanar set of
        // triangles fought the first for depth and stippled the rim.
        var face = _visual.FindChild(FaceSlotName, recursive: false) ?? _visual.AddSlot(FaceSlotName);
        face.LocalPosition.Value = new float3(0f, FaceCentreHeight, 0f);
        var quad = face.GetComponent<QuadMesh>() ?? face.AttachComponent<QuadMesh>();
        quad.Size.Value = new float2(FaceWidth, FaceHeight);
        quad.DualSided.Value = false;
        _face = face.GetComponent<PortalMaterial>() ?? face.AttachComponent<PortalMaterial>();
        _face.QuadAspect.Value = FaceWidth / FaceHeight;
        // The procedural quad's V runs bottom to top while decoded pictures are stored top down, so the
        // picture goes in flipped or the sky ends up on the floor.
        // No flip needed any more: the procedural quad's V is top-origin now, same as the decoded rows.
        _face.FlipPicture.Value = false;
        _face.Seed.Value = (ReferenceID.GetHashCode() & 0xffff) * 0.01f;
        var faceRenderer = face.GetComponent<MeshRenderer>() ?? face.AttachComponent<MeshRenderer>();
        faceRenderer.Mesh.Target = quad;
        faceRenderer.Material.Target = _face;

        BuildSparks(_visual);

        // The labels turn to face whoever is looking, so they read from any side and are never seen
        // from behind. A mirrored label with the sky showing through its glyphs was the alternative.
        var labels = _visual.FindChild(LabelsSlotName, recursive: false) ?? _visual.AddSlot(LabelsSlotName);
        labels.LocalPosition.Value = new float3(0f, FaceCentreHeight + FaceHeight * 0.5f + 0.36f, 0f);
        labels.GetOrAttachComponent<FaceLocalUser>();
        _nameText = EnsureLabel(labels, "Name", float3.Zero, 0.16f, font);
        _infoText = EnsureLabel(labels, "Info", new float3(0f, -0.17f, 0f), 0.09f, font);
        _statusText = EnsureLabel(labels, "Status", new float3(0f, -0.29f, 0f), 0.07f, font);
        _statusText.Color.Value = new color(0.78f, 0.80f, 0.86f, 1f);

        ApplyThumbnail();
        ApplyLabels();
        ApplyRimColor();
        ApplyStatus();
    }

    // Where the drawn rim sits, in the face's own plane: the shader insets the ellipse from the quad
    // to leave room for the glow, and the rim band is the outer part of that ellipse. Both particle
    // systems emit from this ellipse's shell so they hug the crystal rather than the quad.
    private float2 RimHalfSize()
    {
        float glow = _face != null && !_face.IsDestroyed ? _face.GlowWidth.Value : 0.14f;
        float rim = _face != null && !_face.IsDestroyed ? _face.RimWidth.Value : 0.16f;
        float fit = 1f + glow * 2f + 0.04f;
        float band = 1f - rim * 0.5f;
        return new float2(FaceWidth * 0.5f / fit * band, FaceHeight * 0.5f / fit * band);
    }

    // Slow additive sparks born on the rim and drifting a little way out from it, in the face's own
    // plane. The emitter is a shell ellipse in the slot's XY, which is the plane the face lies in, so
    // nothing here flies out of the door. Untextured on purpose: additive dots tinted by the mode
    // colour are the effect, the same choice the system's own default makes. A trail-based crackle
    // was tried on top and pulled: at this size the arcs read as stray lines, not electricity. -xlinka
    private void BuildSparks(Slot visual)
    {
        var sparks = visual.FindChild(SparksSlotName, recursive: false) ?? visual.AddSlot(SparksSlotName);
        sparks.LocalPosition.Value = new float3(0f, FaceCentreHeight, 0.03f);
        sparks.LocalRotation.Value = floatQ.Identity;
        _sparks = sparks.GetComponent<ParticleSystem>() ?? sparks.AttachComponent<ParticleSystem>();
        _sparks.MaxParticles.Value = 128;
        _sparks.Lifetime.Value = 1.6f;
        _sparks.LifetimeVariance.Value = 0.5f;
        _sparks.StartSize.Value = 0.028f;
        _sparks.EndSize.Value = 0f;
        _sparks.InitialSpeed.Value = 0.07f;
        _sparks.SpeedVariance.Value = 0.05f;
        _sparks.Gravity.Value = -0.03f;
        _sparks.EmissionStrength.Value = 2.5f;
        _sparks.RenderQueue.Value = 3011;

        var rim = RimHalfSize();
        var emitter = sparks.GetComponent<CircleEmitter>() ?? sparks.AttachComponent<CircleEmitter>();
        emitter.System.Target = _sparks;
        emitter.Rate.Value = 28f;
        emitter.Radius.Value = 1f;
        emitter.Scale.Value = rim;
        emitter.FromShell.Value = true;
        emitter.Alignment.Value = CircleEmitterAlignment.XY;
        emitter.DirectionMode.Value = CircleEmitterDirection.RadialUniform;
        emitter.RandomDirectionWeight.Value = 0.6f;
    }

    // Text with no font renders NOTHING, so the portal brings its own. On the local visual root, so
    // it is never saved and never sent.
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

    private static TextRenderer EnsureLabel(Slot parent, string name, in float3 offset, float size,
        FontProvider? font)
    {
        var slot = parent.FindChild(name, recursive: false) ?? parent.AddSlot(name);
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

        float delta = World?.Time.Delta ?? 0f;

        if (_armCountdown > 0f)
            _armCountdown -= delta;
        if (_cooldownRemaining > 0f)
            _cooldownRemaining -= delta;

        ApplyThumbnail();
        ApplyLabels();
        ApplyRimColor();

        _statusCountdown -= delta;
        if (_statusCountdown <= 0f)
        {
            _statusCountdown = 1f;
            ApplyStatus();
        }

        CheckWalkThrough();
        CheckExpiry();
    }

    private void CheckWalkThrough()
    {
        var slot = Slot;
        var userSlot = World?.LocalUser?.Root?.Slot;
        if (slot == null || slot.IsDestroyed || userSlot == null || userSlot.IsDestroyed)
            return;

        var here = slot.GlobalPosition;
        var them = userSlot.GlobalPosition;
        float dx = them.x - here.x;
        float dz = them.z - here.z;
        float flat = MathF.Sqrt(dx * dx + dz * dz);
        float vertical = MathF.Abs(them.y - here.y);

        // Step back out and the door re-arms. Without the wider band it would fire again the moment a
        // rounding wobble put you back over the line.
        if (_inside && flat > RearmDistance)
            _inside = false;

        if (_inside || _armCountdown > 0f || _cooldownRemaining > 0f)
            return;
        if (flat > EnterRadiusXZ || vertical > EnterHeight)
            return;

        _inside = true;
        _cooldownRemaining = Cooldown;
        Open();
    }

    // The authority owns the door's life. Every peer sees the slot go when it does.
    private void CheckExpiry()
    {
        if (World?.IsAuthority != true || ExpiresAt.Value <= 0d)
            return;
        if (World.SessionSeconds < ExpiresAt.Value)
            return;
        Slot?.Destroy();
    }

    // The mode can land after the face was built (a portal is attached first and filled a moment
    // later, and on a guest the field arrives over the wire), so the colour is re-asked rather than
    // set once. Equality-gated: a material write is a hook round trip.
    private void ApplyRimColor()
    {
        var wanted = ModeColor();
        if (_appliedRim.r == wanted.r && _appliedRim.g == wanted.g && _appliedRim.b == wanted.b && _appliedRim.a == wanted.a)
            return;
        _appliedRim = wanted;
        if (_face != null && !_face.IsDestroyed)
            _face.RimColor.Value = wanted;
        if (_sparks != null && !_sparks.IsDestroyed)
        {
            _sparks.StartColor.Value = new colorHDR(wanted.r * 1.6f + 0.4f, wanted.g * 1.6f + 0.4f, wanted.b * 1.6f + 0.4f, 1f);
            _sparks.EndColor.Value = new colorHDR(wanted.r, wanted.g, wanted.b, 0f);
        }
    }

    // Name on top, then "3 / 16 users · Builder". The count line is only rewritten when it changes:
    // every TextRenderer write re-shapes the line into fresh geometry.
    private void ApplyLabels()
    {
        string name = DisplayName.Value ?? string.Empty;
        if (_nameText != null && !_nameText.IsDestroyed && !string.Equals(_appliedName, name, StringComparison.Ordinal))
        {
            _appliedName = name;
            _nameText.Text.Value = name;
        }

        string info = DescribeOccupancy();
        if (_infoText != null && !_infoText.IsDestroyed && !string.Equals(_appliedInfo, info, StringComparison.Ordinal))
        {
            _appliedInfo = info;
            _infoText.Text.Value = info;
            var tint = ModeColor();
            _infoText.Color.Value = new color(0.55f + tint.r * 0.45f, 0.55f + tint.g * 0.45f, 0.55f + tint.b * 0.45f, 1f);
        }
    }

    private string DescribeOccupancy()
    {
        string mode = Mode.Value.ToString();
        if (Kind.Value != WorldLinkKind.Session)
            return DescribeState() + " · " + mode;
        int users = ActiveUsers.Value;
        int max = MaxUsers.Value;
        string count = max > 0 ? users + " / " + max + " users" : (users == 1 ? "1 user" : users + " users");
        return count + " · " + mode;
    }

    // "closes in 41 s · dropped by name", once a second.
    private void ApplyStatus()
    {
        if (_statusText == null || _statusText.IsDestroyed)
            return;

        int seconds = (int)MathF.Ceiling(SecondsLeft);
        string status = "closes in " + seconds + " s";
        if (!string.IsNullOrEmpty(DroppedBy.Value))
            status += " · dropped by " + DroppedBy.Value;
        if (string.Equals(_appliedStatus, status, StringComparison.Ordinal))
            return;
        _appliedStatus = status;
        _statusText.Text.Value = status;
    }

    // The picture goes on the face the moment its URL turns up, which for a session link is a few
    // frames after the spawn (the bytes go through the local asset store off-thread first). Same
    // provider settings the world browser's cards use: no mip chain for a 256x144 picture, and the
    // texture cap has nothing to do with it. -xlinka
    private void ApplyThumbnail()
    {
        if (_face == null || _face.IsDestroyed || _visual == null || _visual.IsDestroyed)
            return;

        var url = ThumbnailUrl.Value;
        if (url == null || ReferenceEquals(url, _appliedThumbnail))
            return;

        _appliedThumbnail = url;
        var slot = _visual.FindChild(PictureSlotName, recursive: false) ?? _visual.AddSlot(PictureSlotName);
        _picture = slot.GetComponent<ImageProvider>() ?? slot.AttachComponent<ImageProvider>();
        _picture.GenerateMipmaps.Value = false;
        _picture.MaxSizeOverride.Value = -1;
        _picture.URL.Value = url;
        _face.Picture.Target = _picture;
    }

    // DROP

    // Stand a door in front of the dropper pointing at the same place the source link does.
    public static bool Drop(World into, WorldLink source, User dropper, out string? reason)
    {
        if (source == null || source.IsDestroyed)
        {
            reason = "nothing to link to";
            return false;
        }
        if (source.Kind.Value == WorldLinkKind.Session && source.JoinUrl.Value == null)
        {
            reason = "no join address yet";
            return false;
        }

        return Place(into, dropper, portal =>
        {
            portal.CopyFrom(source);
            portal.Rename();
        }, out reason);
    }

    public static bool Drop(World into, SessionListEntry entry, User dropper, out string? reason)
    {
        if (!CanShareSession(entry, out reason))
            return false;

        return Place(into, dropper, portal =>
        {
            portal.FillFrom(entry);
            portal.Rename();
        }, out reason);
    }

    // A door for a world open on this machine. The share gate answers first: a private session has no
    // door to stand anywhere.
    public static bool DropForWorld(World into, World target, User dropper, out string? reason)
    {
        if (!CanShareSession(target, out reason))
            return false;

        return Place(into, dropper, portal =>
        {
            portal.FillFrom(target);
            portal.Rename();
        }, out reason);
    }

    // The gates and the pose are worked out now; the door itself is made inside the target world's own
    // update. A dash button fires from the userspace world's update, and writing straight into a
    // session world from there races that world's sync thread, which holds the world's lock while it
    // builds each delta. True means it is queued, not that it stands yet. -xlinka
    private static bool Place(World into, User dropper, Action<WorldPortal> fill, out string? reason)
    {
        if (!CanSpawnIn(into, out reason))
            return false;

        var root = into.RootSlot;
        if (root == null || root.IsDestroyed)
        {
            reason = "no world";
            return false;
        }

        var userRoot = dropper?.Root;
        float3 eye = userRoot?.HeadSlot != null ? userRoot.HeadPosition : new float3(0f, 1.6f, 0f);
        // The view direction is the head's MINUS Z, flattened so a glance at the floor does not put the
        // door in it.
        float3 ahead = float3.Backward;
        if (userRoot != null)
        {
            var forward = userRoot.HeadRotation * float3.Backward;
            forward.y = 0f;
            if (forward.LengthSquared > 1e-6f)
                ahead = forward.Normalized;
        }

        float3 point = eye + ahead * 1.8f;
        float floorY = userRoot?.Slot != null ? userRoot.Slot.GlobalPosition.y : point.y - 1.6f;
        // Drop it onto whatever is under that spot. Started above the point so a door meant for the top
        // of a step does not sink into the step.
        if (into.Physics.Raycast(point + float3.Up * 1.5f, float3.Down, 4f, out var hit))
            floorY = hit.Point.y;

        var floorPoint = new float3(point.x, floorY, point.z);
        // The face points back at whoever dropped it, so they are looking through it rather than at
        // its edge. Built by hand: floatQ.LookRotation returns the inverse rotation in this engine.
        bool hasFacing = UserFacing.TryLookRotation(floorPoint, eye, float3.Up, yawOnly: true, out var facing);
        string droppedBy = dropper?.UserName.Value ?? string.Empty;

        into.RunSynchronously(() =>
        {
            if (root.IsDestroyed)
                return;
            using var own = into.EnterSpawnAllocation();
            var slot = root.AddSlot("World Portal");
            // A door with a minute to live has no business in a world save.
            slot.Persistent.Value = false;
            slot.GlobalPosition = floorPoint;
            if (hasFacing)
                slot.GlobalRotation = facing;

            // The dropper's favourite portal when they have one, sent where this door was going.
            FavouriteSpawn.Fill<WorldPortal>(slot, FavouriteKind.Portal, favourite =>
            {
                if (slot.IsDestroyed)
                    return;
                using var mine = into.EnterSpawnAllocation();
                var portal = favourite ?? slot.AttachComponent<WorldPortal>();
                portal.DroppedBy.Value = droppedBy;
                portal.ExpiresAt.Value = into.SessionSeconds + portal.Lifetime.Value;
                fill(portal);
            });
        });
        return true;
    }

    private void Rename()
    {
        var slot = Slot;
        if (slot == null || slot.IsDestroyed)
            return;
        string name = DisplayName.Value;
        slot.SlotName.Value = string.IsNullOrEmpty(name) ? "World Portal" : "World Portal: " + name;
    }
}
