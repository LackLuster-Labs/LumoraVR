// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Math;
using Lumora.Core.Persistence;

namespace Lumora.Core.Components.UI;

[ComponentCategory("UI")]
public class PanelShell : UIComponent
{
    public readonly Sync<string> Title;
    public readonly Sync<float2> Size;
    public readonly Sync<bool> AllowGrab;
    public readonly Sync<bool> Scalable;
    public readonly Sync<bool> ShowHeader;
    public readonly Sync<float> HeaderHeight;
    public readonly Sync<float> Padding;
    public readonly Sync<float> TitleTextSize;
    public readonly Sync<float> CloseButtonPadding;
    public readonly Sync<bool> ShowCloseButton;
    public readonly Sync<color> BackgroundColor;
    public readonly Sync<color> HeaderColor;
    public readonly Sync<color> HeaderSeparatorColor;
    public readonly Sync<color> BorderColor;
    public readonly Sync<color> TextColor;
    public readonly Sync<color> CloseButtonColor;
    public readonly Sync<color> CloseButtonHighlightColor;
    public readonly Sync<color> CloseButtonPressedColor;
    public readonly Sync<color> CloseButtonDisabledColor;
    public readonly Sync<color> CloseIconColor;
    public readonly AssetRef<FontSet> Font;
    // Retained for serialized panels; shell corners are now controlled by CornerRadius. -xlinka
    public readonly AssetRef<TextureAsset> RoundedSprite;
    public readonly Sync<float> CornerRadius;

    private bool _built;
    private Grabbable? _grabbable;
    private RectTransform? _rootRect;
    private RoundedPanel? _background;
    private Slot? _canvasSlot;
    private RectTransform? _canvasRect;
    private RoundedQuadMesh? _glassMesh;
    private Slot? _glassSlot;
    private const float GlassMargin = 12f;
    private const float GlassDepth = 6f;
    // A hand closing near the panel takes it: a thin trigger box behind the face, the size of the glass. It
    // sits clear of the face so a fingertip reaching for a button meets the canvas first, and it is a trigger
    // with raycasts off, so it is never a wall to walk into and no beam stops on it. Its own peer builds it
    // on a local slot; nothing about it is saved or sent. In panel units, about a centimetre at the usual
    // panel scale. -xlinka
    private const float GrabVolumeGap = 2f;
    private const float GrabVolumeDepth = 12f;
    // Below a prop's default, so a grip with a prop and a panel both in reach takes the prop alone.
    private const int PanelGrabPriority = -1;
    private Slot? _grabVolumeSlot;
    private BoxCollider? _grabVolume;
    // The glass draws GlassMargin past the canvas on every side; this stops the beam on that rim the way the
    // backing stops it on the face. First under the canvas, so everything drawn after it still takes the
    // pointer inside the face.
    private Slot? _rimSlot;
    private RectTransform? _rimRect;
    // Nested chunks all pack into the top of one render-priority band, so a chunk with more surfaces
    // reaches further down it - and the title-bar row, sitting on the header's own background chunk, had
    // every image and clipped label drawn UNDER that background: only one text surface won the tie. The
    // title and close button only showed because their surface counts happened to line up. Everything on
    // the bar gets a reserved band above it instead. -xlinka
    private const int HeaderChunkOverlayLevel = 3;
    private Slot? _headerSlot;
    private RectTransform? _headerRect;
    private RoundedPanel? _headerPanel;
    private InteractionElement? _headerGrabSurface;
    private Slot? _separatorSlot;
    private RectTransform? _separatorRect;
    private Image? _separatorImage;
    private Slot? _titleSlot;
    private RectTransform? _titleRect;
    private Text? _titleText;
    private Slot? _headerContentSlot;
    private RectTransform? _headerContentRect;
    private Slot? _closeSlot;
    private RectTransform? _closeRect;
    private Button? _closeButton;
    private Image? _closeImage;
    private ColorDriver? _closeColorDriver;
    private Text? _closeText;
    private Slot? _contentSlot;
    private RectTransform? _contentRect;

