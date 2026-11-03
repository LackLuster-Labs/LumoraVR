// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// A JSON object as a new dictionary, each key read from its text as the key type and each value as the
// value type. A key that does not read is left out and a value that does not read is the type's default,
// and ok is then false. No object gives an empty dictionary.
[Node("Data/JSON/Objects/To Dictionary", Description = "A JSON object as a new dictionary, each key read from its text as the key type and each value as the value type.")]
public sealed class JsonToDictionary<TKey, TValue> : ReadNode where TKey : notnull
{
    private static readonly ScriptType Type = ScriptTypes.Get(typeof(ScriptDictionary<TKey, TValue>));

    public static bool IsValidGenericType
        => ScriptJsonConvert.CarriesKey(typeof(TKey)) && ScriptJsonConvert.Carries(typeof(TValue));

    [return: Port("dictionary")]
    public static ScriptDictionary<TKey, TValue> Run(ScriptContextBase ctx, ScriptJsonObject? @object, out bool ok)
    {
        ok = ScriptJsonConvert.TryTo(ctx.Collections, @object, Type, out var dictionary);
        return dictionary as ScriptDictionary<TKey, TValue> ?? new ScriptDictionary<TKey, TValue>(ctx.Collections);
    }
}
