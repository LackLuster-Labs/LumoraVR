using Godot;

namespace Aquamarine.Kinetix.Core;

public class IKTarget
{
    public Vector3 Position { get; set; }
    public Quaternion? Rotation { get; set; }
    public Vector3? PolePosition { get; set; }
    public float PoleTwist { get; set; } = 0f;

    public static IKTarget FromNode(Node3D targetNode, Node3D poleNode = null)
    {
        if (targetNode == null) return null;

        return new IKTarget
        {
            Position = targetNode.GlobalPosition,
            Rotation = targetNode.GlobalBasis.GetRotationQuaternion(),
            PolePosition = poleNode?.GlobalPosition
        };
    }

    public static IKTarget FromTransform(Transform3D transform, Vector3? polePosition = null)
    {
        return new IKTarget
        {
            Position = transform.Origin,
            Rotation = transform.Basis.GetRotationQuaternion(),
            PolePosition = polePosition
        };
    }

    public IKTarget Clone()
    {
        return new IKTarget
        {
            Position = Position,
            Rotation = Rotation,
            PolePosition = PolePosition,
            PoleTwist = PoleTwist
        };
    }
}
