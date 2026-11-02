// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Reflection;
using Helio.UI;
using Lumora.Core;
using Lumora.Core.Components.UI;
using Lumora.Core.Networking.Sync;
using Lumora.Core.Math;

namespace Lumora.Core.Components;

// dispatch is member kind -> value type, with unknown structs decomposed field-by-field into
// path-addressed leaf editors (float3 becomes three floats, and so on)
public static class SyncMemberEditorBuilder
{
    private const int MaxStructDepth = 2;

    // collections get a collapsible section with per-element rows instead of a single row
    public static void Build(ISyncMember member, string name, FieldInfo? fieldInfo, Slot container, Slot themeContext)
    {
        if (member is ISyncList || ListMemberEditor.IsValueCollection(member))
        {
            ListMemberEditor.Build(member, name, container, themeContext);
            return;
        }

        // Texture refs get a taller row so the preview is a real THUMBNAIL block with info lines.
        float rowHeight = IsTextureRef(member) ? 96f : InspectorUI.RowHeight;
        var row = InspectorUI.FixedRow(container, name, rowHeight, out var ui, themeContext);

        // The chip pulls a card for the member itself, so drive/ref targets can consume it.
        ReferenceProxySource? proxySource = null;
        if (member is IWorldElement memberElement)
        {
            proxySource = row.AttachComponent<ReferenceProxySource>();
            proxySource.Target.Target = memberElement;
        }

        bool driven = IsDriven(member);
        InspectorUI.MemberChip(ui, MemberKindColor(member, driven), proxySource);

        // Quick actions for the member (reset default, break drive, vector helpers) on a compact button.
        if (member is IWorldElement actionTarget)
        {
            var actions = row.AttachComponent<MemberActionsRelay>();
            actions.TargetMember.Target = actionTarget;
            ui.PushStyle();
            ui.MinWidth(InspectorUI.ActionsWidth);
            ui.PreferredWidth(InspectorUI.ActionsWidth);
            ui.FlexibleWidth(0f);
            ui.PushStyle();
            ui.TextColor(InspectorUI.MutedColor);
            ui.Button("…", actions.OnActionsPressed);
            ui.PopStyle();
            ui.PopStyle();
        }

        // FIXED label width so the editor always gets ALL remaining row width.
        ui.PushStyle();
        ui.MinWidth(InspectorUI.LabelMinWidth);
        ui.PreferredWidth(InspectorUI.LabelWidth);
        ui.FlexibleWidth(0f);
        var label = ui.Text($"{name}:", InspectorUI.FontSize, driven ? InspectorUI.DrivenColor : InspectorUI.MutedColor);
        InspectorUI.FillParent(label.RectTransform!);
        label.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
        label.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        ui.PopStyle();

        // Live drive/link state on the label (built-time tint goes stale as drives attach/detach).
        if (member is IWorldElement stateTarget)
        {
            var tint = row.AttachComponent<MemberStateTint>();
            tint.TargetMember.Target = stateTarget;
            tint.Label.Target = label;
            tint.BaseColor.Value = InspectorUI.MutedColor;
        }

        // Grip split on a value row: the chip pulls the MEMBER's reference card, the label pulls a VALUE card
        // with what the field holds now, and each editor pulls its own value and takes dropped cards. Reference rows
        // keep their own split (their ≡ pulls the target). See ValueProxySource. -xlinka
        if (member is IField valueField && member is not ISyncRef && ValueCardConversion.CanCarry(valueField.ValueType))
            label.Slot.AttachComponent<ValueProxySource>().Target.Target = valueField;

        ui.PushStyle();
        ui.FlexibleWidth(1f);
        var editorArea = ui.Next("Editor");
        // Single-child containers need a layout or the child sits as a default 100x100 chunk.
        var areaLayout = editorArea.AttachComponent<Helio.UI.Layout.HorizontalLayout>();
        areaLayout.ForceExpandHeight.Value = true;
        ui.NestInto(editorArea);
        BuildEditor(member, fieldInfo, ui, editorArea);
        ui.NestOut();
        ui.PopStyle();
    }

    // The build-time drive state. This used to read `!element.IsDrivable`, which is the OPPOSITE
    // question: IsDrivable is the "may be driven at all" capability flag, true on every member unless
    // MarkNonDrivable turned it off. So the chip and label only ever went magenta for members that can
    // NEVER be driven, and an actually-driven field always built as a plain one. MemberStateTint fixes
    // the label up on its first update either way, but the chip has no live tint. -xlinka
    private static bool IsDriven(ISyncMember member)
        => member is ILinkable linkable && !linkable.IsDestroyed && linkable.IsDriven;

