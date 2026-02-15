using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Aquamarine.Godot.Hooks.GodotUI.Inspectors;

#nullable enable

/// <summary>
/// Factory for creating appropriate editor controls for sync members.
/// Maps sync types to Godot UI controls with bidirectional binding.
/// </summary>
public static class SyncMemberEditorBuilder
{
    private const int LabelMinWidth = 100;
    private const int EditorMinWidth = 150;
    private const int IndentWidth = 16;

    /// <summary>
    /// Create a property row with label and appropriate editor for a sync member.
    /// </summary>
    public static Control? CreateEditorRow(ISyncMember syncMember, string name, FieldInfo? fieldInfo = null, int depth = 0)
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
            header.AddThemeFontSizeOverride("font_size", 14);
            container.AddChild(header);

            var separator = new HSeparator();
            container.AddChild(separator);
        }

        // Handle SyncList
        if (syncMember is ISyncList syncList)
        {
            var listEditor = CreateListEditor(syncList, name, depth);
            container.AddChild(listEditor);
            return container;
        }

        // Create standard row
        var row = new HBoxContainer();
        row.Name = $"Row_{name}";

        // Add indentation
        if (depth > 0)
        {
            var indent = new Control();
            indent.CustomMinimumSize = new Vector2(IndentWidth * depth, 0);
            row.AddChild(indent);
        }

        // Label
        var label = new Label();
        label.Text = name;
        label.CustomMinimumSize = new Vector2(LabelMinWidth - (IndentWidth * depth), 0);
        label.SizeFlagsHorizontal = Control.SizeFlags.Fill;
        if (tooltipAttr != null)
        {
            label.TooltipText = tooltipAttr.Text;
        }
        row.AddChild(label);

        // Editor control
        var editor = CreateEditor(syncMember, fieldInfo, readOnlyAttr != null);
        if (editor != null)
        {
            editor.CustomMinimumSize = new Vector2(EditorMinWidth, 0);
            editor.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            if (tooltipAttr != null)
            {
                editor.TooltipText = tooltipAttr.Text;
            }
            row.AddChild(editor);
        }
        else
        {
            // Fallback: show read-only value
            var valueLabel = new Label();
            valueLabel.Text = syncMember.GetValueAsObject()?.ToString() ?? "null";
            valueLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(valueLabel);
        }

        container.AddChild(row);
        return container;
    }

    /// <summary>
    /// Create the appropriate editor control for a sync member.
    /// </summary>
    public static Control? CreateEditor(ISyncMember syncMember, FieldInfo? fieldInfo = null, bool readOnly = false)
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
            return CreateColorEditor(field, readOnly);
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

        var slider = new HSlider();
        slider.MinValue = range.Min;
        slider.MaxValue = range.Max;
        slider.Step = range.Step > 0 ? range.Step : (isInteger ? 1 : 0.01);
        slider.Value = Convert.ToDouble(field.BoxedValue ?? range.Min);
        slider.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        slider.Editable = !readOnly && field.CanWrite;

        var valueLabel = new Label();
        valueLabel.CustomMinimumSize = new Vector2(50, 0);
        valueLabel.HorizontalAlignment = HorizontalAlignment.Right;
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
    private static VBoxContainer CreateListEditor(ISyncList syncList, string name, int depth)
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
        addBtn.Pressed += () =>
        {
            syncList.AddElement();
            RebuildListElements(container, syncList, name, depth);
        };
        headerRow.AddChild(addBtn);

        container.AddChild(headerRow);

        // Elements container with indentation
        var elementsContainer = new VBoxContainer();
        elementsContainer.Name = "Elements";
        container.AddChild(elementsContainer);

        // Build initial elements
        RebuildListElements(container, syncList, name, depth);

        // Listen for changes
        syncList.ElementsAdded += (list, idx, count) =>
        {
            if (IsInstanceValid(container))
                RebuildListElements(container, syncList, name, depth);
        };

        syncList.ElementsRemoved += (list, idx, count) =>
        {
            if (IsInstanceValid(container))
                RebuildListElements(container, syncList, name, depth);
        };

        syncList.ListCleared += (list) =>
        {
            if (IsInstanceValid(container))
                RebuildListElements(container, syncList, name, depth);
        };

        return container;
    }

    private static void RebuildListElements(VBoxContainer container, ISyncList syncList, string name, int depth)
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
            var editor = CreateEditorRow(element, $"[{i}]", null, 0);
            if (editor != null)
            {
                editor.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                elementRow.AddChild(editor);
            }

            // Remove button
            var removeBtn = new Button();
            removeBtn.Text = "X";
            removeBtn.TooltipText = "Remove element";
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
        var uri = field.BoxedValue as Uri;
        lineEdit.Text = uri?.ToString() ?? "";
        lineEdit.Editable = !readOnly && field.CanWrite;

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

        var clearBtn = new Button();
        clearBtn.Text = "X";
        clearBtn.TooltipText = "Clear type";
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

    private static CheckBox CreateBoolEditor(IField field, bool readOnly)
    {
        var checkBox = new CheckBox();
        checkBox.ButtonPressed = (bool)(field.BoxedValue ?? false);
        checkBox.Disabled = readOnly || !field.CanWrite;

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

        return checkBox;
    }

    private static SpinBox CreateIntEditor(IField field, bool readOnly)
    {
        var spinBox = new SpinBox();
        spinBox.Step = 1;
        spinBox.AllowGreater = true;
        spinBox.AllowLesser = true;
        spinBox.Value = Convert.ToDouble(field.BoxedValue ?? 0);
        spinBox.Editable = !readOnly && field.CanWrite;

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

    private static ColorPickerButton CreateColorEditor(IField field, bool readOnly)
    {
        var picker = new ColorPickerButton();
        var value = (color)(field.BoxedValue ?? new color(1, 1, 1, 1));
        picker.Color = new Color(value.r, value.g, value.b, value.a);
        picker.Disabled = readOnly || !field.CanWrite;

        picker.ColorChanged += (newColor) =>
        {
            if (!readOnly && field.CanWrite)
                field.BoxedValue = new color(newColor.R, newColor.G, newColor.B, newColor.A);
        };

        if (field is IChangeable changeable)
        {
            changeable.Changed += (_) =>
            {
                if (IsInstanceValid(picker))
                {
                    var c = (color)(field.BoxedValue ?? new color(1, 1, 1, 1));
                    picker.Color = new Color(c.r, c.g, c.b, c.a);
                }
            };
        }

        return picker;
    }

    private static OptionButton CreateEnumEditor(IField field, Type enumType, bool readOnly)
    {
        var optionButton = new OptionButton();
        var names = Enum.GetNames(enumType);
        var values = Enum.GetValues(enumType);
        optionButton.Disabled = readOnly || !field.CanWrite;

        for (int i = 0; i < names.Length; i++)
        {
            optionButton.AddItem(names[i], i);
        }

        var currentValue = field.BoxedValue;
        if (currentValue != null)
        {
            var index = Array.IndexOf(values, currentValue);
            if (index >= 0)
            {
                optionButton.Selected = index;
            }
        }

        optionButton.ItemSelected += (index) =>
        {
            if (!readOnly && field.CanWrite && index >= 0 && index < values.Length)
            {
                field.BoxedValue = values.GetValue((int)index);
            }
        };

        if (field is IChangeable changeable)
        {
            changeable.Changed += (_) =>
            {
                if (IsInstanceValid(optionButton))
                {
                    var val = field.BoxedValue;
                    if (val != null)
                    {
                        var idx = Array.IndexOf(values, val);
                        if (idx >= 0)
                        {
                            optionButton.Selected = idx;
                        }
                    }
                }
            };
        }

        return optionButton;
    }

    private static HBoxContainer CreateRefEditor(ISyncRef syncRef, bool readOnly)
    {
        var container = new HBoxContainer();

        var label = new Label();
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        UpdateRefLabel(label, syncRef);

        var clearBtn = new Button();
        clearBtn.Text = "X";
        clearBtn.TooltipText = "Clear reference";
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
        spinBox.CustomMinimumSize = new Vector2(60, 0);
        spinBox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        spinBox.Editable = !readOnly;
        return spinBox;
    }

    private static bool IsInstanceValid(GodotObject? obj)
    {
        return obj != null && GodotObject.IsInstanceValid(obj);
    }
}
