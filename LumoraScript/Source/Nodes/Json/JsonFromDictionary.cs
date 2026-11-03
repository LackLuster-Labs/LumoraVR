// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// A dictionary as a JSON object: keys as their text, values in their JSON form, in key order. Later changes
// to the dictionary do not reach it. No dictionary gives an empty object.
[Node("Data/JSON/Objects/From Dictionary", Description = "A dictionary as a JSON object: keys as their text, values in their JSON form, in key order.")]
public sealed class JsonFromDictionary<TKey, TValue> : ReadNode where TKey : notnull
{
    public static bool IsValidGenericType
        => ScriptJsonConvert.CarriesKey(typeof(TKey)) && ScriptJsonConvert.Carries(typeof(TValue));

    [return: Port("object")]
    public static ScriptJsonObject Run(ScriptContextBase ctx, ScriptDictionary<TKey, TValue>? dictionary)
        => dictionary == null ? ScriptJsonObject.Empty : (ScriptJsonObject)ScriptJsonConvert.From(ctx.Collections, dictionary);
}
