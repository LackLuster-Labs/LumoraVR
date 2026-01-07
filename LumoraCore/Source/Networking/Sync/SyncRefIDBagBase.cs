using System.IO;
using Lumora.Core;

namespace Lumora.Core.Networking.Sync;

public abstract class SyncRefIDBagBase<T> : SyncBagBase<RefID, T> where T : class, IWorldElement
{
    protected override void EncodeKey(BinaryWriter writer, RefID key)
    {
        writer.Write7BitEncoded((ulong)key);
    }

    protected override RefID DecodeKey(BinaryReader reader)
    {
        return new RefID(reader.Read7BitEncoded());
    }
}
