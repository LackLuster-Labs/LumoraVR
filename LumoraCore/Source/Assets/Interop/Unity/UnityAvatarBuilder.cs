// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using Lumora.Core.Components;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Logging;
using Lumora.Core.Math;

namespace Lumora.Core.Assets.Interop.Unity;

// What a Unity avatar's descriptor and bone scripts said, put onto the imported model: the viewpoint,
// which blendshape is which viseme, and every jiggle chain with its colliders as our dynamic bones.
//
// Two conversions run through all of it. Unity mirrors a model's X axis on import and we do not, so
// every position flips X and every rotation flips its Y and Z. And Unity bakes the file's unit into the
// model's lengths while we keep it as a scale on the model's root, so a length Unity stored in a node's
// local space is that much longer in ours: divide by the root's scale. Forces and gravity are world space
// and only flip.
//
// Dynamic Bone settings carry across by their meaning: it steps once per 60 Hz frame by fractions, ours
// integrates continuously, so elasticity and gravity scale by 60 squared (the rest pose sag stays the
// same) and the per-frame damping becomes a decay rate. PhysBones go onto the same scale by what each
// setting means; see UnityAvatarPhysBones. -xlinka
public static partial class UnityAvatarBuilder
{
    // Constraints come across for anything; the descriptor, bones, links, switches and gestures only for an
    // avatar. Links go before constraints and switches so what they move is already where it hangs.
    public static void Apply(UnityImportCandidate candidate, Slot model, Slot? nodeRoot, UnityModelMeta meta, UnityImportResult result,
        bool asAvatar = true, Func<string, MaterialProvider?>? materialFor = null, UnityVrcPlan? vrc = null,
        Func<string, UnityAnimationClip?>? clipFor = null, LocalDB? localDB = null, Uri? animatorUri = null, Assembly? assembly = null)
    {
        float unit = assembly?.Unit ?? MathF.Abs(model.LocalScale.Value.x);
        if (unit < 1e-6f)
            unit = 1f;
        var nodes = assembly?.Nodes ?? new NodeIndex(nodeRoot, meta, 1f / unit, candidate.NodesByPath);
        MoveRehungNodes(candidate, nodes);
        if (assembly == null)
            ApplyPoseOverrides(candidate, nodes, unit);
        if (asAvatar)
            ApplyAvatar(candidate, model, nodes, meta, result, unit);
        if (asAvatar) ApplyFace(candidate, model, nodes, assembly, result);
        var features = new FeatureBuilder(candidate, model, nodes, unit, result, materialFor, clipFor, localDB);
        features.Arrange(meta, asAvatar, vrc);
        features.Constraints();
        features.Audio();
        if (asAvatar)
        {
            // Zones before the menu, so a switch that turns contacts on and off finds them.
            features.ContactZones();
            bool live = features.LiveAnimator(vrc, animatorUri);
            if (!live)
            {
                features.Menu(vrc);
                features.Contacts(vrc);
            }
            features.Sliders(vrc, live);
            features.Toggles();
            features.Gestures();
            features.BuildTools(vrc);
            features.BodyAnimators(vrc);
            features.SliderInputs();
            features.ContactInputs();
        }
        features.DropIdleConstraints();
        features.Finish();
    }

    // What the placement changed about the model's own nodes: a bone made longer, a part scaled away. The
    // root's pose is where the project put the model and stays with the import.
    //
    // Two traps. A placement may change only some of a rotation's four numbers, and the file's rotation and
    // ours can be the same turn with every sign flipped, so the changed numbers go onto whichever sign of
    // ours they belong to: the one that lands nearest the pose the node has, since a placement nudges a
    // pose rather than flipping it. And the reader can slip pivot helpers above a bone, so a bone's pose in
    // Unity's terms is taken against its nearest real parent, not the helper. -xlinka
    private static void ApplyPoseOverrides(UnityImportCandidate candidate, NodeIndex nodes, float unit)
        => ApplyPoseOverrides(candidate.TransformOverrides, nodes.ById, unit);

