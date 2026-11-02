// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Helio.UI;
using Lumora.Core;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.Components;

// Editor for SyncRef members: a compact square-button cluster then the target readout stretched
// across the remaining row. Cluster: pull the target's reference card, open the target as a tab in
// this panel, then (after the wide target button) clear. Pressing the target button assigns a held
// ReferenceProxy card, or the asset a held object carries (an orb's material, an imported sound's clip, a
// shape's mesh; see HeldAsset), when the presser holds one in either hand, otherwise selects the target in
// this panel. Grip-releasing one over the row assigns too (type-checked either way).
// The ≡ button is the one PULL spot for the CURRENT TARGET, by press or by the grab action, so a target
// can be dragged from one field into another without going through the tree. Nothing else on the editor
// hands out a card: when the whole readout did, a squeeze while pressing it pulled one every time. -xlinka
[ComponentCategory("Utility/Inspectors")]
public class RefMemberEditor : MemberEditor, IProxyReceiver, IProxySource
{
    private readonly SyncRef<Text> _targetText;
    private readonly SyncRef<IField<color>> _targetTint;
    private readonly SyncRef<Slot> _pullArea = new();

    public RefMemberEditor()
    {
        _targetText = new SyncRef<Text>(this);
        _targetTint = new SyncRef<IField<color>>(this);
    }

    private ISyncRef? Ref => TargetMember.Target as ISyncRef;

    protected override void BuildUI(UIBuilder ui)
    {
        // Small square glyph buttons; text glyphs only, no icon art.
        ui.PushStyle();
        ui.MinWidth(26f);
        ui.PreferredWidth(26f);
        ui.FlexibleWidth(0f);
        ui.PushStyle();
        ui.TextColor(InspectorUI.AccentColor);
        _pullArea.Target = ui.Button("≡", OnPullPressed).Slot;
        ui.Button("↑", OnOpenPressed);   // open as a tab
        ui.PopStyle();
        ui.PopStyle();

        // Target readout is itself the assign/select button, stretched across the row.
        ui.PushStyle();
        ui.FlexibleWidth(1f);
        var targetButton = ui.Button("", OnTargetPressed);
        var text = targetButton.Slot.GetComponentInChildren<Text>();
        if (text != null)
        {
            InspectorUI.FillParent(text.RectTransform!);
            text.HorizontalAlignment.Value = TextHorizontalAlignment.Center;
            text.VerticalAlignment.Value = TextVerticalAlignment.Middle;
            // Target labels embed slot names, which are user content with inline style tags, and the
            // null/invalid states are rendered italic.
            text.RichText.Value = true;
            _targetText.Target = text;
        }
        // The readout's BACKING carries the field state color (driven magenta, linked cyan, broken
        // gray), so the row's state is readable from the widget you would actually press. Registered
        // with the row's live tint so it tracks drives attaching and detaching while the panel stays
        // open. -xlinka
        var backing = targetButton.Slot.GetComponent<Image>();
        _targetTint.Target = backing?.Tint!;
        BindStateTint(backing?.Tint, InspectorUI.RowColor);
        ui.PopStyle();

        ui.PushStyle();
        ui.MinWidth(26f);
        ui.PreferredWidth(26f);
        ui.FlexibleWidth(0f);
        ui.TextColor(InspectorUI.DangerColor);
        ui.Button("Ø", OnClearPressed);  // null the reference
        ui.PopStyle();
    }

    [SyncMethod]
    public void OnPullPressed(Button button, UIInteractionContext context)
    {
        var target = Ref?.Target;
        if (target == null || target.IsDestroyed || World == null)
            return;
        var head = World.LocalUser?.Root?.HeadSlot;
        float3 point = context.WorldPoint;
        float3 offset = head != null && (head.GlobalPosition - point).LengthSquared > 1e-6f
            ? (head.GlobalPosition - point).Normalized * 0.25f
            : float3.Up * 0.1f;
        ReferenceProxy.Spawn(World, target, point + offset);
    }

    // grip on the readout hands over a card for the reference's CURRENT TARGET
    public IGrabbable? TryCreateProxy(Grabber grabber, in float3 spawnPoint)
    {
        var target = Ref?.Target;
        if (target == null || target.IsDestroyed || World == null || !ReferenceProxySource.ContainsPoint(_pullArea.Target, spawnPoint))
            return null;
        return ReferenceProxy.Spawn(World, target, spawnPoint);
    }

