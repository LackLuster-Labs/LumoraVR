// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Fluids;

// The module's keys in the Settings store. Keys under Modules.Account follow the account through FluidAccountSync.
public static class FluidSettingKeys
{
    // int, -1 Auto, 0 view only, 1-5 Standalone to Ultra; this machine only
    public const string Quality = "Modules.Fluids.Quality";
    public const string QualityVR = "Modules.Fluids.QualityVR";
    // int FluidStickMode, follows the account
    public const string StickMode = "Modules.Account.Fluids.StickMode";
    public const string StickModeChanged = "Modules.Account.Fluids.StickMode.Changed";
    // this machine's GPU calibration
    public const string Calibration = "Modules.Fluids.Calibration";
}
