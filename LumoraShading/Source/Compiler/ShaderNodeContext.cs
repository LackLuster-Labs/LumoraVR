// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Lumora.Script;

namespace Lumora.Shading;

// What a node sees while it writes itself: its inputs as shader code, its settings as constants, and the
// ways to add outputs, lines, uniforms, varyings, helper functions and render modes. One per node per
// stage.
public sealed class ShaderNodeContext
{
    private static readonly Regex Placeholder = new(@"\{([A-Za-z][A-Za-z0-9_]*)\}", RegexOptions.Compiled);
    private static readonly Regex TextureCall = new(@"\b(?:texture|textureLod|texelFetch|textureGrad|textureProj)\s*\(", RegexOptions.Compiled);

    private readonly ShaderBuild _build;
    private readonly ShaderNodeEntry _entry;

    internal ShaderNodeContext(ShaderBuild build, ShaderNodeEntry entry, ShaderStage stage)
    {
        _build = build;
        _entry = entry;
        Stage = stage;
    }

    public ShaderStage Stage { get; }
    public string NodeId => _entry.Id;
    public GraphNode Node => _entry.Node;
    public NodeOverload Overload => _entry.Overload;

    // A short, stable word for this node, for names it makes: n4.
    public string Prefix => _entry.Prefix;

    public bool IsWired(string port) => _build.IsWired(_entry, port);

    // Whether the port holds a value typed on the node, as opposed to its default.
    public bool IsSet(string port) => _build.Constant(_entry, port) != null;

    public ShaderStage PortStage(string port) => _build.PortStage(_entry, port);

    public GlslValue Input(string port) => _build.Input(_entry, port, null);

    public GlslValue Input(string port, ShaderType type) => _build.Input(_entry, port, type);

    // A setting: an enum typed on the node, or the signature's default.
    public T Option<T>(string port) where T : struct, Enum
    {
        var input = _build.InputPort(_entry, port);
        var value = _build.Constant(_entry, port);
        if (value?.Kind == GraphValueKind.String && Enum.TryParse<T>(value.String, ignoreCase: true, out var named) && Enum.IsDefined(named))
            return named;
        if (value?.Kind == GraphValueKind.Number
            && Enum.TryParse<T>(value.Number.ToString(CultureInfo.InvariantCulture), out var numbered)
            && Enum.IsDefined(numbered))
            return numbered;
        if (value != null)
            Warning($"{port} is not one of {string.Join(", ", Enum.GetNames<T>())}; using the default");
        return input.Default is T fallback ? fallback : default;
    }

    public string Text(string port)
    {
        var input = _build.InputPort(_entry, port);
        var value = _build.Constant(_entry, port);
        if (value?.Kind == GraphValueKind.String)
            return value.String ?? string.Empty;
        return input.Default as string ?? string.Empty;
    }

    // A number that has to be known now, like a slider's range. A wire gives no such number.
    public double Number(string port, double fallback)
    {
        var input = _build.InputPort(_entry, port);
        var value = _build.Constant(_entry, port);
        if (value?.Kind == GraphValueKind.Number)
            return value.Number;
        if (value?.Kind == GraphValueKind.Numbers && value.Numbers.Length > 0)
            return value.Numbers[0];
        return input.Default switch
        {
            float f => f,
            double d => d,
            int i => i,
            _ => fallback,
        };
    }

    // The value typed on the node as shader code, or null when it has none, for a node that declares a
    // uniform's default from it.
    public GlslValue? ConstantValue(string port, ShaderType type)
    {
        var value = _build.Constant(_entry, port);
        if (value != null)
            return GlslLiteral.FromValue(value, type);
        var input = _build.InputPort(_entry, port);
        return input.HasDefault ? GlslLiteral.FromDefault(input.Default, type) : null;
    }

    // The binding slot a texture input holds, or null when it holds none.
    public int? Binding(string port)
    {
        var value = _build.Constant(_entry, port);
        return value?.Kind == GraphValueKind.Binding ? value.Binding : null;
    }

    public string OutputName(string port) => _build.LocalName(_entry, port);

    public void Output(string port, string code)
    {
        _build.CountTextureSamples(TextureCall.Matches(code).Count);
        _build.Output(_entry, port, code);
    }

    public void Output(string port, GlslValue value)
    {
        var output = _build.OutputPort(_entry, port);
        ShaderTypes.TryFrom(output.Type, out var type);
        _build.Output(_entry, port, ShaderTypes.Convert(value, type).Code);
    }

    public string Declare(string port) => _build.Declare(_entry, port);

    public void Line(string code)
    {
        _build.CountTextureSamples(TextureCall.Matches(code).Count);
        _build.Line(code);
    }

    public void Need(string function) => _build.Need(function);

    public string Uniform(string name, ShaderType type, string hints = "", string? defaultCode = null, int binding = -1)
        => _build.Uniform(_entry, new ShaderUniform(name, type, hints, defaultCode, binding, _entry.Id));

    public string Varying(string key, ShaderType type, string vertexCode) => _build.Varying(key, type, vertexCode);

    public void RenderMode(string mode) => _build.AddRenderMode(mode);

    public void Error(string message) => _build.Report(DiagnosticSeverity.Error, _entry.Id, message);

    public void Warning(string message) => _build.Report(DiagnosticSeverity.Warning, _entry.Id, message);

    // What a node without its own Emit does: the [Glsl] on its overload, with its ports filled in.
    public void EmitGlsl()
    {
        // A Loom node's [Gpu] code runs in either stage.
        var glsl = Overload.Method?.GetCustomAttribute<GlslAttribute>();
        var gpu = glsl == null ? Overload.Method?.GetCustomAttribute<GpuAttribute>() : null;
        if (glsl == null && gpu == null)
        {
            Error($"{Overload.Definition.Name} has no shader code for these inputs");
            return;
        }
        if (glsl != null && (glsl.Stages & Stage) == 0)
        {
            Error(Stage == ShaderStage.Vertex
                ? $"{Overload.Definition.Name} only works per pixel, so it cannot feed a vertex input"
                : $"{Overload.Definition.Name} only works per vertex");
            return;
        }
        string code = glsl?.Code ?? gpu!.Code;
        foreach (var need in glsl?.Needs ?? gpu!.Needs)
            Need(need);

        var outputs = Overload.Outputs;
        if (outputs.Count == 1 && !code.Contains(';'))
        {
            Output(outputs[0].Name, Fill(code));
            return;
        }
        foreach (var output in outputs)
            Declare(output.Name);
        foreach (var statement in Fill(code).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            Line(statement + ";");
    }

    private string Fill(string code)
        => Placeholder.Replace(code, match =>
        {
            var name = match.Groups[1].Value;
            if (Overload.Inputs.Any(i => i.Name == name))
                return Input(name).Code;
            if (Overload.Outputs.Any(o => o.Name == name))
                return OutputName(name);
            throw new InvalidOperationException($"the shader code names {{{name}}}, which is not a port");
        });

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{_entry.Title} {NodeId} ({Stage})");
}