    // opens the target as a tab in this panel; a component target comes pre-expanded
    [SyncMethod]
    public void OnOpenPressed(Button button, UIInteractionContext context)
    {
        var panel = Slot.GetComponentInParent<SceneInspectorPanel>();
        if (panel == null)
            return;
        var target = Ref?.Target;
        var slot = target as Slot ?? (target as Component)?.Slot;
        if (slot == null || slot.IsDestroyed)
            return;
        panel.OpenTab(slot);
        if (target is Component component
            && !SceneInspectorPanel.IsExpanded(panel.ExpandedComponents, component.ReferenceID.RawValue))
            panel.ExpandedComponents.Add(component.ReferenceID.RawValue);
    }

    // a held card assigns; an empty hand selects the target here
    [SyncMethod]
    public void OnTargetPressed(Button button, UIInteractionContext context)
    {
        _refusedThisPress = false;
        if (TryConsumeHeldProxy(context.Actor) || _refusedThisPress)
            return;

        var panel = Slot.GetComponentInParent<SceneInspectorPanel>();
        if (panel == null)
            return;
        var target = Ref?.Target;
        var jumpTo = target as Slot ?? (target as Component)?.Slot;
        if (jumpTo == null || jumpTo.IsDestroyed)
            return;
        panel.Selected.Target = jumpTo;
    }

    protected override void RefreshDisplay()
    {
        var text = _targetText.Target;
        if (text == null || text.IsDestroyed)
            return;

        if (_refusal != null && World != null && World.Time.TotalTime < _refusalUntil)
        {
            text.Content.Value = $"<i>can't use: {_refusal}</i>";
            text.Color.Value = Lumora.Core.Components.UI.DashTheme.Warning;
        }
        else
        {
            _refusal = null;
            text.Content.Value = Describe(Ref);
            text.Color.Value = Ref?.Target == null ? InspectorUI.MutedColor : InspectorUI.TextColor;
        }

        // The state color can change without the VALUE changing (a drive attaching writes no value),
        // so the tint component owns the steady-state updates; this just keeps a fresh row honest.
        var backingTint = _targetTint.Target;
        if (backingTint != null && !backingTint.IsDestroyed)
            InspectorUI.ApplyStateTint(backingTint, FieldStateColor(InspectorUI.RowColor));
    }

    // The readout line: "&lt;name&gt; on &lt;component&gt; on &lt;slot&gt; (&lt;RefID&gt;)". A slot target
    // names itself, a component names its type and slot, a member names itself plus the component and
    // slot it lives on. The RefID is load-bearing, not decoration: without it two same-named targets
    // on two same-named slots read identically and there is no way to tell which one a field actually
    // points at. Empty and gone read as italic states rather than an empty button. -xlinka
    private static string Describe(ISyncRef? reference)
    {
        // The reference MEMBER itself is gone (component destroyed under an open panel), not merely empty.
        if (reference == null || (reference is IWorldElement element && element.IsDestroyed))
            return "<i>invalid</i>";

        var target = reference.Target;
        if (target == null)
            return "<i>null</i>";

        if (target is Slot targetSlot)
            return $"{targetSlot.SlotName.Value} ({target.ReferenceID})";

        var component = target as Component ?? FindOwningComponent(target);
        var slot = component?.Slot ?? FindOwningSlot(target);

        string where = component != null && !ReferenceEquals(component, target)
            ? $" on {component.GetType().Name} on {slot?.SlotName.Value}"
            : slot != null ? $" on {slot.SlotName.Value}" : "";

        string name = target is ISyncMember member
            ? InspectorUI.MemberDisplayName(member)
            : target.GetType().Name;

        return $"{name}{where} ({target.ReferenceID})";
    }

    private static Component? FindOwningComponent(IWorldElement? element)
    {
        while (element != null)
        {
            if (element is Component component)
                return component;
            element = (element as SyncElement)?.Parent;
        }
        return null;
    }

    private static Slot? FindOwningSlot(IWorldElement? element)
    {
        while (element != null)
        {
            if (element is Slot slot)
                return slot;
            if (element is Component component)
                return component.Slot;
            element = (element as SyncElement)?.Parent;
        }
        return null;
    }

    [SyncMethod]
    public void OnClearPressed(Button button, UIInteractionContext context)
    {
        var reference = Ref;
        if (reference == null || IsReadOnly)
            return;
        object? before = Field?.BoxedValue;
        reference.Clear();
        if (Field is { } field)
            InspectorUndo.RecordEdit(this, field, before, field.BoxedValue);
        RefreshDisplay();
    }

