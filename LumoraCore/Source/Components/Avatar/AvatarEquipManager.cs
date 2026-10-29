// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Threading;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Input;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Logging;
using Lumora.Core.Math;
using Lumora.Core.Security;

namespace Lumora.Core.Components.Avatar;

// Per-user avatar coordinator. Dispatches IAvatarEquippables from a target avatar
// slot tree onto the user's AvatarSockets by matching BodyNode. Allows
// a custom avatar's hands + default headset + default view to coexist on
// the same user since each body node is filled independently. - xlinka
[ComponentCategory("Users/Avatar")]
public class AvatarEquipManager : UserRootComponent
{
    public readonly SyncRef<Slot> CurrentAvatar = new();
    public readonly SyncRef<UserRoot> UserRoot = new();
    public readonly SyncRef<Slot> DefaultAvatarSlot = new();
    public readonly Sync<bool> IsUsingDefaultAvatar = new();

    // Plug-in supplier for default head/hands/view pieces when a body-node
    // slot has nothing equipped. AvatarAssembler installs itself here on
    // user spawn. Set Target to null to disable auto-filling.
    [OldName("EmptySlotHandler")]
    public readonly SyncRef<IAvatarSocketFiller> SocketFiller = new();

    private CancellationTokenSource _fillCts = null!;
    private bool _isFillingEmptySlots;

    // Display name/color values used by name badges/tags. Per-user state that
    // NameBadgeDriver pushes into avatar text components.
    [OldName("NameTagText")]
    public readonly Sync<string> BadgeText = new();
    [OldName("NameTagColor")]
    public readonly Sync<color> BadgeColor = new();
    [OldName("NameTagOutline")]
    public readonly Sync<color> BadgeOutline = new();
    [OldName("NameTagBackground")]
    public readonly Sync<color> BadgeBackground = new();

    // The trim ring drawn around the plate. This is the supporter-reward surface: one authored colour
    // that every peer draws, so whatever grants a tier writes it HERE and the plate follows. Additive
    // and name-keyed like every other member, so a save written before it existed loads the quiet
    // neutral below rather than a colour nobody picked. -xlinka
    public readonly Sync<color> BadgeRim = new();

    // Whether this user gets the built-in nameplate. NameplateManager reads it live and hides itself
    // when it goes false or when an equipped avatar brings its own NameBadgeDriver.
    public readonly Sync<bool> AutoAddNameBadge = new();

    // Why the authority took something of this user's out of the world, for this user's own machine to say:
    // "serial|kind|worn|owner|name". Only the authority writes it.
    public readonly Sync<string> ProtectionNotice = new();
    private static int s_noticeSerial;

    // Handle on the REPLICATED name badge the old inline builder made. Kept only so a session or save
    // carrying one can be cleaned up: the plate is composed locally per peer now and nothing new is ever
    // written here. -xlinka
    private readonly SyncRef<Slot> _autoNameBadge = new();

    public readonly SyncRefList<Slot> AvailableAvatars;

    public event Action<Slot> OnAvatarChanged = null!;

    public bool IsEquippingManually { get; private set; }

    public Slot? EquippedAvatar
    {
        get
        {
            var userRoot = UserRoot.Target ?? Slot?.ActiveUserRoot;
            var rootSocket = userRoot?.GetRegisteredComponent<AvatarSocket>(s => s.Node.Value == BodyNode.Root && s.HasEquipped);
            return (rootSocket?.Equipped.Target as AvatarForm)?.Slot;
        }
    }

    // The rigs the head is looked up through, found once for the worn avatar and found again when the structure
    // of anything changes. The nameplate asks every frame, and each ask walked the whole avatar.
    private Slot? _rigsOf;
    private int _rigsSearchedAt = -1;
    private QuadrupedRig? _quadRig;
    private HumanoidRig? _humanRig;
    private bool _humanFound;

    public Slot? AvatarHead
    {
        get
        {
            var avatar = EquippedAvatar;
            if (avatar == null || avatar.IsDestroyed || !avatar.IsActive)
                return null;
            RigsOf(avatar);
            return _quadRig?.TryGetBone(QuadNode.Head) ?? HumanoidRigOf(avatar)?.TryGetBone(BodyNode.Head);
        }
    }

    // The first humanoid rig in the worn avatar, as a search of it would find it now.
    internal HumanoidRig? WornHumanoidRig
    {
        get
        {
            var avatar = EquippedAvatar;
            if (avatar == null || avatar.IsDestroyed)
                return null;
            RigsOf(avatar);
            return HumanoidRigOf(avatar);
        }
    }

    private void RigsOf(Slot avatar)
    {
        int structure = Slot.StructureEpoch;
        if (structure == _rigsSearchedAt && ReferenceEquals(avatar, _rigsOf))
            return;
        _rigsOf = avatar;
        _rigsSearchedAt = structure;
        _quadRig = avatar.GetComponentInChildren<QuadrupedRig>();
        _humanRig = null;
        _humanFound = false;
    }

    private HumanoidRig? HumanoidRigOf(Slot avatar)
    {
        if (!_humanFound)
        {
            _humanRig = avatar.GetComponentInChildren<HumanoidRig>();
            _humanFound = true;
        }
        return _humanRig;
    }

    public AvatarEquipManager()
    {
        AvailableAvatars = new SyncRefList<Slot>(this);
    }

    public override void OnAwake()
    {
        base.OnAwake();
        BadgeColor.Value = color.White;
        BadgeOutline.Value = color.Black;
        BadgeBackground.Value = new color(0f, 0f, 0f, 0.6f);
        BadgeRim.Value = UngrantedRim;
        AutoAddNameBadge.Value = true;
        BadgeText.OnChanged += _ => UpdateBadges();
        BadgeColor.OnChanged += _ => UpdateBadges();
        BadgeOutline.OnChanged += _ => UpdateBadges();
        ProtectionNotice.MarkHostOnly();
        ProtectionNotice.OnChanged += ShowProtectionNotice;
        Logger.Debug($"AvatarEquipManager: Awake on slot '{Slot.SlotName.Value}'");
    }

    internal void PostProtectionNotice(string kind, bool worn, string owner, string name)
    {
        int serial = Interlocked.Increment(ref s_noticeSerial);
        ProtectionNotice.Value = $"{serial}|{kind}|{(worn ? 1 : 0)}|{owner}|{name}";
    }

    private void ShowProtectionNotice(string value)
    {
        if (string.IsNullOrEmpty(value) || World == null || Slot?.ActiveUserRoot?.ActiveUser != World.LocalUser)
            return;
        var parts = value.Split('|', 5);
        if (parts.Length < 5)
            return;
        bool worn = parts[2] == "1";
        string owner = ItemSeals.NameOf(World, parts[3]);
        string name = parts[4];
        var title = worn ? ProtectionLocale.TakenOffTitle : ProtectionLocale.TakenOutTitle;
        var text = parts[1] switch
        {
            SealKeeper.KindBroken => worn ? ProtectionLocale.TakenOffBroken
                : ProtectionLocale.TakenOutBroken(name),
            SealKeeper.KindStripped => ProtectionLocale.TakenOutStripped(name, owner),
            _ => ProtectionLocale.TakenOffSealed(owner),
        };
        ItemSeals.Tell(World, title, text);
    }

