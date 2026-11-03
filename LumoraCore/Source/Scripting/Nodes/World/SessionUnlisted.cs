// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Whether the session keeps itself out of session lists.
[Node("World/Session/Hidden From Lists", Capability.WorldRead, Description = "Whether the session keeps itself out of session lists.")]
public sealed class SessionUnlisted : ReadNode
{
    [return: Port("hidden")]
    public static bool Run(ScriptContext ctx) => ScriptSessionReads.Settings(ctx)?.HideFromSessionLists.Value ?? false;
}