    // grip released (or primary pressed) over this row while holding cards or assets: assign the first that fits
    public bool TryReceiveProxy(IReadOnlyList<IGrabbable> held, Grabber grabber)
    {
        var reference = Ref;
        // A driven reference is owned by its drive; dropping onto it would be silently reverted.
        if (reference == null || IsReadOnly)
            return false;

        bool meshField = MeshSource.IsMeshField(reference);
        bool assetField = !meshField && HeldAsset.IsAssetField(reference);
        string? refusal = null;
        for (int i = 0; i < held.Count; i++)
        {
            if (held[i] is not Component component || component.Slot == null)
                continue;
            var proxy = component.Slot.GetComponent<ReferenceProxy>();
            object? before = Field?.BoxedValue;
            if (proxy == null && component.Slot.GetComponent<ValueProxy>() != null)
            {
                refusal ??= ValueCardConversion.NotReference.Resolve();
                continue;
            }
            if (proxy == null)
            {
                // A held object fills an asset field with what it carries and stays in the hand: an orb its
                // material, an imported sound its clip. Only asset fields take one, and only of their own kind:
                // a renderer's mesh field takes any component, and an object is not a card for its own slot.
                var carried = HeldAsset.CarriedBy(component.Slot, reference);
                if (meshField && carried is Component candidate && MeshSource.FeedsBack(reference, candidate) is { } loop)
                {
                    refusal ??= loop;
                    continue;
                }
                string? setRefusal = null;
                if (carried == null || !TrySetGuarded(reference, carried, out setRefusal))
                {
                    if (meshField)
                        refusal ??= HeldAsset.MeshRefusal(component.Slot, reference);
                    else if (assetField)
                        refusal ??= setRefusal ?? $"'{component.Slot.SlotName.Value}' carries no {HeldAsset.KindName(reference)}";
                    continue;
                }
            }
            else if (proxy.Target.Target is not { } element)
            {
                if (meshField || assetField)
                    refusal ??= "the card points at nothing";
                continue;
            }
            else if (!TryAssign(reference, element, out var why))
            {
                if (meshField || assetField)
                    refusal ??= why;
                continue;
            }

            if (Field is { } field && !Equals(before, field.BoxedValue))
                InspectorUndo.RecordEdit(this, field, before, field.BoxedValue);
            _refusal = null;
            RefreshDisplay();
            proxy?.Consume(held[i], grabber);
            return true;
        }
        if (refusal != null)
            ShowRefusal(refusal);
        return false;
    }

    // A mesh field says why it would not take what was handed to it, in place of its readout, for a few seconds.
    private const float RefusalSeconds = 4f;
    private string? _refusal;
    private double _refusalUntil;
    private bool _refusedThisPress;

    private void ShowRefusal(string reason)
    {
        if (World == null)
            return;
        _refusal = reason;
        _refusalUntil = World.Time.TotalTime + RefusalSeconds;
        _refusedThisPress = true;
        RefreshDisplay();
        World.RunInSeconds(RefusalSeconds + 0.1f, () =>
        {
            if (!IsDestroyed)
                RefreshDisplay();
        });
    }

    // What a mesh field is refused for the last card or object handed to it, or null.
    public string? LastRefusal => _refusal;

    private bool TryConsumeHeldProxy(User? actor)
    {
        var root = actor?.Root?.Slot;
        if (root == null)
            return false;
        var grabbers = new List<Grabber>();
        CollectGrabbers(root, grabbers);
        foreach (var grabber in grabbers)
        {
            // Snapshot: a successful receive releases and destroys the card, mutating the hold list.
            if (!grabber.IsHoldingObjects)
                continue;
            var held = new List<IGrabbable>(grabber.GrabbedObjects);
            if (TryReceiveProxy(held, grabber))
                return true;
        }
        return false;
    }

    private static void CollectGrabbers(Slot slot, List<Grabber> result)
    {
        var grabber = slot.GetComponent<Grabber>();
        if (grabber != null)
            result.Add(grabber);
        foreach (var child in slot.Children)
            CollectGrabbers(child, result);
    }

