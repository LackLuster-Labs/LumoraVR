using System;

namespace Lumora.Core;

/// <summary>
/// A field drive that continuously updates a target Sync field with values from a source.
/// Used for driving IK bones, blendshapes, and other synchronized properties.
/// Extends FieldHook to support hook interception patterns.
/// </summary>
/// <typeparam name="T">The type of value being driven</typeparam>
public class FieldDrive<T> : FieldHook<T>
{
	private Func<T> _valueSource;

	/// <summary>
	/// This is a driving link, so always returns true.
	/// </summary>
	public override bool IsDriving => true;

	/// <summary>
	/// Driving links allow modification.
	/// </summary>
	public override bool IsModificationAllowed => true;

	public FieldDrive(World world) : base(world)
	{
	}

	/// <summary>
	/// Set the source function that provides values for the drive.
	/// </summary>
	/// <param name="source">Function that returns the value to drive</param>
	public void DriveFrom(Func<T> source)
	{
		_valueSource = source;
	}

	/// <summary>
	/// Set the target field to drive.
	/// </summary>
	/// <param name="target">The Sync field to drive</param>
	public void DriveTarget(SyncField<T> target)
	{
		// Use the base class HookTarget method
		HookTarget(target);
	}

	/// <summary>
	/// Update the drive by pushing the current value from the source to the target.
	/// Call this each frame to keep the driven value up to date.
	/// </summary>
	public void UpdateDrive()
	{
		if (!IsActive || Target == null || _valueSource == null)
			return;

		try
		{
			T value = _valueSource();
			// Get the target as SyncField<T> and call SetDrivenValue
			if (Target is SyncField<T> syncTarget)
			{
				syncTarget.SetDrivenValue(value);
			}
		}
		catch (Exception ex)
		{
			Logging.Logger.Error($"FieldDrive UpdateDrive error: {ex.Message}");
		}
	}

	/// <summary>
	/// Directly set a value to the driven target field.
	/// Used when the value is computed externally rather than from a source function.
	/// </summary>
	public void SetValue(T value)
	{
		if (!IsActive || Target == null)
			return;

		if (Target is SyncField<T> syncTarget)
		{
			syncTarget.SetDrivenValue(value);
		}
	}
}

/// <summary>
/// Helper extensions for creating and managing field drives.
/// </summary>
public static class FieldDriveExtensions
{
	/// <summary>
	/// Create a field drive that drives this Sync field from a source function.
	/// </summary>
	public static FieldDrive<T> CreateDrive<T>(this SyncField<T> target, Func<T> source)
	{
		var drive = new FieldDrive<T>(target.World);
		drive.DriveFrom(source);
		drive.DriveTarget(target);
		return drive;
	}
}