    public event Action<PanelShell>? CloseRequested;

    public Slot? HeaderSlot
    {
        get
        {
            EnsureBuilt();
            return _headerSlot;
        }
    }

    public Slot? ContentSlot
    {
        get
        {
            EnsureBuilt();
            return _contentSlot;
        }
    }

    public PanelShell()
    {
        Title = new Sync<string>(this, string.Empty);
        Size = new Sync<float2>(this, new float2(640f, 420f));
        AllowGrab = new Sync<bool>(this, true);
        Scalable = new Sync<bool>(this, true);
        ShowHeader = new Sync<bool>(this, true);
        HeaderHeight = new Sync<float>(this, 52f);
        Padding = new Sync<float>(this, 8f);
        TitleTextSize = new Sync<float>(this, DashTheme.FontTitle);
        CloseButtonPadding = new Sync<float>(this, 5f);
        ShowCloseButton = new Sync<bool>(this, true);
        BackgroundColor = new Sync<color>(this, DashTheme.GlassBackdrop);
        HeaderColor = new Sync<color>(this, DashTheme.Surface);
        HeaderSeparatorColor = new Sync<color>(this, DashTheme.Divider);
        BorderColor = new Sync<color>(this, DashTheme.OutlineStrong);
        TextColor = new Sync<color>(this, DashTheme.Text);
        CloseButtonColor = new Sync<color>(this, DashTheme.Field);
        CloseButtonHighlightColor = new Sync<color>(this, DashTheme.Negative);
        CloseButtonPressedColor = new Sync<color>(this, DashTheme.SurfacePressed);
        CloseButtonDisabledColor = new Sync<color>(this, DashTheme.Surface);
        CloseIconColor = new Sync<color>(this, DashTheme.Text);
        Font = new AssetRef<FontSet>(this);
        RoundedSprite = new AssetRef<TextureAsset>(this);
        CornerRadius = new Sync<float>(this, DashTheme.RadiusPanel);
    }

    // Build lazily so callers can set fields immediately after AttachComponent. - xlinka
    public override void OnStart()
    {
        base.OnStart();
        EnsureBuilt();
    }

    public override void OnChanges()
    {
        base.OnChanges();
        UpdateVisualState();
    }

    public override void Load(DataTreeNode node, LoadControl control)
    {
        base.Load(node, control);
        // The structure is serialized as our children, but the cached part references and _built
        // flag are not. Reconnect to the loaded children once the whole subtree is in place
        // (deferred), so title/colour updates take effect again instead of no-op'ing on _built.
        control.OnLoaded(EnsureBuilt);
    }

    // A row in the title bar for panels that put their own controls up there (tabs, tool toggles). The
    // title text hides while it exists and the row stops short of the close button. -xlinka
    public Slot EnsureHeaderContent()
    {
        EnsureBuilt();
        if (_headerContentSlot == null || _headerContentSlot.IsDestroyed)
        {
            _headerContentSlot = _headerSlot!.FindChildOrAdd("HeaderContent");
            _headerContentRect = _headerContentSlot.GetOrAttachComponent<RectTransform>();
            _headerContentSlot.GetOrAttachComponent<GraphicChunkRoot>();
        }
        UpdateVisualState();
        return _headerContentSlot;
    }

    // The level is local state, not synced, so every peer sets it for itself from the visual refresh
    // (which runs on start and on every change). A chunk the canvas already built keeps its old band
    // until it is re-meshed, hence the dirty.
    private void LiftHeaderChunks()
    {
        LiftChunk(_titleSlot);
        LiftChunk(_closeSlot);
        LiftChunk(_headerContentSlot);
    }

    private static void LiftChunk(Slot? slot)
    {
        var chunk = slot != null && !slot.IsDestroyed ? slot.GetComponent<GraphicChunkRoot>() : null;
        if (chunk == null || chunk.OverlayLevel == HeaderChunkOverlayLevel)
            return;
        chunk.OverlayLevel = HeaderChunkOverlayLevel;
        slot!.GetComponent<RectTransform>()?.MarkChangeDirty();
    }

