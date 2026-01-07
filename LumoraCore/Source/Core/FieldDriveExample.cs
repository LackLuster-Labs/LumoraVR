using System;
using Lumora.Core;
using Lumora.Core.Math;

namespace Lumora.Examples;

/// <summary>
/// Example demonstrating how to use FieldDrive for IK bone driving.
/// This is a reference implementation - not meant for production use.
/// </summary>
public class FieldDriveExample
{
    /// <summary>
    /// Example: Drive a bone's position from an IK target.
    /// </summary>
    public static void DrivePositionExample(World world, Sync<float3> bonePosition, Func<float3> ikTargetPosition)
    {
        // Create a field drive that will continuously update the bone position
        var drive = new FieldDrive<float3>(world);

        // Set the source function that provides the IK target position
        drive.DriveFrom(ikTargetPosition);

        // Set the target field to drive
        drive.DriveTarget(bonePosition);

        // Now the bone position will be driven by the IK target
        // Call drive.UpdateDrive() each frame to push the latest value
        // The Sync field will reject direct value sets while driven
    }

    /// <summary>
    /// Example: Drive a bone's rotation from an IK solver.
    /// </summary>
    public static void DriveRotationExample(World world, Sync<floatQ> boneRotation, Func<floatQ> ikSolverRotation)
    {
        // Create and configure the drive
        var drive = new FieldDrive<floatQ>(world);
        drive.DriveFrom(ikSolverRotation);
        drive.DriveTarget(boneRotation);

        // The rotation is now driven
        // Update each frame with drive.UpdateDrive()
    }

    /// <summary>
    /// Example: Using the extension method for cleaner syntax.
    /// </summary>
    public static void DriveWithExtensionExample(Sync<float> blendshapeWeight, Func<float> expressionValue)
    {
        // Create and configure a drive in one line
        var drive = blendshapeWeight.CreateDrive(expressionValue);

        // Update each frame
        drive.UpdateDrive();
    }

    /// <summary>
    /// Example: Updating drives in a component's update loop.
    /// </summary>
    public class IKDriverComponent
    {
        private FieldDrive<float3> _positionDrive;
        private FieldDrive<floatQ> _rotationDrive;
        private Func<float3> _positionSource;
        private Func<floatQ> _rotationSource;

        public void Initialize(World world, Sync<float3> targetPosition, Sync<floatQ> targetRotation)
        {
            // Setup position drive
            _positionDrive = new FieldDrive<float3>(world);
            _positionSource = () => ComputeIKPosition();
            _positionDrive.DriveFrom(_positionSource);
            _positionDrive.DriveTarget(targetPosition);

            // Setup rotation drive
            _rotationDrive = new FieldDrive<floatQ>(world);
            _rotationSource = () => ComputeIKRotation();
            _rotationDrive.DriveFrom(_rotationSource);
            _rotationDrive.DriveTarget(targetRotation);
        }

        public void Update()
        {
            // Update both drives each frame
            _positionDrive?.UpdateDrive();
            _rotationDrive?.UpdateDrive();
        }

        public void Cleanup()
        {
            // Release drives when done
            _positionDrive?.Release();
            _rotationDrive?.Release();
        }

        private float3 ComputeIKPosition()
        {
            // Your IK solver logic here
            return new float3(0, 0, 0);
        }

        private floatQ ComputeIKRotation()
        {
            // Your IK solver logic here
            return floatQ.Identity;
        }
    }

    /// <summary>
    /// Example: Checking if a field is driven before modifying it.
    /// </summary>
    public static void CheckDrivenExample(Sync<float3> position)
    {
        if (position.IsDriven)
        {
            Console.WriteLine("Position is being driven by an IK system");
            Console.WriteLine($"Active link: {position.ActiveLink}");
            Console.WriteLine($"Is hooked: {position.IsHooked}");
        }
        else
        {
            // Safe to set directly
            position.Value = new float3(1, 2, 3);
        }
    }

    /// <summary>
    /// Example: Releasing a drive to restore manual control.
    /// </summary>
    public static void ReleaseDriveExample(FieldDrive<float3> drive, Sync<float3> position)
    {
        // Release the drive
        drive.ReleaseLink();

        // Or fully dispose
        drive.Release();

        // Now the position can be set manually again
        position.Value = new float3(0, 0, 0);
    }
}
