// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Helio.UI;
using Lumora.Core.Assets;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Components.UI;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Chat;

// THE BUBBLE BESIDE SOMEBODY'S HEAD
//
// While they type, three dots bounce in it; once they send, it holds what they said for a while that grows
// with the length of it, then fades. A newer line takes its place. It hangs on the viewer's right of the
// head, a little over the eyes, turned to the viewer, so it never sits on a face, with two beads trailing
// off its corner toward the head so it reads as theirs. Further than a few metres it grows, so it stays as
// big on screen as it was close up, and it goes past the distance a name plate goes.
//
// LOCAL, like the name plate: one per user on every peer, every slot, mesh and renderer minted in this
// machine's byte, built only the first time that user types, and drawn with the name plates' one shared
// material. It reads only what already replicates (the user's chat record) and writes nothing anybody else
// sees. Your own is drawn too, on the band your own eyes skip the way your own name plate is, so a mirror or
// a camera shows it, facing the way you look, and third person or a free camera shows it outright. Never for
// someone you blocked, and not at all with the setting off. -xlinka
[ComponentCategory("Hidden")]
public sealed class ChatBubble : Component
{
    public const string RootSlotName = "Chat Bubble";

    // THE SHAPE, in metres at a bubble scale of one.
    private const float TextSize = 0.042f;
    private const float MaxTextWidth = 0.46f;
    private const int MaxLines = 4;
    private const float PadX = 0.026f;
    private const float PadY = 0.02f;
    private const float CornerRadius = 0.03f;
    private const float RimWidth = 0.0018f;
    private const float DotSize = 0.0135f;
    private const float DotPitch = 0.024f;
    private const float DotRowHeight = 0.024f;
    private const float DotBounce = 0.0075f;
    private const float DotRate = 1.25f;
    private const float DotStagger = 0.16f;
    private const float TrailNear = 0.018f;
    private const float TrailFar = 0.011f;
    private const float TrailGap = 0.005f;
    private const float PanelDepth = -0.004f;
    private const float InkDepth = -0.002f;

    // Where it hangs, in metres at a user scale of one: the near edge this far to the side of the middle of
    // the head, which clears a face, and the bottom edge this far over the eyes.
    private const float SideGap = 0.15f;
    private const float Lift = 0.02f;

    // Grows past this distance, up to this much, and stops drawing at the last.
    private const float GrowFrom = 3f;
    private const float MaxGrow = 2f;
    private const float ViewDistance = 25f;

    // How long a line stays up: a short one long enough to read, a long one longer, never forever.
    private const float HoldBase = 3.5f;
    private const float HoldPerChar = 0.055f;
    private const float HoldMax = 12f;
    private const float FadeSeconds = 0.6f;
    private const float PopSeconds = 0.14f;
    private const float PopFrom = 0.82f;

    // The dots outlive the typing flag this long. The flag goes off as the line is sent, and the line itself can
    // be a moment behind it, held back by the send pacing; without this the bubble blinks out in between and
    // pops back in with the line.
    private const float TypingGrace = 0.7f;

    private static readonly color Fill = new color(DashTheme.Panel.r, DashTheme.Panel.g, DashTheme.Panel.b, 0.86f);
    private static readonly color Ink = NameplateTheme.NameInk;
    private static readonly color DotInk = DashTheme.TextDim;

    private User? _user;
    private WorldChat? _chat;
    private NameplateTheme? _theme;
    private readonly TextShaper _shaper = new();

    private Slot? _visual;
    private RenderLayerOverride? _layer;
    private RoundedQuadMesh? _body;
    private RoundedQuadRingMesh? _rim;
    private TextRenderer? _label;
    private TextMaterial? _labelStyle;
    private readonly RoundedQuadMesh?[] _dots = new RoundedQuadMesh?[3];
    private readonly Slot?[] _dotSlots = new Slot?[3];
    private readonly RoundedQuadMesh?[] _trail = new RoundedQuadMesh?[2];
    private readonly Slot?[] _trailSlots = new Slot?[2];

