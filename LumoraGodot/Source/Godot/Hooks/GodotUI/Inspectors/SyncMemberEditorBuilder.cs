// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;
using Lumora.Godot.UI;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks.GodotUI.Inspectors;

#nullable enable

/// <summary>
/// Factory for creating appropriate editor controls for sync members.
/// Maps sync types to Godot UI controls with bidirectional binding.
/// </summary>
public static class SyncMemberEditorBuilder
{
    private const int LabelMinWidth = 190;
    private const int EditorDefaultWidth = 260;
    private const int IndentWidth = 20;
    private const int RowMinHeight = 44;
    private const int EditorFontSize = 18;
    private const int HeaderFontSize = 20;
    private const int SmallButtonSize = 36;
    private const int ComponentSpinMinWidth = 78;

    /// <summary>
    /// Create a property row with label and appropriate editor for a sync member.
    /// </summary>
    public static Control? CreateEditorRow(
        ISyncMember syncMember,
        string name,
        FieldInfo? fieldInfo = null,
        int depth = 0,
        Action<ISyncMember, string, bool>? openColorPicker = null)
    {
        if (syncMember == null) return null;

        // Check for header attribute
        var headerAttr = fieldInfo?.GetCustomAttribute<HeaderAttribute>();
        var spaceAttr = fieldInfo?.GetCustomAttribute<SpaceAttribute>();
        var tooltipAttr = fieldInfo?.GetCustomAttribute<TooltipAttribute>();
        var readOnlyAttr = fieldInfo?.GetCustomAttribute<ReadOnlyAttribute>();

        var container = new VBoxContainer();
        container.Name = $"Container_{name}";

        // Add space if specified
        if (spaceAttr != null)
        {
            var spacer = new Control();
            spacer.CustomMinimumSize = new Vector2(0, spaceAttr.Height);
            container.AddChild(spacer);
        }

        // Add header if specified
        if (headerAttr != null)
        {
            var header = new Label();
            header.Text = headerAttr.Text;
            header.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 1f));
            header.AddThemeFontSizeOverride("font_size", HeaderFontSize);
            container.AddChild(header);

