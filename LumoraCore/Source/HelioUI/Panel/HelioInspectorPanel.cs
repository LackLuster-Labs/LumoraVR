using Lumora.Core.Math;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Base class for inspector-style panels with split hierarchy/detail layout.
/// </summary>
[ComponentCategory("HelioUI/Panel")]
public class HelioInspectorPanel : Component
{
    // ===== CONFIGURATION =====

    /// <summary>
    /// Canvas pixel size.
    /// </summary>
    protected virtual float2 CanvasSize => new float2(1000f, 2000f);

    /// <summary>
    /// Pixels per world unit for physical sizing.
    /// </summary>
    protected virtual float PixelScale => 4000f; // ~50cm tall

    /// <summary>
    /// Split ratio for hierarchy pane (0-1).
    /// </summary>
    protected virtual float HierarchySplit => 0.4f;

    // ===== REFERENCES =====

    protected SyncRef<HelioWindowPanel> _panel;
    protected SyncRef<HelioCanvas> _canvas;

    /// <summary>
    /// The window panel.
    /// </summary>
    public HelioWindowPanel Panel => _panel?.Target;

    /// <summary>
    /// The HelioUI canvas.
    /// </summary>
    public HelioCanvas Canvas => _canvas?.Target;

    // ===== INITIALIZATION =====

    public override void OnAwake()
    {
        base.OnAwake();
        _panel = new SyncRef<HelioWindowPanel>(this);
        _canvas = new SyncRef<HelioCanvas>(this);
    }

    public override void OnStart()
    {
        base.OnStart();
    }

    /// <summary>
    /// Setup the inspector panel with split layout.
    /// Returns the HelioWindowPanel for further configuration.
    /// </summary>
    protected HelioWindowPanel Setup(
        color hierarchyColor,
        color detailColor,
        out HelioRectTransform hierarchyHeader,
        out HelioRectTransform hierarchyContent,
        out HelioRectTransform detailHeader,
        out HelioRectTransform detailContent,
        out HelioRectTransform detailFooter)
    {
        // Create window panel
        var windowPanel = Slot.AttachComponent<HelioWindowPanel>();
        _panel.Target = windowPanel;
        windowPanel.Padding.Value = 0.005f;
        windowPanel.ZPadding.Value = 0.005f;
        windowPanel.Thickness.Value = 0.01f;

        // Create canvas
        var canvasSlot = windowPanel.ContentSlot?.Target ?? Slot.AddSlot("Canvas");
        var canvas = canvasSlot.AttachComponent<HelioCanvas>();
        _canvas.Target = canvas;
        canvas.ReferenceSize.Value = CanvasSize;
        canvas.PixelScale.Value = PixelScale;
        canvas.BackgroundColor.Value = HelioUITheme.PanelBackground;

        // Root rect
        var rootRect = canvasSlot.GetComponent<HelioRectTransform>() ?? canvasSlot.AttachComponent<HelioRectTransform>();
        rootRect.AnchorMin.Value = float2.Zero;
        rootRect.AnchorMax.Value = float2.One;

        // Main overlay background similar to Radiant UI
        var mainBgSlot = canvasSlot.AddSlot("MainBackground");
        var mainBgRect = mainBgSlot.AttachComponent<HelioRectTransform>();
        mainBgRect.AnchorMin.Value = float2.Zero;
        mainBgRect.AnchorMax.Value = float2.One;
        var mainPanel = mainBgSlot.AttachComponent<HelioPanel>();
        mainPanel.BackgroundColor.Value = HelioUITheme.PanelOverlay;

        // Create horizontal split
        var splitSlot = canvasSlot.AddSlot("Split");
        var splitRect = splitSlot.AttachComponent<HelioRectTransform>();
        splitRect.AnchorMin.Value = float2.Zero;
        splitRect.AnchorMax.Value = float2.One;

        // Left pane (hierarchy)
        var leftSlot = splitSlot.AddSlot("Hierarchy");
        var leftRect = leftSlot.AttachComponent<HelioRectTransform>();
        leftRect.AnchorMin.Value = new float2(0f, 0f);
        leftRect.AnchorMax.Value = new float2(HierarchySplit, 1f);

        var leftPanel = leftSlot.AttachComponent<HelioPanel>();
        leftPanel.BackgroundColor.Value = new color(hierarchyColor.r, hierarchyColor.g, hierarchyColor.b, 0.35f);
        leftPanel.BorderWidth.Value = 1f;
        leftPanel.BorderColor.Value = new color(hierarchyColor.r, hierarchyColor.g, hierarchyColor.b, 0.65f);

        // Right pane (detail)
        var rightSlot = splitSlot.AddSlot("Detail");
        var rightRect = rightSlot.AttachComponent<HelioRectTransform>();
        rightRect.AnchorMin.Value = new float2(HierarchySplit, 0f);
        rightRect.AnchorMax.Value = new float2(1f, 1f);

        var rightPanel = rightSlot.AttachComponent<HelioPanel>();
        rightPanel.BackgroundColor.Value = new color(detailColor.r, detailColor.g, detailColor.b, 0.35f);
        rightPanel.BorderWidth.Value = 1f;
        rightPanel.BorderColor.Value = new color(detailColor.r, detailColor.g, detailColor.b, 0.65f);

        // Setup hierarchy pane (header + content)
        SetupHeaderContent(leftSlot, HelioUITheme.HeaderHeight, out hierarchyHeader, out hierarchyContent);
        AddScrollArea(hierarchyContent);

        // Setup detail pane (header + content + footer)
        SetupHeaderContentFooter(rightSlot, HelioUITheme.HeaderHeight, HelioUITheme.FooterHeight, out detailHeader, out detailContent, out detailFooter);
        AddScrollArea(detailContent);

        return windowPanel;
    }

