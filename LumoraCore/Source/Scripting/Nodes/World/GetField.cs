// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Reads a field. Host-only members read as their type's default: identity and allocation are not a
// script's business.
[Node("World/Fields/Get Field", Capability.WorldRead, Description = "Reads a field.",
    Keywords = "read value, property, member value")]
[Evaluates(Evaluation.Field)]
public sealed class GetField<T> : ReadNode
{
    public static T Run(ScriptContext ctx, IField<T>? field)
        => ScriptWrites.Readable(ctx, field) ? field!.Value : default!;
}