            var separator = new HSeparator();
            container.AddChild(separator);
        }

        // Handle SyncList
        if (syncMember is ISyncList syncList)
        {
            var listEditor = CreateListEditor(syncList, name, depth, openColorPicker);
            container.AddChild(listEditor);
            return container;
        }

        // Create standard row
        var row = new HBoxContainer();
        row.Name = $"Row_{name}";
        row.CustomMinimumSize = new Vector2(0, RowMinHeight);
        row.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddThemeConstantOverride("separation", 10);

        // Add indentation
        if (depth > 0)
        {
            var indent = new Control();
            indent.CustomMinimumSize = new Vector2(IndentWidth * depth, 0);
            row.AddChild(indent);
        }

        // Label, top-aligned so multi-line/component rows read consistently from
        // the top-left of each row. Reviewer noted labels drifted to the middle. - xlinka
        var label = new Label();
        label.Text = name;
        label.CustomMinimumSize = new Vector2(Mathf.Max(80, LabelMinWidth - (IndentWidth * depth)), RowMinHeight);
        label.SizeFlagsHorizontal = Control.SizeFlags.Fill;
        label.SizeFlagsVertical = Control.SizeFlags.Fill;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        label.AddThemeFontSizeOverride("font_size", EditorFontSize);
        if (tooltipAttr != null)
        {
            label.TooltipText = tooltipAttr.Text;
        }
        row.AddChild(label);

        // Editor control. wrap in a fixed-width container so primitive fields don't stretch across the panel - xlinka
        var editor = CreateEditor(syncMember, fieldInfo, readOnlyAttr != null, name, openColorPicker);
        if (editor != null)
        {
            var editorWidth = GetPreferredEditorWidth(syncMember);
            editor.CustomMinimumSize = new Vector2(editorWidth, RowMinHeight);
            editor.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            editor.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            if (tooltipAttr != null)
            {
                editor.TooltipText = tooltipAttr.Text;
            }

            var editorSlot = new MarginContainer();
            editorSlot.Name = $"EditorSlot_{name}";
            editorSlot.CustomMinimumSize = new Vector2(editorWidth, RowMinHeight);
            editorSlot.SizeFlagsHorizontal = Control.SizeFlags.Fill;
            editorSlot.AddChild(editor);
            row.AddChild(editorSlot);
        }
        else
        {
            // Fallback: show read-only value
            var valueLabel = new Label();
            valueLabel.Text = syncMember.GetValueAsObject()?.ToString() ?? "null";
            valueLabel.AddThemeFontSizeOverride("font_size", EditorFontSize);
            valueLabel.CustomMinimumSize = new Vector2(EditorDefaultWidth, RowMinHeight);
            valueLabel.SizeFlagsHorizontal = Control.SizeFlags.Fill;
            valueLabel.VerticalAlignment = VerticalAlignment.Center;
            valueLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            row.AddChild(valueLabel);
        }

        // trailing filler eats remaining width so editors stay aligned at a sensible cap instead of stretching - xlinka
        var rowFiller = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        row.AddChild(rowFiller);

        container.AddChild(row);
        UIReadability.ApplyToTree(container);
        return container;
    }

    private static float GetPreferredEditorWidth(ISyncMember syncMember)
    {
        if (syncMember is ISyncRef) return 420;
        if (syncMember is not IField field) return EditorDefaultWidth;

        var valueType = Nullable.GetUnderlyingType(field.ValueType) ?? field.ValueType;

        if (valueType == typeof(bool)) return 64;
        if (valueType == typeof(float2)) return 180;
        if (valueType == typeof(float3) || valueType == typeof(floatQ)) return 270;
        if (valueType == typeof(float4)) return 430;
        if (valueType == typeof(color) || valueType == typeof(colorHDR)) return 500;
        if (valueType == typeof(string) || valueType == typeof(Uri)) return 360;
        if (valueType == typeof(Type) || valueType.IsEnum) return 340;
        if (valueType == typeof(int) || valueType == typeof(long) ||
            valueType == typeof(short) || valueType == typeof(byte) ||
            valueType == typeof(float) || valueType == typeof(double))
        {
            return 220;
        }

        return EditorDefaultWidth;
    }

    /// <summary>
    /// Create the appropriate editor control for a sync member.
    /// </summary>
    public static Control? CreateEditor(
        ISyncMember syncMember,
        FieldInfo? fieldInfo = null,
        bool readOnly = false,
        string? memberName = null,
        Action<ISyncMember, string, bool>? openColorPicker = null)
    {
        if (syncMember == null) return null;

        // Handle ISyncRef first (before IField check)
        if (syncMember is ISyncRef syncRef)
        {
            return CreateRefEditor(syncRef, readOnly);
        }

        // Need IField for ValueType
        if (syncMember is not IField field) return null;

        var valueType = field.ValueType;

        // Handle nullable types
        var underlyingType = Nullable.GetUnderlyingType(valueType);
        if (underlyingType != null)
        {
            valueType = underlyingType;
        }

        // Check for Range attribute
        var rangeAttr = fieldInfo?.GetCustomAttribute<RangeAttribute>();

        // Boolean
        if (valueType == typeof(bool))
        {
            return CreateBoolEditor(field, readOnly);
        }

        // Integer types
        if (valueType == typeof(int) || valueType == typeof(long) || valueType == typeof(short) || valueType == typeof(byte))
        {
            if (rangeAttr != null)
            {
                return CreateSliderEditor(field, rangeAttr, true, readOnly);
            }
            return CreateIntEditor(field, readOnly);
        }

        // Float types
        if (valueType == typeof(float) || valueType == typeof(double))
        {
            if (rangeAttr != null)
            {
                return CreateSliderEditor(field, rangeAttr, false, readOnly);
            }
            return CreateFloatEditor(field, readOnly);
        }

        // String
        if (valueType == typeof(string))
        {
            return CreateStringEditor(field, readOnly);
        }

        // Uri
        if (valueType == typeof(Uri))
        {
            return CreateUriEditor(field, readOnly);
        }

        // Type
        if (valueType == typeof(Type))
        {
            return CreateTypeEditor(field, readOnly);
        }

        // Vector types
        if (valueType == typeof(float2))
        {
            return CreateFloat2Editor(field, readOnly);
        }
        if (valueType == typeof(float3))
        {
            return CreateFloat3Editor(field, readOnly);
        }
        if (valueType == typeof(float4))
        {
            return CreateFloat4Editor(field, readOnly);
        }

        // Quaternion
        if (valueType == typeof(floatQ))
        {
            return CreateQuaternionEditor(field, readOnly);
        }

        // Color
        if (valueType == typeof(color))
        {
            return CreateColorEditor(field, syncMember, memberName ?? syncMember.Name ?? "Color", readOnly, hdr: false, openColorPicker);
        }
        if (valueType == typeof(colorHDR))
        {
            return CreateColorEditor(field, syncMember, memberName ?? syncMember.Name ?? "Color", readOnly, hdr: true, openColorPicker);
        }

        // Enum
        if (valueType.IsEnum)
        {
            return CreateEnumEditor(field, valueType, readOnly);
        }

        return null;
    }

    /// <summary>
    /// Create a slider editor for range-constrained values.
    /// </summary>
    private static HBoxContainer CreateSliderEditor(IField field, RangeAttribute range, bool isInteger, bool readOnly)
    {
        var container = new HBoxContainer();
        container.AddThemeConstantOverride("separation", 8);

        var slider = new HSlider();
        slider.MinValue = range.Min;
        slider.MaxValue = range.Max;
        slider.Step = range.Step > 0 ? range.Step : (isInteger ? 1 : 0.01);
        slider.Value = Convert.ToDouble(field.BoxedValue ?? range.Min);
        slider.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        slider.Editable = !readOnly && field.CanWrite;

        var valueLabel = new Label();
        valueLabel.CustomMinimumSize = new Vector2(70, RowMinHeight);
        valueLabel.HorizontalAlignment = HorizontalAlignment.Right;
        valueLabel.VerticalAlignment = VerticalAlignment.Center;
        valueLabel.AddThemeFontSizeOverride("font_size", EditorFontSize);
        UpdateSliderLabel(valueLabel, slider.Value, range.TextFormat);

        slider.ValueChanged += (value) =>
        {
            UpdateSliderLabel(valueLabel, value, range.TextFormat);
            if (!readOnly && field.CanWrite)
            {
                if (isInteger)
                    field.BoxedValue = Convert.ChangeType((int)value, field.ValueType);
                else if (field.ValueType == typeof(float))
                    field.BoxedValue = (float)value;
                else
                    field.BoxedValue = value;
            }
        };

        if (field is IChangeable changeable)
        {
            changeable.Changed += (_) =>
            {
                if (IsInstanceValid(slider))
                {
                    slider.SetBlockSignals(true);
                    var val = Convert.ToDouble(field.BoxedValue ?? range.Min);
                    slider.Value = val;
                    UpdateSliderLabel(valueLabel, val, range.TextFormat);
                    slider.SetBlockSignals(false);
                }
            };
        }

        container.AddChild(slider);
        container.AddChild(valueLabel);

        return container;
    }

    private static void UpdateSliderLabel(Label label, double value, string format)
    {
        try
        {
            label.Text = value.ToString(format);
        }
        catch
        {
            label.Text = value.ToString("F2");
        }
    }

    /// <summary>
    /// Create a list editor for ISyncList.
    /// </summary>
    private static VBoxContainer CreateListEditor(
        ISyncList syncList,
        string name,
        int depth,
        Action<ISyncMember, string, bool>? openColorPicker)
    {
        var container = new VBoxContainer();
        container.Name = $"List_{name}";

        // Header row
        var headerRow = new HBoxContainer();

        if (depth > 0)
        {
            var indent = new Control();
            indent.CustomMinimumSize = new Vector2(IndentWidth * depth, 0);
            headerRow.AddChild(indent);
        }

        var headerLabel = new Label();
        headerLabel.Text = $"{name} (List: {syncList.Count} items)";
        headerLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        headerRow.AddChild(headerLabel);

        var addBtn = new Button();
        addBtn.Text = "+";
        addBtn.TooltipText = "Add element";
        addBtn.CustomMinimumSize = new Vector2(SmallButtonSize, SmallButtonSize);
        addBtn.Pressed += () =>
        {
            syncList.AddElement();
            RebuildListElements(container, syncList, name, depth, openColorPicker);
        };
        headerRow.AddChild(addBtn);

        container.AddChild(headerRow);

        // Elements container with indentation
        var elementsContainer = new VBoxContainer();
        elementsContainer.Name = "Elements";
        container.AddChild(elementsContainer);

        // Build initial elements
        RebuildListElements(container, syncList, name, depth, openColorPicker);

        // Listen for changes
        syncList.ElementsAdded += (list, idx, count) =>
        {
            if (IsInstanceValid(container))
                RebuildListElements(container, syncList, name, depth, openColorPicker);
        };

        syncList.ElementsRemoved += (list, idx, count) =>
        {
            if (IsInstanceValid(container))
                RebuildListElements(container, syncList, name, depth, openColorPicker);
        };

        syncList.ListCleared += (list) =>
        {
            if (IsInstanceValid(container))
                RebuildListElements(container, syncList, name, depth, openColorPicker);
        };

        return container;
    }

    private static void RebuildListElements(
        VBoxContainer container,
        ISyncList syncList,
        string name,
        int depth,
        Action<ISyncMember, string, bool>? openColorPicker)
    {
        var elementsContainer = container.GetNodeOrNull<VBoxContainer>("Elements");
        if (elementsContainer == null) return;

        // Clear existing
        foreach (var child in elementsContainer.GetChildren())
        {
            child.QueueFree();
        }

        // Add elements
        for (int i = 0; i < syncList.Count; i++)
        {
            var element = syncList.GetElement(i);
            var elementIndex = i;

            var elementRow = new HBoxContainer();

            // Indentation
            var indent = new Control();
            indent.CustomMinimumSize = new Vector2(IndentWidth * (depth + 1), 0);
            elementRow.AddChild(indent);

            // Element editor
            var editor = CreateEditorRow(element, $"[{i}]", null, 0, openColorPicker);
            if (editor != null)
            {
                editor.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                elementRow.AddChild(editor);
            }

            // Remove button
            var removeBtn = new Button();
            removeBtn.Text = "X";
            removeBtn.TooltipText = "Remove element";
            removeBtn.CustomMinimumSize = new Vector2(SmallButtonSize, SmallButtonSize);
            removeBtn.Pressed += () =>
            {
                syncList.RemoveElement(elementIndex);
            };
            elementRow.AddChild(removeBtn);

            elementsContainer.AddChild(elementRow);
        }
    }

    /// <summary>
    /// Create a Uri editor.
    /// </summary>
    private static LineEdit CreateUriEditor(IField field, bool readOnly)
    {
        var lineEdit = new LineEdit();
        lineEdit.PlaceholderText = "Enter URI...";
        lineEdit.CustomMinimumSize = new Vector2(360, RowMinHeight);
        var uri = field.BoxedValue as Uri;
        lineEdit.Text = uri?.ToString() ?? "";
        lineEdit.Editable = !readOnly && field.CanWrite;
        ApplyEditorFont(lineEdit);

        lineEdit.TextSubmitted += (text) =>
        {
            if (!readOnly && field.CanWrite)
            {
                try
                {
                    field.BoxedValue = string.IsNullOrEmpty(text) ? null : new Uri(text);
                }
                catch
                {
                    // Invalid URI, ignore
                }
            }
        };

        if (field is IChangeable changeable)
        {
            changeable.Changed += (_) =>
            {
                if (IsInstanceValid(lineEdit) && !lineEdit.HasFocus())
                {
                    var u = field.BoxedValue as Uri;
                    lineEdit.Text = u?.ToString() ?? "";
                }
            };
        }

        return lineEdit;
    }

    /// <summary>
    /// Create a Type editor (shows type name, allows selection).
    /// </summary>
    private static HBoxContainer CreateTypeEditor(IField field, bool readOnly)
    {
        var container = new HBoxContainer();

        var label = new Label();
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var type = field.BoxedValue as Type;
        label.Text = type?.Name ?? "(none)";
        label.TooltipText = type?.FullName ?? "";
        label.VerticalAlignment = VerticalAlignment.Center;
        label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;

        var clearBtn = new Button();
        clearBtn.Text = "X";
        clearBtn.TooltipText = "Clear type";
        clearBtn.CustomMinimumSize = new Vector2(SmallButtonSize, RowMinHeight);
        clearBtn.Disabled = readOnly || !field.CanWrite;
        clearBtn.Pressed += () =>
        {
            if (!readOnly && field.CanWrite)
                field.BoxedValue = null;
        };

        container.AddChild(label);
        container.AddChild(clearBtn);

        if (field is IChangeable changeable)
        {
            changeable.Changed += (_) =>
            {
                if (IsInstanceValid(label))
                {
                    var t = field.BoxedValue as Type;
                    label.Text = t?.Name ?? "(none)";
                    label.TooltipText = t?.FullName ?? "";
                }
            };
        }

        return container;
    }

    private static HBoxContainer CreateBoolEditor(IField field, bool readOnly)
    {
        var container = new HBoxContainer();
        container.AddThemeConstantOverride("separation", 0);

        var checkBox = new CheckBox();
        checkBox.ButtonPressed = (bool)(field.BoxedValue ?? false);
        checkBox.Disabled = readOnly || !field.CanWrite;
        checkBox.CustomMinimumSize = new Vector2(RowMinHeight, RowMinHeight);
        checkBox.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        checkBox.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        checkBox.AddThemeFontSizeOverride("font_size", EditorFontSize);
        // Strip the default Godot CheckBox text padding so the indicator sits flush.
        checkBox.AddThemeConstantOverride("h_separation", 0);
        container.AddChild(checkBox);

        var spacer = new Control();
        spacer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        // MouseFilter = Ignore so the spacer doesn't eat clicks the row may want.
        spacer.MouseFilter = Control.MouseFilterEnum.Ignore;
        container.AddChild(spacer);

        checkBox.Toggled += (pressed) =>
        {
            if (!readOnly && field.CanWrite)
                field.BoxedValue = pressed;
        };

        if (field is IChangeable changeable)
        {
            changeable.Changed += (_) =>
            {
                if (IsInstanceValid(checkBox))
                {
                    checkBox.SetBlockSignals(true);
                    checkBox.ButtonPressed = (bool)(field.BoxedValue ?? false);
                    checkBox.SetBlockSignals(false);
                }
            };
        }

        return container;
    }

    // bumps the inner LineEdit font of a SpinBox so VR users can read it. plain font_size override on SpinBox doesn't propagate - xlinka
    private static void ApplyEditorFont(SpinBox spinBox)
    {
        spinBox.AddThemeFontSizeOverride("font_size", EditorFontSize);
        var lineEdit = spinBox.GetLineEdit();
        if (lineEdit != null)
        {
            lineEdit.AddThemeFontSizeOverride("font_size", EditorFontSize);
            lineEdit.Alignment = HorizontalAlignment.Right;
        }
    }

    private static void ApplyEditorFont(LineEdit lineEdit)
    {
        lineEdit.AddThemeFontSizeOverride("font_size", EditorFontSize);
    }

    private static SpinBox CreateIntEditor(IField field, bool readOnly)
    {
        var spinBox = new SpinBox();
        spinBox.Step = 1;
        spinBox.AllowGreater = true;
        spinBox.AllowLesser = true;
        spinBox.Value = Convert.ToDouble(field.BoxedValue ?? 0);
        spinBox.Editable = !readOnly && field.CanWrite;
        spinBox.CustomMinimumSize = new Vector2(220, RowMinHeight);
        ApplyEditorFont(spinBox);

        spinBox.ValueChanged += (value) =>
        {
            if (!readOnly && field.CanWrite)
                field.BoxedValue = Convert.ChangeType((int)value, field.ValueType);
        };

        if (field is IChangeable changeable)
        {
            changeable.Changed += (_) =>
            {
                if (IsInstanceValid(spinBox))
                {
                    spinBox.SetBlockSignals(true);
                    spinBox.Value = Convert.ToDouble(field.BoxedValue ?? 0);
                    spinBox.SetBlockSignals(false);
                }
            };
        }

        return spinBox;
    }

    private static SpinBox CreateFloatEditor(IField field, bool readOnly)
    {
        var spinBox = new SpinBox();
        spinBox.Step = 0.01;
        spinBox.AllowGreater = true;
        spinBox.AllowLesser = true;
        spinBox.Value = Convert.ToDouble(field.BoxedValue ?? 0.0);
        spinBox.Editable = !readOnly && field.CanWrite;
        spinBox.CustomMinimumSize = new Vector2(220, RowMinHeight);
        ApplyEditorFont(spinBox);

        spinBox.ValueChanged += (value) =>
        {
            if (!readOnly && field.CanWrite)
            {
                if (field.ValueType == typeof(float))
                    field.BoxedValue = (float)value;
                else
                    field.BoxedValue = value;
            }
        };

        if (field is IChangeable changeable)
        {
            changeable.Changed += (_) =>
            {
                if (IsInstanceValid(spinBox))
                {
                    spinBox.SetBlockSignals(true);
                    spinBox.Value = Convert.ToDouble(field.BoxedValue ?? 0.0);
                    spinBox.SetBlockSignals(false);
                }
            };
        }

        return spinBox;
    }

    private static LineEdit CreateStringEditor(IField field, bool readOnly)
    {
        var lineEdit = new LineEdit();
        lineEdit.Text = field.BoxedValue?.ToString() ?? "";
        lineEdit.Editable = !readOnly && field.CanWrite;
        lineEdit.CustomMinimumSize = new Vector2(360, RowMinHeight);
        ApplyEditorFont(lineEdit);

        lineEdit.TextSubmitted += (text) =>
        {
            if (!readOnly && field.CanWrite)
                field.BoxedValue = text;
        };

        lineEdit.FocusExited += () =>
        {
            if (!readOnly && field.CanWrite)
                field.BoxedValue = lineEdit.Text;
        };

        if (field is IChangeable changeable)
        {
            changeable.Changed += (_) =>
            {
                if (IsInstanceValid(lineEdit) && !lineEdit.HasFocus())
                {
                    lineEdit.Text = field.BoxedValue?.ToString() ?? "";
                }
            };
        }

        return lineEdit;
    }

    private static HBoxContainer CreateFloat2Editor(IField field, bool readOnly)
    {
        var container = new HBoxContainer();
        container.AddThemeConstantOverride("separation", 6);
        var value = (float2)(field.BoxedValue ?? float2.Zero);

        var xSpin = CreateComponentSpinBox("X", value.x, readOnly);
        var ySpin = CreateComponentSpinBox("Y", value.y, readOnly);

        container.AddChild(xSpin);
        container.AddChild(ySpin);

        void UpdateValue()
        {
            if (!readOnly && field.CanWrite)
                field.BoxedValue = new float2((float)xSpin.Value, (float)ySpin.Value);
        }

        xSpin.ValueChanged += (_) => UpdateValue();
        ySpin.ValueChanged += (_) => UpdateValue();

        if (field is IChangeable changeable)
        {
            changeable.Changed += (_) =>
            {
                if (IsInstanceValid(container))
                {
                    var v = (float2)(field.BoxedValue ?? float2.Zero);
                    xSpin.SetBlockSignals(true);
                    ySpin.SetBlockSignals(true);
                    xSpin.Value = v.x;
                    ySpin.Value = v.y;
                    xSpin.SetBlockSignals(false);
                    ySpin.SetBlockSignals(false);
                }
            };
        }

        return container;
    }

    private static HBoxContainer CreateFloat3Editor(IField field, bool readOnly)
    {
        var container = new HBoxContainer();
        container.AddThemeConstantOverride("separation", 6);
        var value = (float3)(field.BoxedValue ?? float3.Zero);

        var xSpin = CreateComponentSpinBox("X", value.x, readOnly);
        var ySpin = CreateComponentSpinBox("Y", value.y, readOnly);
        var zSpin = CreateComponentSpinBox("Z", value.z, readOnly);

        container.AddChild(xSpin);
        container.AddChild(ySpin);
        container.AddChild(zSpin);

        void UpdateValue()
        {
            if (!readOnly && field.CanWrite)
                field.BoxedValue = new float3((float)xSpin.Value, (float)ySpin.Value, (float)zSpin.Value);
        }

        xSpin.ValueChanged += (_) => UpdateValue();
        ySpin.ValueChanged += (_) => UpdateValue();
        zSpin.ValueChanged += (_) => UpdateValue();

        if (field is IChangeable changeable)
        {
            changeable.Changed += (_) =>
            {
                if (IsInstanceValid(container))
                {
                    var v = (float3)(field.BoxedValue ?? float3.Zero);
                    xSpin.SetBlockSignals(true);
                    ySpin.SetBlockSignals(true);
                    zSpin.SetBlockSignals(true);
                    xSpin.Value = v.x;
                    ySpin.Value = v.y;
                    zSpin.Value = v.z;
                    xSpin.SetBlockSignals(false);
                    ySpin.SetBlockSignals(false);
                    zSpin.SetBlockSignals(false);
                }
            };
        }

        return container;
    }

    private static HBoxContainer CreateFloat4Editor(IField field, bool readOnly)
    {
        var container = new HBoxContainer();
        container.AddThemeConstantOverride("separation", 6);
        var value = (float4)(field.BoxedValue ?? float4.Zero);

        var xSpin = CreateComponentSpinBox("X", value.x, readOnly);
        var ySpin = CreateComponentSpinBox("Y", value.y, readOnly);
        var zSpin = CreateComponentSpinBox("Z", value.z, readOnly);
        var wSpin = CreateComponentSpinBox("W", value.w, readOnly);

        container.AddChild(xSpin);
        container.AddChild(ySpin);
        container.AddChild(zSpin);
        container.AddChild(wSpin);

        void UpdateValue()
        {
            if (!readOnly && field.CanWrite)
                field.BoxedValue = new float4((float)xSpin.Value, (float)ySpin.Value, (float)zSpin.Value, (float)wSpin.Value);
        }

        xSpin.ValueChanged += (_) => UpdateValue();
        ySpin.ValueChanged += (_) => UpdateValue();
        zSpin.ValueChanged += (_) => UpdateValue();
        wSpin.ValueChanged += (_) => UpdateValue();

        if (field is IChangeable changeable)
        {
            changeable.Changed += (_) =>
            {
                if (IsInstanceValid(container))
                {
                    var v = (float4)(field.BoxedValue ?? float4.Zero);
                    xSpin.SetBlockSignals(true);
                    ySpin.SetBlockSignals(true);
                    zSpin.SetBlockSignals(true);
                    wSpin.SetBlockSignals(true);
                    xSpin.Value = v.x;
                    ySpin.Value = v.y;
                    zSpin.Value = v.z;
                    wSpin.Value = v.w;
                    xSpin.SetBlockSignals(false);
                    ySpin.SetBlockSignals(false);
                    zSpin.SetBlockSignals(false);
                    wSpin.SetBlockSignals(false);
                }
            };
        }

        return container;
    }

    private static HBoxContainer CreateQuaternionEditor(IField field, bool readOnly)
    {
        var container = new HBoxContainer();
        container.AddThemeConstantOverride("separation", 6);
        var value = (floatQ)(field.BoxedValue ?? floatQ.Identity);

        var euler = value.ToEuler();

        var xSpin = CreateComponentSpinBox("X", euler.x * 57.2958f, readOnly);
        var ySpin = CreateComponentSpinBox("Y", euler.y * 57.2958f, readOnly);
        var zSpin = CreateComponentSpinBox("Z", euler.z * 57.2958f, readOnly);

        container.AddChild(xSpin);
        container.AddChild(ySpin);
        container.AddChild(zSpin);

        void UpdateValue()
        {
            if (!readOnly && field.CanWrite)
            {
                var eulerRad = new float3(
                    (float)xSpin.Value * 0.0174533f,
                    (float)ySpin.Value * 0.0174533f,
                    (float)zSpin.Value * 0.0174533f
                );
                field.BoxedValue = floatQ.FromEuler(eulerRad);
            }
        }

        xSpin.ValueChanged += (_) => UpdateValue();
        ySpin.ValueChanged += (_) => UpdateValue();
        zSpin.ValueChanged += (_) => UpdateValue();

        if (field is IChangeable changeable)
        {
            changeable.Changed += (_) =>
            {
                if (IsInstanceValid(container))
                {
                    var q = (floatQ)(field.BoxedValue ?? floatQ.Identity);
                    var e = q.ToEuler();
                    xSpin.SetBlockSignals(true);
                    ySpin.SetBlockSignals(true);
                    zSpin.SetBlockSignals(true);
                    xSpin.Value = e.x * 57.2958f;
                    ySpin.Value = e.y * 57.2958f;
                    zSpin.Value = e.z * 57.2958f;
                    xSpin.SetBlockSignals(false);
                    ySpin.SetBlockSignals(false);
                    zSpin.SetBlockSignals(false);
                }
            };
        }

        return container;
    }

    private static HBoxContainer CreateColorEditor(
        IField field,
        ISyncMember syncMember,
        string memberName,
        bool readOnly,
        bool hdr,
        Action<ISyncMember, string, bool>? openColorPicker)
    {
        var container = new HBoxContainer();
        container.AddThemeConstantOverride("separation", 6);

        var value = GetFieldColor(field, hdr);
        bool canWrite = !readOnly && field.CanWrite;

        var swatch = new ColorRect
        {
            Color = value,
            CustomMinimumSize = new Vector2(44, RowMinHeight),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        container.AddChild(swatch);

        var pickerButton = new Button
        {
            Text = "Pick",
            Disabled = !canWrite || openColorPicker == null,
            CustomMinimumSize = new Vector2(60, RowMinHeight),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            TooltipText = hdr ? "Open HDR color panel" : "Open color panel"
        };
        pickerButton.AddThemeFontSizeOverride("font_size", 14);
        pickerButton.Pressed += () => openColorPicker?.Invoke(syncMember, memberName, hdr);
        container.AddChild(pickerButton);

        // Channel spinboxes: R G B A
        static SpinBox MakeSpin(float v, bool enabled, bool hdr, bool alpha)
        {
            var s = new SpinBox();
            s.MinValue = 0.0;
            s.MaxValue = alpha ? 1.0 : (hdr ? 16.0 : 1.0);
            s.Step = 0.001;
            s.AllowGreater = hdr && !alpha;
            s.AllowLesser = false;
            s.Value = v;
            s.Editable = enabled;
            s.CustomMinimumSize = new Vector2(68, RowMinHeight);
            s.AddThemeFontSizeOverride("font_size", 14);
            s.GetLineEdit()?.AddThemeFontSizeOverride("font_size", 14);
            if (s.GetLineEdit() != null)
            {
                s.GetLineEdit()!.Alignment = HorizontalAlignment.Right;
            }
            return s;
        }

        var rSpin = MakeSpin(value.R, canWrite, hdr, alpha: false);
        var gSpin = MakeSpin(value.G, canWrite, hdr, alpha: false);
        var bSpin = MakeSpin(value.B, canWrite, hdr, alpha: false);
        var aSpin = MakeSpin(value.A, canWrite, hdr, alpha: true);

        // Channel labels
        static Label MakeLbl(string t)
        {
            var l = new Label
            {
                Text = t,
                CustomMinimumSize = new Vector2(14, RowMinHeight),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            l.AddThemeFontSizeOverride("font_size", 13);
            return l;
        }

        container.AddChild(MakeLbl("R")); container.AddChild(rSpin);
        container.AddChild(MakeLbl("G")); container.AddChild(gSpin);
        container.AddChild(MakeLbl("B")); container.AddChild(bSpin);
        container.AddChild(MakeLbl("A")); container.AddChild(aSpin);

        void SetSpinValue(SpinBox spin, float spinValue)
        {
            spin.SetBlockSignals(true);
            spin.Value = spinValue;
            spin.SetBlockSignals(false);
        }

        void SetSwatchColor(Color colorValue)
        {
            if (IsInstanceValid(swatch))
            {
                swatch.Color = colorValue;
            }
        }

        void UpdateSpins(Color colorValue)
        {
            SetSpinValue(rSpin, colorValue.R);
            SetSpinValue(gSpin, colorValue.G);
            SetSpinValue(bSpin, colorValue.B);
            SetSpinValue(aSpin, colorValue.A);
        }

        void ApplyFromSpins()
        {
            if (!canWrite) return;
            var c = new Color((float)rSpin.Value, (float)gSpin.Value, (float)bSpin.Value, (float)aSpin.Value);
            SetFieldColor(field, c, hdr);
            SetSwatchColor(c);
        }

        rSpin.ValueChanged += (_) => ApplyFromSpins();
        gSpin.ValueChanged += (_) => ApplyFromSpins();
        bSpin.ValueChanged += (_) => ApplyFromSpins();
        aSpin.ValueChanged += (_) => ApplyFromSpins();

        if (field is IChangeable changeable)
        {
            changeable.Changed += (_) =>
            {
                if (!IsInstanceValid(container)) return;
                var c = GetFieldColor(field, hdr);
                UpdateSpins(c);
                SetSwatchColor(c);
            };
        }

        return container;
    }

    private static Color GetFieldColor(IField field, bool hdr)
    {
        var raw = field.BoxedValue;
        if (raw is color c)
            return new Color(c.r, c.g, c.b, c.a);
        if (raw is colorHDR h)
            return new Color(h.r, h.g, h.b, h.a);
        return new Color(1, 1, 1, 1);
    }

    private static void SetFieldColor(IField field, Color value, bool hdr)
    {
        field.BoxedValue = hdr
            ? new colorHDR(value.R, value.G, value.B, value.A)
            : new color(value.R, value.G, value.B, value.A);
    }

    // TODO(xlinka): placeholder enum editor - << >> cycling is functional but terrible UX
    // for enums with many values. replace with a proper dropdown or searchable popup
    // that doesn't require 47 button presses to get to the value you want.
    private static HBoxContainer CreateEnumEditor(IField field, Type enumType, bool readOnly)
    {
        var container = new HBoxContainer();
        container.AddThemeConstantOverride("separation", 8);

        var names = Enum.GetNames(enumType);
        var values = Enum.GetValues(enumType);
        bool canWrite = !readOnly && field.CanWrite;

        int currentIndex = 0;
        var currentValue = field.BoxedValue;
        if (currentValue != null)
        {
            var idx = Array.IndexOf(values, currentValue);
            if (idx >= 0) currentIndex = idx;
        }

        var valueLabel = new Label();
        valueLabel.Text = names[currentIndex];
        valueLabel.CustomMinimumSize = new Vector2(210, RowMinHeight);
        valueLabel.SizeFlagsHorizontal = Control.SizeFlags.Fill;
        valueLabel.HorizontalAlignment = HorizontalAlignment.Center;
        valueLabel.VerticalAlignment = VerticalAlignment.Center;
        valueLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        valueLabel.AddThemeFontSizeOverride("font_size", EditorFontSize);

        void ShiftEnum(int delta)
        {
            if (!canWrite) return;
            var val = field.BoxedValue;
            int idx = val != null ? Array.IndexOf(values, val) : 0;
            idx = ((idx + delta) % names.Length + names.Length) % names.Length;
            field.BoxedValue = values.GetValue(idx);
            if (IsInstanceValid(valueLabel))
                valueLabel.Text = names[idx];
        }

        var prevBtn = new Button { Text = "<" };
        prevBtn.CustomMinimumSize = new Vector2(SmallButtonSize, RowMinHeight);
        prevBtn.Disabled = !canWrite;
        prevBtn.Pressed += () => ShiftEnum(-1);

        var nextBtn = new Button { Text = ">" };
        nextBtn.CustomMinimumSize = new Vector2(SmallButtonSize, RowMinHeight);
        nextBtn.Disabled = !canWrite;
        nextBtn.Pressed += () => ShiftEnum(+1);

        container.AddChild(prevBtn);
        container.AddChild(valueLabel);
        container.AddChild(nextBtn);

        if (field is IChangeable changeable)
        {
            changeable.Changed += (_) =>
            {
                if (!IsInstanceValid(valueLabel)) return;
                var val = field.BoxedValue;
                if (val != null)
                {
                    var idx = Array.IndexOf(values, val);
                    if (idx >= 0) valueLabel.Text = names[idx];
                }
            };
        }

        return container;
    }

    private static HBoxContainer CreateRefEditor(ISyncRef syncRef, bool readOnly)
    {
        var container = new HBoxContainer();

        var label = new Label();
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        UpdateRefLabel(label, syncRef);

        var clearBtn = new Button();
        clearBtn.Text = "X";
        clearBtn.TooltipText = "Clear reference";
        clearBtn.CustomMinimumSize = new Vector2(SmallButtonSize, RowMinHeight);
        clearBtn.Disabled = readOnly;
        clearBtn.Pressed += () =>
        {
            if (!readOnly)
                syncRef.Target = null;
        };

        container.AddChild(label);
        container.AddChild(clearBtn);

        if (syncRef is IChangeable changeable)
        {
            changeable.Changed += (_) =>
            {
                if (IsInstanceValid(label))
                {
                    UpdateRefLabel(label, syncRef);
                }
            };
        }

        return container;
    }

    private static void UpdateRefLabel(Label label, ISyncRef syncRef)
    {
        var target = syncRef.Target;
        if (target == null)
        {
            label.Text = "(null)";
            label.Modulate = new Color(0.5f, 0.5f, 0.5f);
        }
        else if (target is Slot slot)
        {
            label.Text = slot.Name.Value ?? "Unnamed Slot";
            label.Modulate = new Color(0.7f, 0.9f, 1f);
        }
        else if (target is Component component)
        {
            label.Text = $"{component.GetType().Name} on {component.Slot?.Name.Value ?? "?"}";
            label.Modulate = new Color(0.9f, 0.9f, 0.7f);
        }
        else
        {
            label.Text = target.ToString() ?? "Unknown";
            label.Modulate = new Color(1f, 1f, 1f);
        }
    }

    private static SpinBox CreateComponentSpinBox(string tooltip, float initialValue, bool readOnly = false)
    {
        var spinBox = new SpinBox();
        spinBox.Step = 0.01;
        spinBox.AllowGreater = true;
        spinBox.AllowLesser = true;
        spinBox.Value = initialValue;
        spinBox.TooltipText = tooltip;
        spinBox.CustomMinimumSize = new Vector2(ComponentSpinMinWidth, RowMinHeight);
        spinBox.SizeFlagsHorizontal = Control.SizeFlags.Fill;
        spinBox.Editable = !readOnly;
        ApplyEditorFont(spinBox);
        return spinBox;
    }

    private static bool IsInstanceValid(GodotObject? obj)
    {
        return obj != null && GodotObject.IsInstanceValid(obj);
    }
}
