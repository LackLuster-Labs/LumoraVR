// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.
using Lumora.Core.Math;

namespace Lumora.Core.Components;

// One inspector tab's parked view. The panel's own Root/Selected/expansion fields ARE the live tab; this
// only holds a tab's state while another one is showing.
[HideInInspector]
[ComponentCategory("Hidden")]
public sealed class InspectorTab : Component
{
    public readonly SyncRef<Slot> Root = new();
    public readonly SyncRef<Slot> Selected = new();
    public readonly SyncFieldList<ulong> ExpandedSlots = new();
    public readonly SyncFieldList<ulong> ExpandedComponents = new();
    public readonly Sync<float2> TreeScroll = new();
    public readonly Sync<float2> ComponentScroll = new();
    public readonly Sync<float> HierarchyWidth = new(SceneInspectorPanel.DefaultHierarchyWidth);

    // A parked view, the same kind of state as the panel's live one.
    public override void OnAwake()
    {
        base.OnAwake();
        Root.MarkUiState();
        Selected.MarkUiState();
        ExpandedSlots.MarkUiState();
        ExpandedComponents.MarkUiState();
        TreeScroll.MarkUiState();
        ComponentScroll.MarkUiState();
        HierarchyWidth.MarkUiState();
    }
}
