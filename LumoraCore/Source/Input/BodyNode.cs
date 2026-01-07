namespace Lumora.Core.Input;

/// <summary>
/// Enumeration of all body nodes that can be tracked.
/// </summary>
public enum BodyNode
{
    NONE = 0,
    Root = 1,
    View = 2,
    LeftController = 3,
    RightController = 4,
    Hips = 5,
    Spine = 6,
    Chest = 7,
    UpperChest = 8,
    Neck = 9,
    Head = 10,
    Jaw = 11,
    LeftEye = 12,
    RightEye = 13,
    LeftShoulder = 14,
    LeftUpperArm = 15,
    LeftLowerArm = 16,
    LeftHand = 17,
    LeftPalm = 18,
    LeftThumb_Metacarpal = 19,
    LeftThumb_Proximal = 20,
    LeftThumb_Distal = 21,
    LeftThumb_Tip = 22,
    LeftIndexFinger_Metacarpal = 23,
    LeftIndexFinger_Proximal = 24,
    LeftIndexFinger_Intermediate = 25,
    LeftIndexFinger_Distal = 26,
    LeftIndexFinger_Tip = 27,
    LeftMiddleFinger_Metacarpal = 28,
    LeftMiddleFinger_Proximal = 29,
    LeftMiddleFinger_Intermediate = 30,
    LeftMiddleFinger_Distal = 31,
    LeftMiddleFinger_Tip = 32,
    LeftRingFinger_Metacarpal = 33,
    LeftRingFinger_Proximal = 34,
    LeftRingFinger_Intermediate = 35,
    LeftRingFinger_Distal = 36,
    LeftRingFinger_Tip = 37,
    LeftPinky_Metacarpal = 38,
    LeftPinky_Proximal = 39,
    LeftPinky_Intermediate = 40,
    LeftPinky_Distal = 41,
    LeftPinky_Tip = 42,
    RightShoulder = 43,
    RightUpperArm = 44,
    RightLowerArm = 45,
    RightHand = 46,
    RightPalm = 47,
    RightThumb_Metacarpal = 48,
    RightThumb_Proximal = 49,
    RightThumb_Distal = 50,
    RightThumb_Tip = 51,
    RightIndexFinger_Metacarpal = 52,
    RightIndexFinger_Proximal = 53,
    RightIndexFinger_Intermediate = 54,
    RightIndexFinger_Distal = 55,
    RightIndexFinger_Tip = 56,
    RightMiddleFinger_Metacarpal = 57,
    RightMiddleFinger_Proximal = 58,
    RightMiddleFinger_Intermediate = 59,
    RightMiddleFinger_Distal = 60,
    RightMiddleFinger_Tip = 61,
    RightRingFinger_Metacarpal = 62,
    RightRingFinger_Proximal = 63,
    RightRingFinger_Intermediate = 64,
    RightRingFinger_Distal = 65,
    RightRingFinger_Tip = 66,
    RightPinky_Metacarpal = 67,
    RightPinky_Proximal = 68,
    RightPinky_Intermediate = 69,
    RightPinky_Distal = 70,
    RightPinky_Tip = 71,
    LeftUpperLeg = 72,
    LeftLowerLeg = 73,
    LeftFoot = 74,
    LeftToes = 75,
    RightUpperLeg = 76,
    RightLowerLeg = 77,
    RightFoot = 78,
    RightToes = 79,
    END = 80,

    // Range constants for finger iteration
    LEFT_FINGER_START = 19,
    LEFT_FINGER_END = 42,
    RIGHT_FINGER_START = 48,
    RIGHT_FINGER_END = 71
}

/// <summary>
/// Finger types for hand tracking.
/// </summary>
public enum FingerType
{
    Thumb = 0,
    Index = 1,
    Middle = 2,
    Ring = 3,
    Pinky = 4
}

/// <summary>
/// Finger segment types for detailed hand tracking.
/// </summary>
public enum FingerSegmentType
{
    Metacarpal = 0,
    Proximal = 1,
    Intermediate = 2,
    Distal = 3,
    Tip = 4
}

/// <summary>
/// Handedness/side enumeration.
/// </summary>
public enum Chirality
{
    None = 0,
    Left = 1,
    Right = 2
}

/// <summary>
/// Extension methods for BodyNode.
/// </summary>
public static class BodyNodeExtensions
{
    /// <summary>
    /// Check if this body node is a hand node (LeftHand or RightHand).
    /// </summary>
    public static bool IsHand(this BodyNode node)
    {
        return node == BodyNode.LeftHand || node == BodyNode.RightHand;
    }

    /// <summary>
    /// Check if this body node is a controller node.
    /// </summary>
    public static bool IsController(this BodyNode node)
    {
        return node == BodyNode.LeftController || node == BodyNode.RightController;
    }

    /// <summary>
    /// Check if this body node is a foot node.
    /// </summary>
    public static bool IsFoot(this BodyNode node)
    {
        return node == BodyNode.LeftFoot || node == BodyNode.RightFoot;
    }

    /// <summary>
    /// Check if this body node is a finger bone.
    /// </summary>
    public static bool IsFinger(this BodyNode node)
    {
        return (node >= BodyNode.LEFT_FINGER_START && node <= BodyNode.LEFT_FINGER_END) ||
               (node >= BodyNode.RIGHT_FINGER_START && node <= BodyNode.RIGHT_FINGER_END);
    }