    public void UpdateBadges()
    {
        if (Slot == null || World?.IsAuthority != true)
            return;

        foreach (var assigner in Slot.GetComponentsInChildren<NameBadgeDriver>())
        {
            assigner.UpdateBadge(this);
        }
    }

    // Compose the built-in nameplate for this user.
    //
    // Runs on EVERY peer, not just the authority: the plate and everything under it is local, so each
    // machine builds its own out of state that already replicated. There is nothing here for the
    // authority to hand anybody. -xlinka
    private void EnsureNameplate()
    {
        if (Slot == null || World == null)
            return;

        TrackDisplayName();
        DestroyLegacyBadge();

        var existing = Slot.FindChild(NameplateManager.RootSlotName, recursive: false);
        if (existing != null && !existing.IsDestroyed)
        {
            if (existing.GetComponent<NameplateManager>() != null)
                return;
            // A stray root with no manager on it: a half-built plate from a torn-down equip, or an
            // orphan of the same name out of an old tree. Take the name back.
            existing.Destroy();
        }

        var root = Slot.AddLocalSlot(NameplateManager.RootSlotName);
        root.Persistent.Value = false;
        root.AttachComponent<NameplateManager>();
    }

    // The old inline builder put a REPLICATED "Name Badge" slot under this one. Nothing writes that
    // shape any more, so anything still carrying it - a save from before the plate went local, or a
    // session where one peer is older - gets it taken away rather than ending up with two names over
    // one head. Authority only: destroying a replicated slot is a mutation, and on an observer it would
    // be refused by the gate anyway.
    private void DestroyLegacyBadge()
    {
        if (World?.IsAuthority != true)
            return;

        var tracked = _autoNameBadge.Target;
        if (tracked != null && !tracked.IsDestroyed)
            tracked.Destroy();
        if (_autoNameBadge.Target != null)
            _autoNameBadge.Target = null!;

        foreach (var child in Slot.Children)
        {
            if (child == null || child.IsDestroyed || child.IsLocalElement)
                continue;
            if (child.SlotName.Value != LegacyBadgeSlotName)
                continue;
            if (child.GetComponent<NameBadgeDriver>() == null)
                continue;
            child.Destroy();
            break;
        }
    }

    private const string LegacyBadgeSlotName = "Name Badge";

    private User _nameSource = null!;
    private Action<IChangeable> _nameChanged = null!;

    // BadgeText is the replicated display-name channel every badge and plate reads, seeded from the
    // account name at spawn. Keep it ON that name, or a rename mid-session never reaches anybody's plate
    // and the room keeps calling you what you were called an hour ago. Authority only: it is a synced
    // field and a guest's write would be refused anyway. -xlinka
    private void TrackDisplayName()
    {
        var user = UserRoot.Target?.ActiveUser ?? Slot?.ActiveUserRoot?.ActiveUser;
        if (ReferenceEquals(user, _nameSource))
        {
            PushDisplayName();
            return;
        }

        UntrackDisplayName();
        if (user == null || user.IsDestroyed)
            return;

        _nameSource = user;
        _nameChanged = _ => PushDisplayName();
        user.UserName.Changed += _nameChanged;
        user.AccountId.Changed += _nameChanged;
        user.NameColor.Changed += _nameChanged;
        user.IsSupporter.Changed += _nameChanged;
        user.SupporterColor.Changed += _nameChanged;
        user.IsStaff.Changed += _nameChanged;
        PushDisplayName();
    }

    private void PushDisplayName()
    {
        if (World?.IsAuthority != true || _nameSource == null || _nameSource.IsDestroyed)
            return;
        var name = _nameSource.UserName.Value ?? string.Empty;
        if (BadgeText.Value != name)
            BadgeText.Value = name;

        // The ink and the accent follow the account the host saw proved: its own name colour, the staff
        // colour for staff (it outranks any tier), the tier colour for a supporter, and the quiet guest ink
        // for nobody. -xlinka
        var user = _nameSource;
        color ink;
        if (user.IsGuest)
            ink = NameplateTheme.GuestInk;
        else
            ink = user.NameColor.Value.a > 0f ? user.NameColor.Value : color.White;
        if (!BadgeColor.Value.Equals(ink))
            BadgeColor.Value = ink;

        color rim = UngrantedRim;
        if (!user.IsGuest && user.IsStaff.Value)
            rim = NameplateTheme.StaffRim;
        else if (!user.IsGuest && user.IsSupporter.Value)
            rim = user.SupporterColor.Value.a > 0f ? user.SupporterColor.Value : NameplateTheme.SupporterAccent;
        if (!BadgeRim.Value.Equals(rim))
            BadgeRim.Value = rim;
    }

    private static readonly color UngrantedRim = new color(0.14f, 0.15f, 0.19f, 0.85f);

    private void UntrackDisplayName()
    {
        if (_nameSource != null && !_nameSource.IsDestroyed && _nameChanged != null)
        {
            _nameSource.UserName.Changed -= _nameChanged;
            _nameSource.AccountId.Changed -= _nameChanged;
            _nameSource.NameColor.Changed -= _nameChanged;
            _nameSource.IsSupporter.Changed -= _nameChanged;
            _nameSource.SupporterColor.Changed -= _nameChanged;
            _nameSource.IsStaff.Changed -= _nameChanged;
        }
        _nameSource = null!;
        _nameChanged = null!;
    }

    public override void OnDestroy()
    {
        UntrackDisplayName();
        _rigsOf = null;
        base.OnDestroy();
    }

    public override void OnInit()
    {
        base.OnInit();
        IsUsingDefaultAvatar.Value = true;
    }

    public override void OnStart()
    {
        base.OnStart();
        ResolveUserRoot();
        EnsureRootObjectSlot();

        // Fill default head/hands/view once everything's wired. If the handler
        // isn't attached yet (SimpleUserSpawn sets it after AvatarEquipManager
        // attach), the fill is a no-op and EquipDefaultAvatar can re-trigger
        // it later. - xlinka
        FillEmptySockets();
        EnsureNameplate();
        AvatarArrival.Ensure(this);
        Chat.ChatBubble.Ensure(this);
    }

