// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Helio.UI;
using Lumora.Core;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Components.UI;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.Components;

// A card carrying a copy of one field's value, taken the moment it was pulled off a value row's label.
// Released or pressed onto another field's editor, that field takes the value (MemberEditor's receiver
// does the write); released anywhere else it is gone, the same as a reference card. The copy rides a
// SyncVariant, so whoever ends up holding the card carries the same value on their own machine. -xlinka
[ComponentCategory("Utility/Inspectors")]
public class ValueProxy : Component
{
    public readonly SyncVariant Value;

    // Seconds a card nobody is holding survives (one that never made it into a hand).
    private const float UnheldLifetime = 10f;

    private Grabbable? _grabbable;
    private float _idleTime;

    public ValueProxy()
    {
        Value = new SyncVariant();
    }

    public override void OnStart()
    {
        base.OnStart();
        BindRelease();
    }

    public override void OnDestroy()
    {
        if (_grabbable != null)
            _grabbable.OnLocalReleased -= OnReleased;
        base.OnDestroy();
    }

    private void BindRelease()
    {
        if (_grabbable != null) return;
        _grabbable = Slot.GetComponent<Grabbable>();
        if (_grabbable != null) _grabbable.OnLocalReleased += OnReleased;
    }

    private void OnReleased(IGrabbable released)
    {
        // A receiver under the hand takes the card in the same release; give it that update first.
        World.RunInUpdates(1, () =>
        {
            if (!IsDestroyed && !Slot.IsDestroyed && _grabbable?.IsGrabbed != true)
                Slot.Destroy();
        });
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        if (_grabbable?.IsGrabbed == true) { _idleTime = 0; return; }
        _idleTime += delta;
        if (World.IsAuthority && _idleTime >= UnheldLifetime)
            Slot.Destroy();
    }

    public bool TryGetValue(out object? value, out Type? type)
    {
        value = null;
        type = null;
        if (Value.AsGenericField() is not { } field)
            return false;
        type = field.ValueType;
        value = field.BoxedValue;
        return true;
    }

    // Spend the card once a field took its value: out of the hand first, then gone (see ReferenceProxy.Consume).
    public void Consume(IGrabbable? grabbed, Grabber? grabber)
    {
        if (grabbed != null)
            grabber?.Release(grabbed);
        if (Slot != null && !Slot.IsDestroyed)
            Slot.Destroy();
    }

    public static IGrabbable? Spawn(World world, IField field, float3 position)
    {
        if (world == null || field == null || field.IsDestroyed || !ValueCardConversion.CanCarry(field.ValueType))
            return null;
        object? value;
        try { value = field.BoxedValue; }
        catch { return null; }

        string name = field is ISyncMember member ? InspectorUI.MemberDisplayName(member) : "";
        return Spawn(world, field.ValueType, value, position, name);
    }

    public static IGrabbable? Spawn(World world, Type type, object? value, float3 position, string name = "")
    {
        if (world == null || !ValueCardConversion.CanCarry(type))
            return null;
        using var allocation = world.EnterLocalUserAllocation();
        var slot = world.RootSlot.AddSlot("ValueProxy");
        slot.Persistent.Value = false;
        slot.Tag.Value = "Developer";
        slot.GlobalPosition = position;

        var head = world.LocalUser?.Root?.HeadSlot;
        if (head != null)
        {
            float3 toHead = head.GlobalPosition - position;
            toHead.y = 0f;
            if (toHead.LengthSquared > 1e-6f)
                slot.GlobalRotation = floatQ.LookRotation(toHead.Normalized, float3.Up).Inverse;
        }
        float userScale = world.LocalUser?.Root?.Slot?.GlobalScale.y ?? 1f;
        userScale = userScale > 0.0001f ? userScale : 1f;
        slot.LocalScale.Value = float3.One * InspectorUI.PanelScale * userScale;

        var card = slot.AttachComponent<ValueProxy>();
        card.Value.ElementType = typeof(Sync<>).MakeGenericType(type);
        if (card.Value.AsGenericField() is { } held)
            held.BoxedValue = value!;

        var grabbable = slot.AttachComponent<Grabbable>();
        grabbable.Scalable.Value = false;
        card.BindRelease();

        BuildCard(slot, name, value, type);
        return grabbable;
    }

    // The reference card's shape and backing, with the field chip's blue on the edge instead of the reference
    // purple, so a value card and a reference card tell apart at a glance. A color card adds a swatch.
    private static void BuildCard(Slot slot, string name, object? value, Type type)
    {
        var rect = slot.AttachComponent<RectTransform>();
        rect.AnchorMin.Value = new float2(0.5f, 0.5f);
        rect.AnchorMax.Value = new float2(0.5f, 0.5f);
        rect.OffsetMin.Value = new float2(-240f, -26f);
        rect.OffsetMax.Value = new float2(240f, 26f);

        var canvas = slot.AttachComponent<Canvas>();
        canvas.SizeCollider.Value = true;

        slot.GetOrAttachComponent<UITheme>();

        var ui = new UIBuilder(slot);
        InspectorUI.ApplyTheme(ui, slot);

        var backing = ui.Panel(InspectorUI.HeaderColor);
        InspectorUI.FillParent(backing.RectTransform!);

        var accent = ui.Panel(InspectorUI.AxisZColor);
        var accentRect = accent.RectTransform!;
        accentRect.AnchorMin.Value = float2.Zero;
        accentRect.AnchorMax.Value = new float2(0f, 1f);
        accentRect.OffsetMin.Value = float2.Zero;
        accentRect.OffsetMax.Value = new float2(8f, 0f);

        var swatch = ValueCardConversion.Swatch(value);
        float textRight = -8f;
        if (swatch is { } shown)
        {
            var chip = ui.Panel(shown);
            var chipRect = chip.RectTransform!;
            chipRect.AnchorMin.Value = new float2(1f, 0.5f);
            chipRect.AnchorMax.Value = new float2(1f, 0.5f);
            chipRect.OffsetMin.Value = new float2(-46f, -17f);
            chipRect.OffsetMax.Value = new float2(-12f, 17f);
            textRight = -54f;
        }

        string shownValue = ValueCardConversion.CardText(value, type);
        string label = string.IsNullOrEmpty(name)
            ? Literal(shownValue)
            : $"<color={ColorSwatchStore.FormatHex(InspectorUI.MutedColor)}>{Literal(name)}</color>  {Literal(shownValue)}";
        var text = ui.Text(label, InspectorUI.FontSize + 2f, InspectorUI.TextColor);
        var textRect = text.RectTransform!;
        textRect.AnchorMin.Value = float2.Zero;
        textRect.AnchorMax.Value = float2.One;
        textRect.OffsetMin.Value = new float2(16f, 0f);
        textRect.OffsetMax.Value = new float2(textRight, 0f);
        text.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
        text.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        text.WordWrap.Value = false;
        text.ClipToRect.Value = true;
        // Rich text for the muted name; the name and the value themselves are user content, taken literally.
        text.RichText.Value = true;
    }

    private static string Literal(string text) => text.Length == 0 ? "" : $"<noparse={text.Length}>{text}";
}
