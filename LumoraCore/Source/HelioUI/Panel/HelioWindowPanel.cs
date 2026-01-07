using System;
using System.Collections.Generic;
using Lumora.Core.Math;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Button info for window panel title bar.
/// </summary>
public class HelioTitleButton
{
    public color ButtonColor { get; set; } = new color(0.5f, 0.5f, 0.5f, 1f);
    public color IconColor { get; set; } = color.Black;
    public string IconText { get; set; } = "";
    public Action<HelioTitleButton> OnPressed { get; set; }
    public bool Enabled { get; set; } = true;
    internal Slot ButtonSlot { get; set; }
}

/// <summary>
/// 3D Window panel with title bar and buttons.
/// Provides a titled window frame with interactive controls.
/// </summary>
[ComponentCategory("HelioUI/Panel")]
public class HelioWindowPanel : Component
{
    // ===== SYNC FIELDS =====

    /// <summary>
    /// Show the header title bar.
    /// </summary>
    public Sync<bool> ShowHeader { get; private set; }

    /// <summary>
    /// Show the grab handle.
    /// </summary>
    public Sync<bool> ShowHandle { get; private set; }

    /// <summary>
    /// Padding around content.
    /// </summary>
    public Sync<float> Padding { get; private set; }

    /// <summary>
    /// Z-axis padding.
    /// </summary>
    public Sync<float> ZPadding { get; private set; }

    /// <summary>
    /// Frame thickness.
    /// </summary>
    public Sync<float> Thickness { get; private set; }

    /// <summary>
    /// Panel background color.
    /// </summary>
    public Sync<color> Color { get; private set; }

    /// <summary>
    /// Panel title text.
    /// </summary>
    public Sync<string> Title { get; private set; }

    // ===== REFERENCES =====

    /// <summary>
    /// Slot for UI content.
    /// </summary>
    public SyncRef<Slot> ContentSlot { get; private set; }

    protected SyncRef<Slot> _headerSlot;
    protected SyncRef<Slot> _handleSlot;
    protected SyncRef<Slot> _buttonsSlot;

    // ===== STATE =====

    private readonly List<HelioTitleButton> _titleButtons = new();
    private HelioText _titleText;

    /// <summary>
    /// Event fired when panel is closed.
    /// </summary>
    public event Action OnPanelClose;

    /// <summary>
    /// Override close behavior.
    /// </summary>
    public Action<HelioWindowPanel> CloseOverride { get; set; }

    // ===== INITIALIZATION =====

    public override void OnAwake()
    {
        base.OnAwake();

        ShowHeader = new Sync<bool>(this, true);
        ShowHandle = new Sync<bool>(this, true);
        Padding = new Sync<float>(this, 0.005f);
        ZPadding = new Sync<float>(this, 0.002f);
        Thickness = new Sync<float>(this, 0.01f);
        Color = new Sync<color>(this, new color(1f, 1f, 1f, 0.5f));
        Title = new Sync<string>(this, "Panel");

        ContentSlot = new SyncRef<Slot>(this);
        _headerSlot = new SyncRef<Slot>(this);
        _handleSlot = new SyncRef<Slot>(this);
        _buttonsSlot = new SyncRef<Slot>(this);

        Title.OnChanged += _ => UpdateTitle();
        ShowHeader.OnChanged += _ => UpdateVisibility();
        ShowHandle.OnChanged += _ => UpdateVisibility();
    }

    public override void OnStart()
    {
        base.OnStart();
        // Only setup if not already initialized
        if (ContentSlot?.Target == null)
            SetupStructure();
    }

    /// <summary>
    /// Initialize the panel immediately (for synchronous setup).
    /// </summary>
    public void Initialize()
    {
        if (ContentSlot?.Target == null)
            SetupStructure();
    }

    // ===== SETUP =====

    protected virtual void SetupStructure()
    {
        // Content slot
        var contentSlot = Slot.AddSlot("Content");
        ContentSlot.Target = contentSlot;

        // Header slot
        var headerSlot = Slot.AddSlot("Header");
        _headerSlot.Target = headerSlot;
        SetupHeader(headerSlot);

        // Handle slot
        var handleSlot = Slot.AddSlot("Handle");
        _handleSlot.Target = handleSlot;

        UpdateVisibility();
    }

    protected virtual void SetupHeader(Slot headerSlot)
    {
        // Add rect to header
        var headerRect = headerSlot.AttachComponent<HelioRectTransform>();

        // Title text
        var titleSlot = headerSlot.AddSlot("Title");
        var titleRect = titleSlot.AttachComponent<HelioRectTransform>();
        titleRect.AnchorMin.Value = float2.Zero;
        titleRect.AnchorMax.Value = new float2(0.7f, 1f);

        _titleText = titleSlot.AttachComponent<HelioText>();
        _titleText.Content.Value = Title.Value;
        _titleText.FontSize.Value = 18f;
        _titleText.Color.Value = color.White;
        _titleText.Alignment.Value = TextAlignment.Left;

        // Buttons container
        var buttonsSlot = headerSlot.AddSlot("Buttons");
        _buttonsSlot.Target = buttonsSlot;

        var buttonsRect = buttonsSlot.AttachComponent<HelioRectTransform>();
        buttonsRect.AnchorMin.Value = new float2(0.7f, 0f);
        buttonsRect.AnchorMax.Value = float2.One;

        var buttonsLayout = buttonsSlot.AttachComponent<HelioHorizontalLayout>();
        buttonsLayout.Spacing.Value = new float2(4f, 0f);
    }

