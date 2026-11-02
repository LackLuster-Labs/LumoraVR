// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Helio.UI.Layout;
using Lumora.Core;
using Lumora.Core.Components.UI;
using Lumora.Core.Networking.Sync;
using Lumora.Core.Math;

namespace Lumora.Core.Components;

// Rows carry their action in a synced argument (never closures) so presses work for every user.
// With SwapTarget set it browses in SWAP mode: category tree is replaced by the flat set of types
// that can stand in for the target, and picking one replaces the component instead of adding a second.
[ComponentCategory("Utility/Inspectors")]
public class ComponentSelectorPanel : Component, IInspectorActionHandler
{
    public readonly SyncRef<Slot> TargetSlot;
    public readonly SyncRef<SceneInspectorPanel> Owner;
    public readonly Sync<string> CategoryPath;
    public readonly Sync<string> Query;

    // set to browse replacements for this component instead of attaching a new one
    public readonly SyncRef<Component> SwapTarget;

    // Off for a selector standing on its own, which keeps taking components until it is closed.
    public readonly Sync<bool> CloseAfterAttach;

    private readonly SyncRef<Slot> _listContent;
    private readonly SyncRef<TextInput> _searchInput;
    private readonly Sync<string> _builtPath;
    private readonly Sync<string> _builtQuery;

    // Assembly-qualified name of the open generic currently being picked for, empty when browsing
    // normally. A generic component is ONE row; choosing it drills into its type arguments rather than
    // pouring every closed form into the tree. -xlinka
    public readonly Sync<string> GenericPath;
    private readonly Sync<string> _builtGeneric;

    private const float RowHeight = InspectorUI.RowHeight;
    private const int MaxSearchResults = 120;

    public ComponentSelectorPanel()
    {
        TargetSlot = new SyncRef<Slot>(this);
        Owner = new SyncRef<SceneInspectorPanel>(this);
        CategoryPath = new Sync<string>(this, "");
        Query = new Sync<string>(this, "");
        _listContent = new SyncRef<Slot>(this);
        _searchInput = new SyncRef<TextInput>(this);
        _builtPath = new Sync<string>(this, "unbuilt");
        _builtQuery = new Sync<string>(this, "unbuilt");
        SwapTarget = new SyncRef<Component>(this);
        CloseAfterAttach = new Sync<bool>(this, false);
        GenericPath = new Sync<string>(this, "");
        _builtGeneric = new Sync<string>(this, "unbuilt");
    }

    private bool IsSwapMode => SwapTarget.Target is { IsDestroyed: false };

    public static ComponentSelectorPanel Spawn(SceneInspectorPanel owner, Slot target, string category = "")
    {
        var selector = Create(owner.Slot);
        selector.Owner.Target = owner;
        selector.TargetSlot.Target = target;
        if (!string.IsNullOrEmpty(category))
            selector.CategoryPath.Value = category;
        return selector;
    }

    // anchor is only used to place the picker beside the panel the request came from
    public static ComponentSelectorPanel SpawnForSwap(Slot anchor, Component swapTarget, SceneInspectorPanel? owner)
    {
        var selector = Create(anchor);
        if (owner != null)
            selector.Owner.Target = owner;
        selector.TargetSlot.Target = swapTarget.Slot;
        selector.SwapTarget.Target = swapTarget;
        var shell = selector.Slot.GetComponent<PanelShell>();
        if (shell != null)
            shell.Title.Value = "Swap " + SyncMemberEditorBuilder.NiceTypeName(swapTarget.GetType());
        return selector;
    }

    private static ComponentSelectorPanel Create(Slot anchor)
    {
        var panelSlot = anchor.Parent?.AddSlot("Component Selector") ?? anchor.World.RootSlot.AddSlot("Component Selector");
        panelSlot.Persistent.Value = false;
        panelSlot.Tag.Value = "Developer";
        // Half the inspector, half the picker and a gap, in the anchor's own units so a resized panel is cleared.
        panelSlot.GlobalPosition = anchor.GlobalPosition + anchor.GlobalRotation * new float3(1100f * anchor.GlobalScale.x, 0f, 0f);
        panelSlot.GlobalRotation = anchor.GlobalRotation;
        panelSlot.LocalScale.Value = anchor.LocalScale.Value;
        return panelSlot.AttachComponent<ComponentSelectorPanel>();
    }