    /// <summary>
    /// Get the chirality (left/right) for this body node.
    /// </summary>
    public static Chirality GetChirality(this BodyNode node)
    {
        if (node == BodyNode.LeftController || node == BodyNode.LeftEye ||
            (node >= BodyNode.LeftShoulder && node <= BodyNode.LeftPinky_Tip) ||
            (node >= BodyNode.LeftUpperLeg && node <= BodyNode.LeftToes))
            return Chirality.Left;

        if (node == BodyNode.RightController || node == BodyNode.RightEye ||
            (node >= BodyNode.RightShoulder && node <= BodyNode.RightPinky_Tip) ||
            (node >= BodyNode.RightUpperLeg && node <= BodyNode.RightToes))
            return Chirality.Right;

        return Chirality.None;
    }

    /// <summary>
    /// Get the equivalent right-side body node for a left-side node.
    /// </summary>
    public static BodyNode GetRightSide(this BodyNode node)
    {
        // Controllers
        if (node == BodyNode.LeftController) return BodyNode.RightController;

        // Eyes
        if (node == BodyNode.LeftEye) return BodyNode.RightEye;

        // Arm and fingers (14-42 -> 43-71)
        if (node >= BodyNode.LeftShoulder && node <= BodyNode.LeftPinky_Tip)
            return (BodyNode)((int)node + 29); // Offset is 43 - 14 = 29

        // Legs (72-75 -> 76-79)
        if (node >= BodyNode.LeftUpperLeg && node <= BodyNode.LeftToes)
            return (BodyNode)((int)node + 4);

        return node;
    }

    /// <summary>
    /// Get the equivalent node for a specific side.
    /// </summary>
    public static BodyNode GetSide(this BodyNode node, Chirality chirality)
    {
        if (chirality == Chirality.Right)
            return node.GetRightSide();
        return node;
    }

    /// <summary>
    /// Get the finger type for a finger body node.
    /// </summary>
    public static FingerType GetFingerType(this BodyNode node)
    {
        if (!node.IsFinger())
            return FingerType.Thumb;

        // Normalize to left side for calculation
        int fingerIndex;
        if (node >= BodyNode.RIGHT_FINGER_START)
            fingerIndex = (int)node - (int)BodyNode.RIGHT_FINGER_START;
        else
            fingerIndex = (int)node - (int)BodyNode.LEFT_FINGER_START;

        // Each finger has 5 segments (Metacarpal -> Tip) except thumb which has 4
        // Thumb: 0-3, Index: 4-8, Middle: 9-13, Ring: 14-18, Pinky: 19-23
        if (fingerIndex < 4) return FingerType.Thumb;
        if (fingerIndex < 9) return FingerType.Index;
        if (fingerIndex < 14) return FingerType.Middle;
        if (fingerIndex < 19) return FingerType.Ring;
        return FingerType.Pinky;
    }

    /// <summary>
    /// Get the finger segment type for a finger body node.
    /// </summary>
    public static FingerSegmentType GetFingerSegmentType(this BodyNode node)
    {
        if (!node.IsFinger())
            return FingerSegmentType.Proximal;

        // Normalize to left side for calculation
        int fingerIndex;
        if (node >= BodyNode.RIGHT_FINGER_START)
            fingerIndex = (int)node - (int)BodyNode.RIGHT_FINGER_START;
        else
            fingerIndex = (int)node - (int)BodyNode.LEFT_FINGER_START;

        // Thumb has 4 segments (no intermediate)
        if (fingerIndex < 4)
        {
            return (FingerSegmentType)fingerIndex;
        }

        // Other fingers have 5 segments
        int segmentInFinger = (fingerIndex - 4) % 5;
        return (FingerSegmentType)segmentInFinger;
    }

    /// <summary>
    /// Compose a finger body node from finger type, segment type, and chirality.
    /// </summary>
    public static BodyNode ComposeFinger(this FingerType finger, FingerSegmentType segment, Chirality chirality)
    {
        int baseOffset;

        // Calculate base offset for the finger type
        switch (finger)
        {
            case FingerType.Thumb:
                baseOffset = 0;
                break;
            case FingerType.Index:
                baseOffset = 4; // After 4 thumb segments
                break;
            case FingerType.Middle:
                baseOffset = 9; // After 4 thumb + 5 index
                break;
            case FingerType.Ring:
                baseOffset = 14; // After 4 thumb + 5 index + 5 middle
                break;
            case FingerType.Pinky:
            default:
                baseOffset = 19; // After 4 thumb + 5 index + 5 middle + 5 ring
                break;
        }

        // Add segment offset
        int segmentOffset = (int)segment;

        // Thumb doesn't have intermediate
        if (finger == FingerType.Thumb && segment >= FingerSegmentType.Intermediate)
        {
            segmentOffset = segment == FingerSegmentType.Intermediate ? (int)FingerSegmentType.Distal :
                            segment == FingerSegmentType.Distal ? (int)FingerSegmentType.Distal :
                            (int)FingerSegmentType.Tip;
            if (segment == FingerSegmentType.Tip)
                segmentOffset = 3; // Thumb tip is at offset 3
        }

        // Calculate final node
        int nodeValue = baseOffset + segmentOffset;

        // Apply chirality
        if (chirality == Chirality.Right)
            nodeValue += (int)BodyNode.RIGHT_FINGER_START;
        else
            nodeValue += (int)BodyNode.LEFT_FINGER_START;

        return (BodyNode)nodeValue;
    }
}