    // ===== BUTTON API =====

    /// <summary>
    /// Add a title button.
    /// </summary>
    public HelioTitleButton AddButton(color buttonColor, string iconText, color iconColor, Action<HelioTitleButton> callback)
    {
        var button = new HelioTitleButton
        {
            ButtonColor = buttonColor,
            IconText = iconText,
            IconColor = iconColor,
            OnPressed = callback
        };
        _titleButtons.Insert(0, button);
        RebuildButtons();
        return button;
    }

    /// <summary>
    /// Add close button (red X).
    /// </summary>
    public HelioTitleButton AddCloseButton()
    {
        return AddButton(HelioUITheme.AccentRed, "X", HelioUITheme.TextBlack, _ => Close());
    }

    /// <summary>
    /// Add parent/pin button (purple).
    /// </summary>
    public HelioTitleButton AddParentButton()
    {
        return AddButton(HelioUITheme.AccentPrimary, "P", HelioUITheme.TextPrimary, _ =>
        {
            Logging.Logger.Log($"[HelioWindowPanel] Pin pressed: '{Title.Value}'");
        });
    }

    /// <summary>
    /// Add help button (cyan ?).
    /// </summary>
    public HelioTitleButton AddHelpButton(Action<HelioCanvasPanel> helpGenerator)
    {
        return AddButton(HelioUITheme.AccentCyan, "?", HelioUITheme.TextBlack, _ =>
        {
            // Create help dialog
            var helpSlot = Slot.AddSlot("HelpDialog");
            var helpPanel = helpSlot.AttachComponent<HelioCanvasPanel>();
            helpPanel.CanvasSize = new float2(400f, 300f);
            helpPanel.PhysicalHeight = 0.25f;
            helpPanel.Panel?.AddCloseButton();
            if (helpPanel.Panel != null)
                helpPanel.Panel.Title.Value = Title.Value + " - Help";
            helpGenerator?.Invoke(helpPanel);
        });
    }

    /// <summary>
    /// Remove all buttons.
    /// </summary>
    public void ClearButtons()
    {
        foreach (var btn in _titleButtons)
        {
            btn.ButtonSlot?.Destroy();
        }
        _titleButtons.Clear();
    }

    protected virtual void RebuildButtons()
    {
        var buttonsSlot = _buttonsSlot?.Target;
        if (buttonsSlot == null) return;

        // Clear existing button slots
        foreach (var child in new List<Slot>(buttonsSlot.Children))
        {
            child.Destroy();
        }

        // Create buttons
        foreach (var btn in _titleButtons)
        {
            if (!btn.Enabled) continue;

            var btnSlot = buttonsSlot.AddSlot("Btn_" + btn.IconText);
            btn.ButtonSlot = btnSlot;

            var rect = btnSlot.AttachComponent<HelioRectTransform>();
            var layout = btnSlot.AttachComponent<HelioLayoutElement>();
            layout.PreferredSize.Value = new float2(32f, 32f);

            // Background panel
            var bgPanel = btnSlot.AttachComponent<HelioPanel>();
            bgPanel.BackgroundColor.Value = btn.ButtonColor;

            // Button component
            var button = btnSlot.AttachComponent<HelioButton>();
            button.NormalColor.Value = btn.ButtonColor;
            button.HoveredColor.Value = btn.ButtonColor * 1.2f;
            button.PressedColor.Value = btn.ButtonColor * 0.8f;
            button.Background.Target = bgPanel;

            // Wire up click via delegate
            var capturedBtn = btn;
            button.OnClick.Target = () => capturedBtn.OnPressed?.Invoke(capturedBtn);

            // Icon text
            var textSlot = btnSlot.AddSlot("Icon");
            var textRect = textSlot.AttachComponent<HelioRectTransform>();
            textRect.AnchorMin.Value = float2.Zero;
            textRect.AnchorMax.Value = float2.One;

            var text = textSlot.AttachComponent<HelioText>();
            text.Content.Value = btn.IconText;
            text.FontSize.Value = 16f;
            text.Color.Value = btn.IconColor;
            text.Alignment.Value = TextAlignment.Center;
        }
    }

    // ===== UPDATES =====

    private void UpdateTitle()
    {
        if (_titleText != null)
            _titleText.Content.Value = Title.Value;
    }

    private void UpdateVisibility()
    {
        if (_headerSlot?.Target != null)
            _headerSlot.Target.ActiveSelf.Value = ShowHeader.Value;

        if (_handleSlot?.Target != null)
            _handleSlot.Target.ActiveSelf.Value = ShowHandle.Value;
    }

    /// <summary>
    /// Close the panel.
    /// </summary>
    public void Close()
    {
        if (CloseOverride != null)
        {
            CloseOverride(this);
        }
        else
        {
            OnPanelClose?.Invoke();
            Slot.Destroy();
        }
    }
}