    // The assignment ladder, widest match first: the element itself, then a reference field's current
    // target, then - for a SLOT card - the slot's own ActiveSelf field, then every component on it.
    // Order matters, since a slot card can satisfy several rungs and the most direct one has to win.
    // So ONE slot card can feed a component reference, a bool field, or a mesh field, without the
    // user having to know which sub-element the field actually wanted. A component card on a slot
    // field gives the slot it is on, and on an asset field the asset its object carries. -xlinka
    internal static bool TryAssign(ISyncRef reference, IWorldElement element) => TryAssign(reference, element, out _);

    internal static bool TryAssign(ISyncRef reference, IWorldElement element, out string? refusal)
    {
        refusal = null;
        if (MeshSource.IsMeshField(reference))
            return TryAssignMesh(reference, element, out refusal);
        if (TrySetGuarded(reference, element))
            return true;
        if (element is ISyncRef sourceRef && sourceRef.Target != null && TrySetGuarded(reference, sourceRef.Target))
            return true;
        if (element is Slot slot)
        {
            if (TrySetGuarded(reference, slot.ActiveSelf))
                return true;
            foreach (var component in slot.GetAllComponents())
            {
                if (TrySetGuarded(reference, component))
                    return true;
            }
        }
        if (element is Component owner && TrySetGuarded(reference, owner.Slot))
            return true;
        // A card for one part of an object on an asset field: the asset that object carries, so a card pulled
        // from a sound's player still hands over its clip.
        if (element is Component part && part.Slot is { IsDestroyed: false } partSlot
            && HeldAsset.IsAssetField(reference) && HeldAsset.OnSlot(partSlot, reference) is { } asset
            && TrySetGuarded(reference, asset))
            return true;
        return false;
    }

    // A mesh field takes what the card means as a mesh: a slot the mesh it carries (for a soft body its skinned
    // renderer first), a renderer its mesh or itself, a reference field what it points at, a card for some other part
    // of a mesh object that object's mesh. A component any field would take is not good enough here: a light in a
    // renderer's mesh field draws nothing and says nothing, so it is refused in words instead.
    private static bool TryAssignMesh(ISyncRef reference, IWorldElement element, out string? refusal)
    {
        bool keeps = MeshSource.KeepsRenderers(reference);
        if (element is ISyncRef inner && inner.Target != null)
            element = inner.Target;

        Component? candidate = null;
        refusal = null;
        switch (element)
        {
            case Slot slot:
                candidate = MeshSource.OnSlot(slot, keeps);
                if (candidate == null)
                    refusal = $"'{slot.SlotName.Value}' carries no mesh";
                break;
            case Component component:
                candidate = MeshSource.Accepted(component, keeps);
                if (candidate == null)
                {
                    refusal = MeshSource.Refusal(component, keeps);
                    if (!MeshSource.IsMesh(component) && !MeshSource.IsRenderer(component)
                        && MeshSource.OnSlot(component.Slot, keeps) is { } onSlot)
                    {
                        candidate = onSlot;
                        refusal = null;
                    }
                }
                break;
            default:
                refusal = $"a {element.GetType().Name} is not a mesh";
                break;
        }
        if (candidate == null)
            return false;
        if (MeshSource.FeedsBack(reference, candidate) is { } loop)
        {
            refusal = loop;
            return false;
        }
        if (TrySetGuarded(reference, candidate))
            return true;
        refusal = "it cannot be referenced from here";
        return false;
    }

    // TrySet only type-checks; the assignment behind it still throws for a cross-world or local-only
    // target. A card carrying one of those must fail this rung and fall through to the next, not take
    // the whole grip-release path (and everything else queued behind it) down with it. -xlinka
    private static bool TrySetGuarded(ISyncRef reference, IWorldElement? element) => TrySetGuarded(reference, element, out _);

    // The refusal is for the person holding it: another world (the dash's own space) or something only they have.
    private static bool TrySetGuarded(ISyncRef reference, IWorldElement? element, out string? refusal)
    {
        refusal = null;
        if (element == null || element.IsDestroyed)
            return false;
        if (reference is IWorldElement owner && owner.World != null && element.World != owner.World)
        {
            refusal = "it is in another world";
            return false;
        }
        try
        {
            return reference.TrySet(element);
        }
        catch (System.Exception ex)
        {
            Logging.Logger.Debug($"RefMemberEditor: cannot assign {element.GetType().Name} - {ex.Message}");
            if (ex.Message.Contains("local", System.StringComparison.OrdinalIgnoreCase))
                refusal = "it only exists for you, so nobody else could see it";
            return false;
        }
    }
}