    public override void OnAttach()
    {
        base.OnAttach();
        var theme = Slot.GetOrAttachComponent<UITheme>();

        var shell = Slot.GetOrAttachComponent<PanelShell>();
        shell.Title.Value = "Attach Component";
        shell.Size.Value = new float2(620f, 1100f);
        theme.ApplyTo(shell);
        shell.RebuildContent(BuildLayout);
    }

    private void BuildLayout(UIBuilder ui)
    {
        var page = ui.Current;
        InspectorUI.ApplyTheme(ui, Slot);

        var vLayout = page.AttachComponent<VerticalLayout>();
        vLayout.Spacing.Value = 6f;
        vLayout.PaddingLeft.Value = 6f;
        vLayout.PaddingRight.Value = 6f;
        vLayout.PaddingTop.Value = 6f;
        vLayout.PaddingBottom.Value = 6f;
        vLayout.ForceExpandWidth.Value = true;
        vLayout.ForceExpandHeight.Value = false;

        InspectorUI.FixedRow(page, "SearchRow", 38f, out var searchUi, Slot);
        searchUi.PushStyle();
        searchUi.FlexibleWidth(1f);
        var input = InspectorUI.CreateTextInput(searchUi, "Search");
        input.Placeholder.Value = "Search components...";
        input.SetChangeAction(OnSearchChanged);
        input.SetSubmitAction(OnSearchChanged);
        _searchInput.Target = input;
        searchUi.PopStyle();

        var host = page.AddSlot("List");
        host.AttachComponent<RectTransform>();
        var hostLE = host.AttachComponent<LayoutElement>();
        hostLE.FlexibleHeight.Value = 1f;
        hostLE.MinHeight.Value = 200f;

        var scrollUi = new UIBuilder(host);
        InspectorUI.ApplyTheme(scrollUi, Slot);
        var scroll = scrollUi.ScrollRect(out var content, null, InspectorUI.PaneColor);
        scroll.RowsActOnRelease.Value = true;
        InspectorUI.FillParent(scroll.Slot.GetComponent<RectTransform>()!);

        // Fixed-height rows stacked from the top; never stretched to share the viewport.
        var contentLayout = content.Slot.AttachComponent<VerticalLayout>();
        contentLayout.Spacing.Value = 2f;
        contentLayout.ForceExpandWidth.Value = true;
        contentLayout.ForceExpandHeight.Value = false;
        _listContent.Target = content.Slot;
    }

    public override void OnUpdate(float delta)
    {
        // Close the husk when the slot this selector would attach to dies, or the inspector it belongs to.
        if (World?.IsAuthority != true)
            return;
        if (TargetSlot.RawTarget is { IsDestroyed: true } || SwapTarget.RawTarget is { IsDestroyed: true }
            || Owner.RawTarget is { IsDestroyed: true })
            Slot.Destroy();
    }

    public override void OnChanges()
    {
        base.OnChanges();
        if (World?.IsAuthority != true)
            return;
        if (_builtPath.Value == CategoryPath.Value && _builtQuery.Value == Query.Value
            && _builtGeneric.Value == GenericPath.Value)
            return;
        _builtPath.Value = CategoryPath.Value;
        _builtQuery.Value = Query.Value;
        _builtGeneric.Value = GenericPath.Value;
        RebuildList();
    }

    // every keystroke updates the synced query, which rebuilds the list
    [SyncMethod]
    public void OnSearchChanged(TextInput input, string text)
    {
        Query.Value = text ?? "";
    }

