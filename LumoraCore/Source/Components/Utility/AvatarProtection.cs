// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Helio.UI;
using Lumora.Core.Security;
using Lumora.Nexus.Cloud.Cdn;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

// An account's seal on what it sits on and everything under it: signed by the accounts service with the owner's
// key, checked by every machine against the owner's public keys. Unlike ItemProtection, which guards a world's
// own content from its visitors and lets the host through, this one follows the item from session to session and
// binds the host as much as anyone: only the owner, signed in, may wear it, save or copy it, export it, or look
// inside it. The seal is the whole of it: no file of the item is claimed anywhere, so two people with the same
// files never block each other.
//
// It goes on three ways only: attached by hand, by the Avatar Studio's box, or by the package importer on
// everything it brings in. Each way the machine that attached it asks the accounts service for the signed
// fields and fills them in when they come back; if they cannot be had the component comes off again, and the
// person is told why (an import only logs it). Until it is signed it is inert: it blocks nobody, and the
// authority never treats it as broken or takes anything out over it, so an empty one pasted or copied anywhere
// does nothing at all.
//
// Saves from before the rename name it ItemSeal; WorkerManager loads those as this, fields and signature as they
// were, so seals already issued keep checking out.
//
// The fields are what was signed and nothing else. They are not drivable, scripts may not write them, and the
// inspector does not show them as fields, but nothing stops a determined edit, and nothing has to: any change
// makes the signature fail, a failed seal blocks everyone including its owner, and the authority takes a broken
// object out of the world. There is no off switch either. Enabled is ignored. -xlinka
[ComponentCategory("Utility")]
[SingleInstancePerSlot]
public sealed class AvatarProtection : Component, ICustomInspectorUI, IInspectorActionContextHandler
{
    [HideInInspector] public readonly Sync<string> OwnerId = new();
    [HideInInspector] public readonly Sync<string> KeyId = new();
    [HideInInspector] public readonly Sync<string> ItemId = new();
    [HideInInspector] public readonly Sync<int> Rules = new();
    [HideInInspector] public readonly Sync<long> IssuedAt = new();
    [HideInInspector] public readonly Sync<string> Signature = new();

    // Seconds between tries while the owner's keys cannot be had.
    private static readonly float[] RetryAfter = { 5f, 15f, 60f, 300f };

    public SealState State { get; private set; } = SealState.Checking;

    // On the world's thread, whenever State moves.
    public event Action<AvatarProtection>? StateChanged;

    private Fields _checked;
    private int _generation;
    private int _retries;
    private Text? _stateText;

    private readonly record struct Fields(string OwnerId, string KeyId, string ItemId, int Rules, long IssuedAt, string Signature);

    public string Owner => OwnerId.Value ?? string.Empty;

    public SealRule Covers => (SealRule)(Rules.Value & (int)SealRule.All);

    public bool IsOwnedBy(string? account) => !string.IsNullOrEmpty(account) && string.Equals(account, OwnerId.Value, StringComparison.Ordinal);

    public bool IsVerifiedOwner(User? user) => State == SealState.Verified && IsOwnedBy(ItemSeals.AccountOf(user));

    // Signed, or at least carrying a signature to check. An unsigned seal is inert.
    public bool IsSigned => !string.IsNullOrEmpty(Signature.Value);

    public override void OnAwake()
    {
        base.OnAwake();
        OwnerId.MarkNonDrivable();
        KeyId.MarkNonDrivable();
        ItemId.MarkNonDrivable();
        Rules.MarkNonDrivable();
        IssuedAt.MarkNonDrivable();
        Signature.MarkNonDrivable();
        ItemSeals.Register(this);
    }

    // Only on the machine that attached it, never on a copy, a paste or a load.
    public override void OnAttach()
    {
        base.OnAttach();
        ItemSeals.RequestFor(this);
    }

    public override void OnStart()
    {
        base.OnStart();
        Recheck(force: false);
        if (World?.IsAuthority == true)
            SealKeeper.For(World);
    }

    public override void OnChanges()
    {
        base.OnChanges();
        Recheck(force: false);
    }

    // Runs as the component comes off its slot, inside the removal: a peer's removal is still being decoded here,
    // which is the only moment that says whose it was.
    public override void OnPrepareDestroy()
    {
        ItemSeals.Unregister(this);
        ItemSeals.NoteGone(this);
        base.OnPrepareDestroy();
    }

    public override void OnDestroy()
    {
        ItemSeals.Unregister(this);
        base.OnDestroy();
    }

    internal void Write(SealGrant grant)
    {
        OwnerId.Value = grant.OwnerId;
        KeyId.Value = grant.KeyId;
        ItemId.Value = grant.ItemId;
        Rules.Value = grant.Rules;
        IssuedAt.Value = grant.IssuedAt;
        Signature.Value = grant.Signature;
    }

    internal void CopyFrom(AvatarProtection other)
    {
        OwnerId.Value = other.OwnerId.Value;
        KeyId.Value = other.KeyId.Value;
        ItemId.Value = other.ItemId.Value;
        Rules.Value = other.Rules.Value;
        IssuedAt.Value = other.IssuedAt.Value;
        Signature.Value = other.Signature.Value;
    }

