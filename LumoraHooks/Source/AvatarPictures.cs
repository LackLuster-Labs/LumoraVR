// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Godot;
using Lumora.Core.Components;
using Lumora.Core.Components.Avatar;
using Lumora.Godot.Helpers;

namespace Lumora.Godot.Hooks;

// PICTURES OF FAR-OFF AVATARS.
//
// AvatarCrowd decides who is a picture and when one is taken; this draws them. A picture is the avatar filmed
// from where the viewer stands, in the same world and under the same lights, so the picture and the body it
// stands in for look the same the moment one hands over to the other. It goes on a card that faces the viewer,
// pulled toward it by the avatar's size while the body is still there (and shrunk to match) so the body never
// cuts it, standing back as far off as the body once the body is gone so neighbours overlap it as they overlapped
// the body, and drawn with an ordered dither for its fade so it needs no sorting and puts nothing see-through on
// anybody's avatar.
//
// The camera must see the avatar and nothing else, while the real body may be hidden. So the picture is not of
// the body itself but of a copy of each of its draws: the same mesh, the same materials and, for a skinned part,
// the same skin the real one is posed by, on a band no eye draws (HIDDEN_LAYER), shown for the one frame a
// picture is taken. Each card keeps two pictures and blends from the last to the newest. -xlinka
internal sealed partial class AvatarPictures : Node
{
    private const float Aspect = 0.5f;
    // A picture has half as many rows again as its card covers on screen, enough to stay sharp without fizzing as
    // it is drawn smaller, in steps so a rig is not remade for every take.
    private const float RowsPerPixel = 1.5f;
    private const int RowStep = 32;
    private const int MinRows = 96;
    private const int MaxRows = 512;
    private const float BlendSeconds = 0.2f;
    // Skin, clothes and hair standing off the bones, at a person's size, for a body whose skin gives no boxes.
    private const float BoneMargin = 0.22f;
    // Around the skin's own boxes: a pixel or two of antialiased edge, at a person's size.
    private const float SkinMargin = 0.04f;
    // A card not needed for this long gives its pictures back.
    private const double KeepIdleSeconds = 6.0;

    // What drawing the pictures has cost this machine so far, for the frame profile: milliseconds and pictures taken.
    internal static double SpentMs { get; private set; }
    internal static int Taken { get; private set; }

    private static AvatarPictures? s_node;
    private static Shader? s_shader;
    private static QuadMesh? s_quad;
    private static readonly StringName FadeName = "fade";
    private static readonly StringName PictureName = "picture";
    private static readonly StringName SliceLowName = "slice_low";
    private static readonly StringName SliceHighName = "slice_high";

    private readonly Dictionary<AvatarCrowd.Member, Card> _cards = new();
    private uint _eyeMask = uint.MaxValue;
    private float _focal = 900f;
    private Viewport.Msaa _msaa = Viewport.Msaa.Disabled;
    private readonly List<AvatarCrowd.Member> _drop = new();
    private readonly List<Card> _shownCopies = new();
    private global::Godot.Environment? _environment;
    private global::Godot.Environment? _environmentSource;
    private double _environmentAt = double.NegativeInfinity;

    // Whether the once-a-frame pass after the world runs: what AvatarPlacement keeps waits on it.
    internal static bool Ticking => s_node != null && IsInstanceValid(s_node) && s_node.IsInsideTree();

    public static void Ensure()
    {
        if (s_node != null && IsInstanceValid(s_node))
            return;
        if (global::Godot.Engine.GetMainLoop() is not SceneTree tree || tree.Root == null)
            return;
        s_node = new AvatarPictures { Name = "AvatarPictures", ProcessPriority = 900, ProcessMode = ProcessModeEnum.Always };
        tree.Root.CallDeferred(Node.MethodName.AddChild, s_node);
        AvatarCrowd.CanDraw = static _ => s_node != null && IsInstanceValid(s_node) && s_node.IsInsideTree();
    }

