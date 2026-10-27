// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Lumora.Core.Assets.Interop.Unity;

// A self-contained animator asset. Source curves and the graph travel together by asset hash;
// neither playback nor a joining peer needs the extracted Unity package to remain on disk.
//
// An avatar's playable layers are one program: the descriptor's base, additive, gesture, action and FX
// controllers in that order, then whatever a build tool would have merged in at upload, each a source with
// the playable layer it joins, a path prefix for the object its clips were written against, path rewrites,
// and parameter renames. Every peer rebuilds the same graph from the same texts. -xlinka
public sealed class UnityAnimatorProgram
{
    private const int Magic1 = 0x3141554c; // LUA1
    private const int Magic2 = 0x3241554c; // LUA2
    private const int Magic3 = 0x3341554c; // LUA3
    private const int MaxBytes = 48 * 1024 * 1024;
    private const int MaxTexts = 16384;

    public sealed class Source
    {
        public string Text = "";
        public int Playable = 5;
        public string PathPrefix = "";
        public bool RootBindingsApplyToAvatar;
        public readonly Dictionary<string, string> Renames = new(StringComparer.Ordinal);
        public readonly List<(string From, string To, bool Delete)> Rewrites = new();
        // An override controller's clip swaps over its base controller: clip guid -> clip guid.
        public readonly Dictionary<string, string> Overrides = new(StringComparer.OrdinalIgnoreCase);
        public string Tag = "";

        public bool Moves => PathPrefix.Length > 0 || Rewrites.Count > 0 || Renames.Count > 0;

        public string Rename(string name) => Renames.TryGetValue(name, out var to) ? to : name;

        // Where a clip path of this source lands from the avatar's root; null when a rewrite deletes it.
        public string? Path(string path)
        {
            foreach (var (from, to, delete) in Rewrites)
            {
                if (from.Length > 0 && !(path == from || path.StartsWith(from + "/", StringComparison.Ordinal)))
                    continue;
                if (delete)
                    return null;
                path = to + path[from.Length..];
                if (path.StartsWith("/", StringComparison.Ordinal))
                    path = path[1..];
                break;
            }
            if (PathPrefix.Length == 0 || path.Length == 0 && RootBindingsApplyToAvatar)
                return path;
            return path.Length == 0 ? PathPrefix : PathPrefix + "/" + path;
        }
    }

    // What to read into a program: a controller (or override controller) asset and how it joins.
    public sealed record Spec(string Guid, int Playable, string PathPrefix = "", bool RootBindingsApplyToAvatar = false,
        IReadOnlyDictionary<string, string>? Renames = null, IReadOnlyList<(string From, string To, bool Delete)>? Rewrites = null);

    public readonly List<Source> Sources = new();
    private readonly Dictionary<string, string> _texts;
    public UnityAnimatorController Controller { get; }
    // Clips by reference key ("guid", or "guid|tag" as a merge places it).
    public readonly Dictionary<string, UnityAnimationClip> Clips = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<long, UnityAnimatorController.BlendTree>> _trees = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UnityAvatarMask?> _masks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UnityAnimationClip?> _parsed = new(StringComparer.OrdinalIgnoreCase);
    public int ExternalBlendTrees => _trees.Values.Sum(t => t.Count);
    public readonly Dictionary<string, float> Defaults = new(StringComparer.Ordinal);
    private UnityAnimatorGraph? _graph;
    private readonly object _compile = new();

    // Prefixes merged sources hang their clips under; a path that does not exist below one is tried from
    // the avatar's root, the way the build tool resolves it.
    public IEnumerable<string> PathPrefixes => Sources.Select(s => s.PathPrefix).Where(p => p.Length > 0).Distinct(StringComparer.Ordinal);