    public bool Equip(Slot target, bool isManualEquip = false, bool forceDestroyOld = false, bool isFillingEmptySlot = false)
    {
        if (target == null || target.IsDestroyed)
            return false;

        ResolveUserRoot();
        var userRoot = UserRoot.Target ?? Slot.ActiveUserRoot;
        if (userRoot == null)
        {
            Logger.Warn("AvatarEquipManager: Cannot equip, no user root");
            return false;
        }

        // The same seal gate EquipAvatar asks, for the paths that come straight here: the owner a seal claims passes
        // while it is still being checked, everyone else is refused.
        if (ItemSeals.Judge(target, ItemSeals.AccountOf(userRoot.ActiveUser), SealRule.Equip, out var sealRefusal) == SealVerdict.Refused)
        {
            Logger.Log($"AvatarEquipManager: '{target.SlotName.Value}' not equipped: {sealRefusal}");
            return false;
        }

        IsEquippingManually = isManualEquip;
        EnsureRootObjectSlot();

        // On four legs the quadruped solver says which drivers go in, and a humanoid IK it could only switch off
        // must not get a say. -xlinka
        var equipObjects = new List<IAvatarEquippable>();
        var quadrupedIk = target.GetComponentInChildren<QuadrupedIK>();
        CollectEquippableObjects(target, equipObjects,
            quadrupedIk != null ? null : target.GetComponent<AvatarIK>() ?? target.GetComponentInChildren<AvatarIK>());
        if (equipObjects.Count == 0)
        {
            Logger.Warn($"AvatarEquipManager: No equippable IAvatarEquippable found on '{target.SlotName.Value}'");
            return false;
        }

        // Root is last: its callback must see the newly equipped head and limb proxies. Kept in tree order within
        // a priority, so two parts for one socket go first come, first served, every time.
        var treeOrder = new Dictionary<IAvatarEquippable, int>(equipObjects.Count);
        for (int i = 0; i < equipObjects.Count; i++)
            treeOrder[equipObjects[i]] = i;
        equipObjects.Sort((a, b) => a.EquipPriority != b.EquipPriority
            ? a.EquipPriority.CompareTo(b.EquipPriority)
            : treeOrder[a].CompareTo(treeOrder[b]));

        var objectSlots = new List<AvatarSocket>();
        var seenNodes = new HashSet<BodyNode>();
        foreach (var s in userRoot.GetRegisteredComponents<AvatarSocket>())
        {
            if (seenNodes.Add(s.Node.Value))
                objectSlots.Add(s);
        }

        var pairs = new List<(AvatarSocket objSlot, IAvatarEquippable obj)>();
        var dequipped = new HashSet<IAvatarEquippable>();

        foreach (var obj in equipObjects)
        {
            for (int i = 0; i < objectSlots.Count; i++)
            {
                var objSlot = objectSlots[i];
                if (!objSlot.PreEquip(obj, dequipped)) continue;
                pairs.Add((objSlot, obj));
                objectSlots.RemoveAt(i);
                break;
            }
        }

        if (pairs.Count == 0)
        {
            Logger.Warn($"AvatarEquipManager: No body-node slots matched on '{target.SlotName.Value}'");
            return false;
        }

        // A full-hand avatar piece dequips the controller slot (and similar):
        // collect every exclusive node declared by the incoming objects and
        // dequip those slots from the still-unmatched set.
        var exclusiveNodes = new HashSet<BodyNode>();
        foreach (var (_, obj) in pairs)
        {
            foreach (var node in obj.ConflictingNodes)
                exclusiveNodes.Add(node);
        }
        if (quadrupedIk != null)
        {
            exclusiveNodes.Add(BodyNode.LeftHand);
            exclusiveNodes.Add(BodyNode.RightHand);
            exclusiveNodes.Add(BodyNode.LeftFoot);
            exclusiveNodes.Add(BodyNode.RightFoot);
        }
        foreach (var node in exclusiveNodes)
        {
            for (int i = objectSlots.Count - 1; i >= 0; i--)
            {
                if (objectSlots[i].Node.Value == node)
                    objectSlots[i].Dequip(dequipped);
            }
        }

        // Dequipped trees must not linger under the manager - orphan them to
        // the world root, then optionally destroy. A whole avatar is left
        // standing where it was instead.
        foreach (var dq in dequipped)
        {
            if (dq is not Component dqc || dqc.Slot == null || dqc.Slot.IsDestroyed)
                continue;

            bool underManager = dqc.Slot.IsDescendantOf(Slot);
            if (dq is AvatarForm && !forceDestroyOld)
            {
                if (underManager)
                    LeaveStanding(dqc.Slot);
                continue;
            }

            // A part of an avatar stays in its avatar, worn or standing. Moved out to the root it was a lone proxy or
            // bone there for good, and the avatar went on one part short. -xlinka
            if (dq is not AvatarForm && dqc.Slot.GetComponentInParents<AvatarForm>() != null)
                continue;

            if (underManager)
                dqc.Slot.SetParent(World.RootSlot);

            if (forceDestroyOld)
                dqc.Slot.Destroy();
        }

        foreach (var (objSlot, obj) in pairs)
        {
            // Imported wrappers can hold the only grab. The actual body leaves that wrapper when worn,
            // so give its root a grab of its own; authored grab settings stay as they were. -xlinka
            if (obj is AvatarForm form && form.Slot.GetComponent<Grabbable>() == null)
            {
                var grab = form.Slot.AttachComponent<Grabbable>();
                grab.BlockWhenWorn.Value = true;
            }
            objSlot.Equip(obj);
        }

        // Reparent the target under us so the dequipped tree doesn't leak in the world.
        // AvatarForm.Equip already reparents itself; this handles non-root targets.
        if (target != Slot && !target.IsDescendantOf(Slot))
        {
            target.SetParent(Slot);
        }

        bool fullAvatar = pairs.Exists(p => p.obj is AvatarForm);
        if (fullAvatar)
        {
            CurrentAvatar.Target = target;
            IsUsingDefaultAvatar.Value = false;
            if (!AvailableAvatars.Contains(target))
                AvailableAvatars.Add(target);
        }

        if (!isFillingEmptySlot)
        {
            FillEmptySockets(objectSlots);
        }

        EnsureNameplate();

        Logger.Debug($"AvatarEquipManager: Equipped {pairs.Count} object(s) from '{target.SlotName.Value}'");
        if (fullAvatar)
            OnAvatarChanged?.Invoke(target);
        return true;
    }

