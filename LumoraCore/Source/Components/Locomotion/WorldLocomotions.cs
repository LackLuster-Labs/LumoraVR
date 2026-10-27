// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Components;

// The locomotions this world hands everyone in it. Each machine gives its own user a copy of every locomotion
// under Modules, beside their own ones in the picker under the names the world gave them, and takes the copy away
// again when this goes: the world deleted, or left. A user who arrives later, or comes back with a new body, gets
// theirs then.
//
// Default is the one put on when the copy arrives. With KeepPreferred, a locomotion the person picked in their
// own settings wins if they have one by that name. -xlinka
[ComponentCategory("Users/Locomotion")]
public sealed class WorldLocomotions : Component
{
    public readonly SyncRef<Slot> Modules = new();

    public readonly SyncRef<LocomotionModule> Default = new();

    public readonly Sync<bool> KeepPreferred = new();

    private LocomotionController? _installedOn;
    private Slot? _copy;

    public override void OnInit()
    {
        base.OnInit();
        KeepPreferred.Value = true;
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        var templates = Modules.Target;
        if (templates == null || templates.IsDestroyed)
        {
            Withdraw();
            return;
        }
        var controller = World?.LocalUser?.Root?.Slot?.GetComponent<LocomotionController>();
        // After the controller has put on its own first choice, so the world's default is the one left on.
        if (controller == null || controller.IsDestroyed || controller.UserRoot == null || controller.ActiveModule == null)
            return;
        if (ReferenceEquals(controller, _installedOn) && _copy is { IsDestroyed: false })
            return;

        Withdraw();
        _copy = controller.InstallCopy(templates, clearExisting: false);
        _installedOn = controller;
        if (_copy != null)
            PutOnDefault(controller, templates, _copy);
    }

    public override void OnDestroy()
    {
        Withdraw();
        base.OnDestroy();
    }

    private void Withdraw()
    {
        var copy = _copy;
        _copy = null;
        _installedOn = null;
        if (copy == null || copy.IsDestroyed)
            return;
        using (World?.DataModelPermissions?.EnterSystemBypass())
            copy.Destroy();
    }

    private void PutOnDefault(LocomotionController controller, Slot templates, Slot copy)
    {
        var wanted = Default.Target;
        if (wanted == null || wanted.IsDestroyed)
            return;
        if (KeepPreferred.Value && !string.IsNullOrEmpty(EngineSettings.PreferredLocomotion))
        {
            foreach (var module in controller.Modules)
            {
                if (string.Equals(module.DisplayName, EngineSettings.PreferredLocomotion, System.StringComparison.OrdinalIgnoreCase)
                    && controller.IsModuleUsable(module))
                    return;
            }
        }
        var originals = templates.GetComponentsInChildren<LocomotionModule>();
        var copies = copy.GetComponentsInChildren<LocomotionModule>();
        int index = -1;
        int at = 0;
        foreach (var module in originals)
        {
            if (ReferenceEquals(module, wanted))
            {
                index = at;
                break;
            }
            at++;
        }
        at = 0;
        foreach (var module in copies)
        {
            if (at++ != index)
                continue;
            if (controller.IsModuleUsable(module))
            {
                using (World?.DataModelPermissions?.EnterSystemBypass())
                    controller.ActivateModule(module);
            }
            return;
        }
    }
}
