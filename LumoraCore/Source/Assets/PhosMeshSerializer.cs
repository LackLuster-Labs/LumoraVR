// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using K4os.Compression.LZ4;
using Lumora.Core.Math;
using Lumora.Core.Phos;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Assets;

// Binary (de)serializer for a PhosMesh - the ".lmesh" on-disk/asset format. Engine-agnostic: it only
// touches PhosMesh's public surface, no Godot. This is what lets a skinned avatar mesh travel as a
// single content-hashed asset instead of tens of thousands of replicated data-model elements.
//
// Every read is bounds-checked and the whole decode is wrapped so a malformed/truncated blob throws a
// clean InvalidDataException instead of corrupting state or crashing.
//
// LAYOUT
//   magic "LMSH" (4) | version (4) | ...
//   v1: the body follows the version directly, uncompressed, blend shape deltas dense, UV channels a
//       contiguous count. Files in this shape are already in local caches, so v1 stays readable.
//   v2: container flags (1) + uncompressed body length (4), then the body, LZ4 block compressed when
//       that actually shrinks it. Inside, blend shape frames are SPARSE (moved-vertex indices plus
//       deltas for only those vertices) and UV channels carry a presence bitmask.
//   v3: the same sparse body, optionally Brotli compressed. Repeated morph channels can be hundreds
//       of kilobytes apart, beyond a short block compressor's history. -xlinka
//
// Sparse frames are the whole point of v2. A real avatar moves almost nothing: 7,209,317 vertex-shapes
// across 756 attachments, of which 490,228 move. Dense costs 139 MB of deltas that are 93% zeros; the
// index+delta form costs about 18 MB before compression. It expands back to dense on read because the
// upload path and the mesh hooks both require dense arrays, so nothing downstream has to change.
// -xlinka
public static class PhosMeshSerializer
{
    private static readonly byte[] Magic = { (byte)'L', (byte)'M', (byte)'S', (byte)'H' };

    private const int CurrentVersion = 3;
    private const int SparseVersion = 2;
    private const int LegacyDenseVersion = 1;

    // magic(4) + version(4) + container flags(1) + uncompressed body length(4)
    private const int HeaderBytes = 13;

    private const byte ContainerCompressed = 1 << 0;
    private const byte ContainerBrotli = 1 << 1;

    // Sanity caps so a hostile/garbage header can't make us allocate or loop unbounded. -xlinka
    private const int MaxVertices = 8_000_000;
    private const int MaxCount = 8_000_000;
    private const int MaxNameBytes = 4096;
    private const int MaxBodyBytes = 768 * 1024 * 1024;

    // A delta under a micrometre in model units cannot show on screen and is exporter float noise, so
    // it is not worth an index and twelve bytes. Anything at or above it is kept verbatim. -xlinka
    private const float MovedEpsilon = 1e-6f;

    // Geometry flags packed into one byte.
    [Flags]
    private enum MeshFlags : byte
    {
        None = 0,
        Normals = 1 << 0,
        Tangents = 1 << 1,
        Colors = 1 << 2,
        BoneBindings = 1 << 3,
    }

    [Flags]
    private enum FrameFlags : byte
    {
        None = 0,
        Normals = 1 << 0,
        Tangents = 1 << 1,
    }

    // What one blend shape frame will actually put on disk, worked out once so the measuring pass and
    // the emitting pass agree byte for byte and neither rescans seven million vertex-shapes twice.
    private struct FramePlan
    {
        public int[] Moved;
        public bool HasNormals;
        public bool HasTangents;
    }

    private static void RefuseUnreadable(int count, int max, string what)
    {
        if (count > max)
            throw new NotSupportedException($"{count} {what}; .lmesh reads back at most {max}.");
    }