    // Walk the user's AvatarSockets and ask SocketFiller to fill any
    // that have nothing equipped. Called automatically after every non-fill
    // Equip(); also callable directly when the handler swaps. - xlinka
    //
    // Only the authority fills: Equipped/SocketFiller replicate to every
    // peer, so without the gate each client would see "empty" slots and spawn
    // duplicate default pieces.
    public void FillEmptySockets(List<AvatarSocket> candidateSlots = null!)
    {
        if (World?.IsAuthority != true) return;
        if (_isFillingEmptySlots) return;
        if (SocketFiller.Target == null) return;

        var userRoot = UserRoot.Target ?? Slot?.ActiveUserRoot;
        if (userRoot == null) return;

        // Don't equip into an untracked user - pieces would spawn at authored
        // defaults and visibly snap once the first head pose arrives. Re-check
        // every few frames until tracking is live (desktop reports true
        // immediately).
        if (!userRoot.ReceivedFirstPositionalData)
        {
            Slot?.RunInUpdates(10, () =>
            {
                if (!IsDestroyed)
                    FillEmptySockets(candidateSlots);
            });
            return;
        }

        _fillCts?.Cancel();
        _fillCts?.Dispose();
        _fillCts = new CancellationTokenSource();
        var token = _fillCts.Token;

        candidateSlots ??= userRoot.GetRegisteredComponents<AvatarSocket>();
        _isFillingEmptySlots = true;
        try
        {
            foreach (var slot in candidateSlots)
            {
                if (slot == null || slot.IsDestroyed) continue;
                if (slot.HasEquipped) continue;
                // The worn avatar's own part goes here, from the wearer's machine (OfferSocket). A default as well
                // was the grey head left on a joiner's favourite. -xlinka
                if (FreePartFor(slot) != null) continue;
                if (EquippedAvatar?.GetComponentInChildren<QuadrupedIK>() != null
                    && (slot.Node.Value == BodyNode.LeftHand || slot.Node.Value == BodyNode.RightHand
                        || slot.Node.Value == BodyNode.LeftFoot || slot.Node.Value == BodyNode.RightFoot))
                    continue;
                var handler = SocketFiller.Target;
                if (handler == null) break;
                // The default pieces are the authority's own fill of a user's empty socket, made at the world
                // root and put on the user, and a Social or Event world's freeze refused that like an edit of
                // the world. Past the gate for the world's own filler into a body the authority built. -xlinka
                using (ReferenceID.IsAuthorityID && handler is Component { ReferenceID.IsAuthorityID: true } ? World.DataModelPermissions?.EnterSystemBypass() : null)
                    _ = handler.FillSocket(slot.Node.Value, this, token);
            }
        }
        finally
        {
            _isFillingEmptySlots = false;
        }
    }

    public bool CanEquip(IAvatarEquippable avatarObject)
    {
        if (avatarObject == null) return false;
        if (avatarObject.IsEquipped) return false;
        if (avatarObject is Component c)
        {
            if (c.Slot == null || c.Slot.IsDestroyed) return false;
            // Already under a user - can't equip something already worn. One left loose on this user's own root
            // to be put on, where props may not come in, is the exception.
            if (c.Slot.ActiveUserRoot != null && !IsLooseOnOwnRoot(c.Slot)) return false;
        }
        if (Slot.ActiveUserRoot == null) return false;
        return true;
    }

    // A part the avatar's IK reads goes to the driver it reads. Any other driver for that part would take the
    // socket first in tree order and leave the solver reading a proxy nothing moves. -xlinka
    private void CollectEquippableObjects(Slot slot, List<IAvatarEquippable> output, AvatarIK? ik)
    {
        if (slot == null || slot.IsDestroyed) return;

        foreach (var c in slot.Components)
        {
            if (c is IAvatarEquippable obj && CanEquip(obj) && !KeptOutOfSockets(c, slot, ik))
                output.Add(obj);
        }
        foreach (var child in slot.Children)
        {
            CollectEquippableObjects(child, output, ik);
        }
    }

    private static bool KeptOutOfSockets(Component c, Slot slot, AvatarIK? ik)
        => c is AvatarPoseDriver pose
           && ((slot.GetComponentInParents<QuadrupedIK>() is { } quad && quad.KeepsOutOfSockets(pose))
               || (ik?.PoseDriverFor(pose.Node.Value) is { IsDestroyed: false } read && !ReferenceEquals(read, pose)));

    // A socket can turn up after the avatar went on. The head and controller sockets are made by the wearer's own
    // machine once the body reaches it, so a favourite or anything else put on at arrival can beat them; a seat's
    // stand-in and a tracker plugged in late come after too. The part an equip would have put there goes in now,
    // if it is still free. Without it the head driver never got a socket, the IK never counted as worn and the
    // avatar stood in its stored stance under the default head. Only the wearer's machine does this, as every
    // equip. -xlinka
    internal void OfferSocket(AvatarSocket socket)
    {
        if (socket == null || socket.IsDestroyed)
            return;
        // A default piece gives way to the avatar's own part, as it would at equip.
        if (socket.Equipped.Target is Component held && !IsDefaultPiece(held))
            return;
        var part = FreePartFor(socket);
        if (part != null && socket.PreEquip(part, null!))
            socket.Equip(part);
    }

    internal static bool IsDefaultPiece(Component part) => part.Slot?.GetComponent<DiscardOnDequip>() != null;

    // In no socket, or in one that has since been given to something else.
    private static bool Unplaced(IAvatarEquippable part)
        => part.CurrentSocket is not { IsDestroyed: false } socket || !ReferenceEquals(socket.Equipped.Target, part);

    // The worn avatar's part for this socket that sits in no socket yet, picked the way Equip pairs them. Null
    // with nothing worn, nothing of the avatar's for that part, or a part the avatar keeps empty.
    private IAvatarEquippable? FreePartFor(AvatarSocket socket)
    {
        var node = socket.Node.Value;
        if (node == BodyNode.NONE || node == BodyNode.Root)
            return null;
        var avatar = EquippedAvatar;
        if (avatar == null || avatar.IsDestroyed)
            return null;
        var quadrupedIk = avatar.GetComponentInChildren<QuadrupedIK>();
        if (quadrupedIk != null && node is BodyNode.LeftHand or BodyNode.RightHand or BodyNode.LeftFoot or BodyNode.RightFoot)
            return null;
        var userRoot = UserRoot.Target ?? Slot?.ActiveUserRoot;
        if (userRoot != null)
        {
            foreach (var worn in userRoot.GetRegisteredComponents<AvatarSocket>())
            {
                if (worn.Equipped.Target is not Component { IsDestroyed: false } on || on.Slot == null || !on.Slot.IsDescendantOf(avatar))
                    continue;
                foreach (var conflict in worn.Equipped.Target.ConflictingNodes)
                {
                    if (conflict == node)
                        return null;
                }
            }
        }
        IAvatarEquippable? best = null;
        FindFreePart(avatar, node, quadrupedIk != null ? null : avatar.GetComponent<AvatarIK>() ?? avatar.GetComponentInChildren<AvatarIK>(), ref best);
        return best;
    }

    private static void FindFreePart(Slot slot, BodyNode node, AvatarIK? ik, ref IAvatarEquippable? best)
    {
        if (slot == null || slot.IsDestroyed)
            return;
        foreach (var c in slot.Components)
        {
            if (c is IAvatarEquippable part && !c.IsDestroyed && part.Node == node && Unplaced(part) && !KeptOutOfSockets(c, slot, ik)
                && (best == null || part.EquipPriority < best.EquipPriority))
                best = part;
        }
        foreach (var child in slot.Children)
            FindFreePart(child, node, ik, ref best);
    }

    private void EnsureRootObjectSlot()
    {
        if (Slot == null || Slot.ActiveUserRoot == null) return;

        var existing = Slot.GetComponent<AvatarSocket>();
        if (existing == null || existing.Node.Value != BodyNode.Root)
        {
            var rootObjSlot = Slot.AttachComponent<AvatarSocket>();
            rootObjSlot.Node.Value = BodyNode.Root;
        }
    }

