// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Runtime.InteropServices;
using Godot;
using RDUniformArray = global::Godot.Collections.Array<Godot.RDUniform>;

namespace Lumora.Fluids.Hooks;

// Spots on people, drawn by the GPU: two MultiMeshes of one flattened-ellipsoid mesh (the main one, and the
// wearer's own head on the local head layer), whose instance buffers the residue kernel writes from the uploaded
// spots and residue bones. Spot i is instance i in both; the visible count is set from the CPU, which knows how
// many spots it uploaded. No CPU setter ever touches the instance data: one would turn on motion vectors for the
// MultiMesh under FSR2 and move it to a new, doubled buffer. The buffer RID is asked for again every frame anyway
// and a new one rebuilds the uniform set, and a doubled buffer gets both copies written. -xlinka
public sealed class FluidResiduePass : IDisposable
{
    public const int InstanceFloats = 20;
    public const uint HeadFlag = 0x01000000;

    private readonly RenderingDevice _rd;
    private readonly FluidSurfaceKernels _kernels;
    private readonly byte[] _push = new byte[FluidPushConstants.Size];
    private Rid _spotMesh;
    private Rid _main;
    private Rid _head;
    private Rid _mainInstance;
    private Rid _headInstance;
    private Rid _spots;
    private Rid _bones;
    private Rid _set;
    private Rid _setMainBuffer;
    private Rid _setHeadBuffer;
    private bool _mainDoubled;
    private bool _headDoubled;
    private int _visible = -1;

    public FluidResiduePass(RenderingDevice rd, FluidSurfaceKernels kernels, int capacity, int boneCapacity, bool fine, Rid scenario, uint layers, uint headLayers)
    {
        _rd = rd;
        _kernels = kernels;
        Capacity = Math.Max(capacity, 1);
        BoneCapacity = Math.Max(boneCapacity, 1);
        _spotMesh = BuildSpotMesh(fine);
        _main = MakeMultimesh();
        _head = MakeMultimesh();
        _mainInstance = RenderingServer.InstanceCreate2(_main, scenario);
        _headInstance = RenderingServer.InstanceCreate2(_head, scenario);
        RenderingServer.InstanceSetLayerMask(_mainInstance, layers);
        RenderingServer.InstanceSetLayerMask(_headInstance, headLayers);
        RenderingServer.InstanceGeometrySetCastShadowsSetting(_mainInstance, RenderingServer.ShadowCastingSetting.Off);
        RenderingServer.InstanceGeometrySetCastShadowsSetting(_headInstance, RenderingServer.ShadowCastingSetting.Off);
        _spots = _rd.StorageBufferCreate((uint)(Capacity * FluidSpotRecord.Size), new byte[Capacity * FluidSpotRecord.Size]);
        _bones = _rd.StorageBufferCreate((uint)(BoneCapacity * FluidResidueBone.Size), new byte[BoneCapacity * FluidResidueBone.Size]);
    }

    public int Capacity { get; }

    public int BoneCapacity { get; }

    public Rid MainMultimesh => _main;

    public Rid HeadMultimesh => _head;

    public Rid MainInstance => _mainInstance;

    public Rid HeadInstance => _headInstance;

    public bool MainDoubled => _mainDoubled;

    // Times the instance buffer came back as a different RID and the set was rebuilt.
    public int Refetches { get; private set; }

    private Rid MakeMultimesh()
    {
        var mm = RenderingServer.MultimeshCreate();
        RenderingServer.MultimeshSetMesh(mm, _spotMesh);
        RenderingServer.MultimeshAllocateData(mm, Capacity, RenderingServer.MultimeshTransformFormat.Transform3D, true, true, false);
        RenderingServer.MultimeshSetVisibleInstances(mm, 0);
        return mm;
    }