    public override void _Process(double delta)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            Step(delta);
        }
        finally
        {
            SpentMs += (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }
    }

    private void Step(double delta)
    {
        AvatarPlacement.Flush();

        // The copies shown for last frame's pictures go back out of sight.
        foreach (var card in _shownCopies)
            card.HideCopies();
        _shownCopies.Clear();

        double now = Time.GetTicksMsec() / 1000.0;
        var world = Lumora.Core.Engine.Current?.WorldManager?.FocusedWorld;
        var crowd = AvatarCrowd.Existing(world);
        var head = world?.LocalUser?.Root?.HeadSlot;
        if (crowd != null && head != null && !head.IsDestroyed)
        {
            var camera = RenderHelper.ViewerCamera?.Invoke();
            bool seeing = camera != null && IsInstanceValid(camera) && camera.IsInsideTree();
            Vector3 eye = seeing ? camera!.GlobalPosition : ToGodot(head.GlobalPosition);
            _eyeMask = seeing ? camera!.CullMask : unchecked((uint)RenderHelper.PUBLIC_RENDER_MASK);
            _focal = seeing ? Focal(camera!) : 900f;
            // The eyes' own antialiasing, so fur and lace cut by coverage come out the same.
            _msaa = seeing ? camera!.GetViewport()?.Msaa3D ?? Viewport.Msaa.Disabled : Viewport.Msaa.Disabled;
            float dt = (float)delta;
            foreach (var member in crowd.Members)
            {
                if (member.Phase == AvatarCrowd.Phase.Body)
                    continue;
                if (!_cards.TryGetValue(member, out var card))
                {
                    card = new Card(this);
                    _cards[member] = card;
                }
                card.UsedAt = now;
                if (member.CaptureNow && card.Take(member, eye, world!.Time.TotalTime))
                {
                    _shownCopies.Add(card);
                    Taken++;
                }
                card.Draw(member, eye, dt);
            }
        }

        _drop.Clear();
        foreach (var (member, card) in _cards)
        {
            bool showing = crowd != null && member.Phase != AvatarCrowd.Phase.Body && card.UsedAt == now;
            if (!showing)
                card.HideCard();
            if (now - card.UsedAt > KeepIdleSeconds || member.Root.IsDestroyed)
                _drop.Add(member);
        }
        foreach (var member in _drop)
        {
            _cards[member].Free();
            _cards.Remove(member);
            member.HasPicture = false;
        }
    }

    public override void _ExitTree()
    {
        foreach (var card in _cards.Values)
            card.Free();
        _cards.Clear();
        _shownCopies.Clear();
    }

    // The world's own environment with everything the main view adds afterwards switched off: the picture is
    // drawn into the main view as plain colour, and that view puts its own exposure, tonemap, glow and fog on it
    // again. Its occlusion stays, since the main view lays it on the body and not on a picture. The passes that
    // light a body from what stands around it on screen go, as a picture has nothing around it. Copied again now
    // and then so it follows the world.
    private global::Godot.Environment? CaptureEnvironment(World3D world3D)
    {
        var source = world3D.Environment ?? world3D.FallbackEnvironment;
        double now = Time.GetTicksMsec() / 1000.0;
        if (source == null)
            return null;
        if (_environment != null && ReferenceEquals(source, _environmentSource) && now - _environmentAt < 5.0)
            return _environment;
        var env = (global::Godot.Environment)source.Duplicate();
        env.SsilEnabled = false;
        env.SsrEnabled = false;
        env.SdfgiEnabled = false;
        env.GlowEnabled = false;
        env.FogEnabled = false;
        env.VolumetricFogEnabled = false;
        env.AdjustmentEnabled = false;
        env.TonemapMode = global::Godot.Environment.ToneMapper.Linear;
        env.TonemapExposure = 1f;
        _environment = env;
        _environmentSource = source;
        _environmentAt = now;
        return env;
    }

    // Screen rows one metre covers one metre in front of the eye.
    private static float Focal(Camera3D camera)
    {
        float rows = camera.GetViewport()?.GetVisibleRect().Size.Y ?? 1080f;
        return rows / (2f * Mathf.Tan(Mathf.DegToRad(Mathf.Clamp(camera.Fov, 1f, 170f)) * 0.5f));
    }

    private static Shader CardShader()
    {
        s_shader ??= new Shader
        {
            Code = @"shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_opaque, shadows_disabled;

uniform sampler2D picture : filter_linear, repeat_disable;
instance uniform float fade = 0.0;
instance uniform float slice_low = 0.0;
instance uniform float slice_high = 1.0;

const float ORDER[16] = float[](0.0, 8.0, 2.0, 10.0, 12.0, 4.0, 14.0, 6.0, 3.0, 11.0, 1.0, 9.0, 15.0, 7.0, 13.0, 5.0);

void fragment() {
    // Which of the card's two pictures owns this pixel while the newer takes over from the older: the two own
    // complementary pixels, so between them every pixel is drawn once.
    ivec2 which = ivec2(mod(FRAGCOORD.yx + vec2(1.0, 2.0), 4.0));
    float slice = (ORDER[which.y * 4 + which.x] + 0.5) / 16.0;
    if (slice < slice_low || slice >= slice_high)
        discard;
    vec4 c = texture(picture, UV);
    // A picture can hold a stray bad value from a material that divides by nothing; drawn, it would light up the
    // glow pass and every reflection probe that takes the card in.
    if (any(isnan(c)) || any(isinf(c)))
        discard;
    // Dithered only while fading; a card fully in cuts its edge at half cover, so soft fur does not fizz.
    ivec2 cell = ivec2(mod(FRAGCOORD.xy, 4.0));
    float threshold = fade >= 0.999 ? 0.5 : (ORDER[cell.y * 4 + cell.x] + 0.5) / 16.0;
    if (c.a * fade <= threshold)
        discard;
    ALBEDO = min(c.rgb / max(c.a, 0.05), vec3(64.0));
}
",
        };
        return s_shader;
    }

    private static Vector3 ToGodot(Lumora.Core.Math.float3 v) => new(v.x, v.y, v.z);
    private static Vector3 Least(Vector3 a, Vector3 b) => new(Mathf.Min(a.X, b.X), Mathf.Min(a.Y, b.Y), Mathf.Min(a.Z, b.Z));
    private static Vector3 Most(Vector3 a, Vector3 b) => new(Mathf.Max(a.X, b.X), Mathf.Max(a.Y, b.Y), Mathf.Max(a.Z, b.Z));

    private sealed class Rig
    {
        public SubViewport Viewport = null!;
        public Camera3D Camera = null!;
    }

    // One draw of the avatar, copied for the picture camera.
    private sealed class Copy
    {
        public Lumora.Core.Component Part = null!;
        public Rid Instance;
        public Rid Mesh;
        public Rid Skeleton;
    }

    // Where a picture stands relative to where the body stands, and how big it is.
    private struct Shape
    {
        public Vector3 CenterOffset;
        public float HalfHeight;
        public float HalfWidth;
        public float Radius;
        public bool Valid;
    }

    // A card is two quads, one per picture, each drawn where its own picture was taken: a new picture takes over
    // from the last pixel by pixel, and an old picture is never stretched over the shape of a new one.
    private sealed class Card
    {
        private readonly AvatarPictures _owner;
        private readonly Rig[] _rigs = new Rig[2];
        private readonly ShaderMaterial?[] _materials = new ShaderMaterial?[2];
        private readonly Rid[] _instances = new Rid[2];
        private readonly bool[] _visible = new bool[2];
        private readonly Shape[] _shapes = new Shape[2];
        private readonly List<Copy> _copies = new();
        private Rid _scenario;
        private int _partsVersion = -1;
        private int _shown = -1;
        private float _blend = 1f;
        private bool _pictured;

        public double UsedAt;

        public Card(AvatarPictures owner) => _owner = owner;

        // Films the avatar into the rig not being shown; true when copies were shown for it.
        public bool Take(AvatarCrowd.Member member, Vector3 eye, double now)
        {
            member.RefreshParts(now);
            var root = member.Root;
            if (root == null || root.IsDestroyed)
                return false;
            if (_partsVersion != member.PartsVersion)
            {
                _partsVersion = member.PartsVersion;
                FreeCopies();
                foreach (var part in member.SkinnedParts)
                    _copies.Add(new Copy { Part = part });
                foreach (var part in member.MeshParts)
                    _copies.Add(new Copy { Part = part });
            }

            World3D? world3D = null;
            foreach (var copy in _copies)
            {
                if (Source(copy.Part) is { } source && source.IsInsideTree())
                {
                    world3D = source.GetWorld3D();
                    break;
                }
            }
            if (world3D == null)
                return false;
            if (!_instances[0].IsValid || _scenario != world3D.Scenario)
                MakeCard(world3D);

            if (!Bounds(member, _copies, _owner._eyeMask, out var low, out var high, out var anchor, out float link))
                return false;
            // Rigid parts (a hat, a sword in a hand) can reach past the bones' margin; their boxes are exact. One far
            // bigger than the body is some helper that draws nothing much, and one standing off on its own is not
            // with the body; either would shrink the body to a speck.
            float body = (high - low).Length();
            Vector3 bodyLow = low - new Vector3(link, link, link), bodyHigh = high + new Vector3(link, link, link);
            foreach (var copy in _copies)
            {
                if (copy.Part is not MeshRenderer || Source(copy.Part) is not { } rigid || !rigid.IsInsideTree() || rigid.Mesh == null)
                    continue;
                var box = rigid.GetAabb();
                var frame = Frame(copy.Part, rigid);
                Vector3 partLow = frame * box.GetEndpoint(0), partHigh = partLow;
                for (int i = 1; i < 8; i++)
                {
                    var corner = frame * box.GetEndpoint(i);
                    partLow = Least(partLow, corner);
                    partHigh = Most(partHigh, corner);
                }
                if ((partHigh - partLow).Length() > body * 1.5f || !Inside((partLow + partHigh) * 0.5f, bodyLow, bodyHigh))
                    continue;
                low = Least(low, partLow);
                high = Most(high, partHigh);
            }
            var center = (low + high) * 0.5f;
            var toward = center - eye;
            float distance = toward.Length();
            if (distance < 0.5f)
                return false;
            var look = toward / distance;
            var up = Mathf.Abs(look.Y) > 0.98f ? Vector3.Forward : Vector3.Up;
            var right = look.Cross(up).Normalized();
            var cameraUp = right.Cross(look).Normalized();
            float halfWidth = 0f, halfHeight = 0f;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) != 0 ? high.X : low.X, (i & 2) != 0 ? high.Y : low.Y, (i & 4) != 0 ? high.Z : low.Z) - center;
                halfWidth = Mathf.Max(halfWidth, Mathf.Abs(corner.Dot(right)));
                halfHeight = Mathf.Max(halfHeight, Mathf.Abs(corner.Dot(cameraUp)));
            }
            float half = Mathf.Max(halfHeight, halfWidth / Aspect);
            float radius = (high - low).Length() * 0.5f;

            bool any = false;
            foreach (var copy in _copies)
                any |= ShowCopy(copy, world3D.Scenario, _owner._eyeMask);
            if (!any)
                return false;

            int next = _shown < 0 ? 0 : 1 - _shown;
            var rig = _rigs[next] ??= MakeRig(next);
            int rows = Mathf.Clamp(Mathf.CeilToInt(2f * half * _owner._focal / distance * RowsPerPixel / RowStep) * RowStep, MinRows, MaxRows);
            var pixels = new Vector2I(rows / 2, rows);
            if (rig.Viewport.Size != pixels)
                rig.Viewport.Size = pixels;
            if (rig.Viewport.Msaa3D != _owner._msaa)
                rig.Viewport.Msaa3D = _owner._msaa;
            var environment = _owner.CaptureEnvironment(world3D);
            if (!ReferenceEquals(rig.Viewport.World3D, world3D))
                rig.Viewport.World3D = world3D;
            if (!ReferenceEquals(rig.Camera.Environment, environment))
                rig.Camera.Environment = environment;
            // Filmed from where the viewer stands, so a material that does something with the eye's distance (a fade,
            // a glow that comes on up close) does the same in the picture; near and far hug the body.
            rig.Camera.Size = half * 2f;
            rig.Camera.Near = Mathf.Max(0.05f, distance - radius - 0.5f);
            rig.Camera.Far = distance + radius + 0.5f;
            rig.Camera.GlobalTransform = new Transform3D(Basis.Identity, eye).LookingAt(center, up);
            rig.Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;

            // The first picture shows at once; every later one blends over the last.
            if (_shown < 0)
                _blend = 1f;
            else
                _blend = 0f;
            _shown = next;
            _pictured = true;
            member.HasPicture = true;
            _shapes[next] = new Shape
            {
                CenterOffset = center - anchor,
                HalfHeight = half,
                HalfWidth = half * Aspect,
                Radius = radius,
                Valid = true,
            };
            return any;
        }

        // Where the skin is: each bone's box of the vertices it moves, as the renderer keeps them for culling, posed by
        // that bone now, so fur and skirts and big soft tails are inside however far they stand off the bones. A
        // body without them falls back on its main skeleton with a margin. The tracked head and the floor under it
        // seed the body, which grows out nearest first; a box standing off on its own is not drawn with the body
        // (a bone left at the world's origin, a helper parked out of the way): it would stretch the picture over
        // empty space and shrink the avatar in it to a speck.
        private static readonly List<(float Distance, Vector3 Low, Vector3 High)> s_boxes = new();

        private static bool Bounds(AvatarCrowd.Member member, List<Copy> copies, uint eyeMask,
            out Vector3 low, out Vector3 high, out Vector3 anchor, out float link)
        {
            anchor = Anchor(member, out var top, out bool headed);
            float stature = headed ? Mathf.Clamp(top.Y - anchor.Y, 0.3f, 50f) : 1.7f;
            // How far a box may stand from the body grown so far and still be part of it.
            link = Mathf.Max(0.5f, stature * 0.6f);
            low = Least(anchor, top);
            high = Most(anchor, top);
            s_boxes.Clear();
            foreach (var copy in copies)
            {
                if (copy.Part is not SkinnedMeshRenderer || Source(copy.Part) is not { } source || !source.IsInsideTree()
                    || source.Mesh == null || (source.Layers & eyeMask) == 0)
                    continue;
                var skin = source.GetSkinReference();
                var skeleton = skin != null ? skin.GetSkeleton() : default;
                if (!skeleton.IsValid)
                    continue;
                int boneCount = RenderingServer.SkeletonGetBoneCount(skeleton);
                var frame = source.GlobalTransform;
                foreach (var (bone, box) in BoneBoxes(source.Mesh.GetRid()))
                {
                    if (bone >= boneCount)
                        continue;
                    var world = frame * RenderingServer.SkeletonBoneGetTransform(skeleton, bone);
                    Vector3 boxLow = world * box.GetEndpoint(0), boxHigh = boxLow;
                    for (int i = 1; i < 8; i++)
                    {
                        var corner = world * box.GetEndpoint(i);
                        boxLow = Least(boxLow, corner);
                        boxHigh = Most(boxHigh, corner);
                    }
                    s_boxes.Add((((boxLow + boxHigh) * 0.5f - anchor).LengthSquared(), boxLow, boxHigh));
                }
            }
            bool skinned = s_boxes.Count > 0;
            var skeletonBuilder = member.MainSkeleton;
            if (!skinned && skeletonBuilder != null && !skeletonBuilder.IsDestroyed)
            {
                var bones = skeletonBuilder.BoneSlots;
                int count = bones.Count;
                for (int i = 0; i < count; i++)
                {
                    var bone = bones[i];
                    if (bone == null || bone.IsDestroyed)
                        continue;
                    var p = ToGodot(bone.GlobalPosition);
                    s_boxes.Add(((p - anchor).LengthSquared(), p, p));
                }
            }
            bool any = headed;
            s_boxes.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
            var grow = new Vector3(link, link, link);
            foreach (var (_, boxLow, boxHigh) in s_boxes)
            {
                if (!Overlaps(boxLow, boxHigh, low - grow, high + grow))
                    continue;
                low = Least(low, boxLow);
                high = Most(high, boxHigh);
                any = true;
            }
            s_boxes.Clear();
            if (!any)
                return false;
            float size = Mathf.Max(high.Y - low.Y, 0.5f);
            float margin = (skinned ? SkinMargin : BoneMargin) * Mathf.Clamp(size / 1.7f, 0.3f, 4f);
            low -= new Vector3(margin, margin, margin);
            high += new Vector3(margin, margin, margin);
            return true;
        }

        // Each bone's box of the vertices it moves, in the mesh's own space, read once a mesh from the render server.
        private static readonly Dictionary<Rid, (int Bone, Aabb Box)[]> s_boneBoxes = new();

        private static (int Bone, Aabb Box)[] BoneBoxes(Rid mesh)
        {
            if (s_boneBoxes.TryGetValue(mesh, out var known))
                return known;
            if (s_boneBoxes.Count > 512)
                s_boneBoxes.Clear();
            var boxes = new List<(int, Aabb)>();
            int surfaces = RenderingServer.MeshGetSurfaceCount(mesh);
            for (int s = 0; s < surfaces; s++)
            {
                var surface = RenderingServer.MeshGetSurface(mesh, s);
                if (!surface.TryGetValue("bone_aabbs", out var value) || value.VariantType != Variant.Type.Array)
                    continue;
                var array = value.AsGodotArray();
                for (int b = 0; b < array.Count; b++)
                {
                    var box = array[b].AsAabb();
                    // A bone no vertex follows keeps an empty box.
                    if (box.Size.X < 0f || box.Size.Y < 0f || box.Size.Z < 0f || (box.Size == Vector3.Zero && box.Position == Vector3.Zero))
                        continue;
                    boxes.Add((b, box));
                }
            }
            var result = boxes.ToArray();
            s_boneBoxes[mesh] = result;
            return result;
        }

        private static bool Overlaps(Vector3 aLow, Vector3 aHigh, Vector3 bLow, Vector3 bHigh)
            => aLow.X <= bHigh.X && aHigh.X >= bLow.X && aLow.Y <= bHigh.Y && aHigh.Y >= bLow.Y && aLow.Z <= bHigh.Z && aHigh.Z >= bLow.Z;

        // Where the body stands: on the root's floor under the tracked head. In a room the root is the play space,
        // and the body can be metres from it.
        private static Vector3 Anchor(AvatarCrowd.Member member, out Vector3 top, out bool headed)
        {
            var floor = ToGodot(member.Root.GlobalPosition);
            var head = member.User?.Root?.HeadSlot;
            headed = head != null && !head.IsDestroyed;
            top = headed ? ToGodot(head!.GlobalPosition) : floor;
            return headed ? new Vector3(top.X, floor.Y, top.Z) : floor;
        }

        private static bool Inside(Vector3 p, Vector3 low, Vector3 high)
            => p.X >= low.X && p.Y >= low.Y && p.Z >= low.Z && p.X <= high.X && p.Y <= high.Y && p.Z <= high.Z;

        private Rig MakeRig(int index)
        {
            var viewport = new SubViewport
            {
                Name = "AvatarPicture",
                Size = new Vector2I(MinRows / 2, MinRows),
                TransparentBg = true,
                UseHdr2D = true,
                Msaa3D = Viewport.Msaa.Disabled,
                // Every mesh at its full detail: the picture is small and taken now and then.
                MeshLodThreshold = 0f,
                ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled,
                PositionalShadowAtlasSize = 0,
                UseOcclusionCulling = false,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
                RenderTargetClearMode = SubViewport.ClearMode.Always,
                GuiDisableInput = true,
                HandleInputLocally = false,
                AudioListenerEnable3D = false,
            };
            var camera = new Camera3D
            {
                Name = "Camera",
                Projection = Camera3D.ProjectionType.Orthogonal,
                KeepAspect = Camera3D.KeepAspectEnum.Height,
                CullMask = (uint)RenderHelper.HIDDEN_LAYER,
                Current = true,
            };
            viewport.AddChild(camera);
            _owner.AddChild(viewport);
            var material = _materials[index] ??= new ShaderMaterial { Shader = CardShader() };
            material.SetShaderParameter(PictureName, viewport.GetTexture());
            return new Rig { Viewport = viewport, Camera = camera };
        }

        private void MakeCard(World3D world3D)
        {
            FreeCard();
            s_quad ??= new QuadMesh { Size = Vector2.One };
            _scenario = world3D.Scenario;
            for (int i = 0; i < 2; i++)
            {
                var material = _materials[i] ??= new ShaderMaterial { Shader = CardShader() };
                var instance = RenderingServer.InstanceCreate2(s_quad.GetRid(), _scenario);
                RenderingServer.InstanceSetLayerMask(instance, (uint)RenderHelper.PUBLIC_LAYER);
                RenderingServer.InstanceGeometrySetCastShadowsSetting(instance, RenderingServer.ShadowCastingSetting.Off);
                RenderingServer.InstanceGeometrySetMaterialOverride(instance, material.GetRid());
                RenderingServer.InstanceSetVisible(instance, false);
                _instances[i] = instance;
                _visible[i] = false;
            }
        }

        public void Draw(AvatarCrowd.Member member, Vector3 eye, float dt)
        {
            if (!_pictured || !_instances[0].IsValid || member.Root.IsDestroyed)
            {
                HideCard();
                return;
            }
            if (_blend < 1f)
                _blend = Mathf.Min(1f, _blend + dt / BlendSeconds);
            var anchor = Anchor(member, out _, out _);
            for (int i = 0; i < 2; i++)
            {
                // The newest picture owns the slices below the blend, the one before it the rest.
                bool newest = i == _shown;
                float low = newest ? 0f : _blend, high = newest ? _blend : 1f;
                if (high <= low || !_shapes[i].Valid || _rigs[i] == null || !DrawQuad(i, anchor, eye, member.Fade, low, high))
                    ShowQuad(i, false);
            }
        }

        private bool DrawQuad(int index, Vector3 anchor, Vector3 eye, float fade, float low, float high)
        {
            ref var shape = ref _shapes[index];
            var center = anchor + shape.CenterOffset;
            var toward = eye - center;
            float distance = toward.Length();
            if (distance < 0.05f)
                return false;
            var normal = toward / distance;
            // Pulled toward the viewer by the avatar's reach while the body still shows, so no part of it pokes
            // through. With the body gone it stands as far off as the body itself (the tracked head, not the middle
            // of the box, which a long tail or a wing drags about), so a neighbour in front or behind meets it as it
            // met the body. Scaled with its distance either way, so it fills the same part of the view.
            float front = distance - Mathf.Min(shape.Radius, distance * 0.5f);
            float body = Mathf.Clamp((anchor + new Vector3(0f, shape.CenterOffset.Y, 0f) - eye).Length(), front, distance + shape.Radius);
            float depth = Mathf.Lerp(front, body, fade);
            float pull = distance - depth;
            float scale = depth / distance;
            var up = Mathf.Abs(normal.Y) > 0.98f ? Vector3.Forward : Vector3.Up;
            var right = up.Cross(normal).Normalized();
            var cardUp = normal.Cross(right);
            var basis = new Basis(right * (shape.HalfWidth * 2f * scale), cardUp * (shape.HalfHeight * 2f * scale), normal);
            var instance = _instances[index];
            RenderingServer.InstanceSetTransform(instance, new Transform3D(basis, center + normal * pull));
            RenderingServer.InstanceGeometrySetShaderParameter(instance, FadeName, fade);
            RenderingServer.InstanceGeometrySetShaderParameter(instance, SliceLowName, low);
            RenderingServer.InstanceGeometrySetShaderParameter(instance, SliceHighName, high);
            ShowQuad(index, true);
            return true;
        }

        private void ShowQuad(int index, bool on)
        {
            if (_visible[index] == on || !_instances[index].IsValid)
                return;
            RenderingServer.InstanceSetVisible(_instances[index], on);
            _visible[index] = on;
        }

        public void HideCard()
        {
            ShowQuad(0, false);
            ShowQuad(1, false);
        }

        private static MeshInstance3D? Source(Lumora.Core.Component part)
        {
            if (part.IsDestroyed)
                return null;
            if (part is SkinnedMeshRenderer skinned)
                return (skinned.Hook as SkinnedMeshHook)?.PictureSource;
            if (part is MeshRenderer renderer && renderer.Enabled && (renderer.Slot?.IsActive ?? false)
                && renderer.Hook is IDrawnInstance drawn && drawn.DrawnInstance is { } instance && IsInstanceValid(instance))
                return instance;
            return null;
        }

        // Where a part is drawn now. Somebody else's accessories may be waiting for a pose frame (AvatarPlacement)
        // with their nodes a step behind, so a plain renderer's slot is asked; a skinned one is drawn where its node is.
        private static Transform3D Frame(Lumora.Core.Component part, MeshInstance3D source)
            => part is MeshRenderer && part.Slot is { IsDestroyed: false } slot && SlotHook.TryGetDrawnTransform(slot, out var drawn)
                ? drawn : source.GlobalTransform;

        // Only what the viewer's eyes draw: a part on a band they skip (a mirror-only prop, a twin) stays out. Its
        // visibility range (a level of detail, a draw distance) goes on the copy, and the picture camera stands
        // where the eyes do, so the same level is drawn.
        private static bool ShowCopy(Copy copy, Rid scenario, uint eyeMask)
        {
            var source = Source(copy.Part);
            var mesh = source?.Mesh;
            if (source == null || mesh == null || !source.IsInsideTree() || (source.Layers & eyeMask) == 0)
            {
                if (copy.Instance.IsValid)
                    RenderingServer.InstanceSetVisible(copy.Instance, false);
                return false;
            }
            var meshRid = mesh.GetRid();
            var skin = source.GetSkinReference();
            var skeleton = skin != null ? skin.GetSkeleton() : default;
            if (!copy.Instance.IsValid || copy.Mesh != meshRid || copy.Skeleton != skeleton)
            {
                if (copy.Instance.IsValid)
                    RenderingServer.FreeRid(copy.Instance);
                copy.Instance = RenderingServer.InstanceCreate2(meshRid, scenario);
                copy.Mesh = meshRid;
                copy.Skeleton = skeleton;
                RenderingServer.InstanceSetLayerMask(copy.Instance, (uint)RenderHelper.HIDDEN_LAYER);
                RenderingServer.InstanceGeometrySetCastShadowsSetting(copy.Instance, RenderingServer.ShadowCastingSetting.Off);
                RenderingServer.InstanceSetExtraVisibilityMargin(copy.Instance, 2f);
                if (skeleton.IsValid)
                    RenderingServer.InstanceAttachSkeleton(copy.Instance, skeleton);
            }
            var instance = copy.Instance;
            RenderingServer.InstanceSetTransform(instance, Frame(copy.Part, source));
            RenderingServer.InstanceGeometrySetVisibilityRange(instance, source.VisibilityRangeBegin, source.VisibilityRangeEnd,
                source.VisibilityRangeBeginMargin, source.VisibilityRangeEndMargin, (RenderingServer.VisibilityRangeFadeMode)source.VisibilityRangeFadeMode);
            RenderingServer.InstanceGeometrySetTransparency(instance, source.Transparency);
            int surfaces = mesh.GetSurfaceCount();
            for (int i = 0; i < surfaces; i++)
            {
                var material = source.GetActiveMaterial(i);
                RenderingServer.InstanceSetSurfaceOverrideMaterial(instance, i,
                    material == null ? default : ReadsScreen(material) ? Nothing() : material.GetRid());
            }
            var overlay = source.MaterialOverlay;
            RenderingServer.InstanceGeometrySetMaterialOverlay(instance, overlay != null && !ReadsScreen(overlay) ? overlay.GetRid() : default);
            int shapes = source.GetBlendShapeCount();
            for (int i = 0; i < shapes; i++)
                RenderingServer.InstanceSetBlendShapeWeight(instance, i, source.GetBlendShapeValue(i));
            RenderingServer.InstanceSetVisible(instance, true);
            return true;
        }

        // A material that draws what is behind it on screen (glass, a refracting orb) has nothing behind it in a
        // picture but the empty clear, and comes out as a hole or a glare. That surface is left out of the picture,
        // so the card is clear there and the world behind it shows through, which is near enough what glass does.
        private static readonly Dictionary<ulong, bool> s_readsScreen = new();
        private static ShaderMaterial? s_nothing;

        private static bool ReadsScreen(Material material)
        {
            Material? at = material;
            for (int depth = 0; at != null && depth < 4; depth++, at = at.NextPass)
            {
                if (at is not ShaderMaterial { Shader: { } shader })
                    continue;
                ulong id = shader.GetInstanceId();
                if (!s_readsScreen.TryGetValue(id, out bool reads))
                {
                    reads = CodeReadsScreen(shader.Code ?? string.Empty, 0);
                    s_readsScreen[id] = reads;
                }
                if (reads)
                    return true;
            }
            return false;
        }

        private static bool CodeReadsScreen(string code, int depth)
        {
            if (code.Contains("hint_screen_texture", System.StringComparison.Ordinal) || code.Contains("SCREEN_TEXTURE", System.StringComparison.Ordinal))
                return true;
            if (depth >= 3)
                return false;
            int at = 0;
            while ((at = code.IndexOf("#include", at, System.StringComparison.Ordinal)) >= 0)
            {
                int open = code.IndexOf('"', at);
                int close = open >= 0 ? code.IndexOf('"', open + 1) : -1;
                if (open < 0 || close < 0)
                    break;
                string path = code.Substring(open + 1, close - open - 1);
                at = close + 1;
                if (ResourceLoader.Exists(path) && ResourceLoader.Load<ShaderInclude>(path) is { } included
                    && CodeReadsScreen(included.Code ?? string.Empty, depth + 1))
                    return true;
            }
            return false;
        }

        private static Rid Nothing()
        {
            s_nothing ??= new ShaderMaterial
            {
                Shader = new Shader { Code = "shader_type spatial; render_mode unshaded; void fragment() { discard; }" },
            };
            return s_nothing.GetRid();
        }

        public void HideCopies()
        {
            foreach (var copy in _copies)
            {
                if (copy.Instance.IsValid)
                    RenderingServer.InstanceSetVisible(copy.Instance, false);
            }
        }

        private void FreeCopies()
        {
            foreach (var copy in _copies)
            {
                if (copy.Instance.IsValid)
                    RenderingServer.FreeRid(copy.Instance);
            }
            _copies.Clear();
        }

        private void FreeCard()
        {
            for (int i = 0; i < 2; i++)
            {
                if (_instances[i].IsValid)
                    RenderingServer.FreeRid(_instances[i]);
                _instances[i] = default;
                _visible[i] = false;
            }
        }

        public void Free()
        {
            FreeCopies();
            FreeCard();
            for (int i = 0; i < _rigs.Length; i++)
            {
                var rig = _rigs[i];
                if (rig != null && IsInstanceValid(rig.Viewport))
                    rig.Viewport.QueueFree();
                _rigs[i] = null!;
            }
            _shown = -1;
            _pictured = false;
            _shapes[0] = _shapes[1] = default;
        }
    }
}