    public void DequipCurrentAvatar()
    {
        _fillCts?.Cancel();
        var avatarSlot = EquippedAvatar ?? CurrentAvatar.Target;
        if (avatarSlot == null || avatarSlot.IsDestroyed)
        {
            CurrentAvatar.Target = null!;
            ResetAvatarPresentation();
            return;
        }

        Logger.Debug($"AvatarEquipManager: Dequipping '{avatarSlot.SlotName.Value}'");

        // Dequip every body-node slot whose equipped object lives under this
        // tree so OnDequip fires and pose-node drives release on all peers.
        var userRoot = UserRoot.Target ?? Slot?.ActiveUserRoot;
        if (userRoot != null)
        {
            var dequipped = new HashSet<IAvatarEquippable>();
            foreach (var objSlot in userRoot.GetRegisteredComponents<AvatarSocket>())
            {
                if (objSlot.Equipped?.Target is Component equipped && equipped.Slot != null &&
                    (equipped.Slot == avatarSlot || equipped.Slot.IsDescendantOf(avatarSlot)))
                {
                    objSlot.Dequip(dequipped);
                }
            }
        }
        else
        {
            ReleaseAvatarTracking(avatarSlot);
        }

        var avatarRoot = avatarSlot.GetComponent<AvatarForm>();
        if (avatarRoot != null)
            avatarRoot.IsActive.Value = false;

        if (userRoot != null && avatarSlot.ActiveUserRoot == userRoot)
            LeaveStanding(avatarSlot);

        CurrentAvatar.Target = null!;
        ResetAvatarPresentation();
    }

    // Taken off, an avatar stays where it stood, in the pose it was worn in, as an ordinary thing in the world.
    // The transform goes in before the move: moving it out ends this user's hold on it, and a write after that
    // is a write to someone else's object. A slot this user made may only leave this user's hierarchy with the right
    // to spawn, which the host checks, so without that right it is parked out of sight under the manager. -xlinka
    private void LeaveStanding(Slot avatar)
    {
        var world = World;
        var root = world?.RootSlot;
        if (world == null || root == null || avatar == null || avatar.IsDestroyed)
            return;

        var local = world.LocalUser;
        if (local != null && avatar.IsMintedHere
            && world.DataModelPermissions?.AllowsDomain(local, DataModelPermissionDomain.Spawn) == false)
        {
            avatar.ActiveSelf.Value = false;
            return;
        }

        var position = avatar.GlobalPosition;
        var rotation = avatar.GlobalRotation;
        var scale = avatar.GlobalScale;
        avatar.LocalPosition.Value = root.GlobalPointToLocal(position);
        avatar.LocalRotation.Value = root.GlobalRotationToLocal(rotation);
        avatar.LocalScale.Value = root.GlobalScaleToLocal(scale);
        avatar.SetParent(root);
    }

    public void EquipDefaultAvatar()
    {
        Logger.Debug("AvatarEquipManager: Equipping default avatar via SocketFiller");

        DequipCurrentAvatar();
        ResolveUserRoot();
        FillEmptySockets();
        IsUsingDefaultAvatar.Value = true;
    }

    public void RemoveAvatar(Slot avatarSlot)
    {
        if (avatarSlot == null) return;

        if (CurrentAvatar.Target == avatarSlot)
            EquipDefaultAvatar();

        if (avatarSlot == DefaultAvatarSlot.Target) return;

        AvailableAvatars.Remove(avatarSlot);
        avatarSlot.Destroy();
        Logger.Debug("AvatarEquipManager: Removed avatar");
    }

    public int GetAvatarIndex(Slot avatarSlot)
    {
        for (int i = 0; i < AvailableAvatars.Count; i++)
        {
            if (AvailableAvatars[i] == avatarSlot)
                return i;
        }
        return -1;
    }

    public void CycleNextAvatar()
    {
        if (AvailableAvatars.Count == 0)
        {
            EquipDefaultAvatar();
            return;
        }
        int currentIndex = GetAvatarIndex(CurrentAvatar.Target);
        int nextIndex = (currentIndex + 1) % AvailableAvatars.Count;
        EquipAvatar(AvailableAvatars[nextIndex]!);
    }

    public void CyclePreviousAvatar()
    {
        if (AvailableAvatars.Count == 0)
        {
            EquipDefaultAvatar();
            return;
        }
        int currentIndex = GetAvatarIndex(CurrentAvatar.Target);
        int prevIndex = currentIndex - 1;
        if (prevIndex < 0) prevIndex = AvailableAvatars.Count - 1;
        EquipAvatar(AvailableAvatars[prevIndex]!);
    }

    // Single-avatar equip: an avatar is a self-describing component tree
    // (AvatarForm + skeleton + rig + pose nodes). If it has a rigged body,
    // wire IK; either way, dispatch through the body-node pipeline. No
    // finalize gate. - xlinka
    // Putting an avatar on rewrites it, so it is worn in place only by someone who may: its maker, whoever
    // already holds it, the authority, or a role that may write what others own. -xlinka
    private bool MayWearInPlace(Slot avatar)
    {
        var world = World;
        var local = world?.LocalUser;
        if (world == null || local == null)
            return true;
        var permissions = world.DataModelPermissions;
        // A frozen world is frozen for the authority too, so there only what is this user's own goes on in place.
        // The host wore the world's avatar in place and the floor refused it halfway through. -xlinka
        if (permissions is { Enabled: true, SocialLock: true })
        {
            byte owner = avatar.ReferenceID.GetUserByte();
            return avatar.IsUnderLocalUser || (!world.IsAuthority && avatar.IsMintedHere)
                || (RefIDConstants.IsValidUserByte(owner) && local.HoldsByte(owner));
        }
        if (world.IsAuthority || avatar.IsMintedHere || avatar.IsUnderLocalUser)
            return true;
        if (permissions == null || !permissions.Enabled)
            return true;
        var role = permissions.GetRole(local);
        return role != null && role.Allows(
            DataModelPermissionAction.Write | DataModelPermissionAction.ReferenceWrite | DataModelPermissionAction.Create,
            ownsTarget: false);
    }

    // This user's own copy of an avatar they may not edit. With the right to spawn it stands where the original
    // stands. Without it, in an event say, it is made straight under this user's own root: that is wearing it, not
    // bringing it in, and the host takes a user's own things under their own root from them without Spawn. -xlinka
    private Slot? WearableCopyOf(Slot avatar)
    {
        var world = World;
        var root = UserRoot.Target?.Slot;
        if (world == null || root == null || root.IsDestroyed)
            return null;
        bool spawn = world.DataModelPermissions?.AllowsDomain(world.LocalUser, DataModelPermissionDomain.Spawn) != false;
        // Minted as this user's own, the host's item byte included: left to its parent's, the host's copy of a world
        // avatar was the world's again.
        using var own = spawn ? world.EnterSpawnAllocation() : world.EnterLocalUserAllocation();
        var copy = avatar.Duplicate(spawn ? avatar.Parent ?? world.RootSlot : root, preserveGlobalTransform: true);
        Logger.Debug($"AvatarEquipManager: wearing a copy of '{avatar.SlotName.Value}', which is not this user's to edit.");
        return copy;
    }

