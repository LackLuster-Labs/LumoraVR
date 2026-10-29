// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;

namespace Lumora.Core.Scripting;

// What a script's searches under a slot found, by name or by tag, kept until the tree they walked could
// answer differently: a slot or component coming, going or moving anywhere, children changing order, or any
// name or tag changing. Those are what the answer and the script's view of the tree hang on, so a hit is the
// walk's own answer. A script asking every frame for the same child walked the whole subtree every frame.
// -xlinka
internal sealed class ScriptSearchCache
{
    // Past this many different searches between changes the oldest answers go; a script that asks that many
    // differs every time and gains nothing from keeping them.
    private const int Most = 256;

    private readonly Dictionary<Key, Slot?> _found = new();
    private int _structure = -1;
    private int _labels = -1;

    internal readonly record struct Key(Slot From, string Text, byte Kind, int Depth);

    public const byte ByName = 0;
    public const byte ByNamePartial = 1;
    public const byte ByNameAnyCase = 2;
    public const byte ByTag = 4;

    // Searches answered from what was kept, and walked, by every script on this machine: for the diagnostics.
    public static long Hits;
    public static long Walks;

    public bool TryGet(in Key key, out Slot? found)
    {
        int structure = Slot.StructureEpoch;
        int labels = Slot.LabelEpoch;
        if (structure != _structure || labels != _labels)
        {
            _found.Clear();
            _structure = structure;
            _labels = labels;
            found = null;
            Walks++;
            return false;
        }
        if (_found.TryGetValue(key, out found))
        {
            Hits++;
            return true;
        }
        Walks++;
        return false;
    }

    // Kept only when nothing moved since TryGet looked.
    public void Keep(in Key key, Slot? found)
    {
        if (Slot.StructureEpoch != _structure || Slot.LabelEpoch != _labels)
            return;
        if (_found.Count >= Most)
            _found.Clear();
        _found[key] = found;
    }
}