    public UIBuilder CreateContentBuilder()
    {
        EnsureBuilt();
        return new UIBuilder(_contentSlot!).Font(Font.Target).TextColor(TextColor.Value)
            .BackgroundColor(DashTheme.Field).CornerRadius(DashTheme.RadiusControl);
    }

    public void RebuildContent(Action<UIBuilder> build)
    {
        EnsureBuilt();
        ClearContent();
        build(CreateContentBuilder());
    }

    public void ClearContent()
    {
        if (_contentSlot == null) return;
        DestroyChildren(_contentSlot);
    }

    public void Close()
    {
        CloseRequested?.Invoke(this);
        Slot.Destroy();
    }

    // Bound to the close button as a duplicable action (not a closure) so a
    // cloned panel's close button destroys the clone, not the original.
    [SyncMethod]
    public void OnClosePressed(Button button, UIInteractionContext context)
    {
        Close();
    }

    private void EnsureBuilt()
    {
        if (_built) return;
        _built = true;

        // find-or-create: a fresh panel builds the structure; after a load these reconnect to the
        // already-deserialized children/components instead of duplicating them.
        _rootRect = RectTransform ?? Slot.GetOrAttachComponent<RectTransform>();
        _grabbable = Slot.GetComponent<Grabbable>();
        if (_grabbable == null)
        {
            _grabbable = Slot.AttachComponent<Grabbable>();
            _grabbable.GrabPriority.Value = PanelGrabPriority;
        }
        // A panel saved before it had a grab volume still has the default priority, which ties with props.
        else if (World?.IsAuthority == true && _grabbable.GrabPriority.Value == 0)
        {
            _grabbable.GrabPriority.Value = PanelGrabPriority;
        }
        if (Font.Target == null)
        {
            var theme = Slot.GetComponentInParent<UITheme>() ?? Slot.GetOrAttachComponent<UITheme>();
            SetTarget(Font, theme.ThemeFont);
        }

        // The glass mesh and canvas share a parent, so grabbing/scaling moves both without the
        // canvas layout or graphic batching taking ownership of the glass. -xlinka
        var canvasSlot = Slot.FindChildOrAdd("PanelCanvas");
        _canvasSlot = canvasSlot;
        _canvasRect = canvasSlot.GetOrAttachComponent<RectTransform>();
        canvasSlot.GetOrAttachComponent<Canvas>();
        _background = canvasSlot.GetOrAttachComponent<RoundedPanel>();

        // Reconnect saved shells and preserve their content while retiring the old sprite backing. -xlinka
        Slot.GetComponent<Canvas>()?.Destroy();
        Slot.GetComponent<Image>()?.Destroy();
        var oldHeader = Slot.FindChild("Header", recursive: false);
        oldHeader?.SetParent(canvasSlot, preserveGlobalTransform: false);
        var oldContent = Slot.FindChild("Content", recursive: false);
        oldContent?.SetParent(canvasSlot, preserveGlobalTransform: false);

        // Keep the shell's two-sided backing and depth occlusion. RoundedPanel now emits an actual
        // border ring beside the fill, so these no longer overlap and fight in the depth buffer.
        // Other controls retain the normal one-sided canvas material, on the same side as text.
        var backing = Slot.GetOrAttachComponent<UIUnlitMaterial>();
        backing.ZWrite.Value = Lumora.Core.Assets.ZWrite.On;
        _background.Material.Target = backing;

        _glassSlot = Slot.AddLocalSlot("PanelGlass");
        _glassSlot.LocalPosition.Value = new float3(0f, 0f, -GlassDepth);
        _glassMesh = _glassSlot.AttachComponent<RoundedQuadMesh>();
        _glassMesh.CornerSegments.Value = 8;
        _glassMesh.DualSided.Value = false;
        var glassRenderer = _glassSlot.AttachComponent<MeshRenderer>();
        glassRenderer.Mesh.Target = _glassMesh;
        var glassMaterial = DashGlass.Attach(_glassSlot);
        // Glass must draw before the canvas backdrop, whose root priority is -1.
        glassMaterial.RenderQueue.Value = 2872;
        glassRenderer.Material.Target = glassMaterial;
        glassRenderer.ShadowCastMode.Value = ShadowCastMode.Off;

        _grabVolumeSlot = Slot.AddLocalSlot("PanelGrabVolume");
        _grabVolume = _grabVolumeSlot.AttachComponent<BoxCollider>();
        _grabVolume.Type.Value = Lumora.Core.Physics.ColliderType.Trigger;
        _grabVolume.IgnoreRaycasts.Value = true;
        _grabVolume.CharacterCollider.Value = false;
        _grabVolume.Offset.Value = new float3(0f, 0f, -(GrabVolumeGap + GrabVolumeDepth * 0.5f));

        _rimSlot = canvasSlot.FindChildOrAdd("Rim");
        _rimRect = _rimSlot.GetOrAttachComponent<RectTransform>();
        _rimSlot.GetOrAttachComponent<InteractionBlock>();
        if (canvasSlot.Children.Count > 0 && !ReferenceEquals(canvasSlot.Children[0], _rimSlot))
            canvasSlot.MoveChildToIndex(_rimSlot, 0);

        _headerSlot = canvasSlot.FindChildOrAdd("Header");
        _headerRect = _headerSlot.GetOrAttachComponent<RectTransform>();
        _headerSlot.GetComponent<Image>()?.Destroy();
        _headerSlot.GetOrAttachComponent<GraphicChunkRoot>();
        _headerPanel = _headerSlot.GetOrAttachComponent<RoundedPanel>();
        _headerGrabSurface = _headerSlot.GetOrAttachComponent<InteractionElement>();

        var titleSlot = _headerSlot.FindChildOrAdd("Title");
        _titleSlot = titleSlot;
        _titleRect = titleSlot.GetOrAttachComponent<RectTransform>();
        _titleText = titleSlot.GetOrAttachComponent<Text>();
        titleSlot.GetOrAttachComponent<GraphicChunkRoot>();

        _headerContentSlot = _headerSlot.FindChild("HeaderContent", recursive: false);
        _headerContentRect = _headerContentSlot?.GetComponent<RectTransform>();

        _separatorSlot = _headerSlot.FindChildOrAdd("Separator");
        _separatorRect = _separatorSlot.GetOrAttachComponent<RectTransform>();
        _separatorImage = _separatorSlot.GetOrAttachComponent<Image>();

        _contentSlot = canvasSlot.FindChildOrAdd("Content");
        _contentRect = _contentSlot.GetOrAttachComponent<RectTransform>();
        _contentSlot.GetOrAttachComponent<GraphicChunkRoot>();

        UpdateVisualState();
    }

