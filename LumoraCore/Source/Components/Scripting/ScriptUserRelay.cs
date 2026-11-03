// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Scripting;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components.Scripting;

// What a script on the host asks of this person, carried to their own machine. A Shared script runs on the
// host, and this person's body, locomotion and hands are their machine's to move, so the host writes what it
// wants here and their machine does it to them, as them.
//
// Each machine mints its person's relay in its own byte under their user root, as the undo relay is. Only the
// host writes the asks. The owner takes only asks the host itself minted, newer than the last it took, at most
// ScriptUserAsks.MaxAsksPerSecond a second beyond movement, and writes back the newest it took. -xlinka
[ComponentCategory("Hidden")]
public sealed class ScriptUserRelay : Component
{
    private const string RelaySlotName = "ScriptUserRelay";

    [NonPersistent]
    public readonly SyncList<ScriptUserAsk> Asks = new();

    // The newest ask the owner's machine took. Only the owner writes it.
    [NonPersistent]
    public readonly Sync<int> Taken = new();

    private int _taken;
    private double _windowStart = double.NegativeInfinity;
    private int _windowCount;
    private bool _loggedFlood;

    // On the owner's machine: asks done, and asks that did nothing or came past the rate.
    public int Applied { get; private set; }
    public int Refused { get; private set; }

    public override void OnAwake()
    {
        base.OnAwake();
        Asks.MarkHostOnly();
    }

    public override void OnStart()
    {
        base.OnStart();
        if (World?.IsAuthority == true)
            World.Scripts.UserAsks.Register(this);
    }

    public override void OnDestroy()
    {
        World?.ScriptsIfAny?.UserAsks.Unregister(this);
        base.OnDestroy();
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        if (IsOwnersMachine && Asks.Count > 0)
            Take();
    }

    private bool IsOwnersMachine
        => World is { IsAuthority: false, LocalUser: { } local } && local.AllocationID.Value == ReferenceID.GetUserByte();

    private void Take()
    {
        var user = World.LocalUser;
        if (user == null || user.IsDestroyed)
            return;
        int newest = _taken;
        for (int i = 0; i < Asks.Count; i++)
        {
            var ask = Asks[i];
            int id = ask.Id.Value;
            if (id <= _taken || !ask.ReferenceID.IsAuthorityID)
                continue;
            if (id > newest)
                newest = id;
            if (!TakeSlot(ask.Kind.Value) || !ScriptUserActions.Apply(World, user, ask))
            {
                Refused++;
                continue;
            }
            Applied++;
        }
        if (newest == _taken)
            return;
        _taken = newest;
        using (World.DataModelPermissions?.EnterActor(user))
            Taken.Value = newest;
    }

    private bool TakeSlot(ScriptUserAskKind kind)
    {
        if (kind is ScriptUserAskKind.Push or ScriptUserAskKind.SetVelocity or ScriptUserAskKind.Scale
            or ScriptUserAskKind.PlaceHips or ScriptUserAskKind.TurnHips)
            return true;
        double now = World.Time.TotalTime;
        if (now - _windowStart >= 1.0)
        {
            _windowStart = now;
            _windowCount = 0;
        }
        if (++_windowCount <= ScriptUserAsks.MaxAsksPerSecond)
            return true;
        if (!_loggedFlood)
        {
            _loggedFlood = true;
            LumoraLogger.Warn($"Script user relay: the host asked more than {ScriptUserAsks.MaxAsksPerSecond} things of this person in a second; the rest are dropped.");
        }
        return false;
    }

    // The person at this machine's relay in this world, made on first use.
    internal static ScriptUserRelay? For(World world)
    {
        var local = world.LocalUser;
        var root = local?.Root?.Slot;
        if (local == null || root == null || root.IsDestroyed)
            return null;
        var existing = root.FindChild(RelaySlotName, false)?.GetComponent<ScriptUserRelay>();
        if (existing != null && !existing.IsDestroyed && existing.ReferenceID.GetUserByte() == local.AllocationID.Value)
            return existing;
        if (!world.IsLocalAllocationReady)
            return null;
        var slot = world.AddLocalUserSlot(root, RelaySlotName);
        slot.Persistent.Value = false;
        using (world.EnterLocalUserAllocation())
            return slot.AttachComponent<ScriptUserRelay>();
    }
}
