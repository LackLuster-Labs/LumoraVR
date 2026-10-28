// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using Lumora.Core;
using Lumora.Core.Components;

namespace Lumora.Godot.Hooks;

// Puts the world's ambient light on the scene through SkyEnvironment, which lays it over whatever sky wins
// and puts the sky's own ambient back when this one stops counting.
[ImplementableHook(typeof(AmbientLight))]
public sealed class AmbientLightHook : ComponentHook<AmbientLight>
{
    private bool _claimed;

    public static IHook<AmbientLight> Constructor() => new AmbientLightHook();

    public override void Initialize()
    {
        base.Initialize();
        ApplyChanges();
    }

    public override void ApplyChanges()
    {
        if (Owner == null)
            return;
        if (Owner.IsActiveAmbient && Owner.Enabled && Owner.Slot?.IsActive == true)
        {
            var light = Owner.AverageLight;
            _claimed = true;
            SkyEnvironment.AmbientClaimed(this, Owner.World, new Color(light.x, light.y, light.z), attachedNode);
        }
        else if (_claimed)
        {
            _claimed = false;
            SkyEnvironment.AmbientReleased(this);
        }
    }

    public override void Destroy(bool destroyingWorld)
    {
        SkyEnvironment.AmbientReleased(this);
        _claimed = false;
        base.Destroy(destroyingWorld);
    }
}
