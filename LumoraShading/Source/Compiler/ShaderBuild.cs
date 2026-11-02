// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Lumora.Script;

namespace Lumora.Shading;

// One compile of one graph. Nodes are sorted by id and named by their place in that order, and each
// node's outputs land in locals, so the same graph always writes the same text and no expression grows
// past one node. Each stage walks back from the output's inputs for that stage; a node both stages use is
// written into both. Nodes nothing reaches are still written into a throwaway pass, so a problem on one
// shows on its card without failing the material. -xlinka
internal sealed class ShaderBuild
{
    private static readonly HashSet<string> AllowedRenderModes = new(StringComparer.Ordinal)
    {
        "unshaded", "cull_back", "cull_front", "cull_disabled", "blend_mix", "blend_add", "blend_mul",
        "blend_premul_alpha", "depth_draw_opaque", "depth_draw_always", "depth_prepass_alpha", "shadows_disabled",
        "specular_disabled", "fog_disabled",
    };

    private readonly ScriptGraph _graph;
    private readonly NodeRegistry _registry;
    private readonly List<CompileDiagnostic> _diagnostics = new();
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly List<ShaderNodeEntry> _entries = new();
    private readonly Dictionary<string, ShaderNodeEntry> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, ShaderNode> _behaviours = new();
    private readonly List<ShaderUniform> _uniforms = new();
    private readonly HashSet<string> _functions = new(StringComparer.Ordinal);
    private readonly List<(string Name, ShaderType Type, string Vertex)> _varyings = new();
    private readonly List<string> _renderModes = new();
    private readonly HashSet<string> _emittedAnywhere = new(StringComparer.Ordinal);
    private int _textureSamples;

    private ShaderStage _stage = ShaderStage.Fragment;
    private StringBuilder _body = new();
    private readonly Dictionary<(string Node, string Port), GlslValue> _values = new();
    private readonly HashSet<string> _emitted = new(StringComparer.Ordinal);
    private readonly HashSet<string> _visiting = new(StringComparer.Ordinal);
    private bool _scratch;
    private bool _preview;

    public ShaderBuild(ScriptGraph graph, NodeRegistry registry)
    {
        _graph = graph;
        _registry = registry;
    }

    public ShaderProgram Compile()
    {
        Resolve();
        var outputs = _entries.Where(e => e.Behaviour.IsOutput).ToList();
        if (outputs.Count == 0)
        {
            Report(DiagnosticSeverity.Error, null, "add a Material Output: it is what the material shows");
            return Finish(string.Empty, string.Empty, null, null);
        }
        for (int i = 1; i < outputs.Count; i++)
            Report(DiagnosticSeverity.Warning, outputs[i].Id, "only one Material Output is used, and it is not this one");

        var vertex = WriteStage(ShaderStage.Vertex, outputs[0]);
        var fragment = WriteStage(ShaderStage.Fragment, outputs[0]);
        CheckUnreached();
        return Finish(vertex, fragment, null, null);
    }

    // The shader a node's card shows: numeric curves or a bounded image, independently of material output.
    public ShaderProgram CompilePreview(string nodeId, string? port)
    {
        Resolve();
        if (!_byId.TryGetValue(nodeId, out var entry))
        {
            Report(DiagnosticSeverity.Error, null, $"there is no node {nodeId} to preview");
            return Finish(string.Empty, string.Empty, nodeId, port);
        }
        var output = port == null ? entry.Overload.Outputs.FirstOrDefault() : entry.Output(port);
        if (output == null || !ShaderTypes.TryFrom(output.Type, out var type))
        {
            Report(DiagnosticSeverity.Error, nodeId, port == null ? $"{entry.Title} has nothing to preview" : $"{entry.Title} has no output '{port}'");
            return Finish(string.Empty, string.Empty, nodeId, port);
        }

        BeginStage(ShaderStage.Fragment);
        Require(entry);
        var value = _values.TryGetValue((entry.Id, output.Name), out var v) ? v : Fallback(type);
        _preview = true;
        if (ShaderPreview.IsNumeric(type))
        {
            _uniforms.Add(new ShaderUniform(ShaderPreview.CurveUniform, ShaderType.Bool, "", "true", -1, null));
            _uniforms.Add(new ShaderUniform(ShaderPreview.RangeUniform, ShaderType.Float, "", "1.0", -1, null));
        }
        if (type == ShaderType.Texture && value.Code.Length > 0)
            _textureSamples++;
        AddRenderMode("unshaded");
        AddRenderMode("fog_disabled");
        // Culled as usual. The square a preview is drawn on is two-sided, a face each way in one plane, and
        // with culling off both faces drew: the picture and its mirror fought over every pixel. -xlinka
        var fragment = ShaderPreview.Fragment(_body.ToString(), value);
        return Finish(string.Empty, fragment, nodeId, output.Name);
    }

