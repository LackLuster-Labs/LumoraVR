using System;
using System.Collections.Generic;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core;

/// <summary>
/// Trash system for temporarily holding deleted objects until authority confirms deletion.
/// Prevents permanent data loss from rejected delete operations.
/// </summary>
public class TrashBin
{
	private readonly Dictionary<RefID, TrashEntry> _trashedElements = new();
	private readonly World _world;

    /// <summary>
    /// How long to keep items in trash before permanent deletion (in seconds).
    /// </summary>
    public double TrashRetentionTime { get; set; } = 60.0; // 1 minute default

    public TrashBin(World world)
    {
        _world = world;
    }

    /// <summary>
    /// Move an element to trash instead of destroying it immediately.
    /// </summary>
    public void MoveToTrash(IWorldElement element)
    {
        if (element == null || element.IsDestroyed)
            return;

		var entry = new TrashEntry
		{
			Element = element,
			TrashedTime = _world.TotalTime,
			RefID = element.ReferenceID
		};

		_trashedElements[element.ReferenceID] = entry;

		// Mark as destroyed to prevent further use, but don't actually destroy yet
		// This allows recovery if deletion is rejected
		AquaLogger.Debug($"Moved element {element.ReferenceID} to trash");
	}

	/// <summary>
	/// Restore an element from trash (if deletion was rejected by authority).
	/// </summary>
	public bool RestoreFromTrash(RefID refID)
	{
		if (!_trashedElements.TryGetValue(refID, out var entry))
		{
			AquaLogger.Warn($"Cannot restore {refID} - not in trash");
			return false;
		}

        _trashedElements.Remove(refID);

        // Unmark as destroyed and re-register with world
        if (entry.Element is Slot slot)
        {
            _world.RegisterSlot(slot);
        }
        else if (entry.Element is Component component)
        {
            _world.RegisterComponent(component);
        }

        AquaLogger.Log($"Restored element {refID} from trash");
        return true;
    }

	/// <summary>
	/// Permanently delete an element from trash (authority confirmed deletion).
	/// </summary>
	public void PermanentlyDelete(RefID refID)
	{
		if (!_trashedElements.TryGetValue(refID, out var entry))
		{
			return; // Already deleted
		}

        _trashedElements.Remove(refID);

        // Now actually destroy the element
        if (entry.Element is Slot slot)
        {
            slot.Destroy();
        }
        else if (entry.Element is Component component)
        {
            component.Destroy();
        }

        AquaLogger.Debug($"Permanently deleted element {refID}");
    }

	/// <summary>
	/// Update the trash bin and clean up expired entries.
	/// Call this periodically from World._Process.
	/// </summary>
	public void Update()
	{
		var currentTime = _world.TotalTime;
		var toRemove = new List<RefID>();

        foreach (var kvp in _trashedElements)
        {
            var entry = kvp.Value;
            var timeInTrash = currentTime - entry.TrashedTime;

            // If element has been in trash longer than retention time, permanently delete it
            if (timeInTrash > TrashRetentionTime)
            {
                toRemove.Add(kvp.Key);
            }
        }

        // Permanently delete expired entries
        foreach (var refID in toRemove)
        {
            PermanentlyDelete(refID);
            AquaLogger.Debug($"Auto-deleted expired trash entry {refID}");
        }
    }

	/// <summary>
	/// Check if an element is in trash.
	/// </summary>
	public bool IsInTrash(RefID refID)
	{
		return _trashedElements.ContainsKey(refID);
	}

    /// <summary>
    /// Clear all trash (emergency cleanup).
    /// </summary>
    public void Clear()
    {
        foreach (var entry in _trashedElements.Values)
        {
            if (entry.Element is Slot slot)
            {
                slot.Destroy();
            }
            else if (entry.Element is Component component)
            {
                component.Destroy();
            }
        }

        _trashedElements.Clear();
        AquaLogger.Log("Cleared trash bin");
    }

    /// <summary>
    /// Get statistics about trash contents.
    /// </summary>
    public (int count, int slots, int components) GetStatistics()
    {
        int slots = 0;
        int components = 0;

        foreach (var entry in _trashedElements.Values)
        {
            if (entry.Element is Slot) slots++;
            else if (entry.Element is Component) components++;
        }

        return (_trashedElements.Count, slots, components);
    }
}

/// <summary>
/// Entry in the trash bin.
/// </summary>
internal class TrashEntry
{
	public IWorldElement Element { get; set; }
	public RefID RefID { get; set; }
	public double TrashedTime { get; set; }
}
