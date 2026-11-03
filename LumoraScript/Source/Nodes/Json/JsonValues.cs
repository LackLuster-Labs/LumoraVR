// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Linq;

namespace Lumora.Script;

// Every value in key order, as an array.
[Node("Data/JSON/Objects/Values", Description = "Every value in key order, as an array.")]
public sealed class JsonValues : ReadNode
{
    [return: Port("values")]
    public static ScriptJsonArray Run(ScriptContextBase ctx, ScriptJsonObject? @object)
        => @object == null ? ScriptJsonArray.Empty : ScriptJsonArray.Of(ctx.Collections, @object.Members.Select(member => member.Value));
}
