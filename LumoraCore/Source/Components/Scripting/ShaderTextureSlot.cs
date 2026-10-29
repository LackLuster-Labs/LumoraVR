// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using Helio.UI;
using Lumora.Core.Assets;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Components.UI;
using Lumora.Core.Scripting;

namespace Lumora.Core.Components.Scripting;

// A shader graph card's texture slot: the texture a node reads, as a picture and a name. It is the
// material's property for that node, the same one the material panel's texture row shows, so a texture
// given here is the material's for everyone. Hold a texture's card from an inspector, or the textured object
// itself, and press the slot or let go over it. The picture follows the property whoever changes it. Lives
// on the local card. -xlinka
[ComponentCategory("Hidden")]
public sealed class ShaderTextureSlot : Component, IProxyReceiver, IButtonPressReceiver
{
    // The graph the card is in. A loose graph gets its material later, from its output's well, and the slot finds
    // it there without the card being made again.
    public ScriptGraphSpace? Space { get; set; }
    public string Uniform { get; set; } = string.Empty;
    public RawImage? Picture { get; set; }
    public Text? Label { get; set; }

    private IAssetProvider<TextureAsset>? _shown;
    private ShaderGraphSource? _shownSource;
    private bool _drawn;

    public ShaderGraphSource? Source => Space?.ShaderSource.Target is { IsDestroyed: false } source ? source : null;

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        var source = Source;
        var current = source?.TextureFor(Uniform);
        if (_drawn && ReferenceEquals(current, _shown) && ReferenceEquals(source, _shownSource))
            return;
        _drawn = true;
        _shown = current;
        _shownSource = source;
        if (Picture is { IsDestroyed: false } picture)
        {
            // Empty shows the checker behind it rather than the white an empty picture samples.
            picture.Texture.Target = current!;
            picture.Enabled.Value = current != null;
        }
        if (Label is { IsDestroyed: false } label)
        {
            label.Content.Value = current is Component component ? Shorten(component.Slot?.SlotName.Value ?? "texture", 18)
                : Source == null ? "no material" : "drop texture";
            label.Color.Value = current != null ? DashTheme.Text : DashTheme.TextMuted;
        }
    }

    public bool TryReceiveProxy(IReadOnlyList<IGrabbable> held, Grabber grabber)
    {
        var source = Source;
        if (source == null || source.IsDestroyed || string.IsNullOrEmpty(Uniform))
            return false;
        return HeldTexture.Offer(held, grabber, texture =>
        {
            var before = source.Material.Target?.Parameters.FirstOrDefault(p => p.Name.Value == Uniform)?.Texture.Value ?? default;
            ShaderUniformParam? param;
            try
            {
                param = source.AssignTexture(Uniform, texture);
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            if (param == null)
                return false;
            InspectorUndo.Record(this, new FieldEditUndoBatch(param.Texture, before, param.Texture.Value, ScriptGraphEditing.ValueStep));
            return true;
        });
    }

    // The slot's own press, from a hand that took hold of what it carries some way other than its beam.
    public void OnButtonPressed(Button button, in UIInteractionContext context) => HeldTexture.OfferHeldBy(context.Actor, this);

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 3)] + "...";
}
