using System;
using System.IO;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.Networking.Streams;

/// <summary>
/// Synchronized bag of streams for a user.
/// Streams are synced over the network so clients receive streams with matching RefIDs.
/// User handles initialization via OnElementAdded event.
/// </summary>
public class SyncStreamBag : SyncRefIDBagBase<Stream>
{
    /// <summary>
    /// The user that owns this bag.
    /// </summary>
    public User Owner { get; private set; }

    public SyncStreamBag()
    {
    }

    /// <summary>
    /// Initialize the bag with its owning user.
    /// </summary>
    public void Initialize(User user)
    {
        Owner = user;
        base.Initialize(user.World, user);
    }

    /// <summary>
    /// All streams in the bag.
    /// </summary>
    public System.Collections.Generic.IEnumerable<Stream> Streams => Values;

    /// <summary>
    /// Update all streams.
    /// </summary>
    public void Update()
    {
        foreach (var stream in Values)
        {
            if (stream.Active)
            {
                stream.Update();
            }
        }
    }

    protected override void EncodeElement(BinaryWriter writer, Stream element)
    {
        var typeName = element.GetType().FullName;
        writer.Write(typeName ?? "");
    }

    protected override Stream DecodeElement(BinaryReader reader)
    {
        throw new InvalidOperationException("SyncStreamBag requires CreateElementWithKey");
    }

    protected override void SkipElement(BinaryReader reader)
    {
        reader.ReadString();
    }

    protected override Stream CreateElementWithKey(RefID key, BinaryReader reader)
    {
        var typeName = reader.ReadString();

        Stream stream = typeName switch
        {
            "Lumora.Core.Networking.Streams.Float3ValueStream" => new Float3ValueStream(),
            "Lumora.Core.Networking.Streams.FloatQValueStream" => new FloatQValueStream(),
            _ => throw new InvalidOperationException($"Unknown stream type: {typeName}")
        };

        return stream;
    }

    public override void Dispose()
    {
        base.Dispose();
    }
}