    private UnityAnimatorProgram(List<Source> sources, Dictionary<string, string> texts, IReadOnlyDictionary<string, float> defaults,
        List<UnityAnimatorController?>? parsed = null)
    {
        _texts = texts;
        Sources.AddRange(sources);
        int movedTags = 0;
        foreach (var source in Sources)
            source.Tag = source.Moves ? (++movedTags).ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        UnityAnimatorController? merged = null;
        for (int i = 0; i < Sources.Count; i++)
        {
            var source = Sources[i];
            var controller = parsed != null && i < parsed.Count ? parsed[i] : Parsed(source);
            if (controller == null)
                continue;
            if (merged == null && !source.Moves)
                merged = controller;
            else
                (merged ??= new UnityAnimatorController()).Append(controller, source.Rename, source.Tag, source.Playable);
        }
        Controller = merged ?? throw new InvalidDataException("Missing animator controller.");
        if (Controller.Layers.Count > 512 || Controller.States.Count > 32768 || Controller.Transitions.Count > 131072
            || Controller.Machines.Count > 16384 || Controller.BlendTrees.Count > 32768)
            throw new InvalidDataException("Animator controller exceeds the graph limit.");
        foreach (var (guid, text) in texts)
        {
            if (!text.Contains("--- !u!206 ", StringComparison.Ordinal))
                continue;
            var trees = UnityYaml.Parse(text).Where(d => d.ClassId == 206).ToDictionary(d => d.FileId, d => UnityAnimatorController.ReadTree(d, guid));
            if (trees.Count > 0) _trees[guid] = trees;
        }
        foreach (var motion in Motions())
            if (motion.Guid is { } key && !TryGetTree(motion, out _) && Clip(key) is { } clip)
                Clips[key] = clip;
        foreach (var p in Controller.Parameters) Defaults[p.Name] = p.Default;
        foreach (var (name, value) in defaults) Defaults[name] = value;
    }

    private static UnityAnimatorController? Parsed(Source source)
    {
        var controller = UnityAnimatorController.Parse(source.Text, source.Playable);
        if (controller != null && source.Overrides.Count > 0)
            ApplyOverrides(controller, source.Overrides);
        return controller;
    }

    private static void ApplyOverrides(UnityAnimatorController controller, Dictionary<string, string> map)
    {
        UnityRef Swap(UnityRef motion) => motion.Guid is { } guid && map.TryGetValue(guid, out var to)
            ? new UnityRef(7400000, to, 2) : motion;
        foreach (var state in controller.States.Values)
            state.Motion = Swap(state.Motion);
        foreach (var tree in controller.BlendTrees.Values)
            for (int i = 0; i < tree.Children.Count; i++)
                tree.Children[i] = (Swap(tree.Children[i].Motion), tree.Children[i].Threshold, tree.Children[i].DirectParameter);
        foreach (var layer in controller.Layers)
            foreach (var state in layer.SyncedMotions.Keys.ToList())
                layer.SyncedMotions[state] = Swap(layer.SyncedMotions[state]);
    }

    private Source? SourceOf(string tag) => tag.Length == 0 ? null : Sources.FirstOrDefault(s => s.Tag == tag);

    // An object path as the source a tag names places it.
    public string PathOf(string tag, string path) => SourceOf(tag) is { } source ? source.Path(path) ?? path : path;

    // A clip as the source its key names reads it.
    private UnityAnimationClip? Clip(string key)
    {
        var (guid, tag) = UnityAnimatorController.Untag(key);
        if (!_parsed.TryGetValue(guid, out var clip))
            _parsed[guid] = clip = _texts.TryGetValue(guid, out var text) ? UnityAnimationClip.Parse(text) : null;
        if (clip == null || SourceOf(tag) is not { } source)
            return clip;
        return clip.Rewritten(source.Path, source.Rename);
    }

