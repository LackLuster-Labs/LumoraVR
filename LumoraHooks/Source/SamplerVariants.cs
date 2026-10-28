// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Godot;
using Lumora.Core.Assets;

namespace Lumora.Godot.Hooks;

// A picture's own filtering and wrapping, on the material slots it is bound to.
//
// Godot fixes a sampler's filter and repeat in the uniform's hints, so they belong to the shader, not to the
// texture. A material wearing a picture that asks for something other than what the slot declares wears a copy
// of its shader with that slot's hints rewritten: the shader's text with every include written out in place,
// the filter_ and repeat_ hints of the named samplers swapped, compiled once per text through CustomShaderCache
// and shared by every material asking the same of the same shader. A material whose pictures ask nothing new
// wears its shader untouched. -xlinka
internal static class SamplerVariants
{
    private static readonly Regex Include = new(@"^[ \t]*#include[ \t]+""([^""]+)""[^\r\n]*", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex Sampler = new(@"uniform\s+(?:(?:lowp|mediump|highp)\s+)?sampler2D\s+(\w+)\s*(?::\s*([^;]*))?;", RegexOptions.Compiled);

    private sealed class Parsed
    {
        public string Flat = string.Empty;
        // Each sampler's declared filter_ and repeat_ hints, null where it names none.
        public readonly Dictionary<string, (string? Filter, string? Repeat)> Declared = new(StringComparer.Ordinal);
    }

    private static readonly ConditionalWeakTable<Shader, Parsed> ParsedShaders = new();

    // What a slot asks of its picture, as hints; null keeps what the slot declares.
    public readonly record struct Wanted(string? Filter, string? Repeat);

    public static Wanted Ask(TextureFilter filter, (TextureWrapMode U, TextureWrapMode V)? wrap, string? declaredFilter)
    {
        bool mips = declaredFilter == null || declaredFilter.Contains("mipmap", StringComparison.Ordinal);
        string? hint = filter switch
        {
            TextureFilter.Point => mips ? "filter_nearest_mipmap" : "filter_nearest",
            TextureFilter.Linear => mips ? "filter_linear_mipmap" : "filter_linear",
            TextureFilter.Anisotropic => "filter_linear_mipmap_anisotropic",
            _ => null,
        };
        string? repeat = wrap is { } w ? (Clamps(w.U) && Clamps(w.V) ? "repeat_disable" : "repeat_enable") : null;
        return new Wanted(hint, repeat);
    }

    private static bool Clamps(TextureWrapMode mode) => mode is TextureWrapMode.Clamp or TextureWrapMode.ClampToBorder or TextureWrapMode.MirrorOnce;

    // The hints a shader declares for a sampler, or false when the shader has no such sampler2D.
    public static bool TryDeclared(Shader shader, string uniform, out (string? Filter, string? Repeat) declared)
        => Parse(shader).Declared.TryGetValue(uniform, out declared);

    // The source for this shader with the given samplers' hints replaced.
    public static string Build(Shader shader, IReadOnlyDictionary<string, Wanted> wanted)
    {
        var parsed = Parse(shader);
        return Sampler.Replace(parsed.Flat, match =>
        {
            string name = match.Groups[1].Value;
            if (!wanted.TryGetValue(name, out var want))
                return match.Value;
            var hints = new List<string>();
            string? filter = null, repeat = null;
            if (match.Groups[2].Success)
            {
                foreach (var raw in match.Groups[2].Value.Split(','))
                {
                    string hint = raw.Trim();
                    if (hint.Length == 0)
                        continue;
                    if (hint.StartsWith("filter_", StringComparison.Ordinal))
                        filter = hint;
                    else if (hint.StartsWith("repeat_", StringComparison.Ordinal))
                        repeat = hint;
                    else
                        hints.Add(hint);
                }
            }
            filter = want.Filter ?? filter;
            repeat = want.Repeat ?? repeat;
            if (filter != null)
                hints.Add(filter);
            if (repeat != null)
                hints.Add(repeat);
            var text = new StringBuilder("uniform sampler2D ").Append(name);
            if (hints.Count > 0)
                text.Append(" : ").Append(string.Join(", ", hints));
            return text.Append(';').ToString();
        });
    }

    private static Parsed Parse(Shader shader)
    {
        if (ParsedShaders.TryGetValue(shader, out var known))
            return known;
        var parsed = new Parsed { Flat = Flatten(shader.Code ?? string.Empty, 0) };
        foreach (Match match in Sampler.Matches(parsed.Flat))
        {
            string? filter = null, repeat = null;
            bool bound = true;
            if (match.Groups[2].Success)
            {
                foreach (var raw in match.Groups[2].Value.Split(','))
                {
                    string hint = raw.Trim();
                    if (hint.StartsWith("filter_", StringComparison.Ordinal))
                        filter = hint;
                    else if (hint.StartsWith("repeat_", StringComparison.Ordinal))
                        repeat = hint;
                    // The screen, depth and normal-roughness buffers are the renderer's, never a picture.
                    else if (hint.StartsWith("hint_screen", StringComparison.Ordinal) || hint.StartsWith("hint_depth", StringComparison.Ordinal)
                             || hint.StartsWith("hint_normal_roughness", StringComparison.Ordinal))
                        bound = false;
                }
            }
            if (bound)
                parsed.Declared.TryAdd(match.Groups[1].Value, (filter, repeat));
        }
        ParsedShaders.AddOrUpdate(shader, parsed);
        return parsed;
    }

    // Every include written out where it stands; their own guards still keep a second copy out.
    private static string Flatten(string code, int depth)
    {
        if (depth > 16)
            return code;
        return Include.Replace(code, match =>
        {
            var include = ResourceLoader.Load<ShaderInclude>(match.Groups[1].Value);
            return include == null ? match.Value : Flatten(include.Code ?? string.Empty, depth + 1);
        });
    }
}
