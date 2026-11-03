// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Lumora.Core.Components.Scripting;
using Lumora.Core.Math;
using Lumora.Script;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Scripting;

// What a world node sees as its first parameter, and the runtime's hand on every node call: each one
// runs inside its principal's scope, so a node body cannot forget to, and the gate judges every write
// as the script's author. -xlinka
public sealed class ScriptContext : ScriptContextBase
{
    public World World { get; }
    public ScriptPrincipal Principal { get; }

    // The slot the script is on, for the node that asks where it is. Null for a script run without a host.
    public Slot? Self { get; }

    internal List<User> UserScratch { get; } = new();

    internal List<Slot> HitScratch { get; } = new();

    internal List<User> PeopleScratch { get; } = new();

    // The scopes this script's variable paths reached, kept by ScriptScopedVariables.
    internal object? ScopeLookups;

    // What its searches under slots found, until the tree changes.
    internal ScriptSearchCache Searches => _searches ??= new ScriptSearchCache();
    private ScriptSearchCache? _searches;

    // Rays one script casts in a frame, slots it makes in a second and slots in one copy, past which it is
    // warned. What its author's scripts do between them is counted too: the world's ScriptAuthorLedger.
    public const int WarnRaysPerFrame = 512;
    public const int WarnMadePerSecond = 256;
    public const int WarnCopySlots = 1024;

    // What this script made, for a Local script to take with it when it stops. Swept of what has gone each
    // time it doubles.
    private readonly List<(Slot Slot, int Count)> _made = new();
    private int _sweepMadeAt = 64;
    private double _madeWindow = double.NegativeInfinity;
    private int _madeInWindow;
    private ulong _castFrame = ulong.MaxValue;
    private int _castsInFrame;

    // Local: running on this machine only, for whoever is looking. What that lets it change is in
    // ScriptWrites.
    public bool IsLocal { get; }

    // A world's Local script: what it changes in the world it changes as the user at this machine, judged as
    // their own edit. A person's Local script never does, or a guest's script would write as the host on the
    // host's machine.
    public bool WritesAsMachineUser => IsLocal && Principal.IsWorldContent;

    // The user at this machine, while there is one.
    internal User? MachineUser => World.LocalUser is { IsDestroyed: false } user ? user : null;

    // An Author script on its author's own machine: its writes are the author's own, its input theirs. A world
    // script's author's machine is the host.
    public bool OnAuthorMachine { get; }

    // Running on a machine of its own rather than the host: what only the host keeps is out of its reach.
    public bool OffHost => IsLocal || (OnAuthorMachine && !World.IsAuthority);

    // The host running this script, for the messages it sends. Null for a script run without one.
    internal ScriptHost? Host { get; set; }

    public const int WarnMessagesPerSecond = 60;
    private double _messageWindow = double.NegativeInfinity;
    private int _messagesInWindow;

    // Who set off the call running now: the user its event names, a presser, a grabber, a message's sender.
    // Null when its event names nobody, and for what runs after a Delay. Set as each event is raised.
    public User? Cause { get; internal set; }


    private ScriptUndoRecorder? _undo;

    // What this script's undo nodes record.
    internal ScriptUndoRecorder Undo => _undo ??= new ScriptUndoRecorder(this);

    // The user a payload names, if it names one: the first User property, read through a getter made once per
    // payload type, since every update and press asks.
    internal static User? CauseOf<T>(in T payload) where T : struct => Causes<T>.Read?.Invoke(payload);