    // Fine coats need a round silhouette when viewed against the skin at close range. The mesh is shared by
    // every spot; light tiers and view-only blobs keep the smaller mesh. Front faces wind clockwise. -xlinka
    public static Rid BuildSpotMesh(bool fine)
    {
        int sides = fine ? 24 : 6;
        var verts = new System.Collections.Generic.List<Vector3>();
        var indices = new System.Collections.Generic.List<int>();
        verts.Add(Vector3.Up);
        verts.Add(Vector3.Down);
        var lats = new float[fine ? 7 : 1];
        for (int ring = 0; ring < lats.Length; ring++)
            lats[ring] = Mathf.Pi * (0.5f - (ring + 1f) / (lats.Length + 1f));
        foreach (float lat in lats)
        {
            for (int i = 0; i < sides; i++)
            {
                float a = Mathf.Tau * i / sides;
                verts.Add(new Vector3(Mathf.Cos(a) * Mathf.Cos(lat), Mathf.Sin(lat), Mathf.Sin(a) * Mathf.Cos(lat)));
            }
        }
        int top = 2;
        int bottom = 2 + (lats.Length - 1) * sides;
        for (int i = 0; i < sides; i++)
        {
            int i1 = (i + 1) % sides;
            indices.AddRange(new[] { 0, top + i, top + i1 });
            indices.AddRange(new[] { 1, bottom + i1, bottom + i });
        }
        for (int ring = 0; ring < lats.Length - 1; ring++)
        {
            for (int i = 0; i < sides; i++)
            {
                int i1 = (i + 1) % sides;
                int u0 = top + ring * sides + i, u1 = top + ring * sides + i1;
                int l0 = u0 + sides, l1 = u1 + sides;
                indices.AddRange(new[] { u0, l0, l1 });
                indices.AddRange(new[] { u0, l1, u1 });
            }
        }
        var arrays = new global::Godot.Collections.Array();
        arrays.Resize((int)RenderingServer.ArrayType.Max);
        var positions = verts.ToArray();
        arrays[(int)RenderingServer.ArrayType.Vertex] = positions;
        arrays[(int)RenderingServer.ArrayType.Normal] = positions;
        arrays[(int)RenderingServer.ArrayType.Index] = indices.ToArray();
        var mesh = RenderingServer.MeshCreate();
        RenderingServer.MeshAddSurfaceFromArrays(mesh, RenderingServer.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    public void SetMaterial(Rid material) => RenderingServer.MeshSurfaceSetMaterial(_spotMesh, 0, material);

    public void SetBounds(Aabb bounds)
    {
        RenderingServer.MultimeshSetCustomAabb(_main, bounds);
        RenderingServer.MultimeshSetCustomAabb(_head, bounds);
    }

    public void SetVisible(bool visible)
    {
        RenderingServer.InstanceSetVisible(_mainInstance, visible);
        RenderingServer.InstanceSetVisible(_headInstance, visible);
    }

    public void Upload(ReadOnlySpan<FluidSpotRecord> spots, ReadOnlySpan<FluidResidueBone> bones)
    {
        int count = Math.Min(spots.Length, Capacity);
        int boneCount = Math.Min(bones.Length, BoneCapacity);
        if (count > 0)
            _rd.BufferUpdate(_spots, 0, (uint)(count * FluidSpotRecord.Size), MemoryMarshal.AsBytes(spots[..count]));
        if (boneCount > 0)
            _rd.BufferUpdate(_bones, 0, (uint)(boneCount * FluidResidueBone.Size), MemoryMarshal.AsBytes(bones[..boneCount]));
        if (count != _visible)
        {
            _visible = count;
            RenderingServer.MultimeshSetVisibleInstances(_main, count);
            RenderingServer.MultimeshSetVisibleInstances(_head, count);
        }
    }

    // Records the residue kernel for the spots uploaded last.
    public void Record()
    {
        int count = Math.Max(_visible, 0);
        Rid mainBuffer = RenderingServer.MultimeshGetBufferRdRid(_main);
        Rid headBuffer = RenderingServer.MultimeshGetBufferRdRid(_head);
        if (!mainBuffer.IsValid || !headBuffer.IsValid)
            return;
        if (mainBuffer != _setMainBuffer || headBuffer != _setHeadBuffer || !_set.IsValid || !_rd.UniformSetIsValid(_set))
        {
            if (_set.IsValid && _rd.UniformSetIsValid(_set))
                _rd.FreeRid(_set);
            if (_setMainBuffer.IsValid)
                Refetches++;
            _mainDoubled = _rd.BufferGetData(mainBuffer).Length >= Capacity * InstanceFloats * 4 * 2;
            _headDoubled = _rd.BufferGetData(headBuffer).Length >= Capacity * InstanceFloats * 4 * 2;
            var uniforms = new RDUniformArray();
            int binding = 0;
            foreach (var rid in new[] { _spots, _bones, mainBuffer, headBuffer })
            {
                var u = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = binding++ };
                u.AddId(rid);
                uniforms.Add(u);
            }
            _set = _rd.UniformSetCreate(uniforms, _kernels.ResidueShader, 0);
            _setMainBuffer = mainBuffer;
            _setHeadBuffer = headBuffer;
        }
        if (count == 0)
            return;
        var span = _push.AsSpan();
        uint flags = (_mainDoubled ? 1u : 0u) | (_headDoubled ? 2u : 0u);
        uint capacity = (uint)Capacity;
        uint zero = 0;
        uint spots = (uint)count;
        MemoryMarshal.Write(span, in flags);
        MemoryMarshal.Write(span[4..], in capacity);
        MemoryMarshal.Write(span[8..], in zero);
        MemoryMarshal.Write(span[12..], in spots);
        long list = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(list, _kernels.Residue);
        _rd.ComputeListBindUniformSet(list, _set, 0);
        _rd.ComputeListSetPushConstant(list, span, (uint)span.Length);
        _rd.ComputeListDispatch(list, (uint)((count + 63) / 64), 1, 1);
        _rd.ComputeListEnd();
    }

    // meta bit 25: the spot is a liquid's film (FluidResidue.glsl)
    public const uint FilmFlag = 0x02000000;
    // mL, m: what makes a film and how it spreads; the same numbers as FluidResidue.glsl
    public const float FilmMaxMl = 0.105f;
    public const float FilmThick = 0.00015f;
    public const float FilmMinRadius = 0.008f;
    public const float FilmMaxRadius = 0.022f;
    public const float FilmLens = 0.0008f;
    public const float FilmLift = 0.0006f;

    // A spot of at most a soaked patch in a light, nearly grey colour, or one the people side marked: a film.
    public static bool IsFilm(in FluidSpotRecord spot)
    {
        if ((spot.Meta & FilmFlag) != 0)
            return true;
        float r = (spot.Tint & 255) / 255f, g = ((spot.Tint >> 8) & 255) / 255f, b = ((spot.Tint >> 16) & 255) / 255f;
        float lo = Math.Min(r, Math.Min(g, b)), hi = Math.Max(r, Math.Max(g, b));
        return spot.VolumeMl <= FilmMaxMl && lo >= 0.75f && hi - lo <= 0.2f;
    }

    // The same instance on the CPU, for the view-only path and tests: the bone times the spot's frame (a coat's
    // flattened ellipsoid, or a film spread thin and run downhill, the mesh's pole on the normal), written as the 20
    // floats of a MultiMesh instance (3x4 rows, colour, custom).
    public static bool SpotInstance(in Transform3D bone, in FluidSpotRecord spot, Span<float> instance)
    {
        instance[..InstanceFloats].Clear();
        if (spot.VolumeMl <= 0f)
            return (spot.Meta & HeadFlag) != 0;
        var e = new Vector2((spot.NormalShape & 4095) / 4095f, ((spot.NormalShape >> 12) & 4095) / 4095f);
        var n = OctDecode(e);
        var t1 = Math.Abs(n.Y) < 0.99f ? Vector3.Up.Cross(n).Normalized() : Vector3.Right.Cross(n).Normalized();
        var at = new Vector3(spot.Local.x, spot.Local.y, spot.Local.z);
        bool film = IsFilm(spot);
        Basis frame;
        if (film)
        {
            var down = bone.Basis.Transposed() * Vector3.Down;
            down /= Math.Max(down.Length(), 1e-6f);
            var slope = down - n * down.Dot(n);
            float steep = slope.Length();
            if (steep > 0.05f)
                t1 = slope / steep;
            var t2 = n.Cross(t1);
            float a = Math.Clamp(MathF.Sqrt(spot.VolumeMl * 1e-6f / (MathF.PI * FilmThick)), FilmMinRadius, FilmMaxRadius);
            float x = Math.Clamp((steep - 0.15f) / 0.55f, 0f, 1f);
            float run = 1f + 1.2f * x * x * (3f - 2f * x);
            frame = new Basis(t1 * (a * run), n * FilmLens, -t2 * a);
            at += t1 * (a * (run - 1f) * 0.8f) + n * FilmLift;
        }
        else
        {
            float flatten = Math.Max((spot.NormalShape >> 24) / 255f, 0.05f);
            float a = MathF.Cbrt(spot.VolumeMl * 1e-6f * 0.2387324f / flatten);
            var t2 = n.Cross(t1);
            frame = new Basis(t1 * a, n * (a * flatten), -t2 * a);
        }
        var local = new Transform3D(frame, at);
        var world = bone * local;
        var b = world.Basis;
        instance[0] = b.X.X; instance[1] = b.Y.X; instance[2] = b.Z.X; instance[3] = world.Origin.X;
        instance[4] = b.X.Y; instance[5] = b.Y.Y; instance[6] = b.Z.Y; instance[7] = world.Origin.Y;
        instance[8] = b.X.Z; instance[9] = b.Y.Z; instance[10] = b.Z.Z; instance[11] = world.Origin.Z;
        instance[12] = (spot.Tint & 255) / 255f;
        instance[13] = ((spot.Tint >> 8) & 255) / 255f;
        instance[14] = ((spot.Tint >> 16) & 255) / 255f;
        instance[15] = (spot.Tint >> 24) / 255f;
        instance[16] = (spot.Meta & 255) / 255f;
        instance[17] = ((spot.Meta >> 8) & 255) / 255f;
        instance[18] = film ? 1f : 0f;
        instance[19] = spot.VolumeMl;
        return (spot.Meta & HeadFlag) != 0;
    }

    public static Vector3 OctDecode(Vector2 e)
    {
        var f = e * 2f - Vector2.One;
        var n = new Vector3(f.X, f.Y, 1f - Math.Abs(f.X) - Math.Abs(f.Y));
        float t = Math.Clamp(-n.Z, 0f, 1f);
        n.X += n.X >= 0f ? -t : t;
        n.Y += n.Y >= 0f ? -t : t;
        return n.Normalized();
    }

    public static uint PackNormalShape(Vector3 normal, float flatten)
    {
        normal = normal.Normalized();
        float l1 = Math.Abs(normal.X) + Math.Abs(normal.Y) + Math.Abs(normal.Z);
        var n = normal / l1;
        var o = n.Z >= 0f
            ? new Vector2(n.X, n.Y)
            : new Vector2((1f - Math.Abs(n.Y)) * (n.X >= 0f ? 1f : -1f), (1f - Math.Abs(n.X)) * (n.Y >= 0f ? 1f : -1f));
        o = o * 0.5f + new Vector2(0.5f, 0.5f);
        uint x = (uint)Math.Clamp(MathF.Round(o.X * 4095f), 0f, 4095f);
        uint y = (uint)Math.Clamp(MathF.Round(o.Y * 4095f), 0f, 4095f);
        uint f8 = (uint)Math.Clamp(MathF.Round(flatten * 255f), 0f, 255f);
        return x | (y << 12) | (f8 << 24);
    }

    public void Dispose()
    {
        if (_set.IsValid && _rd.UniformSetIsValid(_set))
            _rd.FreeRid(_set);
        foreach (var rid in new[] { _spots, _bones })
        {
            if (rid.IsValid)
                _rd.FreeRid(rid);
        }
        foreach (var rid in new[] { _mainInstance, _headInstance, _main, _head, _spotMesh })
        {
            if (rid.IsValid)
                RenderingServer.FreeRid(rid);
        }
        _spots = _bones = _mainInstance = _headInstance = _main = _head = _spotMesh = default;
    }
}