    // A layer's mask, its object paths placed the way the layer's clips are.
    public UnityAvatarMask? Mask(UnityAnimatorController.Layer layer)
    {
        if (layer.Mask.Guid is not { Length: > 0 } guid)
            return null;
        var key = UnityAnimatorController.Tagged(guid, layer.Tag);
        if (_masks.TryGetValue(key, out var known))
            return known;
        var parsed = _texts.TryGetValue(guid, out var text) ? UnityAvatarMask.Parse(text) : null;
        if (parsed?.Transforms != null && SourceOf(layer.Tag) is { } source)
        {
            var moved = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var (path, on) in parsed.Transforms)
                if (source.Path(path) is { } to)
                    moved[to] = on;
            parsed.Transforms = moved;
        }
        _masks[key] = parsed;
        return parsed;
    }

    public static UnityAnimatorProgram? Read(UnityPackageArchive archive, IReadOnlyList<Spec> specs, IReadOnlyDictionary<string, float> defaults)
    {
        var sources = new List<Source>();
        foreach (var spec in specs)
        {
            var text = archive.ReadText(archive.Find(spec.Guid));
            if (string.IsNullOrEmpty(text))
                continue;
            var source = new Source { Playable = spec.Playable, PathPrefix = spec.PathPrefix.Trim('/'), RootBindingsApplyToAvatar = spec.RootBindingsApplyToAvatar };
            if (spec.Renames != null)
                foreach (var (from, to) in spec.Renames)
                    if (from != to) source.Renames[from] = to;
            if (spec.Rewrites != null)
                source.Rewrites.AddRange(spec.Rewrites);
            // An override controller swaps clips of the controller it is based on.
            if (text.Contains("--- !u!221 ", StringComparison.Ordinal))
            {
                var root = UnityYaml.Parse(text).First(d => d.ClassId == 221);
                var baseText = archive.ReadText(archive.Find(root["m_Controller"]?.Ref.Guid));
                if (string.IsNullOrEmpty(baseText))
                    continue;
                if (root["m_Clips"] is UnitySeq clips)
                    foreach (var c in clips.Items)
                        if (c["m_OriginalClip"]?.Ref.Guid is { Length: > 0 } from && c["m_OverrideClip"]?.Ref.Guid is { Length: > 0 } to)
                            source.Overrides[from] = to;
                text = baseText;
            }
            else if (!text.Contains("--- !u!91 ", StringComparison.Ordinal))
                continue;
            source.Text = text;
            sources.Add(source);
        }
        if (sources.Count == 0)
            return null;

        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Want(string? guid)
        {
            if (!string.IsNullOrEmpty(guid) && wanted.Add(guid))
                queue.Enqueue(guid);
        }
        var parsed = new List<UnityAnimatorController?>();
        foreach (var source in sources)
        {
            var controller = Parsed(source);
            parsed.Add(controller);
            if (controller == null)
                continue;
            foreach (var motion in ReachableMotions(controller))
                Want(motion.Guid);
            foreach (var layer in controller.Layers)
                Want(layer.Mask.Guid);
        }
        while (queue.TryDequeue(out var guid))
        {
            if (archive.Find(guid) is not { } asset || asset.Extension is not (".anim" or ".asset" or ".mask") || archive.ReadText(asset) is not { } text)
                continue;
            if (texts.Count >= MaxTexts) throw new InvalidDataException("Animator motion table exceeds the limit.");
            texts[guid] = text;
            if (text.Contains("--- !u!206 ", StringComparison.Ordinal))
                foreach (var doc in UnityYaml.Parse(text).Where(d => d.ClassId == 206))
                    foreach (var child in UnityAnimatorController.ReadTree(doc, guid).Children)
                        Want(child.Motion.Guid);
        }
        return new UnityAnimatorProgram(sources, texts, defaults, parsed);
    }

    // A single controller asset, the way the earlier importer stored FX on its own.
    public static UnityAnimatorProgram? Read(UnityPackageArchive archive, string? guid, IReadOnlyDictionary<string, float> defaults)
        => string.IsNullOrEmpty(guid) ? null : Read(archive, new[] { new Spec(guid, 5) }, defaults);

    public UnityAnimatorGraph Graph
    {
        get
        {
            if (_graph != null)
                return _graph;
            lock (_compile)
                return _graph ??= new UnityAnimatorGraph(this);
        }
    }

    public bool TryGetTree(UnityRef motion, out UnityAnimatorController.BlendTree tree)
    {
        tree = null!;
        if (motion.Guid is not { } key)
            return Controller.BlendTrees.TryGetValue(motion.FileId, out tree!);
        var (guid, _) = UnityAnimatorController.Untag(key);
        return _trees.TryGetValue(guid, out var trees) && trees.TryGetValue(motion.FileId, out tree!);
    }

    // A child of a tree read from a tree asset, carrying the tag of the reference that reached the tree.
    public static UnityRef Retag(UnityRef child, UnityRef parent)
    {
        if (child.Guid is not { } guid || parent.Guid is not { } parentKey)
            return child;
        var (_, tag) = UnityAnimatorController.Untag(parentKey);
        return tag.Length == 0 ? child : new UnityRef(child.FileId, UnityAnimatorController.Tagged(UnityAnimatorController.Untag(guid).Guid, tag), child.Type);
    }

    public IEnumerable<UnityRef> Motions()
    {
        var queue = new Queue<UnityRef>(ReachableMotions(Controller));
        var seen = new HashSet<UnityRef>();
        while (queue.TryDequeue(out var motion))
        {
            if (!seen.Add(motion)) continue;
            yield return motion;
            if (TryGetTree(motion, out var tree))
                foreach (var child in tree.Children)
                    queue.Enqueue(Retag(child.Motion, motion));
        }
    }

    public int MissingClipCount => Motions().Where(m => !m.IsNull && !TryGetTree(m, out _)
        && (m.Guid == null || !Clips.ContainsKey(m.Guid))).Distinct().Count();

    public static IEnumerable<UnityRef> ReachableMotions(UnityAnimatorController controller)
    {
        var machines = new HashSet<long>();
        var trees = new HashSet<long>();
        var pendingMachines = new Stack<long>(controller.Layers.Select(l => l.Machine));
        var pendingMotions = new Stack<UnityRef>();
        foreach (var layer in controller.Layers)
            foreach (var motion in layer.SyncedMotions.Values)
                pendingMotions.Push(motion);
        while (pendingMachines.TryPop(out long id))
        {
            if (!machines.Add(id) || !controller.Machines.TryGetValue(id, out var machine)) continue;
            foreach (long state in machine.States)
                if (controller.States.TryGetValue(state, out var s))
                    pendingMotions.Push(s.Motion);
            foreach (long child in machine.Machines) pendingMachines.Push(child);
        }
        while (pendingMotions.TryPop(out var motion))
        {
            yield return motion;
            if (motion.IsLocal && trees.Add(motion.FileId) && controller.BlendTrees.TryGetValue(motion.FileId, out var tree))
                foreach (var child in tree.Children) pendingMotions.Push(child.Motion);
        }
    }

    public byte[] ToBytes()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(Magic2);
        writer.Write(Sources.Count);
        foreach (var s in Sources)
        {
            writer.Write(s.Text);
            writer.Write(s.Playable);
            writer.Write(s.PathPrefix);
            writer.Write(s.RootBindingsApplyToAvatar);
            writer.Write(s.Renames.Count);
            foreach (var (from, to) in s.Renames) { writer.Write(from); writer.Write(to); }
            writer.Write(s.Rewrites.Count);
            foreach (var (from, to, delete) in s.Rewrites) { writer.Write(from); writer.Write(to); writer.Write(delete); }
            writer.Write(s.Overrides.Count);
            foreach (var (from, to) in s.Overrides) { writer.Write(from); writer.Write(to); }
        }
        writer.Write(Defaults.Count);
        foreach (var (name, value) in Defaults) { writer.Write(name); writer.Write(value); }
        writer.Write(_texts.Count);
        foreach (var (guid, text) in _texts) { writer.Write(guid); writer.Write(text); }
        if (stream.Length > MaxBytes) throw new InvalidDataException("Animator asset exceeds 48 MB.");
        // Curves and controller text contain repeated names and keys; keep their exact bytes in a bounded
        // compressed envelope so every peer need not download the expanded text. -xlinka
        var packed = new byte[8 + (int)stream.Length];
        if (BrotliEncoder.TryCompress(stream.GetBuffer().AsSpan(0, (int)stream.Length), packed.AsSpan(8),
            out int length, quality: 5, window: 22) && length + 8 < stream.Length)
        {
            BinaryPrimitives.WriteInt32LittleEndian(packed, Magic3);
            BinaryPrimitives.WriteInt32LittleEndian(packed.AsSpan(4), (int)stream.Length);
            Array.Resize(ref packed, length + 8);
            return packed;
        }
        return stream.ToArray();
    }

    public static UnityAnimatorProgram FromBytes(byte[] bytes)
    {
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Animator asset exceeds 48 MB.");
        if (bytes.Length >= 8 && BinaryPrimitives.ReadInt32LittleEndian(bytes) == Magic3)
        {
            int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4));
            if (length < 4 || length > MaxBytes)
                throw new InvalidDataException("Animator expanded length exceeds the limit.");
            var plain = new byte[length];
            if (!BrotliDecoder.TryDecompress(bytes.AsSpan(8), plain, out int decoded) || decoded != length
                || BinaryPrimitives.ReadInt32LittleEndian(plain) != Magic2)
                throw new InvalidDataException("Invalid compressed animator asset.");
            bytes = plain;
        }
        using var stream = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        int magic = reader.ReadInt32();
        if (magic != Magic1 && magic != Magic2) throw new InvalidDataException("Not an imported animator asset.");
        int Count(int limit = MaxTexts) { int n = reader.ReadInt32(); return n >= 0 && n <= limit ? n : throw new InvalidDataException("Animator table exceeds the limit."); }
        var sources = new List<Source>();
        if (magic == Magic1)
            sources.Add(new Source { Text = reader.ReadString() });
        else
        {
            int sourceCount = Count(256);
            for (int i = 0; i < sourceCount; i++)
            {
                var s = new Source { Text = reader.ReadString(), Playable = reader.ReadInt32(), PathPrefix = reader.ReadString(), RootBindingsApplyToAvatar = reader.ReadBoolean() };
                int n = Count();
                for (int k = 0; k < n; k++) s.Renames[reader.ReadString()] = reader.ReadString();
                n = Count();
                for (int k = 0; k < n; k++) s.Rewrites.Add((reader.ReadString(), reader.ReadString(), reader.ReadBoolean()));
                n = Count();
                for (int k = 0; k < n; k++) s.Overrides[reader.ReadString()] = reader.ReadString();
                sources.Add(s);
            }
        }
        var defaults = new Dictionary<string, float>(StringComparer.Ordinal);
        int count = Count();
        for (int i = 0; i < count; i++) defaults[reader.ReadString()] = reader.ReadSingle();
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        count = Count();
        for (int i = 0; i < count; i++) texts[reader.ReadString()] = reader.ReadString();
        var program = new UnityAnimatorProgram(sources, texts, defaults);
        // The first format held one controller and said nothing of its layer; only a body controller's clips moved
        // humanoid muscles then.
        if (magic == Magic1 && program.Clips.Values.Any(c => c.Floats.Any(f => f.ClassId == 95 && program.Controller.FindParameter(f.Attribute) == null)))
        {
            sources[0].Playable = 0;
            program = new UnityAnimatorProgram(sources, texts, defaults);
        }
        return program;
    }
}

