// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Core.Assets;

// How many shaders a material wears that the platform layer still has queued or building. The world
// manager holds a new world's focus while this is above zero, so the first compiles land before the user
// is standing in front of them. The platform side sets the provider at start-up; with none set it reads zero.
public static class ShaderBuildStatus
{
    public static Func<int>? PendingProvider { get; set; }

    public static int Pending => PendingProvider?.Invoke() ?? 0;
}
