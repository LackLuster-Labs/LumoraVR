// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;

namespace Lumora.Fluids;

// Restore before locomotion mode changes can snapshot a fluid-scaled speed as the user's own pace. -xlinka
[ComponentCategory("Hidden")]
[DefaultUpdateOrder(-499999)]
public sealed class FluidFootingRestore : Component
{
    public override void OnUpdate(float delta) => FluidCoupling.Find(World)?.Footing.Restore();

    public override void OnDestroy()
    {
        FluidCoupling.Find(World)?.Footing.Restore();
        base.OnDestroy();
    }
}