    private WorldChatLine? _shown;
    private double _shownAt;
    private double _typingUntil;
    private float _hold;
    private string _wrapped = string.Empty;
    private float2 _estimate;
    private int _wrappedFor = -1;
    private bool _visible;
    private double _poppedAt;
    private float _appliedAlpha = -1f;
    private float2 _laidOutFor = new float2(-1f, -1f);
    private bool _laidOutDots;
    private float2 _size;

    // What the bubble shows right now, for anything that has to check it from outside.
    public bool IsShowing => _visible;
    public bool ShowsDots => _visible && _dotSlots[0] is { IsDestroyed: false } dot && dot.ActiveSelf.Value;
    public string ShownText => _visible && _label != null && !_label.IsDestroyed && _label.Slot.ActiveSelf.Value ? _label.Text.Value ?? string.Empty : string.Empty;
    public Slot? Visual => _visual;
    public User? Owner => _user;

    // The bubble beside this user's head, once one was made.
    public static ChatBubble? Of(User? user)
    {
        var root = user?.Root?.Slot;
        if (root == null || root.IsDestroyed)
            return null;
        foreach (var child in root.Children)
        {
            if (child.GetComponent<AvatarEquipManager>() == null)
                continue;
            foreach (var inner in child.LocalChildren)
            {
                if (inner.GetComponent<ChatBubble>() is { IsDestroyed: false } found)
                    return found;
            }
        }
        return null;
    }

    // Whether a ray crosses the bubble as this machine draws it, and how far out. The bubble has no collider,
    // so a beam goes straight through it; the ring asks this instead, and pointing at what somebody said is
    // pointing at them. -xlinka
    public bool Crosses(in float3 origin, in float3 direction, out float distance)
    {
        distance = 0f;
        var visual = _visual;
        if (!_visible || visual == null || visual.IsDestroyed || _size.x <= 0f || _size.y <= 0f)
            return false;
        var from = visual.GlobalPointToLocal(origin);
        var along = visual.GlobalPointToLocal(origin + direction) - from;
        if (System.MathF.Abs(along.z) < 1e-6f)
            return false;
        float t = -from.z / along.z;
        if (t <= 0f)
            return false;
        var hit = from + along * t;
        if (System.MathF.Abs(hit.x) > _size.x * 0.5f || System.MathF.Abs(hit.y) > _size.y * 0.5f)
            return false;
        distance = (visual.LocalPointToGlobal(hit) - origin).Length;
        return true;
    }

