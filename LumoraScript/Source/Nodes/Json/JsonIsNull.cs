// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Whether a JSON value is null, or there is no value at all.
[Node("Data/JSON/Values/Is Null", Description = "Whether a JSON value is null, or there is no value at all.")]
public sealed class JsonIsNull : ValueNode
{
    [return: Port("null")]
    public static bool Run(ScriptJson? json) => json == null || json.Kind == ScriptJsonKind.Null;
}
