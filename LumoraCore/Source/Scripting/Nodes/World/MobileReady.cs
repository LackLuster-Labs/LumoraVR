// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Whether the world's settings mark it as fit for standalone headsets and phones.
[Node("World/Session/Mobile Friendly", Capability.WorldRead, Description = "Whether the world's settings mark it as fit for standalone headsets and phones.",
    Keywords = "standalone, phone, low end")]
public sealed class MobileReady : ReadNode
{
    [return: Port("friendly")]
    public static bool Run(ScriptContext ctx) => ScriptSessionReads.Settings(ctx)?.MobileFriendly.Value ?? false;
}
