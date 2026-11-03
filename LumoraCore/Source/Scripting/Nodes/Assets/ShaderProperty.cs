// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Assets;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// A property of a shader graph's or a shader's material as a field, for Get Field, Set Field, Tween Field and the
// rest, which judge every write as the author's own. name is the property as the material lists it or as its
// property node was named ("Base Color"). The material keeps each property in four numbers: a float is the first, a
// colour all four, a switch is on from a half up. Nothing when the material has no property of that name and type,
// or is no shader material. To hold one with Drive Field, drop the property's row from the material panel on the
// drive's field. -xlinka
[Node("Assets/Materials/Shader Property", Capability.WorldRead, Description = "Finds a named material property as a field so it can be read, changed or animated.",
    Keywords = "material value, material color, shader parameter, material field")]
public sealed class ShaderProperty<T> : ReadNode
{
    public static bool IsValidGenericType => ShaderPropertyLanes.IsLaneType(typeof(T));

    [return: Port("field")]
    public static IField<T>? Run(ScriptContext ctx, IAssetProvider<MaterialAsset>? material, string? name)
    {
        if (!ScriptReads.Visible(ctx, material as Component))
            return null;
        var param = ShaderPropertyFields.Find(material, name);
        return param != null && ShaderPropertyLanes.Fits(typeof(T), param) ? ShaderPropertyFields.Of<T>(param) : null;
    }
}