    // An avatar is worn, not brought into the world, so it also comes in where props may not: loaded straight under
    // this user's own root, in their own byte, which the host takes from them without the right to spawn. Taken off
    // there it is parked out of sight (LeaveStanding). For the callers that find Spawn refused. -xlinka
    public Slot? LoadToWear(Persistence.DataTreeDictionary tree, string name, Action<Slot?> whenDone)
    {
        ResolveUserRoot();
        var world = World;
        var root = UserRoot.Target?.Slot;
        if (world == null || tree == null || root == null || root.IsDestroyed)
            return null;
        Slot? slot = null;
        try
        {
            using (world.EnterLocalUserAllocation())
                slot = root.AddSlot(string.IsNullOrWhiteSpace(name) ? "Avatar" : name);
            var loading = slot;
            slot.LoadObjectStaged(tree, world.EnterLocalUserAllocation, null, loaded =>
            {
                if (loaded == null && !loading.IsDestroyed)
                    loading.Destroy();
                whenDone(loaded);
            });
            return slot;
        }
        catch (Exception ex)
        {
            Logger.Warn($"AvatarEquipManager: '{name}' did not load to wear: {ex.Message}");
            if (slot != null && !slot.IsDestroyed)
                slot.Destroy();
            return null;
        }
    }

    // Something under this user's own root that is neither in a hand nor already on: what LoadToWear and a copy made
    // without Spawn leave there to be put on.
    private bool IsLooseOnOwnRoot(Slot avatar)
        => ReferenceEquals(avatar.ActiveUserRoot, UserRoot.Target) && avatar.GetComponentInParents<Interaction.Grabber>() == null
            && !(CurrentAvatar.Target is { } worn && (ReferenceEquals(avatar, worn) || avatar.IsDescendantOf(worn)));

    // replaceCurrent destroys the avatar being worn instead of leaving it standing: an avatar put on from
    // the inventory replaces the one worn, and every swap would otherwise leave another body behind where
    // the user stood. Putting on one that already stands in the world leaves the old one there. -xlinka
    public bool EquipAvatar(Slot avatarSlot, bool replaceCurrent = false)
    {
        if (avatarSlot == null || avatarSlot.IsDestroyed)
            return false;

        ResolveUserRoot();
        if (UserRoot.Target == null)
        {
            Logger.Warn("AvatarEquipManager: Cannot equip avatar without a UserRoot");
            return false;
        }

        // Only a sealed avatar's owner puts it on, and that is refused here, on the wearer's own machine, before
        // anything is touched. The owner it claims goes on at once, checked or not: a first avatar out of the
        // favourites lands with its seal unchecked, the session may not have written the wearer's account yet, and
        // the key service may be down. A seal that later fails to check out is the authority's to take off.
        // -xlinka
        var wearer = UserRoot.Target.ActiveUser ?? World?.LocalUser;
        var sealVerdict = ItemSeals.Judge(avatarSlot, ItemSeals.AccountOf(wearer), SealRule.Equip, out _, out var blocking);
        if (sealVerdict == SealVerdict.Refused)
        {
            if (ReferenceEquals(wearer, World?.LocalUser))
                ItemSeals.TellRefused(World, sealVerdict, blocking);
            return false;
        }

        // An import, and the inventory item saved from it, is a wrapper holding the avatar, whose own root is
        // the slot carrying its AvatarForm. Wearing the wrapper put a second form, IK and face set on it over
        // the ones inside. The avatar inside is what goes on, and a wrapper it leaves empty goes too. -xlinka
        Slot? wrapper = null;
        var inner = avatarSlot.GetComponent<AvatarForm>() == null
            ? avatarSlot.GetComponentInChildren<AvatarForm>()?.Slot
            : WornWrapperInner(avatarSlot);
        if (inner is { IsDestroyed: false } && !ReferenceEquals(inner, avatarSlot))
        {
            wrapper = avatarSlot;
            avatarSlot = inner;
        }

        if (EquippedAvatar == avatarSlot)
            return true;
        if (avatarSlot.ActiveUserRoot != null && !IsLooseOnOwnRoot(avatarSlot))
            return false;

        // An avatar this user may not edit, the host's or the world's own, is worn as this user's own copy,
        // made where the original stands; the original stays there for everyone. Putting one on rewrites its
        // rig, its IK and its parent, and none of that is this user's to write: the equip was refused halfway
        // through and left nothing worn. -xlinka
        if (!MayWearInPlace(avatarSlot))
        {
            Slot? copy;
            using (ItemSeals.Wearing())
                copy = WearableCopyOf(avatarSlot);
            if (copy == null)
                return false;
            avatarSlot = copy;
        }

        // A wrapper's seal goes with the avatar that leaves it, or the worn avatar would carry none. A copy, so
        // the wrapper keeps its own: same fields, same signature. Only the seal's owner gets this far.
        if (wrapper?.GetComponent<AvatarProtection>() is { IsDestroyed: false, IsSigned: true } wrapperSeal && avatarSlot.GetComponent<AvatarProtection>() == null)
            avatarSlot.AttachComponent<AvatarProtection>(runOnAttachBehavior: false).CopyFrom(wrapperSeal);

        if (avatarSlot.GetComponent<AvatarForm>() == null)
            avatarSlot.AttachComponent<AvatarForm>();
        DropExtraForms(avatarSlot);

        // Rigged avatars get IK; unrigged trees still equip as plain
        // pose-node compositions.
        var skeleton = avatarSlot.GetComponentInChildren<SkeletonBuilder>();
        var rig = avatarSlot.GetComponentInChildren<HumanoidRig>();
        if (skeleton != null && (rig != null || avatarSlot.GetComponentInChildren<QuadrupedRig>() != null))
        {
            // A taken-off avatar still holds the pose it was worn in, and everything below reads the rig. -xlinka
            avatarSlot.GetComponent<AvatarIK>()?.ApplyRestPose();

            // Root frame must face the body: equip resets this slot to identity, so a stale frame wears the
            // avatar yawed off the user's forward. World-invariant + reference slots are live transforms, so
            // healing an old avatar here is safe. No-op when already aligned.
            // Four legs get the quadruped solver, and get it INSTEAD - never alongside. Both write the
            // same bone rotations in the same phase at the same update order, and the dispatch sort is
            // not stable, so having both means the pose flickers frame to frame.
            (avatarSlot.GetComponent<QuadrupedRig>() ?? avatarSlot.GetComponentInChildren<QuadrupedRig>())
                ?.LogGirdleSpan("before-TryAttachFor");
            var quadrupedIk = QuadrupedIK.TryAttachFor(avatarSlot, skeleton, rig);
            quadrupedIk?.Rig.Target?.LogGirdleSpan("after-TryAttachFor");
            AvatarIK? avatarIk = null;
            if (quadrupedIk != null)
            {
                if (!quadrupedIk.SetupFromAvatar(UserRoot.Target, equipTracking: false))
                {
                    Logger.Warn("AvatarEquipManager: quadruped IK setup failed");
                    return false;
                }
            }
            else if (rig != null)
            {
                AvatarCalibration.AlignAvatarFacing(avatarSlot, rig);

                avatarIk = avatarSlot.GetComponent<AvatarIK>() ?? avatarSlot.AttachComponent<AvatarIK>();
                if (!avatarIk.SetupFromAvatar(UserRoot.Target, equipTracking: false))
                {
                    Logger.Warn("AvatarEquipManager: Avatar IK setup failed");
                    return false;
                }
            }
            else
            {
                Logger.Warn("AvatarEquipManager: avatar has no usable rig");
                return false;
            }

            // Rigs with finger bones get a per-hand poser. It drives fingers only
            // from a live source (VR tracking); without one the hand rests.
            //
            // NOT on four legs. The HumanoidRig stays attached on a quadruped - it is the thing that got
            // us here, and the quadruped path is entered THROUGH it - so every humanoid consumer still
            // finds it and carries on as if the avatar were a person. On a fox the "hands" are front
            // paws: a hand poser curls them to whatever the controller's fingers are doing while the
            // quadruped solver is trying to plant them on the ground. -xlinka
            if (quadrupedIk == null && rig != null)
            {
                if (rig.HasLeftFingerBones)
                    EnsureHandPoser(rig, Chirality.Left);
                if (rig.HasRightFingerBones)
                    EnsureHandPoser(rig, Chirality.Right);
            }

            // Coarse body colliders so a created/equipped avatar has grab/point hitboxes on every equip
            // path, not just model-import. Idempotent - skips bones that already have one.
            if (avatarIk != null)
                avatarIk.GenerateBodyColliders();
            else
                quadrupedIk?.GenerateBodyColliders();

            // Face drivers (blink/eye-look/eye-expression/mouth/viseme). The in-world AvatarStudio wires
            // these, but an import-built or directly-equipped avatar otherwise has a dead face. Attach the
            // same set here so every equip path gets it; idempotent, so a creator-built avatar isn't doubled.
            if (rig != null)
                AttachFaceDrivers(avatarSlot, rig);

        }

        var spanRig = avatarSlot.GetComponent<QuadrupedRig>() ?? avatarSlot.GetComponentInChildren<QuadrupedRig>();
        spanRig?.LogGirdleSpan("before-Equip");
        var replaced = replaceCurrent ? EquippedAvatar ?? CurrentAvatar.Target : null;
        DequipCurrentAvatar();
        bool equipped = Equip(avatarSlot, isManualEquip: true);
        // Only once the new one is on: a failed equip leaves the old one standing to put back on. One this user
        // wore in place but may not delete, someone else's, stays standing where taking it off left it. -xlinka
        if (equipped && replaced != null && !replaced.IsDestroyed && !ReferenceEquals(replaced, avatarSlot)
            && !avatarSlot.IsDescendantOf(replaced) && replaced.MayDestroy())
            replaced.Destroy();
        if (equipped)
        {
            var quad = avatarSlot.GetComponentInChildren<QuadrupedIK>();
            FitBodyColliderToAvatar(quad);
            if (quad != null)
                quad.FitTrackingSpace();
            if (wrapper != null && !wrapper.IsDestroyed && !avatarSlot.IsDescendantOf(wrapper) && wrapper.MayDestroy()
                && ObjectAssetHolders.TakeAlong(wrapper, avatarSlot, IsPoseProxy))
                wrapper.Destroy();
        }
        spanRig?.LogGirdleSpan("after-Equip");
        return equipped;
    }