    private static void ApplyPoseOverrides(Dictionary<long, Dictionary<string, float>> overrides, Func<long, Slot?> byId, float unit)
    {
        if (overrides.Count == 0)
            return;
        var root = new HashSet<long>
        {
            UnityModelMeta.ObjectId("Transform", "", true), UnityModelMeta.ObjectId("Transform", "", false),
            UnityModelMeta.GameObjectId("", true), UnityModelMeta.GameObjectId("", false),
        };
        int applied = 0, missed = 0;
        foreach (var (id, fields) in overrides)
        {
            if (root.Contains(id))
                continue;
            if (byId(id) is not { } slot || slot.Parent == null)
            {
                missed++;
                continue;
            }
            applied++;

            var frame = slot.Parent;
            while (frame.Parent != null && (frame.SlotName.Value ?? "").Contains("$AssimpFbx$", StringComparison.Ordinal))
                frame = frame.Parent;
            bool direct = ReferenceEquals(frame, slot.Parent);
            var localPosition = direct ? slot.LocalPosition.Value : frame.GlobalPointToLocal(slot.GlobalPosition);
            var localRotation = direct ? slot.LocalRotation.Value : frame.GlobalRotationToLocal(slot.GlobalRotation);

            // Into Unity's terms.
            var p = localPosition * unit;
            var position = new float3(-p.x, p.y, p.z);
            var have = new floatQ(localRotation.x, -localRotation.y, -localRotation.z, localRotation.w);
            var scale = slot.LocalScale.Value;
            bool moved = false, sized = false;
            float? qx = null, qy = null, qz = null, qw = null;
            foreach (var (field, value) in fields)
            {
                switch (field)
                {
                    case "m_LocalPosition.x": position.x = value; moved = true; break;
                    case "m_LocalPosition.y": position.y = value; moved = true; break;
                    case "m_LocalPosition.z": position.z = value; moved = true; break;
                    case "m_LocalRotation.x": qx = value; break;
                    case "m_LocalRotation.y": qy = value; break;
                    case "m_LocalRotation.z": qz = value; break;
                    case "m_LocalRotation.w": qw = value; break;
                    case "m_LocalScale.x": scale.x = value; sized = true; break;
                    case "m_LocalScale.y": scale.y = value; sized = true; break;
                    case "m_LocalScale.z": scale.z = value; sized = true; break;
                }
            }

            bool turned = qx.HasValue || qy.HasValue || qz.HasValue || qw.HasValue;
            var rotation = have;
            if (turned)
            {
                var asIs = Fill(have, qx, qy, qz, qw);
                var flipped = Fill(new floatQ(-have.x, -have.y, -have.z, -have.w), qx, qy, qz, qw);
                rotation = MathF.Abs(floatQ.Dot(asIs, have)) >= MathF.Abs(floatQ.Dot(flipped, have)) ? asIs : flipped;
            }

            // And back.
            if (moved)
            {
                var ours = new float3(-position.x, position.y, position.z) / unit;
                if (direct)
                    slot.LocalPosition.Value = ours;
                else
                    slot.GlobalPosition = frame.LocalPointToGlobal(ours);
            }
            if (turned)
            {
                var ours = new floatQ(rotation.x, -rotation.y, -rotation.z, rotation.w);
                if (direct)
                    slot.LocalRotation.Value = ours;
                else
                    slot.GlobalRotation = frame.GlobalRotation * ours;
            }
            if (sized)
                slot.LocalScale.Value = scale;
        }
        Logger.Debug($"UnityPackage: pose overrides on {applied} nodes, {missed} not found");
    }

    private static floatQ Fill(floatQ q, float? x, float? y, float? z, float? w)
        => new floatQ(x ?? q.x, y ?? q.y, z ?? q.z, w ?? q.w).Normalized;