    public static byte[] Serialize(PhosMesh mesh)
    {
        if (mesh == null) throw new ArgumentNullException(nameof(mesh));

        // Refuse a topology the reader cannot reconstruct, BEFORE anything is written. A blob that
        // round-trips only one way still gets content-hashed into the database and then fails on every
        // load forever, which is worse than not baking it. -xlinka
        for (int i = 0; i < mesh.Submeshes.Count; i++)
        {
            var topology = mesh.Submeshes[i].Topology;
            if (topology != PhosTopology.Triangles && topology != PhosTopology.Points)
                throw new NotSupportedException(
                    $"Submesh {i} of {mesh.Submeshes.Count} has topology {topology}; .lmesh reads back only Triangles and Points.");
        }

        int vc = mesh.VertexCount;

        // The reader's limits, held to on the way out as well. Nothing checked them here, so a mesh past
        // one baked, hashed and saved fine and then failed on every load after. -xlinka
        RefuseUnreadable(vc, MaxVertices, "vertices");
        RefuseUnreadable(mesh.Submeshes.Count, MaxCount, "submeshes");
        for (int i = 0; i < mesh.Submeshes.Count; i++)
            RefuseUnreadable(mesh.Submeshes[i].IndexCount, MaxCount, $"indices in submesh {i}");
        RefuseUnreadable(mesh.BoneCount, MaxCount, "bones");
        RefuseUnreadable(mesh.BlendShapes.Count, MaxCount, "blend shapes");
        for (int s = 0; s < mesh.BlendShapes.Count; s++)
            RefuseUnreadable(mesh.BlendShapes[s].Frames?.Length ?? 0, MaxCount, $"frames in blend shape {s}");

        // Presence per channel, not a contiguous count: writing a count of 4 for a mesh that only has
        // UV0 and UV3 makes the reader materialise empty channels 1 and 2 that the source never had.
        byte uvMask = 0;
        for (int ch = 0; ch < 4; ch++)
        {
            if (GetUVChannel(mesh, ch).Length > 0)
                uvMask |= (byte)(1 << ch);
        }

        var plan = BuildBlendPlan(mesh, vc);

        // Measure with the same writer that emits, so the pre-sized buffer can never disagree with the
        // layout. A MemoryStream that doubles and then hands out ToArray() peaks at two to three times
        // the blob at exactly the moment we are trying to hold memory down. -xlinka
        long measured;
        var counter = new CountingStream();
        using (var cw = new BinaryWriter(counter, Encoding.UTF8, leaveOpen: true))
        {
            WriteBody(cw, mesh, vc, uvMask, plan, reportTruncation: true);
            cw.Flush();
            measured = counter.Length;
        }

        if (measured > MaxBodyBytes)
            throw new InvalidDataException($"Serialized mesh body is {measured} bytes, over the {MaxBodyBytes} cap.");

        int bodyLength = (int)measured;
        var raw = new byte[HeaderBytes + bodyLength];
        using (var ms = new MemoryStream(raw, HeaderBytes, bodyLength, writable: true, publiclyVisible: true))
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            WriteBody(w, mesh, vc, uvMask, plan, reportTruncation: false);
            w.Flush();
        }

        var scratch = new byte[HeaderBytes + LZ4Codec.MaximumOutputSize(bodyLength)];
        int written = LZ4Codec.Encode(raw.AsSpan(HeaderBytes, bodyLength), scratch.AsSpan(HeaderBytes), LZ4Level.L09_HC);
        int bestLength = written > 0 ? System.Math.Min(written, bodyLength) : bodyLength;
        // Reuse the destination so trying a larger history does not hold a third mesh-sized buffer.
        // A cached avatar's repeated normal channels reduced from 235 MB to 11 MB this way. -xlinka
        if (BrotliEncoder.TryCompress(raw.AsSpan(HeaderBytes, bodyLength), scratch.AsSpan(HeaderBytes),
            out int packed, quality: 5, window: 24) && packed < bestLength)
        {
            raw = null!;
            Array.Resize(ref scratch, HeaderBytes + packed);
            WriteHeader(scratch, ContainerBrotli, bodyLength);
            return scratch;
        }
        if (written > 0 && written < bodyLength)
            written = LZ4Codec.Encode(raw.AsSpan(HeaderBytes, bodyLength), scratch.AsSpan(HeaderBytes), LZ4Level.L09_HC);
        if (written > 0 && written < bodyLength)
        {
            // Release the uncompressed body before the trim copy so only one large allocation is
            // standing at the peak.
            raw = null!;
            if (scratch.Length != HeaderBytes + written)
                Array.Resize(ref scratch, HeaderBytes + written);
            WriteHeader(scratch, ContainerCompressed, bodyLength);
            return scratch;
        }