    // A wrapper worn by an older build and saved that way: its own form sits over the avatar's, with the IK and
    // pose proxies that equip put on it, and the avatar's menu answered to neither. The avatar is still the
    // form below whose tree holds every skeleton in the wrapper. A slot with a skeleton of its own, outside any
    // form below it, is an avatar itself and is worn as it is. -xlinka
    private static Slot? WornWrapperInner(Slot slot)
    {
        Slot? inner = null;
        foreach (var skeleton in slot.GetComponentsInChildren<SkeletonBuilder>())
        {
            Slot? top = null;
            for (var at = skeleton.Slot; at != null && !ReferenceEquals(at, slot); at = at.Parent)
            {
                if (at.GetComponent<AvatarForm>() != null)
                    top = at;
            }
            if (top == null || (inner != null && !ReferenceEquals(inner, top)))
                return null;
            inner = top;
        }
        return inner;
    }

    // Imported before the importer kept one: a package's second avatar root, scale one, beside the first. The
    // socket took the second, the avatar went on at its raw model's size, and the IK, menu and user scale all
    // read the first and found it unworn. -xlinka
    private static void DropExtraForms(Slot avatar)
    {
        var forms = new List<AvatarForm>(avatar.GetComponents<AvatarForm>());
        for (int i = 1; i < forms.Count; i++)
        {
            if (avatar.MayRemoveComponentQuietly(forms[i]))
                forms[i].Destroy();
        }
    }

    // One of the pose proxies an older equip left on a wrapper.
    private static bool IsPoseProxy(Slot child) => child.GetComponent<AvatarPoseDriver>() != null;

    // Resize the wearer's character capsule to the avatar actually being worn.
    //
    // The capsule is built once for a standing person: 1.8 m tall, 0.275 m radius, centred at 0.9. Wear a
    // fox and you are still a person-shaped pillar, so you cannot get under anything, you collide with
    // walls a foot before you reach them, and the body the world pushes around has nothing to do with
    // the body on screen. CapsuleCollider is Y-axis only, so an animal cannot get a capsule along its
    // spine; what it CAN get is one that is the right height and the right width, which is the part
    // that decides where you fit. Restored to the standing defaults whenever a humanoid is worn, so
    // taking the fox off gives the person their own body back. -xlinka
    private void FitBodyColliderToAvatar(QuadrupedIK? quadrupedIk)
    {
        var userRoot = UserRoot.Target ?? Slot?.ActiveUserRoot;
        if (userRoot?.Slot == null || userRoot.Slot.IsDestroyed)
            return;

        CapsuleCollider? capsule = null;
        foreach (var candidate in userRoot.Slot.GetComponents<CapsuleCollider>())
        {
            if (candidate.Type.Value == Lumora.Core.Physics.ColliderType.CharacterController)
            {
                capsule = candidate;
                break;
            }
        }
        if (capsule == null)
            return;

        float height = HumanCapsuleHeight;
        float radius = HumanCapsuleRadius;

        if (quadrupedIk != null && quadrupedIk.TryGetBodyMetrics(out float standHeight, out _, out float width))
        {
            height = MathF.Max(standHeight, 0.2f);
            // Half the shoulder track, floored so a narrow animal still has a body, and capped at half
            // the height so the capsule cannot become a sphere wider than the thing is tall.
            radius = System.Math.Clamp(width * 0.5f, 0.1f, height * 0.5f);
        }

        capsule.Height.Value = height;
        capsule.Radius.Value = radius;
        capsule.Offset.Value = new float3(0f, height * 0.5f, 0f);

        Logger.Debug($"AvatarEquipManager: body capsule fitted to {(quadrupedIk != null ? "quadruped" : "humanoid")} "
                 + $"height={height:F2} radius={radius:F2}");
    }