    // chip tint by member kind: driven magenta, refs purple, lists green, fields blue
    internal static color MemberKindColor(ISyncMember member, bool driven)
    {
        if (driven)
            return InspectorUI.DrivenColor;
        return member switch
        {
            ISyncRef => InspectorUI.AccentColor,
            ISyncList => InspectorUI.AxisYColor,
            IField => InspectorUI.AxisZColor,
            _ => InspectorUI.MutedColor,
        };
    }

    private static bool IsTextureRef(ISyncMember member)
    {
        var type = member.GetType();
        return type.IsGenericType
            && type.GetGenericTypeDefinition() == typeof(Lumora.Core.Assets.AssetRef<>)
            && type.GetGenericArguments()[0] == typeof(Lumora.Core.Assets.TextureAsset);
    }

    // Internal so the collection editor dispatches each ELEMENT through the same machinery a
    // top-level member uses (a ref element gets the full ref row, a float element the text field).
    internal static void BuildEditor(ISyncMember member, FieldInfo? fieldInfo, UIBuilder ui, Slot editorSlot)
    {
        switch (member)
        {
            // Shader uniform params are neither a plain field nor a ref, so without this they hit the
            // default "(TypeName)" fallback. Give them the same first-class value editor the material
            // panel builds. -xlinka
            case Lumora.Core.Components.Assets.ShaderUniformParam param:
                ShaderUniformParamEditor.BuildInlineEditor(param, ui, editorSlot);
                return;

            case ISyncRef when member is IField textureField && IsTextureRef(member):
                Attach<TextureRefMemberEditor>(editorSlot).Setup(textureField, "", ui);
                return;

            case ISyncRef syncRef when member is IField refField:
                Attach<RefMemberEditor>(editorSlot).Setup(refField, "", ui);
                return;

            case IField field:
                BuildFieldEditor(field, field.ValueType, "", fieldInfo, ui, editorSlot, 0);
                return;

            case ISyncList list:
                // Only NESTED collections land here (an element that is itself a list); top-level
                // collection members take the section path in Build.
                ui.Text($"(list, {list.Count} items)", InspectorUI.FontSize, InspectorUI.MutedColor);
                return;

            default:
                ui.Text($"({member.GetType().Name})", InspectorUI.FontSize, InspectorUI.MutedColor);
                return;
        }
    }

    private static void BuildFieldEditor(IField field, Type type, string path, FieldInfo? fieldInfo, UIBuilder ui, Slot editorSlot, int depth)
    {
        if (type == typeof(bool))
        {
            Attach<BooleanMemberEditor>(editorSlot).Setup(field, path, ui);
            return;
        }
        if (type.IsEnum)
        {
            Attach<EnumMemberEditor>(editorSlot).Setup(field, path, ui);
            return;
        }
        if (type == typeof(color) || type == typeof(colorHDR))
        {
            Attach<ColorMemberEditor>(editorSlot).Setup(field, path, ui);
            return;
        }
        if (type == typeof(floatQ))
        {
            Attach<QuaternionMemberEditor>(editorSlot).Setup(field, path, ui);
            return;
        }
        if (IsNumeric(type) && fieldInfo?.GetCustomAttribute<RangeAttribute>() is { } range)
        {
            var slider = Attach<SliderMemberEditor>(editorSlot);
            slider.Min.Value = range.Min;
            slider.Max.Value = range.Max;
            slider.WholeNumbers.Value = IsInteger(type);
            slider.Format.Value = range.TextFormat ?? "";
            slider.Setup(field, path, ui);
            return;
        }
        if (IsTextEditable(type))
        {
            Attach<PrimitiveMemberEditor>(editorSlot).Setup(field, path, ui);
            return;
        }

        // Compound struct: one leaf editor per instance field, addressed by dotted path, all
        // sharing the row width equally.
        if (type.IsValueType && depth < MaxStructDepth)
        {
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (fields.Length > 0 && fields.Length <= 6)
            {
                ui.HorizontalLayout(InspectorUI.ColumnSpacing);
                foreach (var structField in fields)
                {
                    ui.PushStyle();
                    ui.FlexibleWidth(1f);
                    var leafSlot = ui.Next(structField.Name);
                    // A layout on the leaf is load-bearing: without one the editor's input slot
                    // gets no layout element and sits as the default centered chunk, ballooning
                    // way past its 30px row and over the neighbors. - xlinka
                    leafSlot.AttachComponent<Helio.UI.Layout.HorizontalLayout>();
                    ui.NestInto(leafSlot);
                    string leafPath = string.IsNullOrEmpty(path) ? structField.Name : path + "." + structField.Name;
                    BuildFieldEditor(field, structField.FieldType, leafPath, null, ui, leafSlot, depth + 1);
                    ui.NestOut();
                    ui.PopStyle();
                }
                ui.NestOut();
                return;
            }
        }

        ui.Text($"({NiceTypeName(type)})", InspectorUI.FontSize, InspectorUI.MutedColor);
    }