    // The getter made ahead, on a compile's worker, rather than as the first event of that payload is raised.
    internal static void WarmCause(Type payload)
    {
        if (payload.IsValueType)
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Causes<>).MakeGenericType(payload).TypeHandle);
    }

    private static class Causes<T> where T : struct
    {
        public static readonly Func<T, User?>? Read = Make();

        private static Func<T, User?>? Make()
        {
            var property = Array.Find(typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance),
                static property => property.PropertyType == typeof(User) && property.GetIndexParameters().Length == 0);
            if (property?.GetMethod == null)
                return null;
            try
            {
                var payload = System.Linq.Expressions.Expression.Parameter(typeof(T), "payload");
                return System.Linq.Expressions.Expression.Lambda<Func<T, User?>>(
                    System.Linq.Expressions.Expression.Property(payload, property), payload).Compile();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or PlatformNotSupportedException)
            {
                return payload => property.GetValue(payload) as User;
            }
        }
    }

    // Around every call the scheduler makes. A batch the call left open closes as it ends.
    internal void BeginCall()
    {
        Cause = null;
        _undo?.CloseAll();
    }

    internal void EndCall()
    {
        _undo?.CloseAll();
        Cause = null;
    }

    // The slot the script runs on, or one above it, that a Destroy asked for in the call running now. Destroyed
    // inside the call, the host would be marked gone and taken apart under the flow still running, which goes on
    // to use the host's synced values, inbox and drives, and the slot. It goes as the call returns instead,
    // judged again by the same checks as the same writer; from then on nothing more of the script runs, and the
    // host's teardown lets go of whatever the call left. -xlinka
    private Slot? _ownDestroy;
    private bool _ownDestroyKeepsAssets;

    internal void DestroyOwn(Slot slot, bool keepAssets)
    {
        if (Host == null)
            throw new UnauthorizedAccessException("a script cannot destroy the slot it runs on");
        if (_ownDestroy is { IsDestroyed: false } asked && (ReferenceEquals(asked, slot) || slot.IsDescendantOf(asked)))
            return;
        _ownDestroy = slot;
        _ownDestroyKeepsAssets = keepAssets;
    }

    // As the scheduler's call returns.
    internal void RunOwnDestroy()
    {
        var slot = _ownDestroy;
        if (slot == null)
            return;
        _ownDestroy = null;
        if (slot.IsDestroyed)
            return;
        bool entered = EnterWholeCall(out var mark);
        try
        {
            if (_ownDestroyKeepsAssets)
            {
                ScriptSlotWrites.DestroyOwnKeepingAssets(this, slot);
            }
            else
            {
                ScriptWrites.CheckDestroy(this, slot, own: true);
                slot.Destroy();
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            Warn(ScriptWarning.Fault, $"its own slot was not destroyed: {ex.Message}");
        }
        finally
        {
            LeaveWholeCall(entered, mark);
        }
    }

    public ScriptContext(World world, ScriptPrincipal principal, Slot? self = null, bool local = false, bool authorMachine = false)
    {
        World = world;
        Principal = principal;
        Self = self;
        IsLocal = local;
        OnAuthorMachine = authorMachine && !local;
        Actor = principal.IsWorldContent && !local ? ScriptPrincipal.WorldContentAt(self) : principal;
    }

    // Who the gate takes this script for: its principal, and for a world script on the host the same with the
    // slot it sits on, so its own worn or held object stays its own.
    internal ScriptPrincipal Actor { get; }

    public override double Now => World.Time.TotalTime;

    protected override void WriteLog(string text)
        => LumoraLogger.Log($"Loom [{Principal}] {Self?.Name.Value}: {text}");

    protected override void WriteWarning(string text)
        => LumoraLogger.Warn($"Loom [{Principal}] {Self?.Name.Value}: {text}");

    // The same scope EnterWriter opens, without a scope object per node call: the saved actor goes on this
    // context's own stack and one leaver puts it back. Node calls nest strictly, each in its invoker's using.
    // The scheduler opens the writer once around a whole call, and a node call inside it already acts as the
    // writer with nothing lifting the gate, so it needs no scope of its own.
    protected override IDisposable? EnterCall(NodeOverload overload)
    {
        if (World.DataModelPermissions == null)
            return null;
        var writer = Writer;
        if (Lumora.Warden.PermissionEngine.IsActingAs(writer))
            return null;
        _callMarks.Add(Lumora.Warden.PermissionEngine.SetActor(writer));
        return _leaveCall ??= new LeaveCall(_callMarks);
    }

    // Who the gate takes this script's changes for, as EnterWriter says.
    private Lumora.Warden.IPermissionActor? Writer => !WritesAsMachineUser ? Actor : MachineUser;

    // The writer for a whole call the scheduler makes, put back with LeaveWholeCall. False when there is no
    // gate to tell.
    internal bool EnterWholeCall(out Lumora.Warden.PermissionEngine.ActorMark mark)
    {
        mark = default;
        if (World.DataModelPermissions == null)
            return false;
        mark = Lumora.Warden.PermissionEngine.SetActor(Writer);
        return true;
    }

    internal static void LeaveWholeCall(bool entered, in Lumora.Warden.PermissionEngine.ActorMark mark)
    {
        if (entered)
            Lumora.Warden.PermissionEngine.Restore(mark);
    }

    private readonly List<Lumora.Warden.PermissionEngine.ActorMark> _callMarks = new();
    private LeaveCall? _leaveCall;

    private sealed class LeaveCall : IDisposable
    {
        private readonly List<Lumora.Warden.PermissionEngine.ActorMark> _marks;

        public LeaveCall(List<Lumora.Warden.PermissionEngine.ActorMark> marks) => _marks = marks;

        public void Dispose()
        {
            int last = _marks.Count - 1;
            if (last < 0)
                return;
            Lumora.Warden.PermissionEngine.Restore(_marks[last]);
            _marks.RemoveAt(last);
        }
    }

    // Who the gate takes this script's changes for: the user at this machine for a world's Local script, with
    // nobody there for one that has no user here, and its author for every other.
    internal IDisposable? EnterWriter()
        => !WritesAsMachineUser ? World.DataModelPermissions?.EnterActor(Actor)
            : World.DataModelPermissions?.EnterActor(MachineUser);

    protected override void CallScriptFunction(CallTicket ticket, object? target, string function, FunctionSignature signature,
        object?[] arguments)
        => ScriptFunctionCalls.Start(this, ticket, target, function, signature, arguments);

    // A destroyed slot, component, field or user reads as nothing to every node.
    protected override bool IsGone(object value)
        => value is IWorldElement { IsDestroyed: true } || value is User { IsDestroyed: true }
            || (value is Networking.Sync.SyncElement element && OwnerGone(element));

    protected override string GoneWords(string port, object value)
        => value is User ? $"the {port} has left" : $"the {port} is gone";

    // A destroyed component's fields are only taken apart at the end of its frame, and read as there until
    // then, so whatever holds them is asked as well.
    private static bool OwnerGone(Networking.Sync.SyncElement element)
    {
        var owner = element.Parent;
        for (int depth = 0; owner is Networking.Sync.SyncElement member && depth < 8; depth++)
            owner = member.Parent;
        return owner is { IsDestroyed: true };
    }

    // What a Tree read hangs on: slots and components coming, going, moving or reordering, names and tags.
    protected override long TreeStamp => ((long)Slot.StructureEpoch << 32) | (uint)Slot.LabelEpoch;

    // What a Field read hangs on: how many times its field has changed, and whether it is still there.
    protected override ulong? FieldStamp(object field)
        => (field as Networking.Sync.SyncElement ?? (field as IFieldView)?.Member) is { } element
            ? (element.IsDestroyed || field is IField { IsDestroyed: true } ? ulong.MaxValue : element.Version)
            : null;

    // Whether this script may use what nodes of that capability do, asked outside a node call.
    internal bool MayUse(Capability capability) => AllowsCapability(capability);

    // Whom a script may move, scale or seat at all: anyone for the world's, only its own author for a person's. A
    // guest's Shared script runs on the host's machine, and the host's body would otherwise be its to push. -xlinka
    internal bool MayActOn(User? user) => user is { IsDestroyed: false } && (Principal.IsWorldContent || ReferenceEquals(user, Principal.Author));

    // The user at this machine, when this script may act on them: the one a script moves itself. Anyone else a
    // script on the host moves through their own machine, ScriptUserAsks.
    internal bool IsMachineUser(User? user) => MayActOn(user) && ReferenceEquals(user, World.LocalUser);

    // Whether the call running now is for the person at this machine: one nobody set off, or one they did. An event
    // every machine hears runs every machine's copy of a Local script, and only the copy where its cause is acts
    // for them.
    internal bool CausedHere => Cause == null || ReferenceEquals(Cause, MachineUser);

    // A script never holds rights over anyone's body, its author's included; what it does to the user here it
    // does as them, the way their own movement does it.
    internal IDisposable? AsSelf(User user) => World.DataModelPermissions?.EnterActor(user);

    // The groups whose rules exist. Input is the viewer's own, so only a Local script reads it, or an Author
    // script on its author's machine. Affecting users is any script's, and its nodes act on a person only on that
    // person's own machine, as their own movement would, inside their role and the world's locomotion rules: a
    // script on the host asks theirs. UI links for nobody until its quotas and world settings do.
    protected override bool AllowsCapability(Capability capability) => capability switch
    {
        Capability.Core or Capability.WorldRead or Capability.WorldWrite or Capability.WorldStructure
            or Capability.UsersRead or Capability.Physics or Capability.Audio or Capability.Events => true,
        Capability.Input => IsLocal || OnAuthorMachine,
        Capability.UsersAffect => true,
        // Links for every script; each request still waits on the sending machine's user for its host.
        Capability.Web => true,
        _ => false,
    };

    internal void CountCast()
    {
        ulong frame = World.Time.UpdateIndex;
        if (frame != _castFrame)
        {
            _castFrame = frame;
            _castsInFrame = 0;
        }
        if (++_castsInFrame == WarnRaysPerFrame + 1)
            Warn(ScriptWarning.Rays, $"more than {WarnRaysPerFrame} rays in one frame");
        if (World.Scripts.Ledger.CountCast(Principal))
            Warn(ScriptWarning.Rays, $"its author's scripts cast more than {ScriptAuthorLedger.WarnRaysPerFrame:N0} rays in one frame");
    }

    // Told before count more slots are made.
    internal void ReserveMade(int count)
    {
        double now = Now;
        if (now - _madeWindow >= 1.0)
        {
            _madeWindow = now;
            _madeInWindow = 0;
        }
        if (_madeInWindow <= WarnMadePerSecond && _madeInWindow + count > WarnMadePerSecond)
            Warn(ScriptWarning.SlotsMade, $"more than {WarnMadePerSecond} slots made in a second");
        if (count > WarnCopySlots)
            Warn(ScriptWarning.Copy, $"one copy of {count:N0} slots");
        if (World.Scripts.Ledger.ReserveMade(Principal, count))
            Warn(ScriptWarning.SlotsMade, $"its author's scripts made more than {ScriptAuthorLedger.WarnMadePerMinute} slots in a minute");
    }

    internal void CountMessage()
    {
        double now = Now;
        if (now - _messageWindow >= 1.0)
        {
            _messageWindow = now;
            _messagesInWindow = 0;
        }
        if (++_messagesInWindow == WarnMessagesPerSecond + 1)
            Warn(ScriptWarning.Messages, $"more than {WarnMessagesPerSecond} messages in a second");
    }

    // A Local script's own things go with it: what it made on this machine is nobody else's to keep.
    internal void DropLocalMade()
    {
        foreach (var (slot, _) in _made)
        {
            if (slot.IsLocalElement && !slot.IsDestroyed)
                slot.Destroy();
        }
        _made.Clear();
    }

    internal void Made(Slot slot, int count)
    {
        _madeInWindow += count;
        if (_made.Count >= _sweepMadeAt)
        {
            _made.RemoveAll(static made => made.Slot.IsDestroyed);
            _sweepMadeAt = System.Math.Max(64, _made.Count * 2);
        }
        _made.Add((slot, count));
        World.Scripts.Ledger.Made(Principal, slot, count);
    }

    // What a For Each walks: the session's users, a slot's children, or what a search found, under the
    // same reads the user and slot nodes make. A slot the script may not see has no children for it.
    protected override int EachCount(EachSource source, object? of) => source switch
    {
        EachSource.Users when AllowsCapability(Capability.UsersRead) => ScriptReads.Users(this).Count,
        EachSource.Children when AllowsCapability(Capability.WorldRead) && of is Slot slot && ScriptReads.Visible(this, slot) => slot.ChildCount,
        EachSource.OverlapSphere or EachSource.OverlapBox when of is Found found => found.Slots.Count,
        _ => 0,
    };

    // A search runs once, as the loop starts, and the loop walks this. It lives as a handle for the call.
    private sealed class Found
    {
        public readonly List<Slot> Slots = new();
    }

    protected override object? EachQuery(EachSource source, ReadOnlySpan<float> query)
    {
        if (!AllowsCapability(Capability.Physics))
            return null;
        var found = new Found();
        switch (source)
        {
            case EachSource.OverlapSphere:
                ScriptPhysics.SearchSphere(this, new float3(query[0], query[1], query[2]), query[3], found.Slots);
                break;
            case EachSource.OverlapBox:
                ScriptPhysics.SearchBox(this, new float3(query[0], query[1], query[2]), new float3(query[3], query[4], query[5]),
                    new floatQ(query[6], query[7], query[8], query[9]), found.Slots);
                break;
            default:
                return null;
        }
        return found;
    }

    protected override object? EachItem(EachSource source, object? of, int index)
    {
        if (index < 0)
            return null;
        switch (source)
        {
            case EachSource.Users when AllowsCapability(Capability.UsersRead):
                var users = ScriptReads.Users(this);
                return index < users.Count ? users[index] : null;
            case EachSource.Children when AllowsCapability(Capability.WorldRead) && of is Slot slot && ScriptReads.Visible(this, slot):
                var children = slot.Children;
                var child = index < children.Count ? children[index] : null;
                return ScriptReads.Visible(this, child) ? child : null;
            case EachSource.OverlapSphere or EachSource.OverlapBox when of is Found found:
                var hit = index < found.Slots.Count ? found.Slots[index] : null;
                return ScriptReads.Visible(this, hit) ? hit : null;
            default:
                return null;
        }
    }
}
