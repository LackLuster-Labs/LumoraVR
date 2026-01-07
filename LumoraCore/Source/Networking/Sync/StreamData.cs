using System;
using System.IO;
using Lumora.Core.Math;

namespace Lumora.Core.Networking.Sync;

/// <summary>
/// Stream data types for high-frequency continuous data.
/// 
/// </summary>
public enum StreamType : int
{
    HeadTransform = 0,
    LeftHandTransform = 1,
    RightHandTransform = 2,
    Audio = 3,
    EyeTracking = 4,
    FaceTracking = 5
}

/// <summary>
/// Transform stream data (position, rotation, scale).
/// </summary>
public struct TransformStreamData
{
    public float3 Position;
    public floatQ Rotation;
    public float3 Scale;

    public byte[] Encode()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Position
        writer.Write(Position.x);
        writer.Write(Position.y);
        writer.Write(Position.z);

        // Rotation (quaternion)
        writer.Write(Rotation.x);
        writer.Write(Rotation.y);
        writer.Write(Rotation.z);
        writer.Write(Rotation.w);

        // Scale
        writer.Write(Scale.x);
        writer.Write(Scale.y);
        writer.Write(Scale.z);

        return ms.ToArray();
    }

    public static TransformStreamData Decode(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        return new TransformStreamData
        {
            Position = new float3(
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle()
            ),
            Rotation = new floatQ(
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle()
            ),
            Scale = new float3(
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle()
            )
        };
    }
}

/// <summary>
/// User stream bag - holds all stream data for a user.
/// 
/// </summary>
public class UserStreamBag
{
    public TransformStreamData? HeadTransform { get; set; }
    public TransformStreamData? LeftHandTransform { get; set; }
    public TransformStreamData? RightHandTransform { get; set; }

    public bool HasData => HeadTransform.HasValue ||
                          LeftHandTransform.HasValue ||
                          RightHandTransform.HasValue;

    public void Clear()
    {
        HeadTransform = null;
        LeftHandTransform = null;
        RightHandTransform = null;
    }
}