    private void RebuildList()
    {
        var container = _listContent.Target;
        if (container == null || container.IsDestroyed)
            return;
        container.DestroyChildren();

        string query = (Query.Value ?? "").Trim();

        if (IsSwapMode)
        {
            BuildSwapCandidates(container, query);
            return;
        }

        // Picking a type argument for a generic. Ahead of search on purpose: while the picker is open a
        // typed query filters the ARGUMENTS, which is what someone hunting "ShadowType" among a hundred
        // of them actually wants.
        if (GenericPath.Value.Length > 0)
        {
            BuildGenericArguments(container, query);
            return;
        }

        if (query.Length > 0)
        {
            BuildSearchResults(container, query);
            return;
        }

        var node = ComponentLibrary.GetNode(CategoryPath.Value) ?? ComponentLibrary.Root;

        if (!string.IsNullOrEmpty(CategoryPath.Value))
            AddRow(container, "< Back", "back:", InspectorUI.MutedColor);

        foreach (var sub in node.Subcategories.Values)
            AddRow(container, sub.Name + " >", "cat:" + sub.Path, InspectorUI.AccentColor);

        foreach (var type in node.Types)
        {
            var label = ComponentLibrary.DisplayName(type);
            if (ComponentExecutionPolicy.IsUserspaceOnly(type))
                label += " (Userspace only)";
            if (!ComponentExecutionPolicy.CanAttach(type, TargetSlot.Target?.World ?? World))
                AddLabelRow(container, label, InspectorUI.MutedColor);
            else
                AddRow(container, label, "type:" + type.AssemblyQualifiedName, InspectorUI.TextColor);
        }
    }