    // ---- resolving ----

    private void Resolve()
    {
        if (_graph.Nodes.Count > ShaderLimits.MaxNodes)
        {
            Report(DiagnosticSeverity.Error, null, $"a shader graph holds at most {ShaderLimits.MaxNodes} nodes");
            return;
        }
        if (_graph.Flow.Count > 0)
            Report(DiagnosticSeverity.Error, null, "a shader graph has no flow wires; take them out");

        foreach (var node in _graph.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal))
            ResolveNode(node);
        foreach (var wire in _graph.Data)
            ResolveWire(wire);
    }

    private void ResolveNode(GraphNode node)
    {
        if (_byId.ContainsKey(node.Id))
        {
            Report(DiagnosticSeverity.Error, node.Id, "two nodes share this id");
            return;
        }
        if (!_registry.TryGetDefinition(node.Type, out var definition))
        {
            Report(DiagnosticSeverity.Error, node.Id, $"'{node.Type}' is not a shader node this build knows", newerBuild: true);
            return;
        }
        if (node.Version > definition.Version)
        {
            Report(DiagnosticSeverity.Error, node.Id, $"this {definition.Name} is from a newer build", newerBuild: true);
            return;
        }
        if (!_registry.TryResolve(definition, node.Signature, out var overload, out var why))
        {
            Report(DiagnosticSeverity.Error, node.Id, why);
            return;
        }
        if (!_behaviours.TryGetValue(definition.ClrType, out var behaviour))
        {
            if (ShaderNodes.IsShared(definition.ClrType))
            {
                behaviour = SharedShaderNode.Instance;
            }
            else if (!typeof(ShaderNode).IsAssignableFrom(definition.ClrType) || definition.ClrType.IsGenericTypeDefinition)
            {
                Report(DiagnosticSeverity.Error, node.Id, $"{definition.Name} is not a shader node");
                return;
            }
            else
            {
                behaviour = (ShaderNode)Activator.CreateInstance(definition.ClrType)!;
            }
            _behaviours.Add(definition.ClrType, behaviour);
        }
        foreach (var input in overload.Inputs)
        {
            if (!ShaderTypes.TryFrom(input.Type, out _) && !IsSetting(input.Type))
            {
                Report(DiagnosticSeverity.Error, node.Id, $"{input.Name} is {input.Type.Name}, which shaders cannot carry");
                return;
            }
        }
        foreach (var output in overload.Outputs)
        {
            if (!ShaderTypes.TryFrom(output.Type, out _))
            {
                Report(DiagnosticSeverity.Error, node.Id, $"{output.Name} is {output.Type.Name}, which shaders cannot carry");
                return;
            }
        }

        var entry = new ShaderNodeEntry(node, _entries.Count, overload, behaviour);
        _entries.Add(entry);
        _byId.Add(node.Id, entry);
    }

    private void ResolveWire(GraphWire wire)
    {
        if (!_byId.TryGetValue(wire.ToNode, out var to))
            return;
        if (!_byId.TryGetValue(wire.FromNode, out var from))
        {
            Report(DiagnosticSeverity.Error, to.Id, $"{wire.ToPort} is wired to a node that is not usable");
            return;
        }
        var input = to.Input(wire.ToPort);
        if (input == null)
        {
            Report(DiagnosticSeverity.Error, to.Id, $"{to.Title} has no input '{wire.ToPort}'");
            return;
        }
        var output = from.Output(wire.FromPort);
        if (output == null)
        {
            Report(DiagnosticSeverity.Error, to.Id, $"{from.Title} has no output '{wire.FromPort}'");
            return;
        }
        if (input.IsSetting || IsSetting(input.Type))
        {
            Report(DiagnosticSeverity.Error, to.Id, $"{input.Name} is a setting: set it on the node instead of wiring it");
            return;
        }
        if (!ShaderTypes.TryFrom(output.Type, out var fromType) || !ShaderTypes.TryFrom(input.Type, out var toType)
            || !ShaderTypes.CanConvert(fromType, toType))
        {
            Report(DiagnosticSeverity.Error, to.Id, $"{input.Name} takes {input.Type.Name}, not {output.Type.Name}");
            return;
        }
        if (from == to)
        {
            Report(DiagnosticSeverity.Error, to.Id, "a node cannot feed itself");
            return;
        }
        if (!to.Wires.TryAdd(input.Name, (from.Id, output.Name)))
            Report(DiagnosticSeverity.Error, to.Id, $"two wires go into {input.Name}");
    }

    internal static bool IsSetting(ScriptType type) => type == ScriptTypes.String || type.ClrType.IsEnum;

    // ---- writing ----

    private void BeginStage(ShaderStage stage)
    {
        _stage = stage;
        _body = new StringBuilder();
        _values.Clear();
        _emitted.Clear();
        _visiting.Clear();
    }

    private string WriteStage(ShaderStage stage, ShaderNodeEntry output)
    {
        BeginStage(stage);
        foreach (var input in output.Overload.Inputs)
        {
            if (PortStage(output, input.Name) != stage)
                continue;
            if (output.Wires.TryGetValue(input.Name, out var source) && _byId.TryGetValue(source.Node, out var from))
                Require(from);
        }
        Write(output);
        return _body.ToString();
    }

    private void CheckUnreached()
    {
        var unreached = _entries.Where(e => !_emittedAnywhere.Contains(e.Id)).ToList();
        if (unreached.Count == 0)
            return;
        _scratch = true;
        BeginStage(ShaderStage.Fragment);
        foreach (var entry in unreached)
            Require(entry);
        _scratch = false;
    }

    private void Require(ShaderNodeEntry entry)
    {
        if (_emitted.Contains(entry.Id))
            return;
        if (!_visiting.Add(entry.Id))
        {
            Report(DiagnosticSeverity.Error, entry.Id, "this node feeds back into itself; a shader cannot loop");
            return;
        }
        foreach (var input in entry.Overload.Inputs)
        {
            if (entry.Wires.TryGetValue(input.Name, out var source) && _byId.TryGetValue(source.Node, out var from))
                Require(from);
        }
        _visiting.Remove(entry.Id);
        Write(entry);
    }

    private void Write(ShaderNodeEntry entry)
    {
        if (_emitted.Contains(entry.Id))
            return;
        _emitted.Add(entry.Id);
        if (!_scratch)
            _emittedAnywhere.Add(entry.Id);

        try
        {
            entry.Behaviour.Emit(new ShaderNodeContext(this, entry, _stage));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
        {
            Report(DiagnosticSeverity.Error, entry.Id, $"{entry.Title} could not be written: {ex.Message}");
        }

        foreach (var output in entry.Overload.Outputs)
        {
            if (!_values.ContainsKey((entry.Id, output.Name)) && ShaderTypes.TryFrom(output.Type, out var type))
                _values[(entry.Id, output.Name)] = Fallback(type);
        }
    }

    internal ShaderStage PortStage(ShaderNodeEntry entry, string port)
        => entry.Ports.TryGetValue(port, out var info) && info.VertexStage
            ? ShaderStage.Vertex
            : ShaderStage.Fragment;

    internal ShaderStage Stage => _stage;

    internal bool IsWired(ShaderNodeEntry entry, string port) => entry.Wires.ContainsKey(port);

    internal GraphValue? Constant(ShaderNodeEntry entry, string port)
        => !entry.Wires.ContainsKey(port) && entry.Node.Values.TryGetValue(port, out var value) && value.Kind != GraphValueKind.Null
            ? value
            : null;

    internal NodePort InputPort(ShaderNodeEntry entry, string port)
        => entry.Input(port) ?? throw new InvalidOperationException($"{entry.Title} has no input '{port}'");

    internal NodePort OutputPort(ShaderNodeEntry entry, string port)
        => entry.Output(port) ?? throw new InvalidOperationException($"{entry.Title} has no output '{port}'");

    internal GlslValue Input(ShaderNodeEntry entry, string port, ShaderType? wanted)
    {
        var input = InputPort(entry, port);
        if (!ShaderTypes.TryFrom(input.Type, out var portType))
            throw new InvalidOperationException($"{port} is a setting, not a value");
        var target = wanted ?? portType;

        GlslValue value;
        if (entry.Wires.TryGetValue(port, out var source))
            value = _values.TryGetValue(source, out var wired) ? wired : Fallback(portType);
        else
            value = Unwired(entry, input, portType);

        if (value.Type == target)
            return value;
        if (!ShaderTypes.CanConvert(value.Type, target))
            return Fallback(target);
        return ShaderTypes.Convert(value, target);
    }

    private GlslValue Unwired(ShaderNodeEntry entry, NodePort port, ShaderType type)
    {
        if (entry.Node.Values.TryGetValue(port.Name, out var set) && set.Kind != GraphValueKind.Null)
        {
            if (set.Kind == GraphValueKind.Binding)
            {
                if (type == ShaderType.Texture)
                    return Fallback(type);
                Report(DiagnosticSeverity.Error, entry.Id, $"{port.Name} holds an object; it takes {port.Type.Name}");
            }
            else if (GlslLiteral.FromValue(set, type) is { } literal)
            {
                return literal;
            }
            else
            {
                Report(DiagnosticSeverity.Error, entry.Id, $"{port.Name} cannot read {set}");
            }
        }
        if (entry.Ports.TryGetValue(port.Name, out var info) && info.Default is { } fallback)
            return new GlslValue(fallback.Code, type);
        if (port.HasDefault && type != ShaderType.Texture && GlslLiteral.FromDefault(port.Default, type) is { } byDefault)
            return byDefault;
        return Fallback(type);
    }

    private static GlslValue Fallback(ShaderType type) => type == ShaderType.Texture ? new GlslValue(string.Empty, type) : GlslLiteral.Zero(type);

    internal string LocalName(ShaderNodeEntry entry, string port) => $"{entry.Prefix}_{port}";

    internal void Output(ShaderNodeEntry entry, string port, string code)
    {
        var output = OutputPort(entry, port);
        ShaderTypes.TryFrom(output.Type, out var type);
        if (type == ShaderType.Texture)
        {
            _values[(entry.Id, port)] = new GlslValue(code, type);
            return;
        }
        var name = LocalName(entry, port);
        _body.Append('\t').Append(ShaderTypes.GlslName(type)).Append(' ').Append(name).Append(" = ").Append(code).Append(";\n");
        _values[(entry.Id, port)] = new GlslValue(name, type);
    }

    internal string Declare(ShaderNodeEntry entry, string port)
    {
        var output = OutputPort(entry, port);
        ShaderTypes.TryFrom(output.Type, out var type);
        if (type == ShaderType.Texture)
            throw new InvalidOperationException("a texture output is set, not declared");
        var name = LocalName(entry, port);
        _body.Append('\t').Append(ShaderTypes.GlslName(type)).Append(' ').Append(name).Append(";\n");
        _values[(entry.Id, port)] = new GlslValue(name, type);
        return name;
    }

    internal void Line(string code)
    {
        foreach (var line in code.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Trim().Length > 0)
                _body.Append('\t').Append(line.Trim()).Append('\n');
        }
    }

    internal void Need(string function)
    {
        if (!GlslLibrary.TryGet(function, out _))
            throw new InvalidOperationException($"no shader function '{function}'");
        if (!_scratch)
            _functions.Add(function);
    }

    internal void CountTextureSamples(int count)
    {
        if (!_scratch)
            _textureSamples += count;
    }

    internal string Uniform(ShaderNodeEntry entry, ShaderUniform uniform)
    {
        // The same node writes its uniform once per stage; any other node with that name is a clash, even an
        // identical one, since the material could only ever show one of them.
        var existing = _uniforms.FirstOrDefault(u => u.Name == uniform.Name);
        if (existing != null)
        {
            if (existing.NodeId != entry.Id || existing.Type != uniform.Type || existing.Hints != uniform.Hints
                || existing.Default != uniform.Default || existing.Binding != uniform.Binding)
            {
                Report(DiagnosticSeverity.Error, entry.Id, $"another property is already called {uniform.Name}");
            }
            return existing.Name;
        }
        if (!_scratch)
            _uniforms.Add(uniform with { NodeId = entry.Id });
        return uniform.Name;
    }

    // A value worked out per vertex and read per pixel. In the vertex stage it is just the expression.
    internal string Varying(string key, ShaderType type, string vertexCode)
    {
        if (_stage == ShaderStage.Vertex)
            return vertexCode;
        var name = "lm_v_" + key;
        foreach (var varying in _varyings)
        {
            if (varying.Name == name)
            {
                if (varying.Type != type || varying.Vertex != vertexCode)
                    throw new InvalidOperationException($"varying {key} is asked for twice, differently");
                return name;
            }
        }
        if (!_scratch)
            _varyings.Add((name, type, vertexCode));
        return name;
    }

    internal void AddRenderMode(string mode)
    {
        if (!AllowedRenderModes.Contains(mode))
            throw new InvalidOperationException($"render mode '{mode}' is not one a graph may set");
        if (!_scratch && !_renderModes.Contains(mode))
            _renderModes.Add(mode);
    }

    internal void Report(DiagnosticSeverity severity, string? nodeId, string message, bool newerBuild = false)
    {
        if (_scratch && severity == DiagnosticSeverity.Error)
            severity = DiagnosticSeverity.Warning;
        var key = $"{nodeId}|{message}";
        if (_reported.Add(key))
            _diagnostics.Add(new CompileDiagnostic(severity, nodeId, message) { NeedsNewerBuild = newerBuild });
    }

    // ---- the source ----

    private ShaderProgram Finish(string vertex, string fragment, string? previewNode, string? previewPort)
    {
        var functions = new List<GlslFunction>();
        int loops = 0;
        if (!HasErrors)
        {
            functions = GlslLibrary.Resolve(_functions);
            loops = functions.Sum(f => f.Loops);
            if (loops > ShaderLimits.MaxLoops)
                Report(DiagnosticSeverity.Error, null, $"the noise nodes here loop {loops} times in all; a shader may loop {ShaderLimits.MaxLoops}");
            if (_textureSamples > ShaderLimits.MaxTextureSamples)
                Report(DiagnosticSeverity.Error, null, $"{_textureSamples} texture reads; a shader may make {ShaderLimits.MaxTextureSamples}");
            if (_uniforms.Count > ShaderLimits.MaxUniforms)
                Report(DiagnosticSeverity.Error, null, $"{_uniforms.Count} properties; a shader may have {ShaderLimits.MaxUniforms}");
        }

        string source = string.Empty;
        if (!HasErrors)
        {
            source = Assemble(vertex, fragment, functions);
            if (source.Length > ShaderLimits.MaxSourceBytes)
            {
                Report(DiagnosticSeverity.Error, null, $"the shader comes to {source.Length / 1024} KB; the most is {ShaderLimits.MaxSourceBytes / 1024} KB");
                source = string.Empty;
            }
        }
        return new ShaderProgram(source, _uniforms.ToArray(), _diagnostics.ToArray(), _textureSamples, loops, previewNode, previewPort);
    }

    private bool HasErrors => _diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    private string Assemble(string vertex, string fragment, List<GlslFunction> functions)
    {
        var source = new StringBuilder();
        source.Append("// Made from a shader graph.\n");
        source.Append("shader_type spatial;\n");
        if (_renderModes.Count > 0)
            source.Append("render_mode ").Append(string.Join(", ", _renderModes)).Append(";\n");

        if (_uniforms.Count > 0)
        {
            source.Append('\n');
            foreach (var uniform in _uniforms)
                source.Append(uniform.Declaration).Append('\n');
        }
        if (_varyings.Count > 0)
        {
            source.Append('\n');
            foreach (var varying in _varyings)
                source.Append("varying ").Append(ShaderTypes.GlslName(varying.Type)).Append(' ').Append(varying.Name).Append(";\n");
        }
        foreach (var function in functions)
            source.Append('\n').Append(function.Code).Append('\n');
        if (_preview)
            source.Append('\n').Append(ShaderPreview.Helpers).Append('\n');

        if (vertex.Length > 0 || _varyings.Count > 0)
        {
            source.Append("\nvoid vertex() {\n");
            foreach (var varying in _varyings)
                source.Append('\t').Append(varying.Name).Append(" = ").Append(varying.Vertex).Append(";\n");
            source.Append(vertex);
            source.Append("}\n");
        }
        source.Append("\nvoid fragment() {\n").Append(fragment).Append("}\n");
        return source.ToString();
    }
}
