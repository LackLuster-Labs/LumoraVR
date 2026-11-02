// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Lumora.Script;

namespace Lumora.Shading;

// The shader node registry: Loom's registry over this assembly, so shader nodes never show up in a script's
// browser and a shader Add never clashes with Loom's Add, and Loom's own value nodes by the overloads that carry
// [Gpu] shader code, unless a shader node already goes by the name: one node for scripts and shaders alike. LumoraCore registers its "texture"
// handle before it first asks for this; with no LumoraCore about (a graph compiled on its own), the
// stand-in becomes the texture type itself. -xlinka
public static class ShaderNodes
{
    private static readonly object _lock = new();
    private static NodeRegistry? _registry;

    public static NodeRegistry Registry
    {
        get
        {
            var registry = _registry;
            if (registry != null)
                return registry;
            lock (_lock)
            {
                if (_registry == null)
                {
                    RegisterTypes();
                    var own = typeof(ShaderNode).Assembly;
                    var taken = new HashSet<string>(own.GetTypes().Where(t => typeof(ShaderNode).IsAssignableFrom(t)).Select(t => t.Name), StringComparer.Ordinal);
                    _registry = NodeRegistry.Scan(new[] { own, typeof(ValueNode).Assembly },
                        type => type.Assembly == own || IsShared(type, taken),
                        method => method.DeclaringType?.Assembly == own || method.GetCustomAttribute<GpuAttribute>() != null);
                }
                return _registry;
            }
        }
    }

    // A Loom value node a shader graph can use: one with shader code on any overload, not generic, and not
    // named like a shader node.
    public static bool IsShared(Type type) => IsShared(type, null);

    private static bool IsShared(Type type, HashSet<string>? taken)
        => typeof(ValueNode).IsAssignableFrom(type) && !typeof(ShaderNode).IsAssignableFrom(type) && !type.IsGenericTypeDefinition
           && (taken == null || !taken.Contains(type.Name))
           && type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly).Any(m => m.GetCustomAttribute<GpuAttribute>() != null);

    public static void RegisterTypes()
    {
        ScriptTypes.RegisterEnum("shaderSurface", typeof(ShaderSurface));
        ScriptTypes.RegisterEnum("shaderLighting", typeof(ShaderLighting));
        ScriptTypes.RegisterEnum("shaderCull", typeof(ShaderCull));
        ScriptTypes.RegisterEnum("shaderSpace", typeof(ShaderSpace));
        ScriptTypes.RegisterEnum("shaderUV", typeof(ShaderUV));
        ScriptTypes.RegisterEnum("shaderColorSpace", typeof(TextureColorSpace));
        ScriptTypes.RegisterEnum("shaderFilter", typeof(TextureFilter));
        ScriptTypes.RegisterEnum("shaderWrap", typeof(TextureWrap));
        ScriptTypes.RegisterEnum("shaderMix", typeof(MixMode));
        ScriptTypes.RegisterEnum("shaderCompare", typeof(CompareMode));
        if (ScriptTypes.TryGet("texture", out _))
            ScriptTypes.RegisterStandIn(typeof(ShaderTexture), "texture");
        else
            ScriptTypes.RegisterHandle("texture", typeof(ShaderTexture));
    }
}