// An avatar mask: which humanoid body parts a layer may move, and which objects' transforms. No transform
// list means every transform.
public sealed class UnityAvatarMask
{
    public readonly bool[] Body = Enumerable.Repeat(true, 13).ToArray();
    public Dictionary<string, bool>? Transforms;

    public static UnityAvatarMask? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        var doc = UnityYaml.Parse(text).FirstOrDefault(d => d.ClassId == 319);
        if (doc == null)
            return null;
        var mask = new UnityAvatarMask();
        var hex = doc["m_Mask"]?.Text ?? "";
        for (int i = 0; i < 13 && i * 8 + 8 <= hex.Length; i++)
            mask.Body[i] = uint.TryParse(hex.AsSpan(i * 8, 2), System.Globalization.NumberStyles.HexNumber, null, out var low) && low != 0
                || hex.AsSpan(i * 8 + 2, 6).ToString() != "000000";
        if (doc["m_Elements"] is UnitySeq elements && elements.Items.Count > 0)
        {
            mask.Transforms = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var e in elements.Items)
                mask.Transforms[e["m_Path"]?.Text ?? ""] = (e["m_Weight"]?.Float() ?? 0f) > 0.5f;
        }
        return mask;
    }

    public bool AllowsTransform(string path) => Transforms == null || Transforms.TryGetValue(path, out var on) && on;
}
