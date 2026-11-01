// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Text;
using Godot;
using GodotDictionary = global::Godot.Collections.Dictionary;

namespace Lumora.Fluids.Hooks;

// A renderer's mesh: three surfaces (0 the bulk, 1 the strand tubes, 2 the spray), each made once at its capacity
// with FlagUseStorageBuffer so compute writes its vertex, attribute and index buffers and the same frame's draws
// read them. Surfaces are built straight from bytes, all zero: every index is 0, so nothing draws until a kernel
// writes a quad. Whether the normal entries carry a tangent follows FluidMeshLayout.NormalStride (4 B: normals
// only, the renderer pads the buffer; 8 B: normal and tangent). Every offset and stride the kernels assume is
// asked of the renderer here and any difference is kept in LayoutProblems, so a renderer change shows up as a
// failed check and never as garbage on screen. -xlinka
public sealed class FluidSurfaceBuffers : IDisposable
{
    public const int Bulk = 0;
    public const int Tubes = 1;
    public const int Spray = 2;
    public const int SurfaceCount = 3;

    private readonly int[] _vertices = new int[SurfaceCount];
    private readonly int[] _quads = new int[SurfaceCount];
    private readonly int[] _normalWords = new int[SurfaceCount];
    private readonly int[] _indexStride = new int[SurfaceCount];
    private readonly Rid[] _vertex = new Rid[SurfaceCount];
    private readonly Rid[] _attribute = new Rid[SurfaceCount];
    private readonly Rid[] _index = new Rid[SurfaceCount];
    private readonly Rid[] _material = new Rid[SurfaceCount];
    private Rid _mesh;

    public FluidSurfaceBuffers(int bulkVertices, int bulkQuads, int tubeVertices, int tubeQuads, int sprayVertices, int sprayQuads)
    {
        _mesh = RenderingServer.MeshCreate();
        Build(bulkVertices, bulkQuads, tubeVertices, tubeQuads, sprayVertices, sprayQuads);
    }

    public FluidSurfaceBuffers(in FluidSurfaceCapacity capacity)
        : this(capacity.BulkVertices, capacity.BulkQuads, capacity.TubeVertices, capacity.TubeQuads, capacity.SprayVertices, capacity.SprayQuads)
    {
    }

    public Rid Mesh => _mesh;

    // Bumped on every rebuild: buffers are new and every uniform set that held the old ones has to go.
    public int Generation { get; private set; }

    public string LayoutProblems { get; private set; } = string.Empty;

    public bool LayoutOk => LayoutProblems.Length == 0;

    public long AllocatedBytes
    {
        get
        {
            if (!_mesh.IsValid) return 0;
            long bytes = 0;
            for (int i = 0; i < SurfaceCount; i++)
                bytes += (long)_vertices[i] * (FluidMeshLayout.PositionStride + _normalWords[i] * 4 + FluidMeshLayout.AttributeStride)
                    + (long)_quads[i] * FluidMeshLayout.QuadIndices * _indexStride[i];
            return bytes;
        }
    }

    public int VertexCapacity(int surface) => _vertices[surface];

    public int QuadCapacity(int surface) => _quads[surface];

    public int NormalWords(int surface) => _normalWords[surface];

    public int IndexStride(int surface) => _indexStride[surface];

    public Rid VertexBuffer(int surface) => _vertex[surface];

    public Rid AttributeBuffer(int surface) => _attribute[surface];

    public Rid IndexBuffer(int surface) => _index[surface];

    public static bool WithTangent => FluidMeshLayout.NormalStride == 8;

    public static RenderingServer.ArrayFormat Format(bool tangent)
    {
        var format = RenderingServer.ArrayFormat.FormatVertex
            | RenderingServer.ArrayFormat.FormatNormal
            | RenderingServer.ArrayFormat.FormatColor
            | RenderingServer.ArrayFormat.FormatCustom0
            | RenderingServer.ArrayFormat.FormatIndex
            | RenderingServer.ArrayFormat.FlagUseStorageBuffer
            | RenderingServer.ArrayFormat.FlagFormatCurrentVersion
            | (RenderingServer.ArrayFormat)((long)RenderingServer.ArrayCustomFormat.RgbaHalf << (int)RenderingServer.ArrayFormat.FormatCustom0Shift);
        if (tangent)
            format |= RenderingServer.ArrayFormat.FormatTangent;
        return format;
    }

    // A capacity change rebuilds all three surfaces (they live in one mesh) and puts the materials back.
    public void Rebuild(int bulkVertices, int bulkQuads, int tubeVertices, int tubeQuads, int sprayVertices, int sprayQuads)
    {
        RenderingServer.MeshClear(_mesh);
        Build(bulkVertices, bulkQuads, tubeVertices, tubeQuads, sprayVertices, sprayQuads);
        for (int s = 0; s < SurfaceCount; s++)
        {
            if (_material[s].IsValid)
                RenderingServer.MeshSurfaceSetMaterial(_mesh, s, _material[s]);
        }
    }

