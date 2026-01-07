using System;
using System.IO;
using Lumora.Core;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Networking.Sync;

public class WorkerBag<C> : SyncRefIDBagBase<C> where C : ComponentBase<C>
{
    protected override void EncodeElement(BinaryWriter writer, C element)
    {
        if (element == null)
        {
            writer.Write("");
            return;
        }

        World?.Workers?.EncodeType(writer, element.GetType());
    }

    protected override C DecodeElement(BinaryReader reader)
    {
        throw new InvalidOperationException("WorkerBag requires CreateElementWithKey.");
    }

    protected override void SkipElement(BinaryReader reader)
    {
        if (World?.Workers != null)
        {
            World.Workers.DecodeType(reader);
            return;
        }

        int index = (int)reader.Read7BitEncoded();
        if (index == 0)
        {
            _ = reader.ReadString();
        }
    }

    protected override C CreateElementWithKey(RefID key, BinaryReader reader)
    {
        if (World?.Workers == null)
            return null;

        var type = World.Workers.DecodeType(reader);
        if (type == null || !typeof(C).IsAssignableFrom(type))
        {
            AquaLogger.Error($"WorkerBag: Unknown component type for {key}");
            return null;
        }

        return (C)WorkerManager.Instantiate(type);
    }
}