    // Restore the user's neutral tracking frame and collider when no custom form is worn.
    private void ResetAvatarPresentation()
    {
        var userRoot = UserRoot.Target ?? Slot?.ActiveUserRoot;
        userRoot?.SetAvatarTrackingOffset(float3.Zero);
        FitBodyColliderToAvatar(null);
    }

    private const float HumanCapsuleHeight = 1.8f;
    private const float HumanCapsuleRadius = 0.275f;

    // Attach the avatar's face-driver set, mirroring the in-world creator. Every driver attaches
    // unconditionally and resolves its own targets; one with nothing to drive is a no-op.
    //
    // The eye set used to gate on eye BONES, and all three of them drive BLENDSHAPES: blink is
    // nothing but blendshapes, and the gaze driver handles bone eyes and eyeLook shapes alike. So an
    // avatar with a full set of face shapes and no eye bones - which is most of them, since the
    // common face-shape conventions carry no eye bones at all - got no blink, no gaze and no eye
    // expression. It sat there with its eyes open forever, which is the single loudest way an avatar
    // reads as dead. Measured on a real import: 'no eye bones' in the log, nothing on the face. Let
    // each driver decide for itself whether it has work. -xlinka
    private static void AttachFaceDrivers(Slot avatarSlot, HumanoidRig rig)
    {
        if (avatarSlot == null || avatarSlot.IsDestroyed)
            return;

        // A face part the avatar brought anywhere in its own tree is the one it uses: an import keeps them where
        // they were made, a mouth on the face mesh it names shapes of and the voice on the head. A second one at
        // the root tapped the microphone twice and went hunting by name for shapes the avatar's own had already
        // been told. -xlinka
        if (!HasOwn<BlinkDriver>(avatarSlot))
            avatarSlot.AttachComponent<BlinkDriver>();
        if (!HasOwn<EyeGazeDriver>(avatarSlot))
            avatarSlot.AttachComponent<EyeGazeDriver>();
        if (!HasOwn<EyeExpressionDriver>(avatarSlot))
            avatarSlot.AttachComponent<EyeExpressionDriver>();

        // Mouth/lip blendshapes from replicated face tracking; rests when nothing is tracking.
        if (!HasOwn<MouthExpressionDriver>(avatarSlot))
            avatarSlot.AttachComponent<MouthExpressionDriver>();

        if (!HasOwn<LipSyncAnalyzer>(avatarSlot))
            avatarSlot.AttachComponent<LipSyncAnalyzer>();
        if (!HasOwn<VisemeWeightDriver>(avatarSlot))
            avatarSlot.AttachComponent<VisemeWeightDriver>();

        // Breath blendshape (chest/belly rise) on the same rhythm as the bone breathing; no-op when
        // the avatar has no breath shape. -xlinka
        if (avatarSlot.GetComponent<BreathingDriver>() == null)
            avatarSlot.AttachComponent<BreathingDriver>();
    }

    // On the avatar's own slot, or below it and not inside another avatar carried along.
    private static bool HasOwn<T>(Slot avatarSlot) where T : Component
    {
        if (avatarSlot.GetComponent<T>() != null)
            return true;
        foreach (var part in avatarSlot.GetComponentsInChildren<T>())
        {
            if (ReferenceEquals(part.Slot?.GetComponentInParents<AvatarForm>()?.Slot, avatarSlot))
                return true;
        }
        return false;
    }

    // The pose graph for one hand, on its wrist bone, once: a driver fed by a multiplexer whose first
    // slot the binder fills with the wearer's finger stream at equip and whose second is the tool grip,
    // an idle-to-fist blend the driver eases while this hand's HandTool holds something. The presets
    // are shared per avatar. Live finger tracking wins whenever it is there; the grip blend covers
    // everything else, so an untracked hand still holds a relaxed shape and closes on a tool. -xlinka
    private static void EnsureHandPoser(HumanoidRig rig, Chirality side)
    {
        var wrist = rig.TryGetBone(side.HandNode());
        if (wrist == null)
            return;

        foreach (var comp in wrist.Components)
            if (comp is HandPoseDriver)
                return;

        var idle = HandPosePresets.GetShared(rig.Slot, HandPoseShape.Idle);
        var fist = HandPosePresets.GetShared(rig.Slot, HandPoseShape.Fist);

        var grip = wrist.AttachComponent<HandPoseBlend>();
        grip.A.Target = idle;
        grip.B.Target = fist;

        var multiplexer = wrist.AttachComponent<HandPoseMultiplexer>();
        multiplexer.Sources.Add(null);
        multiplexer.Sources.Add(grip);

        var poser = wrist.AttachComponent<HandPoseDriver>();
        poser.Side.Value = side;
        poser.HandRoot.Target = wrist;
        poser.PoseSource.Target = multiplexer;
        poser.GripBlend.Target = grip;

        var binder = wrist.AttachComponent<HandPoseBinder>();
        binder.Side.Value = side;
        binder.TargetPoser.Target = poser;
        binder.TargetMultiplexer.Target = multiplexer;
    }

    public bool CanEquipAvatar(Slot avatarSlot)
    {
        if (avatarSlot == null || avatarSlot.IsDestroyed)
            return false;
        return avatarSlot.GetComponentInChildren<SkeletonBuilder>() != null
            && avatarSlot.GetComponentInChildren<HumanoidRig>() != null;
    }

    public async void ImportAndEquipAvatar(string filePath, LocalDB localDB = null!)
    {
        try
        {
            var importSlot = Slot.AddSlot("ImportedAvatar");
            var result = await ModelImporter.ImportAvatarAsync(filePath, importSlot, localDB);

            if (result.Success && result.RootSlot != null)
            {
                Logger.Debug("AvatarEquipManager: Avatar imported - calibrate via the avatar studio, then equip");
            }
            else
            {
                Logger.Error($"AvatarEquipManager: Failed to import avatar: {result.ErrorMessage}");
                importSlot.Destroy();
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"AvatarEquipManager: Exception importing avatar: {ex.Message}");
        }
    }

    private void ResolveUserRoot()
    {
        if (UserRoot.Target != null) return;
        UserRoot.Target = Slot?.ActiveUserRoot ?? Slot?.GetComponentInParent<UserRoot>()!;
    }

    private static void ReleaseAvatarTracking(Slot avatarSlot)
    {
        if (avatarSlot == null || avatarSlot.IsDestroyed) return;

        var dequipped = new HashSet<IAvatarEquippable>();
        foreach (var poseNode in avatarSlot.GetComponentsInChildren<AvatarPoseDriver>())
        {
            poseNode.CurrentSocket?.Dequip(dequipped);
        }
    }
}