    private void UpdateVisualState()
    {
        if (!_built)
        {
            return;
        }

        _rootRect ??= RectTransform ?? Slot.GetComponent<RectTransform>() ?? Slot.AttachComponent<RectTransform>();
        SetCenteredRect(_rootRect, Size.Value);
        // Canvas space is pinned at the panel's TOP-LEFT and the canvas slot is shifted so the panel still
        // centres on its own slot. Centred canvas space meant every resize moved every rect, so the whole
        // panel re-meshed and, until it had, drew its old meshes half a resize off (the "everything goes
        // wonky while stretching" mess). Now a resize from the bottom-right leaves everything hanging off the
        // top-left exactly where its baked mesh already is. -xlinka
        if (_canvasRect != null && !_canvasRect.IsDestroyed)
        {
            Set(_canvasRect.AnchorMin, new float2(0.5f, 0.5f));
            Set(_canvasRect.AnchorMax, new float2(0.5f, 0.5f));
            Set(_canvasRect.OffsetMin, new float2(0f, -Size.Value.y));
            Set(_canvasRect.OffsetMax, new float2(Size.Value.x, 0f));
        }
        if (_canvasSlot != null && !_canvasSlot.IsDestroyed)
            Set(_canvasSlot.LocalPosition, new float3(Size.Value.x * -0.5f, Size.Value.y * 0.5f, 0f));
        if (_glassMesh != null && !_glassMesh.IsDestroyed)
        {
            Set(_glassMesh.Size, Size.Value + new float2(GlassMargin * 2f, GlassMargin * 2f));
            Set(_glassMesh.CornerRadius, MathF.Max(0f, CornerRadius.Value) + GlassMargin);
        }
        float headerSize = MathF.Max(0f, HeaderHeight.Value);
        float headerHeight = ShowHeader.Value ? headerSize : 0f;
        bool canGrab = AllowGrab.Value;

        if (_grabVolume != null && !_grabVolume.IsDestroyed)
        {
            Set(_grabVolume.Size, new float3(Size.Value.x + GlassMargin * 2f, Size.Value.y + GlassMargin * 2f, GrabVolumeDepth));
            Set(_grabVolume.Enabled, canGrab);
        }
        if (_rimRect != null && !_rimRect.IsDestroyed)
            Stretch(_rimRect, new float2(-GlassMargin, -GlassMargin), new float2(GlassMargin, GlassMargin));

        if (_grabbable != null && !_grabbable.IsDestroyed)
        {
            Set(_grabbable.AllowGrab, canGrab);
            Set(_grabbable.Scalable, Scalable.Value);
        }

        if (_background != null && !_background.IsDestroyed)
        {
            Set(_background.Color, BackgroundColor.Value);
            Set(_background.OutlineColor, BorderColor.Value);
            Set(_background.OutlineThickness, DashTheme.OutlineWidth);
            Set(_background.CornerRadius, CornerRadius.Value);
        }

        if (_headerSlot != null && !_headerSlot.IsDestroyed)
        {
            Set(_headerSlot.ActiveSelf, ShowHeader.Value);
        }

        if (_headerGrabSurface != null && !_headerGrabSurface.IsDestroyed)
        {
            Set(_headerGrabSurface.Interactable, canGrab);
        }

        if (_headerRect != null && !_headerRect.IsDestroyed)
        {
            Set(_headerRect.AnchorMin, new float2(0f, 1f));
            Set(_headerRect.AnchorMax, new float2(1f, 1f));
            Set(_headerRect.OffsetMin, new float2(Padding.Value, -headerSize));
            Set(_headerRect.OffsetMax, new float2(-Padding.Value, -Padding.Value));
        }

        if (_headerPanel != null && !_headerPanel.IsDestroyed)
        {
            Set(_headerPanel.Color, HeaderColor.Value);
            Set(_headerPanel.CornerRadius, MathF.Min(CornerRadius.Value, DashTheme.RadiusControl));
            Set(_headerPanel.OutlineColor, BorderColor.Value);
            Set(_headerPanel.OutlineThickness, DashTheme.OutlineWidth);
        }

        if (_separatorRect != null && !_separatorRect.IsDestroyed)
        {
            Set(_separatorRect.AnchorMin, new float2(0f, 0f));
            Set(_separatorRect.AnchorMax, new float2(1f, 0f));
            Set(_separatorRect.OffsetMin, new float2(DashTheme.RadiusControl, -4f));
            Set(_separatorRect.OffsetMax, new float2(-DashTheme.RadiusControl, -3f));
        }

        if (_separatorImage != null && !_separatorImage.IsDestroyed)
        {
            Set(_separatorImage.Tint, HeaderSeparatorColor.Value);
        }

        if (_titleRect != null && !_titleRect.IsDestroyed)
        {
            float closeWidth = ShowCloseButton.Value ? headerSize : 0f;
            Stretch(_titleRect, new float2(Padding.Value, 0f), new float2(ShowCloseButton.Value ? -closeWidth : -Padding.Value, 0f));
        }

        // A peer that did not build the panel can receive the row after start.
        if (_headerContentSlot == null && _headerSlot != null && !_headerSlot.IsDestroyed)
        {
            _headerContentSlot = _headerSlot.FindChild("HeaderContent", recursive: false);
            _headerContentRect = _headerContentSlot?.GetComponent<RectTransform>();
        }
        bool customHeader = _headerContentSlot != null && !_headerContentSlot.IsDestroyed;
        if (_titleSlot != null && !_titleSlot.IsDestroyed)
            Set(_titleSlot.ActiveSelf, !customHeader);

        // Same band as the close button, so anything built in here lines up with it.
        if (customHeader && _headerContentRect != null && !_headerContentRect.IsDestroyed)
        {
            float barHeight = MathF.Max(0f, HeaderHeight.Value - Padding.Value);
            float inset = MathF.Max(0f, MathF.Min(CloseButtonPadding.Value, barHeight * 0.45f));
            Set(_headerContentRect.AnchorMin, float2.Zero);
            Set(_headerContentRect.AnchorMax, float2.One);
            Set(_headerContentRect.OffsetMin, new float2(inset, inset));
            Set(_headerContentRect.OffsetMax, new float2(ShowCloseButton.Value ? -barHeight : -inset, -inset));
        }

        if (_titleText != null && !_titleText.IsDestroyed)
        {
            Set(_titleText.Content, Title.Value);
            Set(_titleText.Color, TextColor.Value);
            Set(_titleText.Size, TitleTextSize.Value);
            Set(_titleText.HorizontalAlignment, TextHorizontalAlignment.Left);
            Set(_titleText.VerticalAlignment, TextVerticalAlignment.Middle);
            SetTarget(_titleText.Font, Font.Target);
        }

        if (ShowCloseButton.Value)
        {
            EnsureCloseButton();
            UpdateCloseButton();
        }
        else
        {
            DestroyCloseButton();
        }
        LiftHeaderChunks();

        if (_contentRect != null && !_contentRect.IsDestroyed)
        {
            Stretch(_contentRect, new float2(Padding.Value, Padding.Value), new float2(-Padding.Value, -headerHeight - Padding.Value));
        }
    }

