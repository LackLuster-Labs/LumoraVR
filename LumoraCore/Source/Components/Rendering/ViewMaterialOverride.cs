// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Assets;

namespace Lumora.Core.Components;

// Draws a renderer with other materials in some views. A copy of the renderer, built on each peer for itself and
// never saved, wears the renderer's materials with Overrides put in by index, and the two split the views
// between them on render bands: for OtherViews the copy draws for mirrors, portals and cameras while the real
// renderer keeps the eyes; for OwnEyes the other way round, and only while this sits under the person looking.
// Water that refracts for the eyes can show a plain surface in a mirror that way. One override per renderer and
// scope: two of them would draw two copies.
//
// Neither copy casts a shadow into a view it is not drawn in, so a scene lit through the eyes is lit by the
// renderer the eyes see. -xlinka
[ComponentCategory("Rendering")]
public class ViewMaterialOverride : Component
{
    public readonly Sync<ViewMaterialScope> View;
    public readonly SyncRef<MeshRenderer> Renderer;
    public readonly SyncList<ViewMaterialSlot> Overrides;

    private MeshRenderer? _held;
    private Slot? _copySlot;
    private MeshRenderer? _copy;

    public ViewMaterialOverride()
    {
        View = new Sync<ViewMaterialScope>(this, ViewMaterialScope.OtherViews);
        Renderer = new SyncRef<MeshRenderer>(this);
        Overrides = new SyncList<ViewMaterialSlot>();
    }

    // The renderer's mesh and materials can change under it without telling this, so the copy is checked each
    // frame; it touches nothing while they hold still.
    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        Refresh();
    }

    public override void OnDisabled()
    {
        base.OnDisabled();
        Release();
    }

    public override void OnDestroy()
    {
        Release();
        base.OnDestroy();
    }

    private void Refresh()
    {
        var renderer = Enabled.Value && !IsDestroyed ? Renderer.Target : null;
        if (renderer is { IsDestroyed: true } || renderer?.Slot == null)
            renderer = null;
        bool active = renderer != null
            && (View.Value == ViewMaterialScope.OtherViews || (Slot?.ActiveUser != null && Slot.ActiveUser == World?.LocalUser));
        if (!active)
        {
            Release();
            return;
        }

        if (!ReferenceEquals(_held, renderer))
        {
            Release();
            _held = renderer;
        }

        if (_copy == null || _copy.IsDestroyed || _copySlot == null || _copySlot.IsDestroyed)
        {
            _copySlot = renderer!.Slot.AddLocalSlot("View Materials");
            _copy = _copySlot.AttachComponent<MeshRenderer>();
        }

        CopyLook(renderer!, _copy);

        bool eyes = View.Value == ViewMaterialScope.OwnEyes;
        renderer!.SetViewLayers(eyes ? RenderLayerOverride.LocalHeadLayer : RenderLayerOverride.EyesOnlyLayer);
        _copy.SetViewLayers(eyes ? RenderLayerOverride.EyesOnlyLayer : RenderLayerOverride.LocalHeadLayer);
    }

    private void CopyLook(MeshRenderer from, MeshRenderer to)
    {
        if (!ReferenceEquals(to.Mesh.Target, from.Mesh.Target))
            to.Mesh.Target = from.Mesh.Target;
        if (to.ShadowCastMode.Value != from.ShadowCastMode.Value)
            to.ShadowCastMode.Value = from.ShadowCastMode.Value;
        if (to.SortingOrder.Value != from.SortingOrder.Value)
            to.SortingOrder.Value = from.SortingOrder.Value;
        if (to.Enabled.Value != from.Enabled.Value)
            to.Enabled.Value = from.Enabled.Value;

        int count = from.Materials.Count;
        while (to.Materials.Count > count)
            to.Materials.RemoveAt(to.Materials.Count - 1);
        while (to.Materials.Count < count)
            to.Materials.Add(null!);
        for (int i = 0; i < count; i++)
        {
            var wanted = OverrideFor(i) ?? from.Materials[i];
            if (!ReferenceEquals(to.Materials[i], wanted))
                to.Materials[i] = wanted;
        }
    }

    private IAssetProvider<MaterialAsset>? OverrideFor(int index)
    {
        IAssetProvider<MaterialAsset>? found = null;
        foreach (var slot in Overrides)
        {
            if (slot.Index.Value == index && slot.Material.Target != null)
                found = slot.Material.Target;
        }
        return found;
    }

    private void Release()
    {
        if (_held is { IsDestroyed: false })
            _held.SetViewLayers(0);
        _held = null;
        if (_copySlot is { IsDestroyed: false })
            _copySlot.Destroy();
        _copySlot = null;
        _copy = null;
    }
}
