// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Lumora.Core.Assets;
using Lumora.Core.Components.Assets;
using Lumora.Shading;

namespace Lumora.Core.Scripting;

// Finding a material's properties and handing them out as fields. A node binds to the property itself, the thing
// a material panel's row hands over, and the script gets the property as a field of the type the node asks for,
// so the binding keeps through saves while the field is what every field node already takes. -xlinka
internal static class ShaderPropertyFields
{
    private static readonly ConditionalWeakTable<ShaderUniformParam, Dictionary<Type, IField>> Views = new();

    // A property of a graph or shader material by name: as the material lists it, else as the name a property node
    // was given ("Base Color" is base_color), else in any case. Nothing on any other kind of material.
    public static ShaderUniformParam? Find(IAssetProvider<MaterialAsset>? material, string? name)
    {
        if (material is not CustomShaderMaterial { IsDestroyed: false } custom || string.IsNullOrWhiteSpace(name))
            return null;
        foreach (var param in custom.Parameters)
        {
            if (param.Name.Value == name)
                return param;
        }
        var uniform = GlslNames.FromText(name, string.Empty);
        if (uniform.Length > 0 && uniform != name)
        {
            foreach (var param in custom.Parameters)
            {
                if (param.Name.Value == uniform)
                    return param;
            }
        }
        foreach (var param in custom.Parameters)
        {
            if (string.Equals(param.Name.Value, name, StringComparison.OrdinalIgnoreCase))
                return param;
        }
        return null;
    }

    // A graph or shader material's main colour: its colour property called Color or Base Color, else the first colour
    // property it has. Null when it has no colour property.
    public static ShaderUniformParam? MainColor(CustomShaderMaterial? material)
    {
        if (material == null || material.IsDestroyed)
            return null;
        ShaderUniformParam? first = null;
        foreach (var param in material.Parameters)
        {
            if (!param.IsColor.Value || !ShaderPropertyLanes.Fits(typeof(Math.colorHDR), param))
                continue;
            if (string.Equals(param.Name.Value, "color", StringComparison.OrdinalIgnoreCase)
                || string.Equals(param.Name.Value, "base_color", StringComparison.OrdinalIgnoreCase))
                return param;
            first ??= param;
        }
        return first;
    }

    // A texture property by name, the way Find finds one.
    public static ShaderUniformParam? Texture(IAssetProvider<MaterialAsset>? material, string? name)
        => Find(material, name) is { Type.Value: ShaderUniformType.Texture2D } param ? param : null;

    public static ShaderPropertyField<T> Of<T>(ShaderUniformParam param) => (ShaderPropertyField<T>)For(param, typeof(T));

    public static IField For(ShaderUniformParam param, Type type)
    {
        var views = Views.GetValue(param, static _ => new Dictionary<Type, IField>());
        lock (views)
        {
            if (!views.TryGetValue(type, out var view))
            {
                view = (IField)Activator.CreateInstance(typeof(ShaderPropertyField<>).MakeGenericType(type), param)!;
                views[type] = view;
            }
            return view;
        }
    }

    // The property as the field a port of this type takes, or null when it is not of that type.
    public static IField? As(ShaderUniformParam param, Type wanted)
    {
        if (param.IsDestroyed)
            return null;
        if (wanted == typeof(IField))
            return ShaderPropertyLanes.Natural(param) is { } natural ? For(param, natural) : null;
        if (!wanted.IsGenericType || wanted.GetGenericTypeDefinition() != typeof(IField<>))
            return null;
        var type = wanted.GetGenericArguments()[0];
        return ShaderPropertyLanes.IsLaneType(type) && ShaderPropertyLanes.Fits(type, param) ? For(param, type) : null;
    }

    public static IField? Natural(ShaderUniformParam param) => As(param, typeof(IField));

    public static bool Accepts(IWorldElement? element, Type wanted) => element is ShaderUniformParam param && As(param, wanted) != null;

    // What a script is handed for something bound to a port of this type: a property as its field, else as it is.
    public static object? Adapt(object? bound, Type wanted)
        => bound is ShaderUniformParam param && As(param, wanted) is { } field ? field : bound;

    // What a field view was made from, for comparing with what is bound.
    public static object? Unwrap(object? value) => value is IFieldView view ? view.Owner : value;
}
