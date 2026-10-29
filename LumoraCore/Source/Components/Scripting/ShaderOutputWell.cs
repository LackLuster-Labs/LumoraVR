// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Lumora.Core.Assets;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Components.UI;
using Lumora.Core.Localization;
using Lumora.Core.Math;
using Lumora.Core.Scripting;

namespace Lumora.Core.Components.Scripting;

// The material well on a Material Output's card: a sphere wearing the graph's own material in front of it, the
// materials the graph drives beside it, and what was last done here under it. Let go of something over it, or
// press it with something in the other hand: an orb, a material or a material's reference card is driven by the
// graph; an object, or a reference card to one, wears the graph's material. Grip the sphere for its reference card,
// or drag from it with the node tool onto anything to paint the surface it lands on. Lives on the local card; what
// it does is done as whoever does it. -xlinka
[ComponentCategory("Hidden")]
public sealed class ShaderOutputWell : Component, IProxyReceiver, IButtonPressReceiver
{
    private const float StatusSeconds = 8f;
    private const int RefreshFrames = 12;

    public ScriptGraphNode? Node { get; set; }
    public Slot? Sphere { get; set; }
    public MeshRenderer? SphereRenderer { get; set; }
    public Text? Empty { get; set; }
    public ReferenceProxySource? Pull { get; set; }
    public List<(Slot Row, Image Chip, Text Label)> Rows { get; } = new();
    public Text? More { get; set; }
    public Text? None { get; set; }
    public Text? Status { get; set; }
    public Slot? OfferRow { get; set; }
    public Text? OfferText { get; set; }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ScriptGraphNode, StatusLine> Statuses = new();

    private sealed class StatusLine
    {
        public string Text = string.Empty;
        public double Until;
        public bool Bad;
    }