        // Already-packed data (small meshes, anything the codec cannot beat) stays raw and costs no
        // copy at all: the header space was reserved at the front of this same buffer.
        WriteHeader(raw, 0, bodyLength);
        return raw;
    }

    public static PhosMesh Deserialize(byte[] data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));

        try
        {
            if (data.Length < 8 ||
                data[0] != Magic[0] || data[1] != Magic[1] || data[2] != Magic[2] || data[3] != Magic[3])
                throw new InvalidDataException("Not an .lmesh blob (bad magic).");

            int version = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));

            if (version == LegacyDenseVersion)
            {
                using var legacy = new MemoryStream(data, 8, data.Length - 8, writable: false);
                using var lr = new BinaryReader(legacy, Encoding.UTF8, leaveOpen: true);
                return ReadBody(lr, version);
            }

            if (version != SparseVersion && version != CurrentVersion)
                throw new InvalidDataException($"Unsupported .lmesh version {version} (expected {LegacyDenseVersion}, {SparseVersion} or {CurrentVersion}).");

            if (data.Length < HeaderBytes)
                throw new InvalidDataException("Truncated .lmesh blob.");

            byte container = data[8];
            if (container != 0 && container != ContainerCompressed && container != ContainerBrotli
                || version == SparseVersion && container == ContainerBrotli)
                throw new InvalidDataException($"Unsupported .lmesh compression {container}.");
            int bodyLength = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(9));
            if (bodyLength < 0 || bodyLength > MaxBodyBytes)
                throw new InvalidDataException($"Body length {bodyLength} out of range.");

            if (container != 0)
            {
                var body = new byte[bodyLength];
                int decoded;
                if (container == ContainerBrotli)
                {
                    if (!BrotliDecoder.TryDecompress(data.AsSpan(HeaderBytes), body.AsSpan(), out decoded))
                        throw new InvalidDataException("Invalid compressed .lmesh body.");
                }
                else
                    decoded = LZ4Codec.Decode(data.AsSpan(HeaderBytes), body.AsSpan());
                if (decoded != bodyLength)
                    throw new InvalidDataException($"Compressed body decoded to {decoded} bytes, expected {bodyLength}.");

                using var ms = new MemoryStream(body, writable: false);
                using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
                return ReadBody(r, version);
            }

            if (data.Length - HeaderBytes < bodyLength)
                throw new InvalidDataException("Truncated .lmesh blob.");

            using var plainStream = new MemoryStream(data, HeaderBytes, bodyLength, writable: false);
            using var plainReader = new BinaryReader(plainStream, Encoding.UTF8, leaveOpen: true);
            return ReadBody(plainReader, version);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("Truncated .lmesh blob.", ex);
        }
    }

    // --- body ---

    private static void WriteBody(BinaryWriter w, PhosMesh mesh, int vc, byte uvMask, FramePlan[][] plan, bool reportTruncation)
    {
        w.Write(vc);

        var flags = MeshFlags.None;
        if (mesh.HasNormals) flags |= MeshFlags.Normals;
        if (mesh.HasTangents) flags |= MeshFlags.Tangents;
        if (mesh.HasColors) flags |= MeshFlags.Colors;
        if (mesh.HasBoneBindings) flags |= MeshFlags.BoneBindings;
        w.Write((byte)flags);
        w.Write(uvMask);

        // Per-vertex blocks (read up to VertexCount, never array.Length - the backing arrays are
        // intentionally over-allocated). -xlinka
        var pos = mesh.RawPositions;
        for (int i = 0; i < vc; i++) WriteFloat3(w, pos[i]);

        if (flags.HasFlag(MeshFlags.Normals))
        {
            var n = mesh.RawNormals;
            for (int i = 0; i < vc; i++) WriteFloat3(w, n[i]);
        }
        if (flags.HasFlag(MeshFlags.Tangents))
        {
            var t = mesh.RawTangents;
            for (int i = 0; i < vc; i++) WriteFloat4(w, t[i]);
        }
        if (flags.HasFlag(MeshFlags.Colors))
        {
            var c = mesh.RawColors;
            for (int i = 0; i < vc; i++) WriteColor(w, c[i]);
        }
        if (flags.HasFlag(MeshFlags.BoneBindings))
        {
            var b = mesh.RawBoneBindings;
            for (int i = 0; i < vc; i++)
            {
                WriteFloat4(w, b[i].boneIndices);
                WriteFloat4(w, b[i].boneWeights);
            }
        }

        for (int ch = 0; ch < 4; ch++)
        {
            if ((uvMask & (1 << ch)) == 0) continue;
            var uv = GetUVChannel(mesh, ch);
            for (int i = 0; i < vc; i++)
                WriteFloat2(w, i < uv.Length ? uv[i] : float2.Zero);
        }

        w.Write(mesh.Submeshes.Count);
        foreach (var sm in mesh.Submeshes)
        {
            w.Write((int)sm.Topology);
            int idxCount = sm.IndexCount;
            w.Write(idxCount);
            var idx = sm.RawIndices;
            for (int i = 0; i < idxCount; i++) w.Write(idx[i]);
        }

        w.Write(mesh.BoneCount);
        for (int i = 0; i < mesh.BoneCount; i++)
        {
            var bone = mesh.GetBone(i);
            WriteString(w, bone.Name, reportTruncation);
            WriteMatrix(w, bone.BindPose);
        }

        w.Write(mesh.BlendShapes.Count);
        for (int s = 0; s < mesh.BlendShapes.Count; s++)
        {
            var shape = mesh.BlendShapes[s];
            WriteString(w, shape.Name ?? string.Empty, reportTruncation);

            var frames = plan[s];
            w.Write(frames.Length);
            for (int f = 0; f < frames.Length; f++)
            {
                var frame = shape.Frames![f];
                var fp = frames[f];

                var frameFlags = FrameFlags.None;
                if (fp.HasNormals) frameFlags |= FrameFlags.Normals;
                if (fp.HasTangents) frameFlags |= FrameFlags.Tangents;
                w.Write((byte)frameFlags);

                var moved = fp.Moved;
                w.Write(moved.Length);
                for (int i = 0; i < moved.Length; i++) w.Write(moved[i]);

                for (int i = 0; i < moved.Length; i++) WriteFloat3(w, SafeGet(frame?.positions, moved[i]));
                if (fp.HasNormals)
                    for (int i = 0; i < moved.Length; i++) WriteFloat3(w, SafeGet(frame!.normals, moved[i]));
                if (fp.HasTangents)
                    for (int i = 0; i < moved.Length; i++) WriteFloat3(w, SafeGet(frame!.tangents, moved[i]));
            }
        }
    }

    private static PhosMesh ReadBody(BinaryReader r, int version)
    {
        int vc = r.ReadInt32();
        if (vc < 0 || vc > MaxVertices)
            throw new InvalidDataException($"Vertex count {vc} out of range.");

        var flags = (MeshFlags)r.ReadByte();

        int uvMask;
        if (version == LegacyDenseVersion)
        {
            int uvCount = r.ReadInt32();
            if (uvCount < 0 || uvCount > 4)
                throw new InvalidDataException($"UV channel count {uvCount} out of range.");
            uvMask = (1 << uvCount) - 1;
        }
        else
        {
            uvMask = r.ReadByte();
            if ((uvMask & ~0xF) != 0)
                throw new InvalidDataException($"UV channel mask 0x{uvMask:X2} out of range.");
        }

        var mesh = new PhosMesh();

        // Set flags BEFORE growing the vertex arrays so IncreaseVertexCount allocates them. -xlinka
        mesh.HasNormals = flags.HasFlag(MeshFlags.Normals);
        mesh.HasTangents = flags.HasFlag(MeshFlags.Tangents);
        mesh.HasColors = flags.HasFlag(MeshFlags.Colors);
        mesh.HasBoneBindings = flags.HasFlag(MeshFlags.BoneBindings);
        if (mesh.HasBoneBindings) mesh.EnsureBoneBindings();

        if (vc > 0) mesh.IncreaseVertexCount(vc);

        var pos = mesh.RawPositions;
        for (int i = 0; i < vc; i++) pos[i] = ReadFloat3(r);

        if (mesh.HasNormals)
        {
            var n = mesh.RawNormals;
            for (int i = 0; i < vc; i++) n[i] = ReadFloat3(r);
        }
        if (mesh.HasTangents)
        {
            var t = mesh.RawTangents;
            for (int i = 0; i < vc; i++) t[i] = ReadFloat4(r);
        }
        if (mesh.HasColors)
        {
            var c = mesh.RawColors;
            for (int i = 0; i < vc; i++) c[i] = ReadColor(r);
        }
        if (mesh.HasBoneBindings)
        {
            var b = mesh.RawBoneBindings;
            for (int i = 0; i < vc; i++)
            {
                var indices = ReadFloat4(r);
                var weights = ReadFloat4(r);
                b[i] = new PhosBoneBinding(indices, weights);
            }
        }

        for (int ch = 0; ch < 4; ch++)
        {
            if ((uvMask & (1 << ch)) == 0) continue;
            mesh.SetHasUV(ch, true);
            for (int i = 0; i < vc; i++)
                mesh.SetUV(ch, i, ReadFloat2(r));
        }

        int submeshCount = r.ReadInt32();
        if (submeshCount < 0 || submeshCount > MaxCount)
            throw new InvalidDataException($"Submesh count {submeshCount} out of range.");
        for (int s = 0; s < submeshCount; s++)
        {
            int topology = r.ReadInt32();
            int idxCount = r.ReadInt32();
            if (idxCount < 0 || idxCount > MaxCount)
                throw new InvalidDataException($"Submesh index count {idxCount} out of range.");

            PhosSubmesh sm = (PhosTopology)topology switch
            {
                PhosTopology.Triangles => new PhosTriangleSubmesh(mesh),
                PhosTopology.Points => new PhosPointSubmesh(mesh),
                _ => throw new InvalidDataException($"Unsupported submesh topology {topology}."),
            };

            if (idxCount % sm.IndicesPerElement != 0)
                throw new InvalidDataException($"Index count {idxCount} is not a multiple of {sm.IndicesPerElement} for topology {topology}.");

            if (idxCount > 0) sm.IncreaseCount(idxCount / sm.IndicesPerElement);
            var idx = sm.RawIndices;
            for (int i = 0; i < idxCount; i++) idx[i] = r.ReadInt32();

            mesh.Submeshes.Add(sm);
        }

        int boneCount = r.ReadInt32();
        if (boneCount < 0 || boneCount > MaxCount)
            throw new InvalidDataException($"Bone count {boneCount} out of range.");
        for (int i = 0; i < boneCount; i++)
        {
            var name = ReadString(r);
            var bindPose = ReadMatrix(r);
            var bone = mesh.AddBone(name);
            bone.BindPose = bindPose;
        }

        int blendCount = r.ReadInt32();
        if (blendCount < 0 || blendCount > MaxCount)
            throw new InvalidDataException($"Blend shape count {blendCount} out of range.");
        for (int s = 0; s < blendCount; s++)
        {
            var name = ReadString(r);
            int frameCount = r.ReadInt32();
            if (frameCount < 0 || frameCount > MaxCount)
                throw new InvalidDataException($"Blend shape frame count {frameCount} out of range.");

            var shape = new PhosBlendShape(name, frameCount);
            for (int f = 0; f < frameCount; f++)
            {
                shape.Frames[f] = version == LegacyDenseVersion
                    ? ReadDenseFrame(r, vc)
                    : ReadSparseFrame(r, vc);
            }
            mesh.BlendShapes.Add(shape);
        }

        return mesh;
    }

    private static PhosBlendShapeFrame ReadDenseFrame(BinaryReader r, int vc)
    {
        bool hasN = r.ReadBoolean();
        bool hasT = r.ReadBoolean();

        // PhosBlendShape allocates a Frames array of NULL elements (it's a class) - we must
        // instantiate each frame before writing into it. -xlinka
        var frame = new PhosBlendShapeFrame();
        frame.Allocate(vc, hasN, hasT);

        for (int i = 0; i < vc; i++) frame.positions[i] = ReadFloat3(r);
        if (hasN)
            for (int i = 0; i < vc; i++) frame.normals[i] = ReadFloat3(r);
        if (hasT)
            for (int i = 0; i < vc; i++) frame.tangents[i] = ReadFloat3(r);
        return frame;
    }

    // Sparse on disk, dense in memory: Godot wants a full delta array at upload and the mesh hooks
    // index it directly, so the scatter has to happen here and not later. -xlinka
    private static PhosBlendShapeFrame ReadSparseFrame(BinaryReader r, int vc)
    {
        var frameFlags = (FrameFlags)r.ReadByte();
        bool hasN = frameFlags.HasFlag(FrameFlags.Normals);
        bool hasT = frameFlags.HasFlag(FrameFlags.Tangents);

        int moved = r.ReadInt32();
        if (moved < 0 || moved > vc)
            throw new InvalidDataException($"Blend shape frame moves {moved} of {vc} vertices.");

        var frame = new PhosBlendShapeFrame();
        frame.Allocate(vc, hasN, hasT);

        var indices = moved > 0 ? new int[moved] : Array.Empty<int>();
        for (int i = 0; i < moved; i++)
        {
            int index = r.ReadInt32();
            if (index < 0 || index >= vc)
                throw new InvalidDataException($"Blend shape delta index {index} out of range (vertex count {vc}).");
            indices[i] = index;
        }

        for (int i = 0; i < moved; i++) frame.positions[indices[i]] = ReadFloat3(r);
        if (hasN)
            for (int i = 0; i < moved; i++) frame.normals[indices[i]] = ReadFloat3(r);
        if (hasT)
            for (int i = 0; i < moved; i++) frame.tangents[indices[i]] = ReadFloat3(r);
        return frame;
    }

    private static FramePlan[][] BuildBlendPlan(PhosMesh mesh, int vc)
    {
        var plan = new FramePlan[mesh.BlendShapes.Count][];
        for (int s = 0; s < mesh.BlendShapes.Count; s++)
        {
            var frames = mesh.BlendShapes[s].Frames;
            int frameCount = frames?.Length ?? 0;
            plan[s] = new FramePlan[frameCount];

            for (int f = 0; f < frameCount; f++)
            {
                var frame = frames![f];
                bool hasN = frame?.normals != null && frame.normals.Length > 0;
                bool hasT = frame?.tangents != null && frame.tangents.Length > 0;

                int count = 0;
                for (int i = 0; i < vc; i++)
                {
                    if (IsMoved(frame, i, hasN, hasT)) count++;
                }

                var moved = count > 0 ? new int[count] : Array.Empty<int>();
                int at = 0;
                for (int i = 0; i < vc && at < count; i++)
                {
                    if (IsMoved(frame, i, hasN, hasT)) moved[at++] = i;
                }

                plan[s][f] = new FramePlan
                {
                    Moved = moved,
                    // A shape that carries a channel but never moves it costs nothing here, and a
                    // channel with no moved vertices is not worth declaring at all.
                    HasNormals = hasN && count > 0,
                    HasTangents = hasT && count > 0,
                };
            }
        }
        return plan;
    }

    private static bool IsMoved(PhosBlendShapeFrame? frame, int i, bool hasN, bool hasT)
    {
        if (frame == null) return false;
        if (Exceeds(SafeGet(frame.positions, i))) return true;
        if (hasN && Exceeds(SafeGet(frame.normals, i))) return true;
        if (hasT && Exceeds(SafeGet(frame.tangents, i))) return true;
        return false;
    }

    private static bool Exceeds(float3 v) =>
        System.Math.Abs(v.x) > MovedEpsilon ||
        System.Math.Abs(v.y) > MovedEpsilon ||
        System.Math.Abs(v.z) > MovedEpsilon;

    private static void WriteHeader(byte[] buffer, byte containerFlags, int bodyLength)
    {
        buffer[0] = Magic[0];
        buffer[1] = Magic[1];
        buffer[2] = Magic[2];
        buffer[3] = Magic[3];
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), containerFlags == ContainerBrotli ? CurrentVersion : SparseVersion);
        buffer[8] = containerFlags;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(9), bodyLength);
    }

    // Counts what a write would cost without holding any of it, so the real buffer can be allocated
    // once at exactly the right size.
    private sealed class CountingStream : Stream
    {
        private long _length;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;

        public override long Position
        {
            get => _length;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _length += count;
        public override void Write(ReadOnlySpan<byte> buffer) => _length += buffer.Length;
        public override void WriteByte(byte value) => _length++;
    }

    // --- primitive read/write helpers (BinaryWriter is little-endian on our targets) ---

    private static void WriteFloat2(BinaryWriter w, float2 v) { w.Write(v.x); w.Write(v.y); }
    private static void WriteFloat3(BinaryWriter w, float3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
    private static void WriteFloat4(BinaryWriter w, float4 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); w.Write(v.w); }
    private static void WriteColor(BinaryWriter w, color v) { w.Write(v.r); w.Write(v.g); w.Write(v.b); w.Write(v.a); }
    private static void WriteMatrix(BinaryWriter w, float4x4 m)
    {
        WriteFloat4(w, m.c0); WriteFloat4(w, m.c1); WriteFloat4(w, m.c2); WriteFloat4(w, m.c3);
    }

    private static float2 ReadFloat2(BinaryReader r) => new float2 { x = r.ReadSingle(), y = r.ReadSingle() };
    private static float3 ReadFloat3(BinaryReader r) => new float3 { x = r.ReadSingle(), y = r.ReadSingle(), z = r.ReadSingle() };
    private static float4 ReadFloat4(BinaryReader r) => new float4 { x = r.ReadSingle(), y = r.ReadSingle(), z = r.ReadSingle(), w = r.ReadSingle() };
    private static color ReadColor(BinaryReader r) => new color { r = r.ReadSingle(), g = r.ReadSingle(), b = r.ReadSingle(), a = r.ReadSingle() };
    private static float4x4 ReadMatrix(BinaryReader r) => new float4x4 { c0 = ReadFloat4(r), c1 = ReadFloat4(r), c2 = ReadFloat4(r), c3 = ReadFloat4(r) };

    private static void WriteString(BinaryWriter w, string s, bool reportTruncation)
    {
        var text = s ?? string.Empty;
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > MaxNameBytes)
        {
            // An over-long bone or shape name is a broken exporter, and throwing here throws away the
            // whole import after the parse and every mesh already baked. Cut it on a character
            // boundary instead so the name stays valid UTF-8, and say so once. The read side keeps
            // the throw: a length past the cap there means the blob is malformed. -xlinka
            int was = bytes.Length;
            text = TruncateUtf8(text, MaxNameBytes);
            bytes = Encoding.UTF8.GetBytes(text);
            if (reportTruncation)
                LumoraLogger.Warn($"PhosMeshSerializer: name of {was} UTF-8 bytes truncated to the {MaxNameBytes} cap (starts \"{text.Substring(0, System.Math.Min(48, text.Length))}\").");
        }
        w.Write(bytes.Length);
        w.Write(bytes);
    }

    private static string TruncateUtf8(string s, int maxBytes)
    {
        var encoder = Encoding.UTF8.GetEncoder();
        var buffer = new byte[maxBytes];
        encoder.Convert(s.AsSpan(), buffer.AsSpan(), flush: true, out int charsUsed, out _, out _);
        return s.Substring(0, charsUsed);
    }

    private static string ReadString(BinaryReader r)
    {
        int len = r.ReadInt32();
        if (len < 0 || len > MaxNameBytes)
            throw new InvalidDataException($"String length {len} out of range.");
        var bytes = r.ReadBytes(len);
        if (bytes.Length != len)
            throw new InvalidDataException("Truncated string.");
        return Encoding.UTF8.GetString(bytes);
    }

    private static float2[] GetUVChannel(PhosMesh mesh, int ch) => ch switch
    {
        0 => mesh.RawUV0s,
        1 => mesh.RawUV1s,
        2 => mesh.RawUV2s,
        3 => mesh.RawUV3s,
        _ => Array.Empty<float2>(),
    };

    private static float3 SafeGet(float3[]? arr, int i) => (arr != null && i < arr.Length) ? arr[i] : float3.Zero;
}
