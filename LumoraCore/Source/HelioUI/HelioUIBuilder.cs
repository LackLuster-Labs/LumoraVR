using System;
using System.Collections.Generic;
using Lumora.Core.Math;
using Lumora.Core.Logging;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Style settings for UIBuilder elements.
/// </summary>
public class HelioUIStyle
{
    /// <summary>
    /// Minimum width for elements (-1 = not set).
    /// </summary>
    public float MinWidth { get; set; } = -1f;

    /// <summary>
    /// Minimum height for elements (-1 = not set).
    /// </summary>
    public float MinHeight { get; set; } = -1f;

    /// <summary>
    /// Preferred width for elements (-1 = not set).
    /// </summary>
    public float PreferredWidth { get; set; } = -1f;

    /// <summary>
    /// Preferred height for elements (-1 = not set).
    /// </summary>
    public float PreferredHeight { get; set; } = -1f;

    /// <summary>
    /// Flexible width factor (-1 = not set).
    /// </summary>
    public float FlexibleWidth { get; set; } = -1f;

    /// <summary>
    /// Flexible height factor (-1 = not set).
    /// </summary>
    public float FlexibleHeight { get; set; } = -1f;

    /// <summary>
    /// Force child elements to expand width.
    /// </summary>
    public bool ForceExpandWidth { get; set; } = true;

    /// <summary>
    /// Force child elements to expand height.
    /// </summary>
    public bool ForceExpandHeight { get; set; } = true;

    /// <summary>
    /// Child alignment in layouts.
    /// </summary>
    public TextAlignment ChildAlignment { get; set; } = TextAlignment.Left;

    /// <summary>
    /// Apply style to a layout element.
    /// </summary>
    public void ApplyTo(HelioLayoutElement element)
    {
        if (element == null) return;

        if (MinWidth >= 0f || MinHeight >= 0f)
        {
            element.MinSize.Value = new float2(
                MinWidth >= 0f ? MinWidth : element.MinSize.Value.x,
                MinHeight >= 0f ? MinHeight : element.MinSize.Value.y);
        }

        if (PreferredWidth >= 0f || PreferredHeight >= 0f)
        {
            element.PreferredSize.Value = new float2(
                PreferredWidth >= 0f ? PreferredWidth : element.PreferredSize.Value.x,
                PreferredHeight >= 0f ? PreferredHeight : element.PreferredSize.Value.y);
        }

        if (FlexibleWidth >= 0f || FlexibleHeight >= 0f)
        {
            element.FlexibleSize.Value = new float2(
                FlexibleWidth >= 0f ? FlexibleWidth : element.FlexibleSize.Value.x,
                FlexibleHeight >= 0f ? FlexibleHeight : element.FlexibleSize.Value.y);
        }
    }

    /// <summary>
    /// Reset all values to defaults.
    /// </summary>
    public void Reset()
    {
        MinWidth = -1f;
        MinHeight = -1f;
        PreferredWidth = -1f;
        PreferredHeight = -1f;
        FlexibleWidth = -1f;
        FlexibleHeight = -1f;
        ForceExpandWidth = true;
        ForceExpandHeight = true;
        ChildAlignment = TextAlignment.Left;
    }
}

/// <summary>
/// Fluent API builder for Helio UI hierarchies.
/// </summary>
public class HelioUIBuilder
{
    // ===== STATE =====

    private readonly Slot _root;
    private Slot _current;
    private readonly Stack<Slot> _layoutStack = new();
    private Component? _lastComponent;

    /// <summary>
    /// Style settings for created elements.
    /// </summary>
    public HelioUIStyle Style { get; } = new();

    /// <summary>
    /// The root slot of this builder.
    /// </summary>
    public Slot Root => _root;

    /// <summary>
    /// The current slot being built.
    /// </summary>
    public Slot CurrentSlot => _current;

    /// <summary>
    /// The last component that was created.
    /// </summary>
    public Component LastComponent => _lastComponent;

    /// <summary>
    /// Get the current rect transform.
    /// </summary>
    public HelioRectTransform CurrentRect => _current?.GetComponent<HelioRectTransform>();

    // ===== CONSTRUCTOR =====

