// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// The host's command ring writer, one per system. Every GPU machine applies each command once, by sequence; a joiner
// starts at the current sequence and replays nothing, because a command's effect is already in the cells by then.
// The ring is session-only and sized on first use: 128 empty entries appended from nothing are one append however
// many commands go into the tail that frame. At most 16 commands go out a frame so a burst never laps the ring
// before readers see it; the rest wait in order. -xlinka
public sealed class FluidCommandWriter
{
    public const int Entries = 64;
    public const int PerFrame = 16;

    private readonly Queue<FluidCommand> _queue = new();

    public FluidCommandWriter(FluidSystem system)
    {
        System = system;
    }

    public FluidSystem System { get; }

    public int Written { get; private set; }

    public int Waiting => _queue.Count;

    public void Add(in FluidCommand command) => _queue.Enqueue(command);

    // Host, on the world thread. Returns how many went out.
    public int Flush()
    {
        if (_queue.Count == 0 || System.IsDestroyed || System.World is not { IsAuthority: true } world)
            return 0;
        int sent = 0;
        using (world.DataModelPermissions?.EnterSystemBypass())
        {
            if (!FluidRings.EnsureLength(System.Commands, Entries * 2))
                return 0;
            while (_queue.Count > 0 && sent < PerFrame)
            {
                _queue.Dequeue().Write(out int4 a, out int4 b);
                FluidRings.Write(System.Commands, System.CommandSequence, a, b);
                sent++;
            }
        }
        Written += sent;
        return sent;
    }

    [System.ThreadStatic]
    private static List<(int4 A, int4 B)>? _raw;

    // A reader's side, on any machine: the commands since its cursor, oldest first. Returns the entries a gap wider
    // than the ring skipped.
    public static int Read(FluidSystem system, ref int cursor, List<FluidCommand> output)
    {
        var raw = _raw ??= new List<(int4 A, int4 B)>();
        raw.Clear();
        int skipped = FluidRings.Read(system.Commands, system.CommandSequence.Value, ref cursor, raw);
        foreach (var (a, b) in raw)
            output.Add(FluidCommand.Read(a, b));
        return skipped;
    }
}
