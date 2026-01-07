using System;
using System.IO;
using Lumora.Core;

namespace Lumora.Core.Networking.Sync;

/// <summary>
/// Network bag for Slots.
/// When a FullBatch is received, this creates the Slot objects that don't exist yet.
/// </summary>
public class SlotBag : SyncRefIDBagBase<Slot>
{
    public SlotBag()
    {
        // Hook into element added to initialize slots
        OnElementAdded += HandleSlotAdded;
    }

    /// <summary>
    /// Encode slot data to the stream.
    /// Slot data itself is empty here - the slot's sync members are encoded separately
    /// as their own SyncElements with their own RefIDs.
    /// </summary>
    protected override void EncodeElement(BinaryWriter writer, Slot element)
    {
        // Intentionally empty - slot structure is encoded in the key list,
        // slot VALUES (name, position, etc.) are synced as separate SyncElements
    }

    /// <summary>
    /// Decode (CREATE) a Slot from the stream.
    /// This is the magic - it creates a new Slot object during network decode!
    /// </summary>
    protected override Slot DecodeElement(BinaryReader reader)
    {
        // Create a new slot - it will be initialized in HandleSlotAdded
        return new Slot();
    }

    /// <summary>
    /// Handle slot addition - initialize and register with World.
    /// </summary>
    private void HandleSlotAdded(ReplicatedDictionary<RefID, Slot> dict, RefID key, Slot slot, bool isNew)
    {
        if (World == null) return;

        // Only initialize if this slot doesn't already have a world
        if (slot.World == null)
        {
            Lumora.Core.Logging.Logger.Log($"SlotBag.HandleSlotAdded: Creating slot RefID={key}, isNew={isNew}");

            // Initialize with the world using the network-assigned RefID
            slot.InitializeFromReplicator(World, key);

            // Keep sync members in loading state until their values
            // are decoded from FullBatch. This prevents them from being marked dirty
            // if the world transitions to Running before all values are decoded.
            foreach (var member in slot.SyncMembers)
            {
                if (member is SyncElement syncElement)
                {
                    syncElement.IsLoading = true;
                }
            }

            Lumora.Core.Logging.Logger.Log($"SlotBag.HandleSlotAdded: Slot initialized - Name='{slot.Name?.Value}', NameRefID={slot.Name?.ReferenceID}");

            // Register with the world's slot tracking
            World.RegisterSlot(slot);
        }
    }

    public override void Dispose()
    {
        OnElementAdded -= HandleSlotAdded;
        base.Dispose();
    }
}