    // One per user root, beside the name plate.
    public static ChatBubble? Ensure(AvatarEquipManager manager)
    {
        var slot = manager?.Slot;
        if (slot == null || slot.IsDestroyed || manager!.World == null || manager.World.IsUserspace)
            return null;

        // a local slot under a synced one is filed with the local children; under a local one, with the rest
        foreach (var child in slot.LocalChildren)
        {
            if (child.GetComponent<ChatBubble>() is { IsDestroyed: false } found)
                return found;
        }
        foreach (var child in slot.Children)
        {
            if (child.IsLocalElement && child.GetComponent<ChatBubble>() is { IsDestroyed: false } found)
                return found;
        }

        var root = slot.AddLocalSlot(RootSlotName);
        root.Persistent.Value = false;
        root.GetOrAttachComponent<ImmutableComponent>();
        return root.AttachComponent<ChatBubble>();
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        var world = World;
        if (Slot == null || world == null)
            return;

        var root = Slot.ActiveUserRoot;
        _user = root?.ActiveUser;
        if (root == null || root.IsDestroyed || _user == null || _user.IsDestroyed)
        {
            SetVisible(false);
            return;
        }

        _chat ??= WorldChat.For(world);
        double now = world.Time.TotalTime;

        var latest = _chat?.LatestFor(_user);
        if (latest != null && !ReferenceEquals(latest, _shown))
        {
            _shown = latest;
            _shownAt = now;
            _hold = System.Math.Min(HoldMax, HoldBase + HoldPerChar * latest.Text.Length);
            _typingUntil = 0.0;
            _wrappedFor = -1;
        }

        float age = (float)(now - _shownAt);
        bool line = _shown != null && age < _hold + FadeSeconds;
        if (_user.ChatTyping.Value)
            _typingUntil = now + TypingGrace;
        bool typing = now < _typingUntil;
        var head = root.HeadSlot;
        // Past where its words fade out there is nothing to see, and a crowd typing at the far end of a room
        // bounced its dots every frame all the same. Behind the viewer it keeps up now and then. -xlinka
        var view = AvatarViewLod.Look(root.Slot);
        bool want = EngineSettings.ShowChatBubbles && !_user.IsBlockedByMe && (line || typing)
            && head != null && !head.IsDestroyed && head.IsActive && view.Distance <= ViewDistance + 2f;
        if (!want)
        {
            SetVisible(false);
            return;
        }
        if (_visible && view.Level == AvatarViewLod.Level.Hidden && !view.RunsThisFrame(world.Time.UpdateIndex))
            return;

        if (!Build())
            return;
        if (!_visible)
        {
            _poppedAt = now;
            SetVisible(true);
        }

        if (line && _wrappedFor != _shown!.Seq)
            Rewrap();
        if (line && _wrappedFor >= 0 && FontGeneration() != _fontGeneration)
            Rewrap();

        bool showText = line;
        bool showDots = typing;
        if (_label != null && !_label.IsDestroyed && _label.Slot.ActiveSelf.Value != showText)
        {
            _label.Slot.ActiveSelf.Value = showText;
            _laidOutFor = new float2(-1f, -1f);
        }
        for (int i = 0; i < _dotSlots.Length; i++)
        {
            var dot = _dotSlots[i];
            if (dot != null && !dot.IsDestroyed && dot.ActiveSelf.Value != showDots)
                dot.ActiveSelf.Value = showDots;
        }

        Layout(showText, showDots);
        float alpha = typing ? 1f : 1f - System.Math.Clamp((age - _hold) / FadeSeconds, 0f, 1f);
        ApplyAlpha(alpha);
        if (showDots)
            BounceDots(now);
        int band = root.IsLocalUserRoot && !UserInputState.FocusedExternalCameraActive
            ? RenderLayerOverride.OwnNameplateLayer : RenderLayerOverride.NameplateLayer;
        if (_layer != null && !_layer.IsDestroyed && _layer.Layer.Value != band)
            _layer.Layer.Value = band;
        Place(root, head!, now);
    }

    // BUILDING