    private void EnsureCloseButton()
    {
        if (_headerSlot == null || _headerSlot.IsDestroyed)
        {
            return;
        }

        if (_closeSlot != null && !_closeSlot.IsDestroyed)
        {
            return;
        }

        _closeSlot = _headerSlot.FindChildOrAdd("Close");
        _closeSlot.GetOrAttachComponent<GraphicChunkRoot>();
        _closeRect = _closeSlot.GetOrAttachComponent<RectTransform>();
        _closeImage = _closeSlot.GetOrAttachComponent<Image>();
        _closeButton = _closeSlot.GetOrAttachComponent<Button>();
        _closeButton.SetAction(OnClosePressed);
        _closeColorDriver = _closeSlot.GetComponent<ColorDriver>() ?? _closeButton.SetupBackgroundColor(_closeImage.Tint);

        var closeLabel = _closeSlot.FindChildOrAdd("Label");
        var closeLabelRect = closeLabel.GetOrAttachComponent<RectTransform>();
        Fill(closeLabelRect);
        _closeText = closeLabel.GetOrAttachComponent<Text>();
    }

    private void UpdateCloseButton()
    {
        if (_closeRect != null && !_closeRect.IsDestroyed)
        {
            float headerSize = MathF.Max(0f, HeaderHeight.Value - Padding.Value);
            float inset = MathF.Max(0f, MathF.Min(CloseButtonPadding.Value, headerSize * 0.45f));
            float buttonSize = MathF.Max(0f, headerSize - inset * 2f);
            Set(_closeRect.AnchorMin, new float2(1f, 0f));
            Set(_closeRect.AnchorMax, new float2(1f, 1f));
            Set(_closeRect.OffsetMin, new float2(-buttonSize - inset, inset));
            Set(_closeRect.OffsetMax, new float2(-inset, -inset));
        }

        if (_closeColorDriver != null && !_closeColorDriver.IsDestroyed)
        {
            Set(_closeColorDriver.TintColorMode, InteractionColorMode.Direct);
            Set(_closeColorDriver.NormalColor, CloseButtonColor.Value);
            Set(_closeColorDriver.HighlightColor, CloseButtonHighlightColor.Value);
            Set(_closeColorDriver.PressedColor, CloseButtonPressedColor.Value);
            Set(_closeColorDriver.DisabledColor, CloseButtonDisabledColor.Value);
            if (_closeButton != null && !_closeButton.IsDestroyed)
            {
                _closeColorDriver.Apply(_closeButton);
            }
        }
        else if (_closeImage != null && !_closeImage.IsDestroyed)
        {
            Set(_closeImage.Tint, CloseButtonColor.Value);
        }

        if (_closeImage != null && !_closeImage.IsDestroyed)
        {
            SetTarget(_closeImage.Texture, null);
            Set(_closeImage.NineSlice, false);
            Set(_closeImage.CornerRadius, DashTheme.RadiusChip);
        }

        if (_closeText != null && !_closeText.IsDestroyed)
        {
            Set(_closeText.Content, "×");
            Set(_closeText.Color, CloseIconColor.Value);
            Set(_closeText.Size, TitleTextSize.Value);
            Set(_closeText.HorizontalAlignment, TextHorizontalAlignment.Center);
            Set(_closeText.VerticalAlignment, TextVerticalAlignment.Middle);
            SetTarget(_closeText.Font, Font.Target);
        }
    }

