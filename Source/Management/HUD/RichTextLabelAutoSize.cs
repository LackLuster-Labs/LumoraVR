using LumoraVR.Source.Helpers;
using Godot;
using System;

namespace LumoraVR.Source.Management.HUD;

/// <summary>
/// Implements the Auto Size text feature.
/// </summary>
[Tool]
public partial class RichTextLabelAutoSize : RichTextLabel
{
    /// <summary>
    /// Whether or not the text will automatically resize.
    /// </summary>
    [Export]
    public bool AutoSize = true;
    /// <summary>
    /// The minimum and maximum size the text can resize to.
    /// </summary>
    [Export]
    public Vector2 MinMaxSize = new(8f, 128f);

    public override void _Ready()
    {
        LayoutMode = 1;
        SetAnchorsPreset(LayoutPreset.FullRect);

        _label = new Label
        {
            Visible = false
        };
        _label.GetMinimumSize();
        _label.LabelSettings = new LabelSettings
        {
            FontSize = GetThemeDefaultFontSize(),
            Font = GetThemeDefaultFont()
        };

        var node = new Node();
        AddChild(node);
        node.AddChild(_label);
    }

    /// <summary>
    /// The current sizer value.
    /// </summary>
    float _sizer;
    /// <summary>
    /// The dummy label used to get sizing information.
    /// </summary>
    Label _label;
    /// <summary>
    /// The last string used.
    /// </summary>
    string _lastString;

    private static readonly StringName _normal_font_size = "normal_font_size";
    private static readonly StringName _bold_font_size = "bold_font_size";
    private static readonly StringName _italics_font_size = "italics_font_size";
    private static readonly StringName _bold_italics_font_size = "bold_italics_font_size";
    private static readonly StringName _mono_font_size = "mono_font_size";

    public override void _Process(double delta)
    {
        if (!AutoSize)
        {
            return;
        }
        if (MinMaxSize.Y < MinMaxSize.X)
        {
            return;
        }

        if (_lastString != Text)
        {
            _lastString = Text;
            _label.Text = GeneralHelpers.StripBBCode(Text);
        }

        var portions = (GetParent() as Control).Size / _label.GetMinimumSize();
        var sizer = GetThemeDefaultFontSize() * Math.Min(portions.X, portions.Y);
        var clamp = (int)Mathf.Clamp(sizer, MinMaxSize.X, MinMaxSize.Y);
        sizer = clamp;

        if (_sizer != sizer)
        {
            AddThemeFontSizeOverride(_normal_font_size, clamp);
            AddThemeFontSizeOverride(_bold_font_size, clamp);
            AddThemeFontSizeOverride(_italics_font_size, clamp);
            AddThemeFontSizeOverride(_bold_italics_font_size, clamp);
            AddThemeFontSizeOverride(_mono_font_size, clamp);
            _sizer = sizer;
        }
    }
}