    private static T Attach<T>(Slot slot) where T : Component, new() => slot.AttachComponent<T>();

    private static bool IsNumeric(Type type)
        => type == typeof(float) || type == typeof(double) || type == typeof(int) || type == typeof(uint)
        || type == typeof(long) || type == typeof(ulong) || type == typeof(short) || type == typeof(ushort)
        || type == typeof(byte) || type == typeof(sbyte) || type == typeof(decimal);

    private static bool IsInteger(Type type)
        => type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong)
        || type == typeof(short) || type == typeof(ushort) || type == typeof(byte) || type == typeof(sbyte);

    private static bool IsTextEditable(Type type)
        => type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type == typeof(Uri);

    public static string NiceTypeName(Type type) => ComponentLibrary.DisplayName(type);
}

// optional hook: a component builds its own inspector body instead of the reflected rows
public interface ICustomInspector
{
    void BuildInspectorUI(UIBuilder ui);
}

// reflected member rows for a worker, skips [HideInInspector]
public static class WorkerInspectorBuilder
{
    // beforeRow runs ahead of each member's row with its name, for a header that belongs over one of them.
    public static void BuildMemberRows(Worker worker, Slot container, Slot themeContext, Action<string>? beforeRow = null)
    {
        // [Group] on a field opens a section: cyan header + rule before that member's row. Only a
        // NAME CHANGE emits a header, so annotating just the first field of a block or every field
        // in it renders the same. There is no way to close a section - ungrouped fields after a
        // grouped block read as part of it, so order declarations accordingly.
        string? currentGroup = null;
        for (int i = 0; i < worker.SyncMemberCount; i++)
        {
            var fieldInfo = worker.GetSyncMemberFieldInfo(i);
            if (fieldInfo?.GetCustomAttribute<HideInInspectorAttribute>() != null)
                continue;
            var member = worker.GetSyncMember(i);
            if (member == null)
                continue;
            var group = fieldInfo?.GetCustomAttribute<GroupAttribute>();
            if (group != null && !string.Equals(group.Name, currentGroup, StringComparison.Ordinal))
            {
                InspectorUI.SectionHeader(container, group.Name.ToUpperInvariant(), themeContext);
                currentGroup = group.Name;
            }
            string name = worker.GetSyncMemberName(i);
            beforeRow?.Invoke(name);
            SyncMemberEditorBuilder.Build(member, name, fieldInfo, container, themeContext);
        }
    }