    private bool Build()
    {
        if (_visual != null && !_visual.IsDestroyed)
            return true;
        _theme ??= NameplateTheme.For(World);
        if (_theme == null || _theme.PlateMaterial == null)
            return false;

        var visual = Slot.AddLocalSlot("Bubble");
        visual.Persistent.Value = false;
        visual.ActiveSelf.Value = false;
        // The name plates' band, so a camera told to leave plates out leaves these out with them.
        _layer = visual.AttachComponent<RenderLayerOverride>();
        _layer.Layer.Value = RenderLayerOverride.NameplateLayer;
        _visual = visual;

        var rimSlot = visual.AddSlot("Rim");
        rimSlot.LocalPosition.Value = new float3(0f, 0f, PanelDepth);
        _rim = rimSlot.AttachComponent<RoundedQuadRingMesh>();
        _rim.RimWidth.Value = RimWidth;
        _rim.Color = NameplateTheme.PlateEdge;
        AttachRenderer(rimSlot, _rim);

        var bodySlot = visual.AddSlot("Body");
        bodySlot.LocalPosition.Value = new float3(0f, 0f, PanelDepth);
        _body = bodySlot.AttachComponent<RoundedQuadMesh>();
        _body.Color = Fill;
        AttachRenderer(bodySlot, _body);

        for (int i = 0; i < _trail.Length; i++)
        {
            var bead = visual.AddSlot("Trail");
            bead.LocalPosition.Value = new float3(0f, 0f, PanelDepth);
            var mesh = bead.AttachComponent<RoundedQuadMesh>();
            float size = i == 0 ? TrailNear : TrailFar;
            mesh.Size.Value = new float2(size, size);
            mesh.CornerRadius.Value = size * 0.5f;
            mesh.Color = Fill;
            AttachRenderer(bead, mesh);
            _trail[i] = mesh;
            _trailSlots[i] = bead;
        }

        for (int i = 0; i < _dots.Length; i++)
        {
            var dot = visual.AddSlot("Dot");
            var mesh = dot.AttachComponent<RoundedQuadMesh>();
            mesh.Size.Value = new float2(DotSize, DotSize);
            mesh.CornerRadius.Value = DotSize * 0.5f;
            mesh.Color = DotInk;
            // On the glass: told to draw after it, or the glass sorts over them from some angles.
            AttachRenderer(dot, mesh).SortingOrder.Value = 1;
            dot.ActiveSelf.Value = false;
            _dots[i] = mesh;
            _dotSlots[i] = dot;
        }

        var labelSlot = visual.AddSlot("Text");
        _label = labelSlot.AttachComponent<TextRenderer>();
        if (_theme.Font != null)
            _label.Font.Target = _theme.Font;
        _label.Size.Value = TextSize;
        _label.HorizontalAlign.Value = TextHorizontalAlignment.Left;
        _label.VerticalAlign.Value = TextVerticalAlignment.Top;
        _label.Color.Value = Ink;
        _label.MaxViewDistance.Value = ViewDistance;
        // Blended rather than cut: a cut letter vanishes halfway through the fade instead of fading with the
        // glass under it.
        _labelStyle = labelSlot.AttachComponent<TextMaterial>();
        _labelStyle.AlphaClip.Value = false;
        _labelStyle.BlendMode.Value = BlendMode.Alpha;
        _labelStyle.Culling.Value = Culling.None;
        _labelStyle.OutlineThickness.Value = 0f;
        _label.Style.Target = _labelStyle;
        labelSlot.ActiveSelf.Value = false;

        _laidOutFor = new float2(-1f, -1f);
        _appliedAlpha = -1f;
        return true;
    }

    private static MeshRenderer AttachRenderer(Slot slot, ProceduralMesh mesh, NameplateTheme theme)
    {
        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        renderer.Material.Target = theme.PlateMaterial!;
        renderer.ShadowCastMode.Value = ShadowCastMode.Off;
        renderer.MaxViewDistance = ViewDistance;
        renderer.ViewDistanceFadeMargin = TextRenderer.ViewDistanceFadeMargin;
        renderer.MarkChangeDirty();
        return renderer;
    }

    private MeshRenderer AttachRenderer(Slot slot, ProceduralMesh mesh) => AttachRenderer(slot, mesh, _theme!);

    private void SetVisible(bool visible)
    {
        if (_visible == visible)
            return;
        _visible = visible;
        if (_visual != null && !_visual.IsDestroyed)
            _visual.ActiveSelf.Value = visible;
        if (!visible)
            _laidOutFor = new float2(-1f, -1f);
    }

    // TEXT

    private int _fontGeneration = -1;

    private int FontGeneration() => _theme?.Font?.Asset?.CacheGeneration ?? -1;

    // The line, broken into rows here rather than by the text renderer, which only breaks where it is told.
    private void Rewrap()
    {
        if (_shown == null || _label == null || _label.IsDestroyed)
            return;
        var font = _theme?.Font?.Asset;
        _fontGeneration = FontGeneration();
        _wrapped = ChatText.Wrap(_shaper, font, ChatText.Clean(_shown.Text), TextSize, MaxTextWidth, MaxLines,
            out float width, out int lines);
        float lineHeight = font != null && font.IsValid ? font.GetLineHeight(TextSize) : TextSize * 1.25f;
        _estimate = new float2(width, lines * lineHeight);
        if (!string.Equals(_label.Text.Value, _wrapped, StringComparison.Ordinal))
            _label.Text.Value = _wrapped;
        _wrappedFor = _shown.Seq;
        _laidOutFor = new float2(-1f, -1f);
    }