    /// <summary>
    /// Create a builder for the given slot.
    /// </summary>
    public HelioUIBuilder(Slot root)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _current = root;
    }

    /// <summary>
    /// Create a builder for a RectTransform.
    /// </summary>
    public HelioUIBuilder(HelioRectTransform rect) : this(rect?.Slot ?? throw new ArgumentNullException(nameof(rect)))
    {
    }

    // ===== STATIC FACTORY METHODS (backward compatibility) =====

    /// <summary>
    /// Create a new Helio canvas under the world's root slot.
    /// </summary>
    public static Slot CreateCanvas(World world, string name = "HelioCanvas", float2? referenceSize = null)
    {
        var root = world.RootSlot.AddSlot(name);
        var canvas = root.AttachComponent<HelioCanvas>();
        if (referenceSize.HasValue)
        {
            canvas.ReferenceSize.Value = referenceSize.Value;
        }
        return root;
    }

    /// <summary>
    /// Add a simple panel with RectTransform, optional size and anchors.
    /// </summary>
    public static Slot AddPanel(Slot parent, string name = "Panel", float2? size = null, float2? anchorMin = null, float2? anchorMax = null)
    {
        var slot = parent.AddSlot(name);
        var rect = slot.AttachComponent<HelioRectTransform>();

        if (anchorMin.HasValue)
            rect.AnchorMin.Value = anchorMin.Value;
        if (anchorMax.HasValue)
            rect.AnchorMax.Value = anchorMax.Value;

        if (size.HasValue)
        {
            rect.AnchorMin.Value = float2.Zero;
            rect.AnchorMax.Value = float2.Zero;
            rect.OffsetMin.Value = float2.Zero;
            rect.OffsetMax.Value = size.Value;
        }

        return slot;
    }

    public static HelioVerticalLayout AddVerticalLayout(Slot slot)
    {
        return slot.AttachComponent<HelioVerticalLayout>();
    }

    public static HelioHorizontalLayout AddHorizontalLayout(Slot slot)
    {
        return slot.AttachComponent<HelioHorizontalLayout>();
    }

    // ===== LAYOUT METHODS =====

    /// <summary>
    /// Start a vertical layout group.
    /// </summary>
    public HelioUIBuilder VerticalLayout(float spacing = 8f, float4? padding = null)
    {
        var slot = _current.AddSlot("VerticalLayout");
        slot.AttachComponent<HelioRectTransform>();
        var layout = slot.AttachComponent<HelioVerticalLayout>();
        layout.Spacing.Value = new float2(spacing, spacing);
        if (padding.HasValue)
            layout.Padding.Value = padding.Value;

        _layoutStack.Push(_current);
        _current = slot;
        _lastComponent = layout;
        return this;
    }

    /// <summary>
    /// Start a horizontal layout group.
    /// </summary>
    public HelioUIBuilder HorizontalLayout(float spacing = 8f, float4? padding = null)
    {
        var slot = _current.AddSlot("HorizontalLayout");
        slot.AttachComponent<HelioRectTransform>();
        var layout = slot.AttachComponent<HelioHorizontalLayout>();
        layout.Spacing.Value = new float2(spacing, spacing);
        if (padding.HasValue)
            layout.Padding.Value = padding.Value;

        _layoutStack.Push(_current);
        _current = slot;
        _lastComponent = layout;
        return this;
    }

    /// <summary>
    /// End the current layout group.
    /// </summary>
    public HelioUIBuilder EndLayout()
    {
        if (_layoutStack.Count > 0)
        {
            _current = _layoutStack.Pop();
        }
        return this;
    }

    /// <summary>
    /// Split current area horizontally. Returns list of RectTransforms.
    /// </summary>
    public List<HelioRectTransform> SplitHorizontally(params float[] proportions)
    {
        var results = new List<HelioRectTransform>();
        float cursor = 0f;

        for (int i = 0; i < proportions.Length; i++)
        {
            var slot = _current.AddSlot($"Split_{i}");
            var rect = slot.AttachComponent<HelioRectTransform>();
            rect.AnchorMin.Value = new float2(cursor, 0f);
            cursor += proportions[i];
            rect.AnchorMax.Value = new float2(cursor, 1f);
            results.Add(rect);
            _lastComponent = rect;
        }

        return results;
    }

    /// <summary>
    /// Split with left/right output and optional gap.
    /// </summary>
    public void SplitHorizontally(float proportion, out HelioRectTransform left, out HelioRectTransform right, float gap = 0f)
    {
        float halfGap = gap * 0.5f;

        var leftSlot = _current.AddSlot("Left");
        left = leftSlot.AttachComponent<HelioRectTransform>();
        left.AnchorMin.Value = new float2(0f, 0f);
        left.AnchorMax.Value = new float2(proportion - halfGap, 1f);

        var rightSlot = _current.AddSlot("Right");
        right = rightSlot.AttachComponent<HelioRectTransform>();
        right.AnchorMin.Value = new float2(proportion + halfGap, 0f);
        right.AnchorMax.Value = new float2(1f, 1f);

        _lastComponent = right;
    }

    /// <summary>
    /// Create header at top with fixed height, content below.
    /// </summary>
    public void HorizontalHeader(float size, out HelioRectTransform header, out HelioRectTransform content)
    {
        var headerSlot = _current.AddSlot("Header");
        header = headerSlot.AttachComponent<HelioRectTransform>();
        header.AnchorMin.Value = new float2(0f, 1f);
        header.AnchorMax.Value = new float2(1f, 1f);
        header.OffsetMin.Value = new float2(0f, -size);
        header.OffsetMax.Value = float2.Zero;

        var contentSlot = _current.AddSlot("Content");
        content = contentSlot.AttachComponent<HelioRectTransform>();
        content.AnchorMin.Value = float2.Zero;
        content.AnchorMax.Value = float2.One;
        content.OffsetMax.Value = new float2(0f, -size);

        _lastComponent = content;
    }

    /// <summary>
    /// Create footer at bottom with fixed height, content above.
    /// </summary>
    public void HorizontalFooter(float size, out HelioRectTransform footer, out HelioRectTransform content)
    {
        var contentSlot = _current.AddSlot("Content");
        content = contentSlot.AttachComponent<HelioRectTransform>();
        content.AnchorMin.Value = float2.Zero;
        content.AnchorMax.Value = float2.One;
        content.OffsetMin.Value = new float2(0f, size);

        var footerSlot = _current.AddSlot("Footer");
        footer = footerSlot.AttachComponent<HelioRectTransform>();
        footer.AnchorMin.Value = float2.Zero;
        footer.AnchorMax.Value = new float2(1f, 0f);
        footer.OffsetMin.Value = float2.Zero;
        footer.OffsetMax.Value = new float2(0f, size);

        _lastComponent = content;
    }

    /// <summary>
    /// Create scrollable area with mask.
    /// </summary>
    public HelioScrollView ScrollArea()
    {
        var slot = _current.AddSlot("ScrollArea");
        var rect = slot.AttachComponent<HelioRectTransform>();
        rect.AnchorMin.Value = float2.Zero;
        rect.AnchorMax.Value = float2.One;

        var mask = slot.AttachComponent<HelioMask>();
        var maskPanel = slot.AttachComponent<HelioPanel>();
        maskPanel.BackgroundColor.Value = color.Transparent;
        maskPanel.BorderWidth.Value = 0f;

        var scrollView = slot.AttachComponent<HelioScrollView>();
        scrollView.Viewport.Target = slot;

        var contentSlot = slot.AddSlot("Content");
        var contentRect = contentSlot.AttachComponent<HelioRectTransform>();
        contentRect.AnchorMin.Value = float2.Zero;
        contentRect.AnchorMax.Value = float2.One;
        scrollView.Content.Target = contentSlot;

        _layoutStack.Push(_current);
        _current = contentSlot;
        _lastComponent = scrollView;

        return scrollView;
    }

    /// <summary>
    /// Add content size fitter behavior.
    /// </summary>
    public HelioUIBuilder FitContent(SizeFit horizontal, SizeFit vertical)
    {
        var fitter = _current.GetComponent<HelioContentSizeFitter>()
            ?? _current.AttachComponent<HelioContentSizeFitter>();

        fitter.HorizontalFit.Value = horizontal;
        fitter.VerticalFit.Value = vertical;
        _lastComponent = fitter;

        return this;
    }

    // ===== SPACING =====

    /// <summary>
    /// Add a fixed-height spacer.
    /// </summary>
    public HelioUIBuilder Spacer(float height)
    {
        var slot = _current.AddSlot("Spacer");
        var rect = slot.AttachComponent<HelioRectTransform>();
        rect.OffsetMax.Value = new float2(0, height);

        var layoutElement = slot.AttachComponent<HelioLayoutElement>();
        layoutElement.MinSize.Value = new float2(0, height);
        layoutElement.PreferredSize.Value = new float2(0, height);
        layoutElement.FlexibleSize.Value = float2.Zero;

        return this;
    }

    /// <summary>
    /// Add a flexible spacer that expands to fill available space.
    /// </summary>
    public HelioUIBuilder FlexibleSpacer()
    {
        var slot = _current.AddSlot("FlexibleSpacer");
        slot.AttachComponent<HelioRectTransform>();

        var layoutElement = slot.AttachComponent<HelioLayoutElement>();
        layoutElement.MinSize.Value = float2.Zero;
        layoutElement.PreferredSize.Value = float2.Zero;
        layoutElement.FlexibleSize.Value = float2.One;

        return this;
    }

    // ===== VISUAL COMPONENTS =====

    /// <summary>
    /// Add a text element.
    /// </summary>
    public HelioText Text(string content, float fontSize = 14f, color? textColor = null)
    {
        var slot = _current.AddSlot("Text");
        var rect = slot.AttachComponent<HelioRectTransform>();
        rect.AnchorMin.Value = float2.Zero;
        rect.AnchorMax.Value = float2.One;

        var text = slot.AttachComponent<HelioText>();
        text.Content.Value = content;
        text.FontSize.Value = fontSize;
        text.Color.Value = textColor ?? HelioUITheme.TextPrimary;

        Logging.Logger.Log($"[HelioUIBuilder.Text] Created '{content}' size={fontSize}");

        // Add layout element for sizing
        var layoutElement = slot.AttachComponent<HelioLayoutElement>();
        layoutElement.PreferredSize.Value = new float2(0, fontSize * 1.5f);

        _lastComponent = text;
        return text;
    }

    /// <summary>
    /// Add an image element.
    /// </summary>
    public HelioImage Image(string texturePath = null, color? tint = null)
    {
        var slot = _current.AddSlot("Image");
        slot.AttachComponent<HelioRectTransform>();

        var image = slot.AttachComponent<HelioImage>();
        if (!string.IsNullOrEmpty(texturePath))
            image.TexturePath.Value = texturePath;
        if (tint.HasValue)
            image.Tint.Value = tint.Value;

        _lastComponent = image;
        return image;
    }

    /// <summary>
    /// Add a panel element.
    /// </summary>
    public HelioPanel Panel(color? backgroundColor = null)
    {
        var slot = _current.AddSlot("Panel");
        slot.AttachComponent<HelioRectTransform>();

        var panel = slot.AttachComponent<HelioPanel>();
        if (backgroundColor.HasValue)
            panel.BackgroundColor.Value = backgroundColor.Value;

        _lastComponent = panel;
        return panel;
    }

    // ===== INTERACTIVE COMPONENTS =====

    /// <summary>
    /// Add a button.
    /// </summary>
    public HelioButton Button(string label, Action onClick = null)
    {
        var slot = _current.AddSlot("Button");
        var rect = slot.AttachComponent<HelioRectTransform>();

        // Add button component
        var button = slot.AttachComponent<HelioButton>();
        if (onClick != null)
            button.OnClick.Target = onClick;

        // Add background panel
        var bgSlot = slot.AddSlot("Background");
        bgSlot.AttachComponent<HelioRectTransform>().AnchorMax.Value = float2.One;
        var panel = bgSlot.AttachComponent<HelioPanel>();
        panel.BackgroundColor.Value = button.NormalColor.Value;
        button.Background.Target = panel;

        // Add label text
        var labelSlot = slot.AddSlot("Label");
        var labelRect = labelSlot.AttachComponent<HelioRectTransform>();
        labelRect.AnchorMin.Value = float2.Zero;
        labelRect.AnchorMax.Value = float2.One;
        var text = labelSlot.AttachComponent<HelioText>();
        text.Content.Value = label;
        text.Alignment.Value = TextAlignment.Center;
        button.Label.Target = text;

        // Add layout element
        var layoutElement = slot.AttachComponent<HelioLayoutElement>();
        layoutElement.PreferredSize.Value = new float2(100f, 32f);

        _lastComponent = button;
        return button;
    }

    /// <summary>
    /// Button with custom colors (for title bar buttons).
    /// </summary>
    public HelioButton Button(string label, color buttonColor, color textColor, Action onClick = null)
    {
        var button = Button(label, onClick);
        button.NormalColor.Value = buttonColor;
        button.HoveredColor.Value = buttonColor.Lighten(0.15f);
        button.PressedColor.Value = buttonColor.Darken(0.1f);

        if (button.Label?.Target != null)
            button.Label.Target.Color.Value = textColor;

        if (button.Background?.Target != null)
            button.Background.Target.BackgroundColor.Value = buttonColor;

        return button;
    }

    /// <summary>
    /// Add a toggle (checkbox).
    /// </summary>
    public HelioToggle Toggle(string label, bool value = false, Action<bool> onChanged = null)
    {
        var slot = _current.AddSlot("Toggle");
        slot.AttachComponent<HelioRectTransform>();

        var toggle = slot.AttachComponent<HelioToggle>();
        toggle.Value.Value = value;
        if (onChanged != null)
            toggle.OnValueChanged.Target = onChanged;

        // Add background
        var bgSlot = slot.AddSlot("Background");
        var bgRect = bgSlot.AttachComponent<HelioRectTransform>();
        bgRect.OffsetMax.Value = new float2(24f, 24f);
        var panel = bgSlot.AttachComponent<HelioPanel>();
        toggle.Background.Target = panel;

        // Add checkmark
        var checkSlot = bgSlot.AddSlot("Checkmark");
        var checkRect = checkSlot.AttachComponent<HelioRectTransform>();
        checkRect.AnchorMin.Value = new float2(0.2f, 0.2f);
        checkRect.AnchorMax.Value = new float2(0.8f, 0.8f);
        var checkImage = checkSlot.AttachComponent<HelioImage>();
        toggle.Checkmark.Target = checkImage;

        // Add label
        if (!string.IsNullOrEmpty(label))
        {
            var labelSlot = slot.AddSlot("Label");
            var labelRect = labelSlot.AttachComponent<HelioRectTransform>();
            labelRect.OffsetMin.Value = new float2(32f, 0);
            var text = labelSlot.AttachComponent<HelioText>();
            text.Content.Value = label;
            toggle.Label.Target = text;
        }

        // Layout element
        var layoutElement = slot.AttachComponent<HelioLayoutElement>();
        layoutElement.PreferredSize.Value = new float2(0, 24f);

        _lastComponent = toggle;
        return toggle;
    }

    /// <summary>
    /// Add a slider.
    /// </summary>
    public HelioSlider Slider(float value, float min, float max, Action<float> onChanged = null)
    {
        var slot = _current.AddSlot("Slider");
        slot.AttachComponent<HelioRectTransform>();

        var slider = slot.AttachComponent<HelioSlider>();
        slider.MinValue.Value = min;
        slider.MaxValue.Value = max;
        slider.Value.Value = value;
        if (onChanged != null)
            slider.OnValueChanged.Target = onChanged;

        // Add track
        var trackSlot = slot.AddSlot("Track");
        var trackRect = trackSlot.AttachComponent<HelioRectTransform>();
        trackRect.AnchorMin.Value = new float2(0, 0.4f);
        trackRect.AnchorMax.Value = new float2(1, 0.6f);
        var track = trackSlot.AttachComponent<HelioPanel>();
        track.BackgroundColor.Value = new color(0.2f, 0.2f, 0.2f, 1f);
        slider.Track.Target = track;

        // Add fill
        var fillSlot = trackSlot.AddSlot("Fill");
        var fillRect = fillSlot.AttachComponent<HelioRectTransform>();
        fillRect.AnchorMin.Value = float2.Zero;
        fillRect.AnchorMax.Value = new float2(slider.GetNormalizedValue(), 1f);
        var fill = fillSlot.AttachComponent<HelioImage>();
        fill.Tint.Value = new color(0.2f, 0.6f, 1f, 1f);
        slider.FillImage.Target = fill;

        // Add handle
        var handleSlot = slot.AddSlot("Handle");
        var handleRect = handleSlot.AttachComponent<HelioRectTransform>();
        handleRect.OffsetMax.Value = new float2(16f, 16f);
        var handle = handleSlot.AttachComponent<HelioImage>();
        handle.Tint.Value = new color(1f, 1f, 1f, 1f);
        slider.HandleImage.Target = handle;

        // Layout element
        var layoutElement = slot.AttachComponent<HelioLayoutElement>();
        layoutElement.PreferredSize.Value = new float2(0, 24f);

        _lastComponent = slider;
        return slider;
    }

    /// <summary>
    /// Add a text field.
    /// </summary>
    public HelioTextField TextField(string placeholder = "", Action<string> onChanged = null)
    {
        var slot = _current.AddSlot("TextField");
        slot.AttachComponent<HelioRectTransform>();

        var field = slot.AttachComponent<HelioTextField>();
        field.PlaceholderText.Value = placeholder;
        if (onChanged != null)
            field.OnValueChanged.Target = onChanged;

        // Add background
        var bgSlot = slot.AddSlot("Background");
        var bgRect = bgSlot.AttachComponent<HelioRectTransform>();
        bgRect.AnchorMax.Value = float2.One;
        var panel = bgSlot.AttachComponent<HelioPanel>();
        field.Background.Target = panel;

        // Add placeholder text
        var placeholderSlot = slot.AddSlot("Placeholder");
        var phRect = placeholderSlot.AttachComponent<HelioRectTransform>();
        phRect.AnchorMax.Value = float2.One;
        phRect.OffsetMin.Value = new float2(8f, 0);
        var phText = placeholderSlot.AttachComponent<HelioText>();
        phText.Content.Value = placeholder;
        phText.Color.Value = new color(0.5f, 0.5f, 0.5f, 1f);
        field.PlaceholderComponent.Target = phText;

        // Add text display
        var textSlot = slot.AddSlot("Text");
        var textRect = textSlot.AttachComponent<HelioRectTransform>();
        textRect.AnchorMax.Value = float2.One;
        textRect.OffsetMin.Value = new float2(8f, 0);
        var text = textSlot.AttachComponent<HelioText>();
        field.TextComponent.Target = text;

        // Layout element
        var layoutElement = slot.AttachComponent<HelioLayoutElement>();
        layoutElement.PreferredSize.Value = new float2(0, 32f);

        _lastComponent = field;
        return field;
    }

    /// <summary>
    /// Add a dropdown.
    /// </summary>
    public HelioDropdown Dropdown(string[] options, int selected = 0, Action<int> onChanged = null)
    {
        var slot = _current.AddSlot("Dropdown");
        slot.AttachComponent<HelioRectTransform>();

        var dropdown = slot.AttachComponent<HelioDropdown>();
        dropdown.SelectedIndex.Value = selected;
        if (onChanged != null)
            dropdown.OnValueChanged.Target = onChanged;

        // Add options
        foreach (var opt in options)
            dropdown.Options.Add(opt);

        // Add background
        var bgSlot = slot.AddSlot("Background");
        var bgRect = bgSlot.AttachComponent<HelioRectTransform>();
        bgRect.AnchorMax.Value = float2.One;
        var panel = bgSlot.AttachComponent<HelioPanel>();
        dropdown.Background.Target = panel;

        // Add label
        var labelSlot = slot.AddSlot("Label");
        var labelRect = labelSlot.AttachComponent<HelioRectTransform>();
        labelRect.AnchorMax.Value = float2.One;
        labelRect.OffsetMin.Value = new float2(8f, 0);
        labelRect.OffsetMax.Value = new float2(-32f, 0);
        var label = labelSlot.AttachComponent<HelioText>();
        label.Content.Value = options.Length > selected ? options[selected] : "";
        dropdown.LabelText.Target = label;

        // Layout element
        var layoutElement = slot.AttachComponent<HelioLayoutElement>();
        layoutElement.PreferredSize.Value = new float2(0, 32f);

        _lastComponent = dropdown;
        return dropdown;
    }

    // ===== UTILITY =====

    /// <summary>
    /// Get the last created component as a specific type.
    /// </summary>
    public T Current<T>() where T : Component
    {
        return _lastComponent as T;
    }

    /// <summary>
    /// Nest into current slot (push to stack, stay at current).
    /// </summary>
    public HelioUIBuilder Nest()
    {
        _layoutStack.Push(_current);
        return this;
    }

    /// <summary>
    /// Nest out to parent level.
    /// </summary>
    public HelioUIBuilder NestOut()
    {
        if (_layoutStack.Count > 0)
            _current = _layoutStack.Pop();
        return this;
    }

    /// <summary>
    /// Nest into specific slot.
    /// </summary>
    public HelioUIBuilder NestInto(Slot slot)
    {
        if (slot != null)
        {
            _layoutStack.Push(_current);
            _current = slot;
        }
        return this;
    }

    /// <summary>
    /// Nest into RectTransform's slot.
    /// </summary>
    public HelioUIBuilder NestInto(HelioRectTransform rect)
    {
        return rect != null ? NestInto(rect.Slot) : this;
    }

    /// <summary>
    /// Pop out of nested slot.
    /// </summary>
    public HelioUIBuilder Unnest()
    {
        return NestOut();
    }
}