    /// <summary>
    /// Create header + content layout.
    /// </summary>
    protected void SetupHeaderContent(Slot parent, float headerHeight, out HelioRectTransform header, out HelioRectTransform content)
    {
        var vLayout = parent.AttachComponent<HelioVerticalLayout>();
        vLayout.Spacing.Value = new float2(0f, 4f);
        vLayout.Padding.Value = new float4(4f, 4f, 4f, 4f);

        // Header
        var headerSlot = parent.AddSlot("Header");
        header = headerSlot.AttachComponent<HelioRectTransform>();
        var headerLayout = headerSlot.AttachComponent<HelioLayoutElement>();
        headerLayout.PreferredSize.Value = new float2(0f, headerHeight);
        headerLayout.FlexibleSize.Value = new float2(1f, 0f);

        var headerHLayout = headerSlot.AttachComponent<HelioHorizontalLayout>();
        headerHLayout.Spacing.Value = new float2(4f, 0f);
        headerHLayout.Padding.Value = new float4(4f, 0f, 4f, 0f);

        // Content
        var contentSlot = parent.AddSlot("Content");
        content = contentSlot.AttachComponent<HelioRectTransform>();
        var contentLayout = contentSlot.AttachComponent<HelioLayoutElement>();
        contentLayout.FlexibleSize.Value = new float2(1f, 1f);
    }

    /// <summary>
    /// Create header + content + footer layout.
    /// </summary>
    protected void SetupHeaderContentFooter(Slot parent, float headerHeight, float footerHeight,
        out HelioRectTransform header, out HelioRectTransform content, out HelioRectTransform footer)
    {
        var vLayout = parent.AttachComponent<HelioVerticalLayout>();
        vLayout.Spacing.Value = new float2(0f, 4f);
        vLayout.Padding.Value = new float4(4f, 4f, 4f, 4f);

        // Header
        var headerSlot = parent.AddSlot("Header");
        header = headerSlot.AttachComponent<HelioRectTransform>();
        var headerLayout = headerSlot.AttachComponent<HelioLayoutElement>();
        headerLayout.PreferredSize.Value = new float2(0f, headerHeight);
        headerLayout.FlexibleSize.Value = new float2(1f, 0f);

        var headerHLayout = headerSlot.AttachComponent<HelioHorizontalLayout>();
        headerHLayout.Spacing.Value = new float2(4f, 0f);

        // Content
        var contentSlot = parent.AddSlot("Content");
        content = contentSlot.AttachComponent<HelioRectTransform>();
        var contentLayout = contentSlot.AttachComponent<HelioLayoutElement>();
        contentLayout.FlexibleSize.Value = new float2(1f, 1f);

        // Footer
        var footerSlot = parent.AddSlot("Footer");
        footer = footerSlot.AttachComponent<HelioRectTransform>();
        var footerLayout = footerSlot.AttachComponent<HelioLayoutElement>();
        footerLayout.PreferredSize.Value = new float2(0f, footerHeight);
        footerLayout.FlexibleSize.Value = new float2(1f, 0f);

        var footerHLayout = footerSlot.AttachComponent<HelioHorizontalLayout>();
        footerHLayout.Spacing.Value = new float2(4f, 0f);
    }

    /// <summary>
    /// Add scroll area to content.
    /// </summary>
    protected void AddScrollArea(HelioRectTransform content)
    {
        if (content?.Slot == null) return;

        var scrollSlot = content.Slot.AddSlot("ScrollArea");
        var scrollRect = scrollSlot.AttachComponent<HelioRectTransform>();
        scrollRect.AnchorMin.Value = float2.Zero;
        scrollRect.AnchorMax.Value = float2.One;

        var vLayout = scrollSlot.AttachComponent<HelioVerticalLayout>();
        vLayout.Spacing.Value = new float2(0f, 4f);

        // Add content size fitter for scroll behavior
        var fitter = scrollSlot.AttachComponent<HelioContentSizeFitter>();
        fitter.HorizontalFit.Value = SizeFit.Disabled;
        fitter.VerticalFit.Value = SizeFit.MinSize;
    }
}