    // CHECKING

    private Fields Read() => new(OwnerId.Value ?? string.Empty, KeyId.Value ?? string.Empty, ItemId.Value ?? string.Empty,
        Rules.Value, IssuedAt.Value, Signature.Value ?? string.Empty);

    private void Recheck(bool force)
    {
        if (IsDestroyed || World == null || World.IsDisposed)
            return;
        var fields = Read();
        if (!force && fields.Equals(_checked) && _generation > 0)
            return;
        if (!fields.Equals(_checked))
            _retries = 0;
        _checked = fields;
        int generation = ++_generation;
        if (string.IsNullOrEmpty(fields.Signature))
        {
            SetState(SealState.Unsigned);
            return;
        }
        SetState(SealState.Checking);

        var directory = SealCloud.Directory;
        if (directory == null)
        {
            ScheduleRetry(generation);
            return;
        }
        var world = World;
        var seen = directory.Now;
        _ = Task.Run(async () =>
        {
            ProtectionDirectory.Verdict verdict;
            try
            {
                verdict = await directory.CheckAsync(fields.OwnerId, fields.KeyId, fields.ItemId, fields.Rules, fields.IssuedAt,
                    fields.Signature, seen).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LumoraLogger.Warn($"Avatar Protection: checking a seal failed: {ex.Message}");
                verdict = ProtectionDirectory.Verdict.Unreachable;
            }
            world.RunSynchronously(() => Settle(generation, verdict));
        });
    }

    private void Settle(int generation, ProtectionDirectory.Verdict verdict)
    {
        if (IsDestroyed || generation != _generation)
            return;
        switch (verdict)
        {
            case ProtectionDirectory.Verdict.Verified:
                _retries = 0;
                SetState(SealState.Verified);
                SealCloud.Directory?.NameOf(Owner);
                break;
            case ProtectionDirectory.Verdict.Broken:
                _retries = 0;
                LumoraLogger.Warn($"Avatar Protection: the seal on '{Slot?.Name.Value}' does not check out against {Owner}'s keys");
                SetState(SealState.Broken);
                break;
            default:
                ScheduleRetry(generation);
                break;
        }
    }

    internal void RetryIfWaiting()
    {
        if (!IsDestroyed && State == SealState.Checking && IsSigned)
            Recheck(force: true);
    }

    private void ScheduleRetry(int generation)
    {
        var world = World;
        if (world == null || world.IsDisposed)
            return;
        float wait = RetryAfter[System.Math.Min(_retries, RetryAfter.Length - 1)];
        _retries++;
        world.RunInSeconds(wait, () =>
        {
            if (!IsDestroyed && generation == _generation && State == SealState.Checking)
                Recheck(force: true);
        });
    }

    private void SetState(SealState state)
    {
        if (State == state)
            return;
        State = state;
        if (_stateText is { IsDestroyed: false } text)
            text.Content.Value = StateLabel(state);
        try
        {
            StateChanged?.Invoke(this);
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"Avatar Protection: a state listener failed: {ex.Message}");
        }
        ItemSeals.NoteState(this);
    }

    private static string StateLabel(SealState state) => state switch
    {
        SealState.Verified => ProtectionLocale.StateVerified.Resolve(),
        SealState.Broken => ProtectionLocale.StateBroken.Resolve(),
        SealState.Unsigned => ProtectionLocale.StateUnsigned.Resolve(),
        _ => ProtectionLocale.StateChecking.Resolve(),
    };

    // INSPECTOR

    public void BuildInspectorBody(UIBuilder ui)
    {
        InspectorStats.AddRow(ui, ProtectionLocale.InspectorOwner.Resolve(), ItemSeals.NameOf(World, Owner));
        _stateText = InspectorStats.AddRow(ui, ProtectionLocale.InspectorState.Resolve(), StateLabel(State));
        InspectorStats.AddRow(ui, ProtectionLocale.InspectorBlocks.Resolve(), BlocksLabel(Covers));
        InspectorStats.AddRow(ui, string.Empty, ProtectionLocale.InspectorForOthers.Resolve());
        InspectorUI.FixedRow(ui.Root, "RemoveProtection", InspectorUI.RowHeight, out var row, ui.Root);
        InspectorUI.RelayButton(row, this, "remove", ProtectionLocale.MenuRemove.Resolve(), 0f);
    }

    private static string BlocksLabel(SealRule rules)
    {
        var parts = new List<string>(4);
        if ((rules & SealRule.Equip) != 0)
            parts.Add(ProtectionLocale.RuleEquip.Resolve());
        if ((rules & SealRule.SaveCopy) != 0)
            parts.Add(ProtectionLocale.RuleSaveCopy.Resolve());
        if ((rules & SealRule.Export) != 0)
            parts.Add(ProtectionLocale.RuleExport.Resolve());
        if ((rules & SealRule.Inspect) != 0)
            parts.Add(ProtectionLocale.RuleInspect.Resolve());
        return string.Join(", ", parts);
    }

    // On the machine of whoever pressed, which is the only one that may speak for its owner.
    public void HandleInspectorAction(string argument, UIInteractionContext context)
    {
        if (argument == "remove")
            ItemSeals.RemoveProtection(this);
    }
}
