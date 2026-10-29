// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Components.UI;
using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Components.Scripting;

// What the node editor looks like: card metrics in canvas units, the card's world scale, and the colours
// for categories, types and flow. One place, so the cards, the wires and the tool agree on where a port
// is and what colour it wears.
public static class ScriptEditorStyle
{
    // Sized to be read from a step back: a card is 24 cm across, port text 14 mm and the title 18 mm. At
    // 13 cm, 5.6 mm and regular weight the words thinned away to hairlines, and 9 mm was still a squint.
    // -xlinka
    public const float CardWidth = 400f;
    public const float HeaderHeight = 64f;
    public const float RowHeight = 48f;
    public const float Padding = 14f;
    public const float HandleSize = 20f;
    public const float CardScale = 0.0006f;
    public const float FontSize = 24f;
    public const float TitleSize = 30f;
    public const float CornerRadius = 16f;
    public const float PortInset = 36f;
    public const float ColumnGap = 24f;
    public const int MaxProblemLines = 2;

    public static readonly color Flow = new(0.86f, 0.52f, 0.25f, 1f);
    public static readonly color Problem = DashTheme.Negative;

    // A node the running script faulted in, lately; a wire a cut would take; the port a wire end is pulled
    // onto; flow moving along a wire.
    public static readonly color Fault = DashTheme.Warning;
    public static readonly color Cut = DashTheme.Negative;
    public static readonly color Snap = new(1f, 1f, 1f, 1f);
    public static readonly color Signal = new(1f, 0.93f, 0.78f, 1f);
    public static readonly color CardBackground = DashTheme.Panel;
    public static readonly color CardOutline = DashTheme.OutlineStrong;
    public static readonly color PortText = DashTheme.Text;
    public static readonly color HeaderButton = new(0f, 0f, 0f, 0.28f);

    // The header band: the category's colour, held well down toward the card so the white title on it stays
    // the brightest thing there. These colours are linear; at even half strength the world camera showed
    // them as a pale band the title washed out on. -xlinka
    public static color HeaderTint(string category) => color.Lerp(DashTheme.Surface, Category(category), 0.3f);

    public static color Category(string category) => category switch
    {
        "Interaction" => new color(0.72f, 0.28f, 0.32f, 1f),
        "Flow" => new color(0.62f, 0.38f, 0.18f, 1f),
        "Math" => new color(0.22f, 0.42f, 0.66f, 1f),
        "Logic" => new color(0.36f, 0.30f, 0.62f, 1f),
        "Strings" => new color(0.20f, 0.52f, 0.40f, 1f),
        "Variables" => new color(0.55f, 0.46f, 0.16f, 1f),
        "Graph" => new color(0.46f, 0.56f, 0.20f, 1f),
        "World" => new color(0.18f, 0.50f, 0.56f, 1f),
        "Network" => new color(0.24f, 0.62f, 0.66f, 1f),
        "Users" => new color(0.56f, 0.26f, 0.52f, 1f),
        "Time" => new color(0.34f, 0.46f, 0.30f, 1f),
        "Debug" => new color(0.40f, 0.40f, 0.44f, 1f),
        "Assets" => new color(0.48f, 0.42f, 0.24f, 1f),
        "Audio" => new color(0.30f, 0.56f, 0.42f, 1f),
        "Avatars" => new color(0.56f, 0.36f, 0.52f, 1f),
        "Data" => new color(0.40f, 0.48f, 0.48f, 1f),
        "Physics" => new color(0.50f, 0.44f, 0.24f, 1f),
        "Rendering" => new color(0.54f, 0.40f, 0.60f, 1f),
        "Transform" => new color(0.34f, 0.40f, 0.70f, 1f),
        "UI" => new color(0.36f, 0.52f, 0.58f, 1f),
        "Undo" => new color(0.42f, 0.46f, 0.30f, 1f),
        "Media" => new color(0.56f, 0.38f, 0.30f, 1f),
        // Input and Color are shared by both browsers; the rest are shader folders. -xlinka
        "Input" => new color(0.62f, 0.30f, 0.42f, 1f),
        "Properties" => new color(0.60f, 0.50f, 0.20f, 1f),
        "Texture" => new color(0.66f, 0.38f, 0.20f, 1f),
        "Procedural" => new color(0.30f, 0.52f, 0.30f, 1f),
        "UV" => new color(0.26f, 0.50f, 0.60f, 1f),
        "Color" => new color(0.66f, 0.40f, 0.56f, 1f),
        "Output" => new color(0.72f, 0.28f, 0.32f, 1f),
        _ => DashTheme.Surface,
    };

    public static color Type(ScriptType? type)
    {
        if (type == null)
            return Flow;
        switch (type.Kind)
        {
            case ScriptTypeKind.Bool:
                return new color(0.85f, 0.35f, 0.35f, 1f);
            case ScriptTypeKind.Int:
            case ScriptTypeKind.Long:
                return new color(0.30f, 0.78f, 0.62f, 1f);
            case ScriptTypeKind.Float:
            case ScriptTypeKind.Double:
                return new color(0.35f, 0.62f, 0.95f, 1f);
            case ScriptTypeKind.Vector:
                if (type.Name.StartsWith("int", StringComparison.Ordinal))
                    return new color(0.30f, 0.78f, 0.62f, 1f);
                if (type.Name.StartsWith("bool", StringComparison.Ordinal))
                    return new color(0.85f, 0.35f, 0.35f, 1f);
                if (!type.IsFloatVector || type.LaneCount > 4)
                    return new color(0.95f, 0.62f, 0.28f, 1f);
                return new color(0.95f, 0.80f, 0.30f, 1f);
            case ScriptTypeKind.Coded:
                if (type.ClrType.IsEnum)
                    return new color(0.70f, 0.55f, 0.95f, 1f);
                return type.Name switch
                {
                    "byte" or "sbyte" or "short" or "ushort" or "uint" or "ulong" => new color(0.30f, 0.78f, 0.62f, 1f),
                    "half" => new color(0.35f, 0.62f, 0.95f, 1f),
                    "char" => new color(0.90f, 0.45f, 0.80f, 1f),
                    "datetime" or "timespan" => new color(0.40f, 0.82f, 0.80f, 1f),
                    "guid" => new color(0.70f, 0.70f, 0.75f, 1f),
                    _ => new color(0.62f, 0.86f, 0.42f, 1f),
                };
        }
        if (type == ScriptTypes.String)
            return new color(0.90f, 0.45f, 0.80f, 1f);
        if (type.Name.StartsWith("field<", StringComparison.Ordinal) || type.Name == "reference")
            return new color(0.70f, 0.70f, 0.75f, 1f);
        if (type.Family is "list" or "dict")
            return new color(0.95f, 0.66f, 0.46f, 1f);
        return new color(0.55f, 0.85f, 0.95f, 1f);
    }

    public static float3 CanvasToLocal(float2 point) => new(point.x * CardScale, point.y * CardScale, 0f);
}