    // The type-argument page for one open generic.
    //
    // The browser used to list every closed form as its own entry, so twenty-odd generic components
    // spent 2200 rows of the tree between them and adding one type to the table cost another forty-odd.
    // One row per component and a second step to choose the argument is both smaller to read and free
    // to extend. -xlinka
    private void BuildGenericArguments(Slot container, string query)
    {
        var definition = Type.GetType(GenericPath.Value);
        if (definition == null || !definition.IsGenericTypeDefinition)
        {
            GenericPath.Value = "";
            return;
        }

        AddRow(container, "< Back", "generic:", InspectorUI.MutedColor);
        AddLabelRow(container, ComponentLibrary.DisplayName(definition), InspectorUI.AccentColor);

        int shown = 0;
        foreach (var closed in GenericComponentTypes.Enumerate(definition))
        {
            var argument = closed.GetGenericArguments()[0];
            var label = ComponentLibrary.DisplayName(argument);
            if (query.Length > 0 && !label.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;
            shown++;
            AddRow(container, label, "type:" + closed.AssemblyQualifiedName, InspectorUI.TextColor);
        }

        if (shown == 0)
            AddLabelRow(container, query.Length > 0 ? "No matching types" : "No types available",
                InspectorUI.MutedColor);
    }

    // Swap mode: one flat list of the types that can replace the target. A family is small enough
    // that making the user walk the category tree to find its siblings is pure friction.
    private void BuildSwapCandidates(Slot container, string query)
    {
        var source = SwapTarget.Target;
        if (source == null)
            return;
        var sourceType = source.GetType();

        var candidates = new List<Type>();
        CollectSwapCandidates(ComponentLibrary.Root, sourceType, query, candidates);
        candidates.Sort((a, b) => string.Compare(ComponentLibrary.DisplayName(a), ComponentLibrary.DisplayName(b),
            StringComparison.OrdinalIgnoreCase));

        if (candidates.Count == 0)
        {
            AddLabelRow(container, query.Length > 0 ? "No matches" : "Nothing can replace this type",
                InspectorUI.MutedColor);
            return;
        }

        foreach (var type in candidates)
            AddRow(container, ComponentLibrary.DisplayName(type), "type:" + type.AssemblyQualifiedName, InspectorUI.TextColor);
    }

    private static void CollectSwapCandidates(ComponentLibrary.CategoryNode node, Type sourceType, string query,
        List<Type> output)
    {
        foreach (var type in node.Types)
        {
            // The tree now holds open generics, and you cannot swap a component FOR an open generic.
            // Expand them here so swap keeps offering concrete siblings; this is the one place that
            // still wants every closed form. -xlinka
            if (type.IsGenericTypeDefinition)
            {
                foreach (var closed in GenericComponentTypes.Enumerate(type))
                    Consider(closed, sourceType, query, output);
                continue;
            }
            Consider(type, sourceType, query, output);
        }
        foreach (var sub in node.Subcategories.Values)
            CollectSwapCandidates(sub, sourceType, query, output);
    }

    private static void Consider(Type type, Type sourceType, string query, List<Type> output)
    {
        if (!ComponentTypeSwapUndoBatch.IsCompatible(sourceType, type))
            return;
        if (query.Length > 0 && !ComponentLibrary.DisplayName(type).Contains(query, StringComparison.OrdinalIgnoreCase))
            return;
        if (!output.Contains(type))
            output.Add(type);
    }

    // Flattened matches across every category: type name OR category path substring, case-insensitive.
    private void BuildSearchResults(Slot container, string query)
    {
        int shown = 0;
        int total = 0;
        CollectMatches(ComponentLibrary.Root, query, container, ref shown, ref total);
        if (total == 0)
            AddLabelRow(container, "No matches", InspectorUI.MutedColor);
        else if (total > shown)
            AddLabelRow(container, $"{total - shown} more, refine the search", InspectorUI.MutedColor);
    }

    private void CollectMatches(ComponentLibrary.CategoryNode node, string query, Slot container, ref int shown, ref int total)
    {
        bool pathMatches = node.Path.Contains(query, StringComparison.OrdinalIgnoreCase);
        foreach (var type in node.Types)
        {
            if (!pathMatches && !ComponentLibrary.DisplayName(type).Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;
            total++;
            if (shown >= MaxSearchResults)
                continue;
            shown++;
            AddSearchRow(container, type, node.Path);
        }
        foreach (var sub in node.Subcategories.Values)
            CollectMatches(sub, query, container, ref shown, ref total);
    }

    // Search hit: type name button plus a muted category path, one 30px row.
    private void AddSearchRow(Slot container, Type type, string categoryPath)
    {
        var label = ComponentLibrary.DisplayName(type);
        if (ComponentExecutionPolicy.IsUserspaceOnly(type))
            label += " (Userspace only)";
        if (!ComponentExecutionPolicy.CanAttach(type, TargetSlot.Target?.World ?? World))
        {
            AddLabelRow(container, label, InspectorUI.MutedColor);
            return;
        }
        InspectorUI.FixedRow(container, label, RowHeight, out var ui, Slot);
        ui.PushStyle();
        ui.FlexibleWidth(1f);
        var button = InspectorUI.RelayButton(ui, this, "type:" + type.AssemblyQualifiedName, label, 0f);
        AlignButtonTextLeft(button);
        ui.PopStyle();

        ui.PushStyle();
        ui.MinWidth(200f);
        ui.PreferredWidth(220f);
        ui.FlexibleWidth(0f);
        var path = ui.Text(categoryPath, InspectorUI.FontSize - 2f, InspectorUI.MutedColor);
        InspectorUI.FillParent(path.RectTransform!);
        path.HorizontalAlignment.Value = TextHorizontalAlignment.Right;
        path.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        path.WordWrap.Value = false;
        path.ClipToRect.Value = true;
        path.Ellipsis.Value = true;
        ui.PopStyle();
    }

    private void AddRow(Slot container, string label, string action, color tint)
    {
        InspectorUI.FixedRow(container, label, RowHeight, out var ui, Slot);
        ui.PushStyle();
        ui.TextColor(tint);
        var button = InspectorUI.RelayButton(ui, this, action, label, 0f);
        AlignButtonTextLeft(button);
        ui.PopStyle();
    }

    private void AddLabelRow(Slot container, string label, color tint)
    {
        InspectorUI.FixedRow(container, "Note", RowHeight, out var ui, Slot);
        ui.PushStyle();
        ui.FlexibleWidth(1f);
        var text = ui.Text(label, InspectorUI.FontSize, tint);
        InspectorUI.FillParent(text.RectTransform!);
        text.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
        text.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        ui.PopStyle();
    }

    // Browser rows read better ragged-left than centered.
    private static void AlignButtonTextLeft(Button button)
    {
        var text = button.Slot.GetComponentInChildren<Text>();
        if (text == null)
            return;
        text.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
        text.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        text.WordWrap.Value = false;
        text.ClipToRect.Value = true;
        text.Ellipsis.Value = true;
        var rect = text.RectTransform;
        if (rect != null)
        {
            rect.OffsetMin.Value = new float2(10f, 0f);
            rect.OffsetMax.Value = new float2(-10f, 0f);
        }
    }

    // action strings: "back:", "cat:Path", "type:AssemblyQualifiedName"
    public void HandleInspectorAction(string action)
    {
        if (action.StartsWith("back:", StringComparison.Ordinal))
        {
            var path = CategoryPath.Value ?? "";
            int slash = path.LastIndexOf('/');
            CategoryPath.Value = slash > 0 ? path[..slash] : "";
            GenericPath.Value = "";
            return;
        }
        if (action.StartsWith("cat:", StringComparison.Ordinal))
        {
            CategoryPath.Value = action[4..];
            GenericPath.Value = "";
            return;
        }
        if (action.StartsWith("generic:", StringComparison.Ordinal))
        {
            // Empty payload is the back row out of the picker.
            GenericPath.Value = action[8..];
            return;
        }
        if (action.StartsWith("type:", StringComparison.Ordinal))
        {
            var type = Type.GetType(action[5..]);

            // An open generic cannot be attached, so picking one opens its argument list instead of
            // doing nothing. Swap mode never reaches here with an open generic: its candidate list is
            // built from closed forms only.
            if (type != null && type.IsGenericTypeDefinition && !IsSwapMode)
            {
                // The search box has to be cleared as well as the query, or it keeps displaying the term
                // that found the component while the list below it is filtered by nothing. The picker
                // filters on the same field, so whatever is typed next narrows the ARGUMENTS.
                Query.Value = "";
                var input = _searchInput.Target;
                if (input != null && !input.IsDestroyed)
                    input.Text.Value = "";
                GenericPath.Value = type.AssemblyQualifiedName ?? "";
                return;
            }

            var swapTarget = SwapTarget.Target;
            if (type != null && !ComponentExecutionPolicy.CanAttach(type, swapTarget?.World ?? TargetSlot.Target?.World ?? World))
                return;
            if (swapTarget != null && !swapTarget.IsDestroyed
                && !WorldConfigLock.IsConfigComponentType(type)
                && !WorldConfigLock.IsConfigComponent(swapTarget))
            {
                var batch = ComponentTypeSwapUndoBatch.Swap(swapTarget, type);
                if (batch != null)
                    InspectorUndo.Record(this, batch);
                Owner.Target?.MarkListsDirty();
                Slot.Destroy();
                return;
            }

            var target = TargetSlot.Target;
            // The browser is built from ComponentLibrary, which skips the Hidden category these two sit
            // in, so this is the backstop for a relay argument that named the type directly. -xlinka
            if (target != null && type != null && typeof(Component).IsAssignableFrom(type)
                && !WorldConfigLock.IsConfigComponentType(type))
            {
                var attached = target.AttachComponent(type);
                ComponentUndo.RecordAttach(this, attached);
                var owner = Owner.Target;
                owner?.MarkListsDirty();
                // Keep browsing the same category after an attachment unless the author opted into
                // closing this selector. The close button is always available. -xlinka
                bool close = CloseAfterAttach.Value;
                if (close)
                {
                    owner?.RememberSelectorCategory(CategoryPath.Value);
                    ClosePanel();
                }
                ShowAttached(owner, attached);
                if (IsDestroyed || Slot == null || Slot.IsDestroyed)
                    return;
            }
            // A picked generic argument goes back to the list the generic was found in. -xlinka
            if (GenericPath.Value.Length > 0)
                GenericPath.Value = "";
        }
    }

    // Anyone can press the rows, but only someone allowed to remove the panel closes it. Anyone else is
    // refused and it stays up for whoever opened it.
    private void ClosePanel()
    {
        if (IsDestroyed || Slot == null || Slot.IsDestroyed)
            return;
        try
        {
            var shell = Slot.GetComponent<PanelShell>();
            if (shell != null)
                shell.Close();
            else
                Slot.Destroy();
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // The new component's section opens in the inspector, so it shows straight away. A presser that
    // inspector refuses goes without.
    private static void ShowAttached(SceneInspectorPanel? owner, Component attached)
    {
        if (owner == null || owner.IsDestroyed || attached.IsDestroyed)
            return;
        ulong id = attached.ReferenceID.RawValue;
        try
        {
            if (!SceneInspectorPanel.IsExpanded(owner.ExpandedComponents, id))
                owner.ExpandedComponents.Add(id);
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
