// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Components.IK;

/// <summary>
/// Solves inverse kinematics constraints for bone hierarchies.
/// Attach to the skeleton root slot alongside BipedRig.
/// </summary>
[ComponentCategory("IK")]
public class IKSolver : Component
{
    public readonly Sync<bool> Enabled = new();
    public readonly Sync<float> TimeStepDuration = new();
    public readonly Sync<int> ControlIterations = new();
    public readonly Sync<int> FixerIterations = new();

    public override void OnInit()
    {
        base.OnInit();
        Enabled.Value = true;
        TimeStepDuration.Value = 0.02f;
        ControlIterations.Value = 4;
        FixerIterations.Value = 4;
    }
}
