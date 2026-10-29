// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Components.Scripting;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// What a running script's drives function worked out, shown on this machine through the drive links the host
// put on the script's slot. A value goes into its field only when it differs from the one shown there last,
// or the link is new or newly granted. A link not here yet is looked for again twice a second. On the host
// it also says, for every other machine, which links it granted. -xlinka
internal sealed class ScriptDriveApplier
{
    private const double LookSeconds = 0.5;

    private readonly ScriptHostRuntime _runtime;
    private readonly IScriptDrive?[] _links;
    private readonly ulong[][] _shown;
    private readonly object?[] _shownObjects;
    private readonly bool[] _stale;
    // On the host: whether each link is aimed at its field, which a drive switched off is not.
    private readonly bool[] _aimed;
    private double _nextLook = double.NegativeInfinity;

    public ScriptDriveApplier(ScriptHostRuntime runtime)
    {
        _runtime = runtime;
        var drives = runtime.Runner.Compiled.Drives;
        _links = new IScriptDrive?[drives.Count];
        _shown = new ulong[drives.Count][];
        _shownObjects = new object?[drives.Count];
        _stale = new bool[drives.Count];
        _aimed = new bool[drives.Count];
        for (int i = 0; i < drives.Count; i++)
        {
            _aimed[i] = true;
            _shown[i] = new ulong[drives[i].ValueType.LaneCount];
            _stale[i] = true;
        }
    }

    public void Apply(double now)
    {
        var instance = _runtime.Runner.Instance;
        var drives = instance.Compiled.Drives;
        bool authority = _runtime.Host.World?.IsAuthority == true;
        bool look = now >= _nextLook;
        if (look)
            _nextLook = now + LookSeconds;
        Span<ulong> lanes = stackalloc ulong[16];
        for (int i = 0; i < drives.Count; i++)
        {
            var link = _links[i];
            if (link == null || link.IsGone || !ReferenceEquals(link.Owner, _runtime.Host))
            {
                link = look ? Find(drives[i].NodeId) : null;
                _links[i] = link;
                _stale[i] = true;
                if (link == null)
                    continue;
            }
            bool on = instance.DriveOn(i);
            if (authority && on != _aimed[i])
            {
                Aim(link, on ? Target(drives[i]) : null);
                _aimed[i] = on;
                _stale[i] = true;
            }
            if (authority)
                link.Confirm();
            if (!on)
                continue;

            var type = drives[i].ValueType;
            if (type.LaneCount > lanes.Length)
                continue;
            var current = lanes[..type.LaneCount];
            instance.ReadDriveLanes(i, current);
            if (type.IsHandle)
            {
                var value = instance.DecodeDrive(i, current);
                if (!_stale[i] && Equals(value, _shownObjects[i]))
                    continue;
                if (link.Show(value))
                {
                    _shownObjects[i] = value;
                    _stale[i] = false;
                }
                continue;
            }
            if (!_stale[i] && current.SequenceEqual(_shown[i]))
                continue;
            if (link.Show(instance.DecodeDrive(i, current)))
            {
                current.CopyTo(_shown[i]);
                _stale[i] = false;
            }
        }
    }

    private IWorldElement? Target(ScriptDriveEntry entry)
        => entry.Binding >= 0 && entry.Binding < _runtime.Bindings.Count ? _runtime.Bindings[entry.Binding] as IWorldElement : null;

    // Aimed as the author, the way the link was made; let go of by the engine.
    private void Aim(IScriptDrive link, IWorldElement? target)
    {
        var permissions = _runtime.Host.World?.DataModelPermissions;
        try
        {
            using (target == null ? permissions?.EnterSystemBypass() : permissions?.EnterActor(_runtime.Runner.Context.Actor))
                link.Aim(target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            _runtime.Runner.Context.Warn(ScriptWarning.Fault, $"drive {link.NodeId} could not be linked: {ex.Message}");
        }
    }

    private IScriptDrive? Find(string nodeId)
    {
        foreach (var drive in ScriptDriveLinks.Owned(_runtime.Host))
        {
            if (drive.NodeId == nodeId)
                return drive;
        }
        return null;
    }
}