    private readonly List<CustomShaderMaterial> _shown = new();
    private int _frames;
    private ShaderGraphSwapOffer? _offerShown;
    private string _statusShown = "\0";
    private IAssetProvider<MaterialAsset>? _sphereShown;
    private bool _drawn;
    private bool _listDrawn;

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        if (_drawn && ++_frames < RefreshFrames)
            return;
        _frames = 0;
        _drawn = true;
        Refresh();
    }

    public override void OnDestroy()
    {
        if (Sphere is { IsDestroyed: false } sphere)
            sphere.Destroy();
        base.OnDestroy();
    }

    // Says what was done or why it was not on this output's card, on this machine, for a few seconds.
    public static void Say(ScriptGraphNode node, LocaleText message, bool bad)
    {
        if (node.World == null)
            return;
        var line = Statuses.GetOrCreateValue(node);
        line.Text = message.Resolve();
        line.Until = node.World.Time.TotalTime + StatusSeconds;
        line.Bad = bad;
    }

    public void Refresh()
    {
        var node = Node;
        if (node == null || node.IsDestroyed || node.World == null)
            return;
        var space = node.Space;
        var own = ShaderGraphOutput.MaterialOf(space);

        if (!ReferenceEquals(own, _sphereShown))
        {
            _sphereShown = own;
            if (SphereRenderer is { IsDestroyed: false } renderer)
            {
                if (renderer.Materials.Count == 0)
                    renderer.Materials.Add(own!);
                else
                    renderer.Materials.GetElement(0).Target = own!;
            }
            if (Sphere is { IsDestroyed: false } sphere)
                sphere.ActiveSelf.Value = own != null;
            if (Empty is { IsDestroyed: false } empty)
                empty.Slot.ActiveSelf.Value = own == null;
            if (Pull is { IsDestroyed: false } pull)
                pull.Target.Target = own!;
        }

        var driven = ShaderGraphOutput.Driven(space);
        if (!_listDrawn || !Same(driven))
        {
            _listDrawn = true;
            _shown.Clear();
            _shown.AddRange(driven);
            for (int i = 0; i < Rows.Count; i++)
            {
                var (row, chip, label) = Rows[i];
                bool shown = i < driven.Count && (i < Rows.Count - 1 || driven.Count == Rows.Count);
                row.ActiveSelf.Value = shown;
                if (!shown)
                    continue;
                var material = driven[i];
                label.Content.Value = Shorten(material.Slot?.SlotName.Value ?? "material", 22)
                    + (ReferenceEquals(material, own) ? "  " + OwnLabel.Resolve() : string.Empty);
            }
            if (More is { IsDestroyed: false } more)
            {
                more.Slot.ActiveSelf.Value = driven.Count > Rows.Count;
                more.Content.Value = LocaleText.Keyed("Loom.Shader.More", "+{0} more", driven.Count - (Rows.Count - 1)).Resolve();
            }
            if (None is { IsDestroyed: false } none)
                none.Slot.ActiveSelf.Value = driven.Count == 0;
        }

        // A row's chip is its material's main colour, which its properties only have once the host made them.
        for (int i = 0; i < Rows.Count && i < _shown.Count; i++)
        {
            var chip = Rows[i].Chip;
            var tint = ShaderPropertyFields.MainColor(_shown[i]) is { } main ? ShaderPropertyFields.Of<colorHDR>(main).Value : (colorHDR?)null;
            var shown = tint is { } c ? new color(c.r, c.g, c.b, 1f) : DashTheme.TextMuted;
            if (chip.Tint.Value != shown)
                chip.Tint.Value = shown;
        }

        var offer = ShaderGraphOutput.OfferOf(node);
        if (!ReferenceEquals(offer, _offerShown))
        {
            _offerShown = offer;
            if (OfferRow is { IsDestroyed: false } offerRow)
                offerRow.ActiveSelf.Value = offer != null;
            if (OfferText is { IsDestroyed: false } offerText && offer != null)
                offerText.Content.Value = LocaleText.Keyed("Loom.Shader.OfferLine", "Swap the copy in on {0}?", offer.ObjectList()).Resolve();
        }

        string status = string.Empty;
        bool bad = false;
        if (offer == null && Statuses.TryGetValue(node, out var line) && node.World.Time.TotalTime < line.Until)
        {
            status = line.Text;
            bad = line.Bad;
        }
        if (status != _statusShown && Status is { IsDestroyed: false } text)
        {
            _statusShown = status;
            text.Slot.ActiveSelf.Value = status.Length > 0;
            text.Content.Value = status;
            text.Color.Value = bad ? DashTheme.Warning : DashTheme.TextDim;
        }
    }

    private bool Same(List<CustomShaderMaterial> driven)
    {
        if (driven.Count != _shown.Count)
            return false;
        for (int i = 0; i < driven.Count; i++)
        {
            if (!ReferenceEquals(driven[i], _shown[i]))
                return false;
        }
        return true;
    }

    public static readonly LocaleText OwnLabel = LocaleText.Keyed("Loom.Shader.Own", "(own)");
    public static readonly LocaleText NoneLabel = LocaleText.Keyed("Loom.Shader.DrivesNone", "drives no material yet");

    // The row's material as an orb beside the card.
    public void SpawnRow(int index)
    {
        if (Node is not { IsDestroyed: false } node || index < 0 || index >= _shown.Count)
            return;
        var orb = ShaderGraphOutput.SpawnOrb(node, _shown[index], out var message);
        Say(node, message, orb == null);
        _frames = RefreshFrames;
    }

    public void SpawnOwn()
    {
        if (Node is not { IsDestroyed: false } node)
            return;
        var orb = ShaderGraphOutput.SpawnOrb(node, null, out var message);
        Say(node, message, orb == null);
        _frames = RefreshFrames;
    }

    public void TakeOffer(bool swap)
    {
        if (Node is not { IsDestroyed: false } node || ShaderGraphOutput.OfferOf(node) is not { } offer)
            return;
        ShaderGraphOutput.ClearOffer(node);
        if (swap)
        {
            int swapped = offer.Swap();
            Say(node, swapped > 0
                ? LocaleText.Keyed("Loom.Shader.Swapped", "swapped in on {0} surfaces", swapped)
                : LocaleText.Keyed("Loom.Shader.SwapRefused", "nothing could be swapped: you may not change those"), swapped == 0);
        }
        else
        {
            Say(node, LocaleText.Keyed("Loom.Shader.Kept", "kept as an orb beside the card"), false);
        }
        _frames = RefreshFrames;
    }

    public bool TryReceiveProxy(IReadOnlyList<IGrabbable> held, Grabber grabber)
    {
        if (Node is not { IsDestroyed: false } node)
            return false;
        for (int i = 0; i < held.Count; i++)
        {
            if (held[i] is not Component { IsDestroyed: false } component || component.Slot is not { IsDestroyed: false } slot)
                continue;
            var card = slot.GetComponent<ReferenceProxy>();
            bool done;
            LocaleText message;
            if (card != null)
            {
                if (!Take(node, card.Target.Target, out done, out message))
                    continue;
                if (done)
                    card.Consume(held[i], grabber);
            }
            else if (IsOrb(slot) && MaterialOrb.CarriedBy(slot) is { } carried)
            {
                done = ShaderGraphOutput.Drive(node, carried, null, out message);
            }
            else
            {
                done = ShaderGraphOutput.ApplyTo(node, slot, out message) > 0;
            }
            Say(node, message, !done);
            _frames = RefreshFrames;
            return true;
        }
        return false;
    }

    // What a reference card stands for here: a material or a material field to drive, an object or a renderer to paint.
    private static bool Take(ScriptGraphNode node, IWorldElement? target, out bool done, out LocaleText message)
    {
        done = false;
        message = default;
        switch (target)
        {
            case null:
                return false;
            case IAssetProvider<MaterialAsset> material when !material.IsDestroyed:
                done = ShaderGraphOutput.Drive(node, material, null, out message);
                return true;
            case AssetRef<MaterialAsset> { Target: { IsDestroyed: false } worn } field:
                done = ShaderGraphOutput.Drive(node, worn, field, out message);
                return true;
            case Slot slot when !slot.IsDestroyed:
                if (IsOrb(slot) && MaterialOrb.CarriedBy(slot) is { } carried)
                    done = ShaderGraphOutput.Drive(node, carried, null, out message);
                else
                    done = ShaderGraphOutput.ApplyTo(node, slot, out message) > 0;
                return true;
            case MeshRenderer or SkinnedMeshRenderer when target is Component { Slot: { } owner }:
                done = ShaderGraphOutput.ApplyTo(node, owner, out message) > 0;
                return true;
        }
        return false;
    }

    private static bool IsOrb(Slot slot) => slot.GetComponent<MaterialOrb>() != null || slot.GetComponent<ShaderGraphSource>() != null;

    // A press from a hand that took hold of what it carries some way other than its beam.
    public void OnButtonPressed(Button button, in UIInteractionContext context) => HeldTexture.OfferHeldBy(context.Actor, this);

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 3)] + "...";
}
