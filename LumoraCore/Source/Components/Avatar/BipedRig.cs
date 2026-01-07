using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Input;
using Lumora.Core.Math;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components.Avatar;

/// <summary>
/// Component that stores bone slot references for a biped humanoid rig.
/// Used by VRIK and other avatar systems to drive skeleton bones.
/// </summary>
[ComponentCategory("Rendering")]
public class BipedRig : Component
{
    /// <summary>
    /// Optional forward axis hint for the rig.
    /// </summary>
    public Sync<float3?> ForwardAxis { get; private set; }

    /// <summary>
    /// Dictionary mapping BodyNode types to their corresponding Slot references.
    /// </summary>
    public SyncRefDictionary<BodyNode, Slot> Bones { get; private set; }

    /// <summary>
    /// Minimal set of bones required for a valid biped rig.
    /// </summary>
    public static readonly BodyNode[] MinimalBiped = new BodyNode[]
    {
        BodyNode.Hips,
        BodyNode.Spine,
        BodyNode.Head,
        BodyNode.LeftUpperArm,
        BodyNode.LeftLowerArm,
        BodyNode.LeftHand,
        BodyNode.RightUpperArm,
        BodyNode.RightLowerArm,
        BodyNode.RightHand,
        BodyNode.LeftUpperLeg,
        BodyNode.LeftLowerLeg,
        BodyNode.LeftFoot,
        BodyNode.RightUpperLeg,
        BodyNode.RightLowerLeg,
        BodyNode.RightFoot
    };

    /// <summary>
    /// Get or set a bone slot by body node type.
    /// </summary>
    public Slot this[BodyNode boneType]
    {
        get => TryGetBone(boneType);
        set
        {
            if (value != null)
                Bones.Add(boneType, value);
            else
                Bones.Remove(boneType);
        }
    }