    // LAYOUT

    private void Layout(bool showText, bool showDots)
    {
        if (_body == null || _body.IsDestroyed || _rim == null || _rim.IsDestroyed)
            return;

        var text = float2.Zero;
        if (showText && _label != null && !_label.IsDestroyed)
        {
            text = _label.RenderedSize;
            if (text.x <= 0f || text.y <= 0f)
                text = _estimate;
        }

        var signature = new float2(text.x + (showDots ? 1000f : 0f), text.y + (showText ? 1000f : 0f));
        if ((signature - _laidOutFor).LengthSquared < 1e-10f && showDots == _laidOutDots)
            return;
        _laidOutFor = signature;
        _laidOutDots = showDots;

        float dotsWidth = DotPitch * 2f + DotSize;
        float contentWidth = System.MathF.Max(showText ? text.x : 0f, showDots ? dotsWidth : 0f);
        float contentHeight = (showText ? text.y : 0f) + (showDots ? DotRowHeight : 0f);
        float width = contentWidth + PadX * 2f;
        float height = System.MathF.Max(contentHeight + PadY * 2f, DotRowHeight + PadY * 2f);
        _size = new float2(width, height);

        float radius = System.MathF.Min(CornerRadius, height * 0.5f);
        _body.Size.Value = _size;
        _body.CornerRadius.Value = radius;
        _rim.Size.Value = _size;
        _rim.CornerRadius.Value = radius;

        float left = -width * 0.5f + PadX;
        float top = height * 0.5f - PadY;
        if (showText && _label != null && !_label.IsDestroyed)
            _label.Slot.LocalPosition.Value = new float3(left, top, 0f);

        // Dots alone sit in the middle of a small pill; under a line they start where the line starts.
        float dotsY = -height * 0.5f + PadY + DotRowHeight * 0.5f;
        float dotsX = showText ? left + DotSize * 0.5f : -DotPitch;
        for (int i = 0; i < _dotSlots.Length; i++)
        {
            var dot = _dotSlots[i];
            if (dot != null && !dot.IsDestroyed)
                dot.LocalPosition.Value = new float3(dotsX + i * DotPitch, dotsY, InkDepth);
        }
        _dotsBaseY = dotsY;

        // Two beads off the lower corner, toward the head, which sits beside and under that corner.
        float inset = radius * (1f - 1f / System.MathF.Sqrt(2f));
        var corner = new float2(-width * 0.5f + inset, -height * 0.5f + inset);
        var toward = new float2(-SideGap, -Lift).Normalized;
        float along = TrailGap + TrailNear * 0.5f;
        for (int i = 0; i < _trailSlots.Length; i++)
        {
            var bead = _trailSlots[i];
            if (bead != null && !bead.IsDestroyed)
                bead.LocalPosition.Value = new float3(corner.x + toward.x * along, corner.y + toward.y * along, PanelDepth);
            along += (i == 0 ? TrailNear : TrailFar) * 0.5f + TrailGap + TrailFar * 0.5f;
        }
    }

    private float _dotsBaseY;

    // Each dot hops in turn off the world clock: up and back down in the first half of its beat, resting for
    // the second, the next one a little behind it.
    private void BounceDots(double now)
    {
        for (int i = 0; i < _dotSlots.Length; i++)
        {
            var dot = _dotSlots[i];
            if (dot == null || dot.IsDestroyed)
                continue;
            double beat = now * DotRate - i * DotStagger;
            float phase = (float)(beat - System.Math.Floor(beat));
            float hop = phase < 0.5f ? System.MathF.Sin(phase * 2f * System.MathF.PI) : 0f;
            var position = dot.LocalPosition.Value;
            float y = _dotsBaseY + hop * DotBounce;
            if (position.y != y)
                dot.LocalPosition.Value = new float3(position.x, y, position.z);
        }
    }