    private void DestroyCloseButton()
    {
        if (_closeSlot != null && !_closeSlot.IsDestroyed)
        {
            _closeSlot.Destroy();
        }

        _closeSlot = null;
        _closeRect = null;
        _closeButton = null;
        _closeImage = null;
        _closeColorDriver = null;
        _closeText = null;
    }

    private static void DestroyChildren(Slot slot)
    {
        var children = new List<Slot>(slot.Children);
        foreach (var child in children)
        {
            child.Destroy();
        }

        var localChildren = new List<Slot>(slot.LocalChildren);
        foreach (var child in localChildren)
        {
            child.Destroy();
        }
    }

    public override void OnDestroy()
    {
        if (_glassSlot != null && !_glassSlot.IsDestroyed)
            _glassSlot.Destroy();
        if (_grabVolumeSlot != null && !_grabVolumeSlot.IsDestroyed)
            _grabVolumeSlot.Destroy();
        base.OnDestroy();
    }

    private static void SetCenteredRect(RectTransform rect, in float2 size)
    {
        Set(rect.AnchorMin, new float2(0.5f, 0.5f));
        Set(rect.AnchorMax, new float2(0.5f, 0.5f));
        Set(rect.OffsetMin, new float2(size.x * -0.5f, size.y * -0.5f));
        Set(rect.OffsetMax, new float2(size.x * 0.5f, size.y * 0.5f));
    }

    private static void Fill(RectTransform rect) => Stretch(rect, float2.Zero, float2.Zero);

    // Stretched to the parent and inset by the offsets, each field written once and only when it differs:
    // on a machine that did not build the panel the values already match, and nothing is written there.
    private static void Stretch(RectTransform rect, in float2 offsetMin, in float2 offsetMax)
    {
        Set(rect.AnchorMin, float2.Zero);
        Set(rect.AnchorMax, float2.One);
        Set(rect.OffsetMin, offsetMin);
        Set(rect.OffsetMax, offsetMax);
    }

    // Every peer works these out from the same synced fields. One this machine may not write (a panel in
    // somebody else's hand) is theirs to write: asking first keeps the refusal out of the log, and racing a
    // pickup it reached the host and counted against whoever sent it. -xlinka
    private static void Set<T>(Sync<T> field, T value)
    {
        if (!EqualityComparer<T>.Default.Equals(field.Value, value) && field.IsWriteAllowedQuietly())
        {
            field.Value = value;
        }
    }

    private static void SetTarget<T>(AssetRef<T> field, IAssetProvider<T>? target) where T : Asset
    {
        if (!ReferenceEquals(field.Target, target))
        {
            field.Target = target!;
        }
    }
}