    public void SetMaterial(int surface, Rid material)
    {
        _material[surface] = material;
        RenderingServer.MeshSurfaceSetMaterial(_mesh, surface, material);
    }

    public void SetBounds(Aabb bounds) => RenderingServer.MeshSetCustomAabb(_mesh, bounds);

    private void Build(int bulkVertices, int bulkQuads, int tubeVertices, int tubeQuads, int sprayVertices, int sprayQuads)
    {
        var problems = new StringBuilder();
        AddSurface(Bulk, bulkVertices, bulkQuads, problems);
        AddSurface(Tubes, tubeVertices, tubeQuads, problems);
        AddSurface(Spray, sprayVertices, sprayQuads, problems);
        LayoutProblems = problems.ToString();
        Generation++;
    }

    private void AddSurface(int surface, int vertices, int quads, StringBuilder problems)
    {
        vertices = Math.Max(vertices, 4);
        quads = Math.Max(quads, 1);
        bool tangent = WithTangent;
        var format = Format(tangent);
        int normalStride = tangent ? 8 : 4;
        int indexStride = FluidMeshLayout.IndexStride(vertices);
        int indexCount = quads * FluidMeshLayout.QuadIndices;
        var surfaceData = new GodotDictionary
        {
            ["primitive"] = (int)RenderingServer.PrimitiveType.Triangles,
            ["format"] = (long)format,
            ["vertex_data"] = new byte[vertices * (FluidMeshLayout.PositionStride + normalStride)],
            ["attribute_data"] = new byte[vertices * FluidMeshLayout.AttributeStride],
            ["vertex_count"] = vertices,
            ["index_data"] = new byte[indexCount * indexStride],
            ["index_count"] = indexCount,
            ["aabb"] = new Aabb(new Vector3(-1, -1, -1), new Vector3(2, 2, 2)),
        };
        RenderingServer.MeshAddSurface(_mesh, surfaceData);

        _vertices[surface] = vertices;
        _quads[surface] = quads;
        _vertex[surface] = RenderingServer.MeshSurfaceGetVertexBufferRdRid(_mesh, surface);
        _attribute[surface] = RenderingServer.MeshSurfaceGetAttributeBufferRdRid(_mesh, surface);
        _index[surface] = RenderingServer.MeshSurfaceGetIndexBufferRdRid(_mesh, surface);

        string name = surface switch { Bulk => "bulk", Tubes => "tubes", _ => "spray" };
        long position = RenderingServer.MeshSurfaceGetFormatOffset(format, vertices, (int)RenderingServer.ArrayType.Vertex);
        long normal = RenderingServer.MeshSurfaceGetFormatOffset(format, vertices, (int)RenderingServer.ArrayType.Normal);
        long colour = RenderingServer.MeshSurfaceGetFormatOffset(format, vertices, (int)RenderingServer.ArrayType.Color);
        long custom = RenderingServer.MeshSurfaceGetFormatOffset(format, vertices, (int)RenderingServer.ArrayType.Custom0);
        long vertexStride = RenderingServer.MeshSurfaceGetFormatVertexStride(format, vertices);
        long normalTangentStride = RenderingServer.MeshSurfaceGetFormatNormalTangentStride(format, vertices);
        long attributeStride = RenderingServer.MeshSurfaceGetFormatAttributeStride(format, vertices);
        long renderIndexStride = RenderingServer.MeshSurfaceGetFormatIndexStride(format, vertices);
        _normalWords[surface] = (int)Math.Max(1, normalTangentStride / 4);
        _indexStride[surface] = (int)renderIndexStride;

        Expect(problems, name, "position offset", position, 0);
        Expect(problems, name, "position stride", vertexStride, FluidMeshLayout.PositionStride);
        Expect(problems, name, "normal offset", normal, FluidMeshLayout.NormalOffset(vertices));
        Expect(problems, name, "normal stride", normalTangentStride, FluidMeshLayout.NormalStride);
        Expect(problems, name, "COLOR offset", colour, FluidMeshLayout.ColorOffset);
        Expect(problems, name, "CUSTOM0 offset", custom, FluidMeshLayout.Custom0Offset);
        Expect(problems, name, "attribute stride", attributeStride, FluidMeshLayout.AttributeStride);
        Expect(problems, name, "index stride", renderIndexStride, indexStride);
        if (!_vertex[surface].IsValid || !_attribute[surface].IsValid || !_index[surface].IsValid)
            problems.Append(name).Append(": no RD buffer for vertex, attribute or index; ");
    }

    private static void Expect(StringBuilder problems, string surface, string what, long actual, long expected)
    {
        if (actual != expected)
            problems.Append(surface).Append(' ').Append(what).Append(" is ").Append(actual).Append(", expected ").Append(expected).Append("; ");
    }

    public void Dispose()
    {
        if (_mesh.IsValid)
            RenderingServer.FreeRid(_mesh);
        _mesh = default;
    }
}