    // An unpacked prefab can hang one of the model's own nodes under another object than the file does: a
    // hair armature dragged onto the head bone. Its path then names no node of ours and it is found by its
    // name alone, still under the file's parent, so the hair stayed put while the head moved. It goes under
    // the object the project put it under, where it stands (dragging keeps the world pose). -xlinka
    private static void MoveRehungNodes(UnityImportCandidate candidate, NodeIndex nodes)
    {
        // Only nodes of the model's own, looked up without making anything: a renamed object is not one.
        Slot? Own(UnityNodeRef node)
        {
            if (node.Part != null || node.Path == null)
                return null;
            if (nodes.ByPath(node.Path) is { } byPath)
                return byPath;
            int slash = node.Path.LastIndexOf('/');
            return nodes.ByName(slash >= 0 ? node.Path[(slash + 1)..] : node.Path) is { } byName && !nodes.IsAdded(byName) ? byName : null;
        }
        // A node the hierarchy still has at the file's own path was not moved, whatever else shares its name
        // (an empty the project named after a bone to hold its jiggle settings); nor is a name used twice.
        var stays = new HashSet<Slot>();
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in candidate.NodesByPath.Values)
        {
            if (node.Path == null)
                continue;
            if (nodes.ByPath(node.Path) is { } kept)
                stays.Add(kept);
            var leaf = node.Path[(node.Path.LastIndexOf('/') + 1)..];
            names[leaf] = names.GetValueOrDefault(leaf) + 1;
        }
        int moved = 0;
        foreach (var node in candidate.NodesByPath.Values)
        {
            if (node.Path == null || node.Parent == null || nodes.ByPath(node.Path) != null)
                continue;
            if (names.GetValueOrDefault(node.Path[(node.Path.LastIndexOf('/') + 1)..]) != 1)
                continue;
            if (Own(node) is not { } slot || stays.Contains(slot) || Own(node.Parent) is not { } parent)
                continue;
            parent = nodes.Inside(parent);
            var own = slot.Parent;
            while (own?.Parent != null && (own.SlotName.Value ?? "").Contains("$AssimpFbx$", StringComparison.Ordinal))
                own = own.Parent;
            if (own == null || ReferenceEquals(own, parent) || ReferenceEquals(parent, slot) || parent.IsDescendantOf(slot))
                continue;
            slot.SetParent(parent, preserveGlobalTransform: true);
            moved++;
        }
        if (moved > 0)
            Logger.Debug($"UnityPackage: {moved} model nodes moved under the objects the project hung them from");
    }

    private static void ApplyAvatar(UnityImportCandidate candidate, Slot model, NodeIndex nodes, UnityModelMeta meta, UnityImportResult result, float unit)
    {

        if (candidate.ViewPosition is { } view)
        {
            var place = model.FindChild("AvatarReferences", recursive: false)?.FindChild("View", recursive: false);
            if (place?.Parent is { } refs)
            {
                // The descriptor's view is right/up/front of the avatar, in units scaled by the descriptor
                // object, which the import leaves off. The references frame was already turned to face the
                // body, so the body's front is read off the rig here rather than assumed to be an axis. -xlinka
                float3 up = refs.GlobalDirectionToLocal(float3.Up);
                up = up.LengthSquared > 1e-8f ? up.Normalized : float3.Up;
                float3 front = new float3(0f, 0f, -1f);
                var rig = model.GetComponent<HumanoidRig>() ?? model.GetComponentInChildren<HumanoidRig>();
                if (rig?.GuessForwardAxis() is { } forward)
                {
                    float3 f = refs.GlobalDirectionToLocal(forward);
                    f -= up * float3.Dot(f, up);
                    if (f.LengthSquared > 1e-8f)
                        front = f.Normalized;
                }
                float3 right = float3.Cross(front, up).Normalized;
                place.LocalPosition.Value = (right * view.x + up * view.y + front * view.z) / (unit * candidate.ViewRootScale);
                result.Notes.Add("Viewpoint taken from the avatar descriptor.");
            }
        }

        if (candidate.VisemeShapes.Count > 0 && candidate.VisemeMesh != null)
        {
            var meshSlot = nodes.Resolve(candidate.VisemeMesh);
            var mesh = meshSlot?.GetComponent<SkinnedMeshRenderer>() ?? meshSlot?.GetComponentInChildren<SkinnedMeshRenderer>();
            if (mesh != null)
            {
                var driver = model.GetComponent<VisemeWeightDriver>() ?? model.AttachComponent<VisemeWeightDriver>();
                driver.ShapeMesh.Target = mesh;
                driver.ShapeNames.Clear();
                foreach (var shape in candidate.VisemeShapes)
                    driver.ShapeNames.Add(shape);
                result.VisemesMapped = true;
            }
        }

        var colliders = new Dictionary<long, IDynamicBoneCollider?>();
        var heldRoots = HeldRoots(candidate, nodes);
        foreach (var part in candidate.Chains)
        {
            try
            {
                if (BuildChain(candidate, part, nodes, colliders, unit, result, heldRoots))
                    result.Chains++;
                else
                    result.ChainsSkipped++;
            }
            catch (Exception ex)
            {
                result.ChainsSkipped++;
                Logger.Warn($"UnityPackage: a bone chain on '{part.Node.Name}' could not be built: {ex.Message}");
            }
        }
        foreach (var collider in colliders.Values)
            if (collider != null)
                result.Colliders++;

        foreach (var link in candidate.Links)
        {
            try
            {
                if (ApplyLink(candidate, link, nodes, meta))
                    result.Links++;
                else
                    result.LinksSkipped++;
            }
            catch (Exception ex)
            {
                result.LinksSkipped++;
                Logger.Warn($"UnityPackage: an armature link on '{link.Node.Name}' could not be applied: {ex.Message}");
            }
        }
    }

    // A VRCFury armature link: the prop's bones ride the avatar bone it names (a humanoid bone through the
    // model's humanoid table, or an object), merged bone for bone where names match and hung whole off
    // the target where they do not. Our ArmatureLink does exactly that and stays on the prop.
    private static bool ApplyLink(UnityImportCandidate candidate, UnityComponentRef link, NodeIndex nodes, UnityModelMeta meta)
    {
        var data = link.Data;
        if (data == null)
            return false;
        long propId = data["propBone"]?.Ref.FileId ?? 0;
        var propRef = propId != 0 ? candidate.ResolveObject(propId) : link.Node;
        var prop = propRef != null ? nodes.Resolve(propRef) : null;
        if (prop == null)
            return false;

        Slot? target = null;
        if (data["linkTo"] is UnitySeq targets)
        {
            foreach (var item in targets.Items)
            {
                Slot? at = null;
                var offset = item["offset"]?.Text ?? "";
                if ((item["useObj"]?.Int() ?? 0) != 0)
                {
                    long obj = item["obj"]?.Ref.FileId ?? 0;
                    at = obj != 0 && candidate.ResolveObject(obj) is { } r ? nodes.Resolve(r) : null;
                }
                else if ((item["useBone"]?.Int(1) ?? 1) != 0)
                {
                    if (meta.HumanBone(item["bone"]?.Int() ?? 0) is { } bone)
                        at = nodes.ByName(bone);
                }
                else if (offset.Length > 0)
                {
                    // Neither a bone nor an object: the offset is a path from the avatar's root.
                    at = nodes.AtPath(offset);
                    offset = "";
                }
                if (at != null && offset.Length > 0)
                    at = Descend(at, offset);
                if (at != null)
                {
                    target = at;
                    break;
                }
            }
        }
        if (target == null || ReferenceEquals(target, prop) || target.IsDescendantOf(prop))
            return false;

        bool alignPosition = (data["alignPosition"]?.Int() ?? 0) != 0;
        bool alignRotation = (data["alignRotation"]?.Int() ?? 0) != 0;
        bool alignScale = (data["alignScale"]?.Int() ?? 0) != 0;
        var linker = prop.AttachComponent<ArmatureLink>();
        linker.Avatar.Target = target;
        linker.Clothing.Target = prop;
        linker.KeepPose.Value = !(alignPosition && alignRotation);
        LinkOptions(linker, data, prop, target);
        linker.Link();
        if (ReferenceEquals(prop.Parent, target))
        {
            if (alignPosition)
                prop.LocalPosition.Value = float3.Zero;
            if (alignRotation)
                prop.LocalRotation.Value = floatQ.Identity;
            if (alignScale)
                prop.LocalScale.Value = float3.One;
        }
        return true;
    }

    private static Slot? Descend(Slot from, string path)
    {
        var current = from;
        foreach (var name in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current.Children.FirstOrDefault(c => c.SlotName.Value == name);
            if (current == null)
                return null;
        }
        return current;
    }

    private static bool BuildChain(UnityImportCandidate candidate, UnityComponentRef part, NodeIndex nodes,
        Dictionary<long, IDynamicBoneCollider?> colliders, float unit, UnityImportResult result, HashSet<Slot> heldRoots)
    {
        var doc = part.Doc;
        bool physBone = part.Type == UnityComponentRef.Kind.PhysBone;
        var root = ChainRoot(candidate, part, nodes);
        if (root == null)
            return false;

        // The chain sits where the project's script sat, so switching that object off stops it the way it
        // stopped the script; a chain kept on its root bone ran even with its object switched off.
        var owner = nodes.Resolve(part.Node) ?? root;
        var chain = owner.AttachComponent<DynamicBoneChain>();
        chain.Root.Target = root;
        chain.SetupFromChildren();
        // Objects the project hung inside the chain, and models placed there, ride it rather than join it.
        for (int i = chain.Bones.Count - 1; i >= 0; i--)
            if (i < chain.Bones.Count && chain.Bones[i] is { } bone && !ReferenceEquals(bone, root) && nodes.IsAdded(bone))
                RemoveSubtree(chain, bone);
        chain.Enabled.Value = part.Enabled;
        Script(result, owner, chain);

        var ignored = physBone ? doc["ignoreTransforms"] : doc["m_Exclusions"];
        if (ignored is UnitySeq list)
        {
            foreach (var item in list.Items)
            {
                var skip = candidate.ResolveTransform(item.Ref.FileId) is { } r ? nodes.Resolve(r) : null;
                if (skip != null)
                    RemoveSubtree(chain, skip);
            }
        }
        // The reader's pivot helpers between two bones are not bones: the bone below one hangs from the bone
        // above it, and the helper keeps its pose.
        for (int i = chain.Bones.Count - 1; i > 0; i--)
            if (chain.Bones[i] is { } helper && (helper.SlotName.Value ?? "").Contains("$AssimpFbx$", StringComparison.Ordinal))
                chain.Bones.RemoveAt(i);

        // Unity's own scale of the chain's root: ours carries the model's unit on top of it.
        var gs = root.GlobalScale;
        float unityScale = (MathF.Abs(gs.x) + MathF.Abs(gs.y) + MathF.Abs(gs.z)) / 3f / unit;

        if (physBone)
        {
            ApplyPhysBone(chain, doc, unit, heldRoots.Contains(root));
        }
        else
        {
            chain.SwingRoot.Value = !heldRoots.Contains(root);
            float damping = System.Math.Clamp(doc["m_Damping"]?.Float(0.1f) ?? 0.1f, 0f, 0.99f);
            chain.Damping.Value = -MathF.Log(1f - damping) * 60f;
            chain.Elasticity.Value = MathF.Max(1f, (doc["m_Elasticity"]?.Float(0.1f) ?? 0.1f) * 3600f);
            chain.Stiffness.Value = System.Math.Clamp(doc["m_Stiffness"]?.Float(0.1f) ?? 0.1f, 0f, 1f);
            chain.Inertia.Value = System.Math.Clamp(doc["m_Inert"]?.Float() ?? 0f, 0f, 1f);
            chain.BaseBoneRadius.Value = (doc["m_Radius"]?.Float() ?? 0f) / unit;
            var force = Vector(doc["m_Gravity"]) + Vector(doc["m_Force"]);
            chain.Gravity.Value = Mirror(force) * (3600f * unityScale);
            chain.SimulateTerminalBones.Value = (doc["m_EndLength"]?.Float() ?? 0f) > 0f || Vector(doc["m_EndOffset"]).LengthSquared > 1e-10f;
            chain.AllowGrab.Value = false;
        }

        var colliderList = (physBone ? doc["colliders"] : doc["m_Colliders"]) as UnitySeq;
        if (colliderList != null)
        {
            foreach (var item in colliderList.Items)
            {
                long id = item.Ref.FileId;
                if (id == 0)
                    continue;
                if (!colliders.TryGetValue(id, out var collider))
                {
                    collider = candidate.Colliders.TryGetValue(id, out var source) ? BuildCollider(candidate, source, nodes, unit) : null;
                    colliders[id] = collider;
                    CollideSegments(collider, source);
                    if (collider is Component built && source != null)
                    {
                        built.Enabled.Value = source.Enabled;
                        Script(result, nodes.Resolve(source.Node), built);
                    }
                }
                if (collider != null)
                    chain.StaticColliders.Add(collider);
            }
        }
        return true;
    }

    private static IDynamicBoneCollider? BuildCollider(UnityImportCandidate candidate, UnityComponentRef part, NodeIndex nodes, float unit)
    {
        var doc = part.Doc;
        Slot? host;
        float radius, height;
        float3 center;
        floatQ rotation = floatQ.Identity;
        if (part.Type == UnityComponentRef.Kind.PhysBoneCollider)
        {
            int shape = doc["shapeType"]?.Int() ?? 0;
            if ((doc["insideBounds"]?.Int() ?? 0) != 0)
                return BuildInside(candidate, part, nodes, unit);
            long rootId = doc["rootTransform"]?.Ref.FileId ?? 0;
            var at = rootId != 0 ? candidate.ResolveTransform(rootId) : part.Node;
            host = at != null ? nodes.Resolve(at) : null;
            if (shape == 2)
                return host != null ? BuildPlane(host, doc, unit) : null;
            radius = doc["radius"]?.Float() ?? 0f;
            height = shape == 1 ? doc["height"]?.Float() ?? 0f : 0f;
            center = Vector(doc["position"]);
            if (doc["rotation"] is { } r)
                rotation = MirrorRotation(new floatQ(r["x"]?.Float() ?? 0f, r["y"]?.Float() ?? 0f, r["z"]?.Float() ?? 0f, r["w"]?.Float(1f) ?? 1f));
        }
        else
        {
            if ((doc["m_Bound"]?.Int() ?? 0) != 0)
                return null;
            host = nodes.Resolve(part.Node);
            radius = doc["m_Radius"]?.Float() ?? 0f;
            height = doc["m_Height"]?.Float() ?? 0f;
            center = Vector(doc["m_Center"]);
            // Its capsule runs along the chosen local axis; ours runs along Y.
            rotation = (doc["m_Direction"]?.Int(1) ?? 1) switch
            {
                0 => floatQ.AxisAngle(new float3(0f, 0f, 1f), MathF.PI * 0.5f),
                2 => floatQ.AxisAngle(new float3(1f, 0f, 0f), MathF.PI * 0.5f),
                _ => floatQ.Identity,
            };
        }
        if (host == null || radius <= 0f)
            return null;

        var offset = Mirror(center) / unit;
        bool capsule = height > radius * 2f;
        Slot target = host;
        if (capsule && !IsIdentity(rotation))
        {
            target = host.AddSlot("Collider");
            target.LocalPosition.Value = offset;
            target.LocalRotation.Value = rotation;
            offset = float3.Zero;
        }
        if (capsule)
        {
            var c = target.AttachComponent<DynamicBoneCapsuleCollider>();
            c.Radius.Value = radius / unit;
            c.Height.Value = height / unit;
            c.Offset.Value = offset;
            return c;
        }
        var sphere = target.AttachComponent<DynamicBoneSphereCollider>();
        sphere.Radius.Value = radius / unit;
        sphere.Offset.Value = offset;
        return sphere;
    }

    // Its plane faces along the collider's turned local up.
    private static IDynamicBoneCollider BuildPlane(Slot host, UnityDocument doc, float unit)
    {
        var rotation = floatQ.Identity;
        if (doc["rotation"] is { } r)
            rotation = MirrorRotation(new floatQ(r["x"]?.Float() ?? 0f, r["y"]?.Float() ?? 0f, r["z"]?.Float() ?? 0f, r["w"]?.Float(1f) ?? 1f));
        var plane = host.AttachComponent<DynamicBonePlaneCollider>();
        plane.Offset.Value = Mirror(Vector(doc["position"])) / unit;
        plane.Normal.Value = rotation * float3.Up;
        return plane;
    }

    private static void Script(UnityImportResult result, Slot? owner, Component component)
    {
        if (owner == null)
            return;
        if (!result.Scripts.TryGetValue(owner, out var list))
            result.Scripts[owner] = list = new List<Component>();
        list.Add(component);
    }

    private static void RemoveSubtree(DynamicBoneChain chain, Slot top)
    {
        for (int i = chain.Bones.Count - 1; i >= 0; i--)
        {
            var bone = chain.Bones[i];
            if (bone != null && (ReferenceEquals(bone, top) || bone.IsDescendantOf(top)))
                chain.Bones.RemoveAt(i);
        }
    }

    private static float3 Vector(UnityNode? node)
        => node == null ? float3.Zero : new float3(node["x"]?.Float() ?? 0f, node["y"]?.Float() ?? 0f, node["z"]?.Float() ?? 0f);

    private static float3 Mirror(float3 v) => new(-v.x, v.y, v.z);

    private static floatQ MirrorRotation(floatQ q) => new(q.x, -q.y, -q.z, q.w);

    private static bool IsIdentity(floatQ q) => MathF.Abs(q.x) < 1e-5f && MathF.Abs(q.y) < 1e-5f && MathF.Abs(q.z) < 1e-5f;

    // Every slot of the imported model by the ids and paths the project used for it. Objects the project
    // added under the model's nodes are made on first use, with their pose converted.
    internal sealed class NodeIndex
    {
        private readonly float _toLocal;
        private readonly Dictionary<long, Slot> _byId = new();
        private readonly Dictionary<string, Slot> _byPath = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Slot?> _byName = new(StringComparer.Ordinal);
        private readonly Dictionary<UnityNodeRef, Slot> _made = new();
        private readonly IReadOnlyDictionary<string, UnityNodeRef> _sourceNodes;
        // Other placed models: their ids, and for walking names, where each one's own root is.
        private readonly Dictionary<UnityModelPart, Dictionary<long, Slot>> _partIds = new();
        private readonly Dictionary<Slot, Slot> _tops = new();
        // A model's own root in the project is its model slot, not the reader's root node under it, which
        // carries the file's axis turn: things hung from the root are posed in the model slot's space. The
        // main model's slot is also turned to face the body's front, a turn its root node takes back, so its
        // frame keeps the slot's place and size but its parent's turn.
        private readonly Dictionary<Slot, (Slot Model, bool Turned)> _frames = new();
        // How many of our local units one of the project's is, below a model slot or a made object.
        private readonly Dictionary<Slot, float> _units = new();

        public NodeIndex(Slot? nodeRoot, UnityModelMeta meta, float toLocal, IReadOnlyDictionary<string, UnityNodeRef> sourceNodes)
        {
            _toLocal = toLocal;
            _sourceNodes = sourceNodes;
            Top = Index(nodeRoot, meta, _byId, _byPath, _byName);
            if (nodeRoot != null && Top != null)
                _tops[nodeRoot] = Top;
        }

        // Every slot of an imported model by the ids and paths the project used for it. The model's own
        // root, which the project hangs its added objects from, answers to the root ids; a lone top node is
        // folded into it, so that node stands for it.
        private static Slot? Index(Slot? nodeRoot, UnityModelMeta meta, Dictionary<long, Slot> byId, Dictionary<string, Slot>? byPath,
            Dictionary<string, Slot?> byName)
        {
            var stack = new Stack<Slot>();
            if (nodeRoot != null)
                foreach (var child in nodeRoot.Children)
                    stack.Push(child);
            while (stack.Count > 0)
            {
                var slot = stack.Pop();
                foreach (var child in slot.Children)
                    stack.Push(child);
                foreach (var path in UnityPackageImporter.UnityPaths(nodeRoot, slot))
                {
                    foreach (bool withRoot in new[] { true, false })
                    {
                        byId.TryAdd(UnityModelMeta.GameObjectId(path, withRoot), slot);
                        foreach (var type in new[] { "Transform", "SkinnedMeshRenderer", "MeshRenderer" })
                            byId.TryAdd(UnityModelMeta.ObjectId(type, path, withRoot), slot);
                    }
                    byPath?.TryAdd(path, slot);
                }
                var name = slot.SlotName.Value ?? "";
                byName[name] = byName.ContainsKey(name) ? null : slot;
            }
            foreach (var (id, named) in meta.NamedIds)
                if (byName.TryGetValue(named.Name, out var slot) && slot != null)
                    byId.TryAdd(id, slot);

            if (nodeRoot == null)
                return null;
            var top = nodeRoot.ChildCount == 1 ? nodeRoot.Children.First() : nodeRoot;
            foreach (bool withRoot in new[] { true, false })
            {
                byId.TryAdd(UnityModelMeta.GameObjectId("", withRoot), top);
                byId.TryAdd(UnityModelMeta.ObjectId("Transform", "", withRoot), top);
            }
            byId.TryAdd(400000, top);
            byId.TryAdd(100000, top);
            byPath?.TryAdd("", top);
            return top;
        }

        // Another model file placed inside the avatar, imported under its own model slot. Its root is that
        // slot, which carries the placement's pose and switch, so a toggle or a link that names the root
        // takes the whole model; what the project added under the root still hangs beside its nodes.
        public void AddPart(UnityModelPart part, Slot model, Slot? nodeRoot, float unit)
        {
            var ids = new Dictionary<long, Slot>();
            var top = Index(nodeRoot, part.Meta, ids, null, new Dictionary<string, Slot?>(StringComparer.Ordinal)) ?? model;
            foreach (bool withRoot in new[] { true, false })
            {
                ids[UnityModelMeta.GameObjectId("", withRoot)] = model;
                ids[UnityModelMeta.ObjectId("Transform", "", withRoot)] = model;
            }
            ids[400000] = model;
            ids[100000] = model;
            _partIds[part] = ids;
            _tops[model] = top;
            if (nodeRoot != null)
                _tops[nodeRoot] = top;
            _frames[top] = (model, false);
            _units[model] = unit;
            _added.Add(model);
        }

        // The main model's slot and, when the avatar's root is an object of the project's own rather than a
        // model, that root: both read the main model's unit.
        public void SetMain(Slot model, float unit, bool authoredRoot)
        {
            _units[model] = unit;
            if (Top != null)
            {
                _tops[model] = Top;
                _frames[Top] = (model, true);
            }
            if (authoredRoot)
            {
                ImportRoot = model;
                PathTop = model;
            }
        }

        public Slot? ImportRoot { get; private set; }

        // Where animation paths start: the avatar's root.
        public Slot? PathTop { get; private set; }

        public Slot? ByName(string name) => _byName.TryGetValue(name, out var slot) ? slot : null;

        public Slot? ByPath(string path) => _byPath.TryGetValue(path, out var slot) ? slot : null;

        public Slot? ById(long id) => _byId.TryGetValue(id, out var slot) ? slot : null;

        public Slot? PartById(UnityModelPart part, long id)
            => _partIds.TryGetValue(part, out var ids) && ids.TryGetValue(id, out var slot) ? slot : null;

        // The object an animation path names: a path below the avatar's root (Unity's root, which a lone
        // top node folds into), walked by name so objects the project added are found too. A placed model is
        // stepped into at its own root, the way the project shows it.
        public Slot? AtPath(string path)
        {
            if (PathTop == null && _byPath.TryGetValue(path, out var known))
                return known;
            var current = PathTop ?? Top;
            if (current == null)
                return null;
            foreach (var name in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                Slot? next = null;
                foreach (var child in current.Children)
                {
                    if (child.SlotName.Value == name)
                    {
                        next = child;
                        break;
                    }
                }
                if (next == null)
                {
                    if (_snapshot.TryGetValue(path, out var moved) && !moved.IsDestroyed)
                        return moved;
                    return _sourceNodes.TryGetValue(path, out var source) ? Resolve(source) : null;
                }
                current = _tops.TryGetValue(next, out var top) ? top : next;
            }
            return current;
        }

        // Every object's path as the project wrote it, taken before links move outfit bones onto the body:
        // the project's own tools rewrite animation paths after such a move, so they still name the old place.
        private readonly Dictionary<string, Slot> _snapshot = new(StringComparer.Ordinal);

        public void SnapshotPaths()
        {
            var start = PathTop ?? Top;
            if (start == null)
                return;
            var stack = new Stack<(Slot Slot, string Path)>();
            stack.Push((start, ""));
            while (stack.Count > 0)
            {
                var (slot, path) = stack.Pop();
                foreach (var child in slot.Children)
                {
                    var childPath = path.Length == 0 ? child.SlotName.Value ?? "" : path + "/" + child.SlotName.Value;
                    _snapshot.TryAdd(childPath, child);
                    stack.Push((_tops.TryGetValue(child, out var top) ? top : child, childPath));
                }
            }
        }

        public Slot? Top { get; private set; }

        public Slot? Resolve(UnityNodeRef node)
        {
            if (node.IsImportRoot)
                return ImportRoot ?? Top;
            if (node.ModelId != 0)
            {
                var ids = node.Part == null ? _byId : _partIds.GetValueOrDefault(node.Part);
                if (ids == null)
                    return null;
                if (ids.TryGetValue(node.ModelId, out var slot) || (node.ModelIdAlt != 0 && ids.TryGetValue(node.ModelIdAlt, out slot)))
                    return slot;
                return null;
            }
            if (node.Path != null)
            {
                if (_byPath.TryGetValue(node.Path, out var byPath))
                    return byPath;
                int slash = node.Path.LastIndexOf('/');
                var leaf = slash >= 0 ? node.Path[(slash + 1)..] : node.Path;
                if (_byName.TryGetValue(leaf, out var byName) && byName != null)
                    return byName;
            }
            if (node.Parent == null)
                return null;
            if (_made.TryGetValue(node, out var made))
                return made;
            var parent = Resolve(node.Parent);
            if (parent == null)
                return null;
            parent = Inside(parent);
            float unit = UnitOf(parent);
            var slotMade = parent.AddSlot(string.IsNullOrEmpty(node.Name) ? "Added" : node.Name);
            Pose(slotMade, parent, node.Position, node.Rotation, node.Scale);
            slotMade.ActiveSelf.Value = node.Active;
            _made[node] = slotMade;
            _added.Add(slotMade);
            _units[slotMade] = unit;
            if (node.Path != null)
                _byPath[node.Path] = slotMade;
            return slotMade;
        }

        // Made here for an object the project added, or the slot of another placed model.
        public bool IsAdded(Slot slot) => _added.Contains(slot);

        private readonly HashSet<Slot> _added = new();

        // Where something hung from this slot goes: inside a model slot, beside the model's own nodes.
        public Slot Inside(Slot slot) => _tops.TryGetValue(slot, out var top) ? top : slot;

        // Puts a slot at a pose the project gave relative to the parent (its root's space for a model's
        // root). Scale is relative to that space too.
        // The slot need not hang from that parent directly: a model's lone top node is posed this way through
        // the reader's root node above it.
        public void Pose(Slot slot, Slot parent, float3 position, floatQ rotation, float3 scale)
        {
            float unit = UnitOf(parent);
            if (ReferenceEquals(slot.Parent, parent) && (!_frames.TryGetValue(parent, out var own) || ReferenceEquals(own.Model, parent)))
            {
                slot.LocalPosition.Value = Mirror(position) / unit;
                slot.LocalRotation.Value = MirrorRotation(rotation);
                slot.LocalScale.Value = scale;
                return;
            }
            var frame = parent;
            var turn = parent.GlobalRotation;
            if (_frames.TryGetValue(parent, out var entry) && !ReferenceEquals(entry.Model, parent))
            {
                frame = entry.Model;
                turn = entry.Turned ? frame.Parent?.GlobalRotation ?? floatQ.Identity : frame.GlobalRotation;
            }
            var want = frame.GlobalScale;
            slot.GlobalPosition = frame.GlobalPosition + turn * (Mirror(position) / unit * want);
            slot.GlobalRotation = turn * MirrorRotation(rotation);
            var have = slot.Parent?.GlobalScale ?? float3.One;
            slot.LocalScale.Value = new float3(scale.x * want.x / have.x, scale.y * want.y / have.y, scale.z * want.z / have.z);
        }

        // The project's length unit in this slot's local space, in ours.
        public float UnitOf(Slot slot)
        {
            for (var s = slot; s != null; s = s.Parent)
                if (_units.TryGetValue(s, out var unit))
                    return unit;
            return 1f / _toLocal;
        }
    }
}
