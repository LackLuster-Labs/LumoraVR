using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Lumora.Core.Math;

namespace Lumora.Core.Assets;

/// <summary>
/// Parses Godot gdshader uniform declarations into definitions.
/// </summary>
public static class ShaderUniformParser
{
    private static readonly Regex UniformRegex = new(
        @"^\s*uniform\s+(?<type>\w+)\s+(?<name>\w+)\s*(?::\s*(?<hint>[^=;]+))?\s*(?:=\s*(?<default>[^;]+))?;",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public sealed class Definition
    {
        public string Name { get; set; } = string.Empty;
        public ShaderUniformType Type { get; set; }
        public float4 DefaultValue { get; set; }
        public bool HasDefault { get; set; }
        public bool IsColor { get; set; }
        public bool HasRange { get; set; }
        public float2 Range { get; set; }
    }

    public static List<Definition> Parse(string shaderCode)
    {
        var results = new List<Definition>();
        if (string.IsNullOrWhiteSpace(shaderCode))
        {
            return results;
        }

        foreach (Match match in UniformRegex.Matches(shaderCode))
        {
            var typeText = match.Groups["type"].Value.Trim();
            var name = match.Groups["name"].Value.Trim();
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (!TryMapType(typeText, out var uniformType))
            {
                continue;
            }

            var def = new Definition
            {
                Name = name,
                Type = uniformType
            };

            var hintText = match.Groups["hint"].Value;
            if (!string.IsNullOrEmpty(hintText))
            {
                ParseHints(hintText, def);
            }

            var defaultText = match.Groups["default"].Value;
            if (!string.IsNullOrEmpty(defaultText))
            {
                if (TryParseDefault(uniformType, defaultText, out var defaultValue))
                {
                    def.DefaultValue = defaultValue;
                    def.HasDefault = true;
                }
            }

            results.Add(def);
        }

        return results;
    }

    private static bool TryMapType(string typeText, out ShaderUniformType type)
    {
        switch (typeText)
        {
            case "float":
                type = ShaderUniformType.Float;
                return true;
            case "vec2":
                type = ShaderUniformType.Vec2;
                return true;
            case "vec3":
                type = ShaderUniformType.Vec3;
                return true;
            case "vec4":
                type = ShaderUniformType.Vec4;
                return true;
            case "int":
                type = ShaderUniformType.Int;
                return true;
            case "bool":
                type = ShaderUniformType.Bool;
                return true;
            case "sampler2D":
                type = ShaderUniformType.Texture2D;
                return true;
            default:
                type = default;
                return false;
        }
    }

    private static void ParseHints(string hintText, Definition def)
    {
        var hints = hintText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var hint in hints)
        {
            if (hint.StartsWith("hint_range", StringComparison.OrdinalIgnoreCase))
            {
                var range = ParseRange(hint);
                if (range.HasValue)
                {
                    def.HasRange = true;
                    def.Range = range.Value;
                }
            }
            else if (hint.Equals("source_color", StringComparison.OrdinalIgnoreCase) ||
                     hint.Equals("hint_color", StringComparison.OrdinalIgnoreCase))
            {
                def.IsColor = true;
            }
        }
    }

    private static float2? ParseRange(string hint)
    {
        var start = hint.IndexOf('(');
        var end = hint.IndexOf(')');
        if (start < 0 || end <= start)
        {
            return null;
        }

        var inner = hint.Substring(start + 1, end - start - 1);
        var parts = inner.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        if (TryParseFloat(parts[0], out var min) && TryParseFloat(parts[1], out var max))
        {
            return new float2(min, max);
        }

        return null;
    }

    private static bool TryParseDefault(ShaderUniformType type, string text, out float4 value)
    {
        value = default;
        text = text.Trim();

        switch (type)
        {
            case ShaderUniformType.Float:
                if (TryParseFloat(text, out var f))
                {
                    value = new float4(f, 0f, 0f, 0f);
                    return true;
                }
                return false;
            case ShaderUniformType.Int:
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                {
                    value = new float4(i, 0f, 0f, 0f);
                    return true;
                }
                return false;
            case ShaderUniformType.Bool:
                if (bool.TryParse(text, out var b))
                {
                    value = new float4(b ? 1f : 0f, 0f, 0f, 0f);
                    return true;
                }
                return false;
            case ShaderUniformType.Vec2:
                return TryParseVector(text, 2, out value);
            case ShaderUniformType.Vec3:
                return TryParseVector(text, 3, out value);
            case ShaderUniformType.Vec4:
                return TryParseVector(text, 4, out value);
            default:
                return false;
        }
    }

    private static bool TryParseVector(string text, int components, out float4 value)
    {
        value = default;
        var start = text.IndexOf('(');
        var end = text.LastIndexOf(')');
        if (start < 0 || end <= start)
        {
            return false;
        }

        var inner = text.Substring(start + 1, end - start - 1);
        var parts = inner.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < components)
        {
            return false;
        }

        var values = new float[4];
        for (int i = 0; i < components; i++)
        {
            if (!TryParseFloat(parts[i], out values[i]))
            {
                return false;
            }
        }

        value = new float4(values[0], values[1], values[2], values[3]);
        return true;
    }

    private static bool TryParseFloat(string text, out float value)
    {
        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