    // every public void [SyncMethod] whose parameters are all simple values renders as a clickable row.
    // a lone value sits on the button's own row; two or more get an indented row each under the button,
    // labelled with the parameter's name, all seeded with the declared defaults.
    // base plumbing stays hidden since its [SyncMethod]s take UI objects, which no row can carry.
    // a "Swap Type" row leads the set when the component has interchangeable siblings.
    public static void BuildMethodRows(Component component, Slot container, Slot themeContext)
    {
        BuildTypeSwapRow(component, container, themeContext);

        foreach (var method in component.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.IsSpecialName || method.ReturnType != typeof(void))
                continue;
            var parameters = method.GetParameters();
            if (!InspectorMethodArgument.AreSupported(parameters))
                continue;
            if (method.GetCustomAttribute<SyncMethodAttribute>() == null)
                continue;
            BuildMethodRow(component, method, parameters, container, themeContext);
        }
    }

    private const float ArgumentWidth = 170f;

    private static void BuildMethodRow(Component component, MethodInfo method, ParameterInfo[] parameters, Slot container, Slot themeContext)
    {
        InspectorUI.FixedRow(container, method.Name, InspectorUI.RowHeight, out var ui, themeContext);
        ui.PushStyle();
        ui.FlexibleWidth(1f);
        ui.TextColor(InspectorUI.AccentColor);
        var names = new string[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
            names[i] = parameters[i].Name ?? $"arg{i}";
        var button = ui.Button($"{method.Name}({string.Join(", ", names)})", null!);
        var relay = button.Slot.AttachComponent<InspectorMethodButton>();
        relay.Target.Target = component;
        relay.MethodName.Value = method.Name;
        button.SetAction(relay.OnPressed);
        var text = button.Slot.GetComponentInChildren<Text>();
        if (text != null)
        {
            InspectorUI.FillParent(text.RectTransform!);
            text.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
            var rect = text.RectTransform;
            if (rect != null)
                rect.OffsetMin.Value = new Lumora.Core.Math.float2(10f, 0f);
        }
        ui.PopStyle();

        if (parameters.Length == 1)
        {
            ui.PushStyle();
            ui.MinWidth(ArgumentWidth);
            ui.PreferredWidth(ArgumentWidth);
            ui.FlexibleWidth(0f);
            relay.Arguments.Add(BuildArgument(parameters[0], ui));
            ui.PopStyle();
            return;
        }

        foreach (var parameter in parameters)
        {
            InspectorUI.FixedRow(container, parameter.Name ?? "Argument", InspectorUI.RowHeight, out var argumentUi, themeContext);
            argumentUi.PushStyle();
            argumentUi.MinWidth(24f);
            argumentUi.PreferredWidth(24f);
            argumentUi.FlexibleWidth(0f);
            argumentUi.Next("Indent");
            argumentUi.PopStyle();

            argumentUi.PushStyle();
            argumentUi.MinWidth(150f);
            argumentUi.PreferredWidth(166f);
            argumentUi.FlexibleWidth(0f);
            var label = argumentUi.Text($"{parameter.Name}:", InspectorUI.FontSize, InspectorUI.MutedColor);
            InspectorUI.FillParent(label.RectTransform!);
            label.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
            label.VerticalAlignment.Value = TextVerticalAlignment.Middle;
            argumentUi.PopStyle();

            argumentUi.PushStyle();
            argumentUi.FlexibleWidth(1f);
            relay.Arguments.Add(BuildArgument(parameter, argumentUi));
            argumentUi.PopStyle();
        }
    }

    // The value's editor gets a slot of its own with a layout, or its input sits as the default centred chunk.
    // Inside it the widths start over, so the host's fixed width does not stretch every piece of the editor.
    private static InspectorMethodArgument BuildArgument(ParameterInfo parameter, UIBuilder ui)
    {
        var host = ui.Next(parameter.Name ?? "Argument");
        var layout = host.AttachComponent<Helio.UI.Layout.HorizontalLayout>();
        layout.ForceExpandHeight.Value = true;
        var argument = host.AttachComponent<InspectorMethodArgument>();
        ui.NestInto(host);
        ui.PushStyle();
        ui.MinWidth(0f);
        ui.PreferredWidth(0f);
        ui.FlexibleWidth(1f);
        argument.Setup(parameter, ui);
        ui.PopStyle();
        ui.NestOut();
        return argument;
    }

    // Only rendered for a component that HAS siblings to swap to, so the row never appears as a dead
    // end on a one-of-a-kind component.
    private static void BuildTypeSwapRow(Component component, Slot container, Slot themeContext)
    {
        if (ComponentTypeSwapUndoBatch.SwapFamily(component.GetType()) == null)
            return;

        InspectorUI.FixedRow(container, "SwapType", InspectorUI.RowHeight, out var ui, themeContext);
        ui.PushStyle();
        ui.FlexibleWidth(1f);
        ui.TextColor(InspectorUI.AccentColor);
        var button = ui.Button("Swap Type...", null!);
        var relay = button.Slot.AttachComponent<ComponentTypeSwapButton>();
        relay.Target.Target = component;
        button.SetAction(relay.OnPressed);
        var text = button.Slot.GetComponentInChildren<Text>();
        if (text != null)
        {
            InspectorUI.FillParent(text.RectTransform!);
            text.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
            var rect = text.RectTransform;
            if (rect != null)
                rect.OffsetMin.Value = new Lumora.Core.Math.float2(10f, 0f);
        }
        ui.PopStyle();
    }
}