    /// <summary>
    /// Whether this rig has all minimal biped bones.
    /// </summary>
    public bool IsBiped
    {
        get
        {
            foreach (var node in MinimalBiped)
            {
                if (!Bones.ContainsKey(node))
                    return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Whether this rig has left hand finger bones.
    /// </summary>
    public bool HasLeftHandFingers => HasMinimalHand(Chirality.Left);

    /// <summary>
    /// Whether this rig has right hand finger bones.
    /// </summary>
    public bool HasRightHandFingers => HasMinimalHand(Chirality.Right);

    public override void OnAwake()
    {
        base.OnAwake();

        ForwardAxis = new Sync<float3?>(this, null);
        Bones = new SyncRefDictionary<BodyNode, Slot>(this);
    }

    /// <summary>
    /// Try to get a bone slot for a body node type.
    /// </summary>
    public Slot TryGetBone(BodyNode boneType)
    {
        if (Bones.TryGetTarget(boneType, out var target))
            return target;
        return null;
    }

    /// <summary>
    /// Get the body node type for a given bone slot.
    /// </summary>
    public BodyNode GetBoneType(Slot bone)
    {
        foreach (var kvp in Bones)
        {
            if (kvp.Value.Target == bone)
                return kvp.Key;
        }
        return BodyNode.NONE;
    }

    /// <summary>
    /// Check if a body node is a required bone.
    /// </summary>
    public static bool IsRequiredBone(BodyNode node)
    {
        foreach (var required in MinimalBiped)
        {
            if (required == node)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Check if this rig has minimal hand bones for a chirality.
    /// </summary>
    public bool HasMinimalHand(Chirality chirality)
    {
        // Need at least 2 thumb segments
        if (FingerSegmentCount(FingerType.Thumb, chirality, excludeMetacarpal: false) < 2)
            return false;

        // Need at least 2 other fingers with 2+ segments
        int fingerCount = 0;
        for (var finger = FingerType.Index; finger <= FingerType.Pinky; finger++)
        {
            if (FingerSegmentCount(finger, chirality, excludeMetacarpal: true) >= 2)
                fingerCount++;
        }

        return fingerCount >= 2;
    }

    /// <summary>
    /// Count segments for a finger.
    /// </summary>
    public int FingerSegmentCount(FingerType finger, Chirality chirality, bool excludeMetacarpal)
    {
        int count = 0;
        foreach (var kvp in Bones)
        {
            var node = kvp.Key;
            if (!node.IsFinger())
                continue;
            if (node.GetChirality() != chirality)
                continue;
            if (excludeMetacarpal && node.GetFingerSegmentType() == FingerSegmentType.Metacarpal)
                continue;
            if (node.GetFingerType() == finger)
                count++;
        }
        return count;
    }

    /// <summary>
    /// Get list of missing bones for a valid biped rig.
    /// </summary>
    public void GetMissingBipedBones(List<BodyNode> list)
    {
        foreach (var node in MinimalBiped)
        {
            if (!Bones.ContainsKey(node))
                list.Add(node);
        }
    }

    /// <summary>
    /// Populate bones from a SkeletonBuilder by matching bone names.
    /// </summary>
    public void PopulateFromSkeleton(SkeletonBuilder skeleton)
    {
        if (skeleton == null || !skeleton.IsBuilt.Value)
        {
            AquaLogger.Warn("BipedRig: Cannot populate from null or unbuilt skeleton");
            return;
        }

        // Map bone names to body nodes
        var nameToNode = new Dictionary<string, BodyNode>(System.StringComparer.OrdinalIgnoreCase)
        {
			// Core
			{ "Hips", BodyNode.Hips },
            { "Spine", BodyNode.Spine },
            { "Spine1", BodyNode.Chest },
            { "Spine2", BodyNode.UpperChest },
            { "Chest", BodyNode.Chest },
            { "UpperChest", BodyNode.UpperChest },
            { "Neck", BodyNode.Neck },
            { "Head", BodyNode.Head },

			// Left arm
			{ "LeftShoulder", BodyNode.LeftShoulder },
            { "LeftUpperArm", BodyNode.LeftUpperArm },
            { "LeftArm", BodyNode.LeftUpperArm },
            { "LeftLowerArm", BodyNode.LeftLowerArm },
            { "LeftForeArm", BodyNode.LeftLowerArm },
            { "LeftHand", BodyNode.LeftHand },

			// Right arm
			{ "RightShoulder", BodyNode.RightShoulder },
            { "RightUpperArm", BodyNode.RightUpperArm },
            { "RightArm", BodyNode.RightUpperArm },
            { "RightLowerArm", BodyNode.RightLowerArm },
            { "RightForeArm", BodyNode.RightLowerArm },
            { "RightHand", BodyNode.RightHand },

			// Left leg
			{ "LeftUpperLeg", BodyNode.LeftUpperLeg },
            { "LeftThigh", BodyNode.LeftUpperLeg },
            { "LeftLeg", BodyNode.LeftUpperLeg },
            { "LeftLowerLeg", BodyNode.LeftLowerLeg },
            { "LeftShin", BodyNode.LeftLowerLeg },
            { "LeftFoot", BodyNode.LeftFoot },
            { "LeftToes", BodyNode.LeftToes },
            { "LeftToeBase", BodyNode.LeftToes },

			// Right leg
			{ "RightUpperLeg", BodyNode.RightUpperLeg },
            { "RightThigh", BodyNode.RightUpperLeg },
            { "RightLeg", BodyNode.RightUpperLeg },
            { "RightLowerLeg", BodyNode.RightLowerLeg },
            { "RightShin", BodyNode.RightLowerLeg },
            { "RightFoot", BodyNode.RightFoot },
            { "RightToes", BodyNode.RightToes },
            { "RightToeBase", BodyNode.RightToes },
        };

        // Iterate through skeleton bones and map them
        for (int i = 0; i < skeleton.BoneCount; i++)
        {
            var boneName = skeleton.BoneNames[i];
            var boneSlot = skeleton.BoneSlots[i];

            if (boneSlot == null)
                continue;

            // Try direct name match
            if (nameToNode.TryGetValue(boneName, out var node))
            {
                Bones.Add(node, boneSlot);
                continue;
            }

            // Try partial name matching
            var lowerName = boneName.ToLower();
            foreach (var kvp in nameToNode)
            {
                if (lowerName.Contains(kvp.Key.ToLower()))
                {
                    if (!Bones.ContainsKey(kvp.Value))
                    {
                        Bones.Add(kvp.Value, boneSlot);
                        break;
                    }
                }
            }
        }

        AquaLogger.Log($"BipedRig: Populated {Bones.Count} bones from skeleton, IsBiped={IsBiped}");
    }

    /// <summary>
    /// Log diagnostic info about this rig.
    /// </summary>
    public void LogDiagnosticInfo()
    {
        AquaLogger.Log($"BipedRig Diagnostic Info:");
        AquaLogger.Log($"  Total bones: {Bones.Count}");
        AquaLogger.Log($"  IsBiped: {IsBiped}");
        AquaLogger.Log($"  HasLeftHandFingers: {HasLeftHandFingers}");
        AquaLogger.Log($"  HasRightHandFingers: {HasRightHandFingers}");

        var missing = new List<BodyNode>();
        GetMissingBipedBones(missing);
        if (missing.Count > 0)
        {
            AquaLogger.Log($"  Missing bones: {string.Join(", ", missing)}");
        }

        foreach (var kvp in Bones)
        {
            AquaLogger.Log($"  {kvp.Key}: {kvp.Value?.Target?.SlotName.Value ?? "null"}");
        }
    }
}

/// <summary>
/// A simple wrapper for slot references in dictionaries.
/// </summary>
public class SlotRef
{
    public Slot Target { get; set; }

    public SlotRef(Slot target = null)
    {
        Target = target;
    }
}

/// <summary>
/// A dictionary that maps BodyNode keys to Slot values.
/// Simplified version that doesn't use SyncRef (which requires IWorldElement).
/// </summary>
public class SyncRefDictionary<TKey, TValue> : System.Collections.Generic.IEnumerable<KeyValuePair<TKey, SlotRef>>
    where TKey : notnull
    where TValue : Slot
{
    private readonly Dictionary<TKey, SlotRef> _dict = new();
    private readonly Component _owner;

    public SyncRefDictionary(Component owner)
    {
        _owner = owner;
    }

    public int Count => _dict.Count;

    public void Add(TKey key, TValue value)
    {
        if (_dict.TryGetValue(key, out var existing))
        {
            existing.Target = value;
        }
        else
        {
            _dict[key] = new SlotRef(value);
        }
    }

    public bool Remove(TKey key)
    {
        return _dict.Remove(key);
    }

    public bool ContainsKey(TKey key)
    {
        return _dict.ContainsKey(key);
    }

    public bool TryGetTarget(TKey key, out TValue target)
    {
        if (_dict.TryGetValue(key, out var slotRef) && slotRef.Target is TValue typedTarget)
        {
            target = typedTarget;
            return target != null;
        }
        target = default;
        return false;
    }

    public TValue this[TKey key]
    {
        get => _dict.TryGetValue(key, out var sr) && sr.Target is TValue t ? t : default;
        set => Add(key, value);
    }

    public System.Collections.Generic.IEnumerator<KeyValuePair<TKey, SlotRef>> GetEnumerator()
    {
        return _dict.GetEnumerator();
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