    // Alpha goes onto the corners and the ink, in steps fine enough to read as a fade and coarse enough that a
    // held bubble re-meshes nothing.
    private void ApplyAlpha(float alpha)
    {
        alpha = System.MathF.Round(System.Math.Clamp(alpha, 0f, 1f) * 48f) / 48f;
        if (alpha == _appliedAlpha)
            return;
        _appliedAlpha = alpha;
        if (_body != null && !_body.IsDestroyed)
            _body.Color = Fill.WithAlpha(Fill.a * alpha);
        if (_rim != null && !_rim.IsDestroyed)
            _rim.Color = NameplateTheme.PlateEdge.WithAlpha(NameplateTheme.PlateEdge.a * alpha);
        foreach (var bead in _trail)
        {
            if (bead != null && !bead.IsDestroyed)
                bead.Color = Fill.WithAlpha(Fill.a * alpha);
        }
        foreach (var dot in _dots)
        {
            if (dot != null && !dot.IsDestroyed)
                dot.Color = DotInk.WithAlpha(alpha);
        }
        if (_label != null && !_label.IsDestroyed)
            _label.Color.Value = Ink.WithAlpha(alpha);
    }

    // PLACEMENT

    private void Place(UserRoot root, Slot head, double now)
    {
        var visual = _visual;
        var parent = visual?.Parent;
        if (visual == null || visual.IsDestroyed || parent == null)
            return;
        if (!TryViewpoint(World, out var viewer))
            return;

        var anchor = head.GlobalPosition;
        var toViewer = viewer - anchor;
        float distance = toViewer.Length;
        toViewer.y = 0f;
        if (toViewer.LengthSquared < 1e-6f)
        {
            toViewer = head.GlobalRotation * float3.Backward;
            toViewer.y = 0f;
            if (toViewer.LengthSquared < 1e-6f)
                toViewer = float3.Backward;
        }

        // +Z at the viewer, so +X is the viewer's right and the head is off the bubble's left.
        var rotation = floatQ.AxisAngle(float3.Up, System.MathF.Atan2(toViewer.x, toViewer.z));
        float user = System.MathF.Max(0.05f, root.GlobalScale);
        float grow = System.Math.Clamp(distance / GrowFrom, 1f, MaxGrow);
        float pop = (float)System.Math.Clamp((now - _poppedAt) / PopSeconds, 0.0, 1.0);
        float scale = grow * (PopFrom + (1f - PopFrom) * (1f - (1f - pop) * (1f - pop)));

        var right = rotation * float3.Right;
        var corner = anchor + right * (SideGap * user) + float3.Up * (Lift * user);
        var center = corner + right * (_size.x * 0.5f * scale) + float3.Up * (_size.y * 0.5f * scale);

        float parentScale = parent.GlobalScale.x;
        if (System.MathF.Abs(parentScale) < 1e-6f)
            parentScale = 1f;

        // A remote user's root is not this machine's to write. These are local slots nobody else has, so the
        // gate is stepped round the same way a name plate turning to face you steps round it.
        using var bypass = World.DataModelPermissions?.EnterSystemBypass();
        visual.GlobalPosition = center;
        visual.GlobalRotation = rotation;
        var local = float3.One * (scale / parentScale);
        if (!visual.LocalScale.Value.Equals(local))
            visual.LocalScale.Value = local;
    }

    // The eye this machine renders from: the free or orbit camera when one is up, the user's head otherwise.
    private static bool TryViewpoint(World world, out float3 point)
    {
        var input = Engine.Current?.InputInterface;
        if (input != null && world.Focus == World.WorldFocus.Focused && UserInputState.FocusedExternalCameraActive)
        {
            point = input.DesktopCameraPosition;
            return true;
        }
        var head = world.LocalUser?.Root?.HeadSlot;
        if (head == null || head.IsDestroyed)
        {
            point = default;
            return false;
        }
        point = head.GlobalPosition;
        return true;
    }
}
