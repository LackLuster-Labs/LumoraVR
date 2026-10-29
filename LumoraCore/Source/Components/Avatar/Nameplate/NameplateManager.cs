// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Lumora.Core.Localization;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Avatar;

// The composition root for one user's nameplate: plate body and its hairline, the accent bar, the name,
// the badge row, the group card and the status dot.
//
// LOCAL, TOP TO BOTTOM. This component and every slot, mesh, material and renderer under it is minted in
// the local RefID byte, so a nameplate adds no sync member, no delta, and nothing to a save. Every peer
// builds its own plate for every user out of state that was already on the wire - the name, the
// permission config, the head stream, User.IsPresent - which is also why the plate can face YOU while
// facing everyone else at the same time. There is no such thing as "the" nameplate; there is one per
// viewer per user, and none of them talk.
//
// DATA-DRIVEN, NOT POLLED. The name, the badge set and the presence state each have an owner that says
// when they changed: field change events for the name, the permission config's change events for roles,
// a 4Hz motion sample for presence. The per-frame work is a handful of float compares. The one thing on
// a timer is the role reconcile, and only because a host can move somebody's role through a path that
// writes the gate's session override without touching the replicated config. -xlinka
[ComponentCategory("Users/Avatar")]
public sealed class NameplateManager : UserRootComponent
{
    public const string RootSlotName = "Nameplate";

    private const string PlateSlotName = "Plate";
    private const string PanelSlotName = "Panel";
    private const string RimSlotName = "Rim";
    private const string AccentSlotName = "Accent";
    private const string LabelSlotName = "Label";
    private const string BadgeRowSlotName = "Badges";
    private const string StatusSlotName = "Status";
    private const string GroupCardSlotName = "GroupCard";
    private const string StaffTabSlotName = "StaffTab";
    private const string BadgeSlotPrefix = "badge_";

    // Reconcile cadence for the role read. Not a frame cost: a couple of dictionary lookups twice a
    // second, and it only touches geometry when the answer actually moved.
    private const float ReconcileInterval = 0.5f;

    // ONE SPACING UNIT. Every inset, gap and chip on the plate is a multiple of it, which is the whole
    // reason the badge row reads even: the gap between two chips, the gap from the chips to the plate and
    // the plate's own inset were three unrelated numbers before and no two of them lined up. -xlinka
    private const float Gap = 0.012f;

    // Total inset, not per side: the panel is the name plus this.
    private const float PanelPadX = Gap * 3f;
    private const float PanelPadY = Gap * 2f;
    private const float BadgeGap = Gap * 0.6f;

    // The group card's base unit. The chips are sized off the TEXT instead (ChipSize) so the row scales
    // with whatever TextSize a world set rather than only reading right at the default. -xlinka
    private const float CardUnit = Gap * 3.5f;

    // Chip side as a fraction of the name's size. The row sits ABOVE the plate, so a chip is not bound by
    // the name's line box and can run a little over it without pushing the plate around. -xlinka
    private const float ChipHeightRatio = 0.8f;
    // How much of a chip the mark inside it takes. The icon art is drawn to the edge of its own square and
    // a letter is not, so the two need different numbers to sit at the same optical size.
    private const float IconScale = 0.66f;
    private const float GlyphScale = 0.74f;
    // A two-letter mark (the role chips) steps down so both letters fit the same square.
    private const float PairGlyphScale = 0.44f;
    private const float RowGap = Gap * 1.2f;
    private const float StatusSize = Gap * 2.2f;
    private const float StatusGap = Gap;
    private const float PanelDepth = -0.004f;
    private const float ChipDepth = -0.002f;
    private const float IconDepth = -0.001f;
    // On the group strip, in front of the strip (+Z faces the viewer here): the chip, then its mark and tag.
    private const float StripChipDepth = 0.001f;
    private const float CardMarkDepth = 0.002f;
    private const float StripTextDepth = 0.003f;

    // Corner geometry as a FRACTION of the piece it rounds, not a fixed metre count, so the plate keeps a
    // dash card's corner at every name length instead of turning into a pill on a short name. The numbers
    // are the dash's own ratios: RadiusCard over a card's height, RadiusChip over ChipHeight. -xlinka
    private const float PanelRadiusRatio = 0.26f;
    private const float ChipRadiusRatio = 0.27f;

    // A hairline, the same line the dash draws round a card. The ring sits at the SAME depth as the panel
    // and grows outward from its outline, so the two share an edge and there is no second alpha layer
    // stacked behind the panel to darken it.
    private const float RimWidth = 0.002f;
    // A staff ring is wider so the light running round it has room to be seen.
    private const float StaffRimWidth = RimWidth * 2.5f;
    // A tier's colour runs round the whole ring as well as the bar, wide enough to read across a room.
    private const float TierRimWidth = RimWidth * 1.5f;

    // THE STAFF TAB
    // A pill on the plate's bottom edge, half on and half off it, with the word on it: the one mark on a
    // staff plate that says staff without anybody having to learn what the colours mean. It takes the bar's
    // place. Sized off the name like the chips. -xlinka
    private const float StaffTabHeightRatio = 0.56f;
    private const float StaffTabTextRatio = 0.66f;
    private const float StaffTabPadRatio = 1.2f;

    // The one place the user's own colour still lands: a short bar on the plate's bottom edge instead of
    // the fat coloured border that used to wrap the whole thing.
    private const float AccentHeight = 0.004f;
    private const float AccentWidthRatio = 0.26f;
    private const float AccentInset = Gap * 0.35f;

    // A display name is whatever somebody typed, and a 60-character one draws a plate wider than the
    // avatar wearing it and shoves the badge row out with it. Clipped for DRAWING only - nothing else
    // reads this, and the name on the wire is untouched. -xlinka
    private const int MaxNameChars = 26;

    // THE GROUP CARD
    //
    // A popout that sits directly on the plate's top edge rather than floating in the badge row. Top
    // corners take the panel's radius and the bottom two the chip's, which is what makes it read as part
    // of the plate: a card rounded the same all the way round looks like a separate thing that happens to
    // be nearby. The gap is one hairline of daylight, not a margin. -xlinka
    private const float GroupCardGap = Gap * 0.3f;
    private const float GroupCardHeight = CardUnit * 1.3f;
    private const float GroupIconSize = CardUnit;
    private const float GroupDotSize = CardUnit * 0.3f;
    private const float GroupContentGap = Gap * 0.7f;
    private const float GroupTagScale = 0.8f;
    // How much of the group colour lands over the plate's own tint. Held back from the slab it used to be
    // so the card still reads as part of the plate rather than a sticker on top of it.
    private const float GroupFillStrength = 0.6f;

    // THE GROUP BANNER
    // The represented group as a strip over the plate: the group's banner, a little see-through, rounded
    // all round, lower than the plate so the name stays the thing you read. A group with no banner gets the
    // same strip in its colour. The mark and the tag sit on a small dark chip at the strip's left so they
    // read over any picture. -xlinka
    private const float BannerHeightRatio = 0.62f;
    // the strip's widest shape, and how much of the plate's width it may take at most
    private const float BannerAspect = 3.2f;
    private const float BannerMinAspect = 2.2f;
    private const float BannerWidthRatio = 0.86f;
    private const float BannerAlpha = 0.86f;
    private const float BannerChipHeightRatio = 0.7f;
    private const float BannerChipInset = Gap * 0.6f;
    private const float BannerChipPad = Gap * 0.7f;
    // the tag's size over the chip's height
    private const float BannerTagScale = 0.8f;
    private static readonly color BannerChipFill = new color(0.04f, 0.04f, 0.06f, 0.55f);

    public readonly Sync<float> HeightOffset = new();
    public readonly Sync<float> TextSize = new();

    // Metres past which the plate stops drawing, with a fade band in front of the cut so it dissolves
    // rather than pops. Zero draws at any distance.
    public readonly Sync<float> MaxViewDistance = new();

    // Seconds of no head, hand or root motion before the dot goes amber.
    public readonly Sync<float> IdleSeconds = new();

    public readonly Sync<bool> ShowBadges = new();
    public readonly Sync<bool> ShowStatus = new();

    // The group popout. Same gate style as ShowBadges: a world that does not want group identity over
    // people's heads turns it off and the plate lays out exactly as it did before the card existed.
    public readonly Sync<bool> ShowGroupCard = new();

    // Your own plate hangs in front of your own face in first person. Third-person and free-cam still
    // show it, because there the point of view is not behind your eyes.
    public readonly Sync<bool> HideInFirstPerson = new();

    // Colour is NOT a knob here. The plate BODY belongs to the theme - one tint, one hairline, the same
    // ones a dash card is drawn with - because twenty people authoring twenty background colours is how a
    // room ends up with twenty different plates and no readable one. What stays authored is the ink, its
    // outline and the accent bar: the name is whoever's it is, and the accent is the supporter-reward
    // colour the equip manager replicates. The constants below are what those fall back to when there is
    // no equip manager to ask, which in practice means a template that spawned a user root by hand.
    private static readonly color FallbackText = NameplateTheme.NameInk;
    private static readonly color FallbackOutline = NameplateTheme.NameOutline;
    private static readonly color FallbackAccent = NameplateTheme.Accent;

    // What the equip manager hands somebody nobody granted a colour to: a near-black neutral that was only
    // ever meant to be invisible behind a border. As an underline it is a grey smear under every name in
    // the room, so the plate reads that exact value as "ungranted" and draws the house accent instead. A
    // granted colour is anything else and still wins. Compared here, never written: the default belongs to
    // the equip manager. -xlinka
    private static readonly color UngrantedAccent = new color(0.14f, 0.15f, 0.19f, 0.85f);

    private Slot? _plate;
    private Slot? _badgeRow;
    private Slot? _accentSlot;
    private TextRenderer? _label;
    private Meshes.RoundedQuadMesh? _panelMesh;
    private Meshes.RoundedQuadRingMesh? _rimMesh;
    private MeshRenderer? _rimRenderer;
    private MeshRenderer? _accentRenderer;
    private MeshRenderer? _panelRenderer;
    private Slot? _staffTab;
    private Meshes.RoundedQuadMesh? _staffTabMesh;
    private MeshRenderer? _staffTabRenderer;
    private TextRenderer? _staffTabWord;
    private float2 _tabLaidOutFor = new float2(-1f, -1f);
    private NameplateTheme? _theme;
    private PlateFrame _frame;
    private Meshes.RoundedQuadMesh? _accentMesh;
    private Meshes.RoundedQuadMesh? _statusMesh;

    private Slot? _groupCard;
    private Meshes.RoundedQuadMesh? _groupCardMesh;
    private Slot? _groupIconSlot;
    private Meshes.RoundedQuadMesh? _groupIconMesh;
    private Lumora.Core.Assets.UnlitMaterial? _groupIconMaterial;
    private Lumora.Core.Assets.ImageProvider? _groupIconProvider;
    private TextRenderer? _groupTag;
    private Slot? _groupDotSlot;
    private Meshes.RoundedQuadMesh? _groupDotMesh;
    private MeshRenderer? _groupCardRenderer;
    private Slot? _groupChipSlot;
    private Meshes.RoundedQuadMesh? _groupChipMesh;
    private Lumora.Core.Assets.UnlitMaterial? _groupBannerMaterial;
    private Lumora.Core.Assets.ImageProvider? _groupBannerProvider;
    private string _cardBannerUrl = string.Empty;
    // a banner asked for and not drawn yet: the strip shows the colour until the picture is in
    private bool _bannerWanted;
    private bool _bannerShown;
    private float _cardHeight;
    private float _stripAspect = BannerAspect;

    private readonly NameplateActivityWatcher _activity = new();
    private readonly List<NameplateBadge> _wanted = new();
    private readonly List<NameplateBadge> _drawn = new();

    private User? _user;
    private AvatarEquipManager? _equip;
    private WorldPermissionConfig? _watchedConfig;
    private Action<IChangeable>? _userChanged;
    private Action<IChangeable>? _configChanged;
    private Action<IChangeable>? _equipChanged;
    private Action<IChangeable>? _groupChanged;

    private bool _groupDirty = true;
    private bool _cardActive;
    private bool _cardIconShown;
    private bool _cardTagShown;
    private bool _cardDotShown;
    private string _cardIconUrl = string.Empty;
    private string _hostGroupId = string.Empty;
    private float2 _cardLaidOutFor = new float2(-1f, -1f);

    private float _reconcileCountdown;
    // Time sat out behind the viewer, handed to the next run so the plate's idle clock keeps time.
    private float _heldDelta;
    private float _chipsBuiltFor = -1f;
    private bool _hasCustomBadgeDriver;
    private bool _avatarShowing;
    private bool _badgesDirty = true;
    private bool _labelDirty = true;
    private float2 _laidOutFor = new float2(-1f, -1f);
    private bool _plateVisible = true;
    private RenderLayerOverride? _plateLayer;
    private NameplatePress? _press;
    private bool _pressHovered;

    public NameplatePresence Presence => _activity.State;

    public IReadOnlyList<NameplateBadge> Badges => _drawn;

    public Slot? PlateSlot => _plate;

    public NameplatePress? Press => _press;

    internal void SetPressHovered(bool hovered)
    {
        if (_pressHovered == hovered)
            return;
        _pressHovered = hovered;
        ApplyPlateColors();
    }

    public TextRenderer? Label => _label;

    public override void OnInit()
    {
        base.OnInit();
        HeightOffset.Value = 0.35f;
        // Smaller than it was: a name over a head is a label, not a sign, and the plate around it is
        // tighter now so the old size filled the whole thing.
        TextSize.Value = 0.06f;
        MaxViewDistance.Value = 40f;
        IdleSeconds.Value = 60f;
        ShowBadges.Value = true;
        ShowGroupCard.Value = true;
        // Off by default: the presence dot is real data but it reads as clutter floating beside the
        // panel. The machinery stays for worlds that want it. -xlinka
        ShowStatus.Value = false;
        // Off: your own plate never reaches your own eyes (it rides the band they skip), and mirrors and
        // cameras should show it. On hides it from them too. -xlinka
        HideInFirstPerson.Value = false;
    }

    public override void OnStart()
    {
        base.OnStart();

        HeightOffset.OnChanged += _ => MarkLayoutDirty();
        TextSize.OnChanged += _ => { _labelDirty = true; _badgesDirty = true; };
        MaxViewDistance.OnChanged += _ => ApplyViewDistance();
        IdleSeconds.OnChanged += _ => _activity.IdleThreshold = MathF.Max(1f, IdleSeconds.Value);
        ShowBadges.OnChanged += _ => _badgesDirty = true;
        ShowStatus.OnChanged += _ => MarkLayoutDirty();
        ShowGroupCard.OnChanged += _ => _groupDirty = true;

        _activity.IdleThreshold = MathF.Max(1f, IdleSeconds.Value);
        Bind();
        Compose();
    }

    // BINDING
    // Subscribe to the things that actually say when the plate is stale. Nothing here reads a value; the
    // handlers only raise a flag and the next update does the work, so a burst of changes in one frame
    // costs one rebuild rather than one each.

    private void Bind()
    {
        var root = Slot?.ActiveUserRoot;
        _user = root?.ActiveUser;
        _equip = root?.GetRegisteredComponent<AvatarEquipManager>()
                 ?? Slot?.GetComponentInParent<AvatarEquipManager>();

        if (_user != null && !_user.IsDestroyed)
        {
            _userChanged = _ => { _labelDirty = true; _badgesDirty = true; };
            _user.UserName.Changed += _userChanged;
            _user.IsPresent.Changed += _userChanged;
            _user.IsLive.Changed += _userChanged;
            _user.Capture.Changed += _userChanged;
            _user.AccountId.Changed += _userChanged;
            _user.IsSupporter.Changed += _userChanged;
            _user.SupporterColor.Changed += _userChanged;
            _user.SupporterStyle.Changed += _userChanged;
            _user.IsStaff.Changed += _userChanged;
            _user.AccountKind.Changed += _userChanged;

            // The card is rebuilt off a change event, never polled: six string compares per frame per
            // visible user is the sort of thing that only shows up when a room fills. -xlinka
            _groupChanged = _ => _groupDirty = true;
            _user.GroupId.Changed += _groupChanged;
            _user.GroupTag.Changed += _groupChanged;
            _user.GroupName.Changed += _groupChanged;
            _user.GroupIconHash.Changed += _groupChanged;
            _user.GroupBannerHash.Changed += _groupChanged;
            _user.GroupRole.Changed += _groupChanged;
            _user.GroupColor.Changed += _groupChanged;
        }

        if (_equip != null && !_equip.IsDestroyed)
        {
            _equipChanged = _ => { _labelDirty = true; ApplyAccentColor(); };
            _equip.BadgeText.Changed += _equipChanged;
            _equip.BadgeColor.Changed += _equipChanged;
            _equip.BadgeOutline.Changed += _equipChanged;
            _equip.BadgeRim.Changed += _equipChanged;
        }

        WatchPermissionConfig();

        // A late bind (the user root resolved after this component started) has to repaint what it just
        // learned the authored colours are.
        _labelDirty = true;
        _badgesDirty = true;
        _groupDirty = true;
        ApplyAccentColor();
    }

    // Read the config through RootSlot rather than World.PermissionConfig: that property ATTACHES the
    // component on the authority, and a nameplate must not author world state to draw itself.
    private void WatchPermissionConfig()
    {
        var config = World?.RootSlot?.GetComponent<WorldPermissionConfig>();
        if (config == null || config.IsDestroyed || ReferenceEquals(config, _watchedConfig))
            return;

        UnwatchPermissionConfig();
        _watchedConfig = config;
        _configChanged = _ => _badgesDirty = true;
        for (int i = 0; i < config.SyncMemberCount; i++)
        {
            if (config.GetSyncMember(i) is IChangeable changeable)
                changeable.Changed += _configChanged;
        }
    }

    private void UnwatchPermissionConfig()
    {
        if (_watchedConfig == null || _configChanged == null)
        {
            _watchedConfig = null;
            return;
        }

        if (!_watchedConfig.IsDestroyed)
        {
            for (int i = 0; i < _watchedConfig.SyncMemberCount; i++)
            {
                if (_watchedConfig.GetSyncMember(i) is IChangeable changeable)
                    changeable.Changed -= _configChanged;
            }
        }
        _watchedConfig = null;
        _configChanged = null;
    }

    // COMPOSITION

    private void Compose()
    {
        if (Slot == null || World == null || _plate != null)
            return;

        var theme = NameplateTheme.For(World);
        if (theme == null)
            return;

        var existing = Slot.FindChild(PlateSlotName, recursive: false);
        existing?.Destroy();

        var plate = Slot.AddLocalSlot(PlateSlotName);
        plate.Persistent.Value = false;
        plate.AttachComponent<PositionAtUser>().VerticalOffset.Value = HeightOffset.Value;
        plate.AttachComponent<FaceLocalUser>();
        // Everything under the plate draws on the name plate band, so a camera told to leave plates out
        // drops the name, the chips and the card together, and nothing else goes with them.
        _plateLayer = plate.AttachComponent<RenderLayerOverride>();
        _plateLayer.Layer.Value = RenderLayerOverride.NameplateLayer;
        // None of this is anybody's to edit, and this component rewrites all of it every time the name
        // reshapes - which is exactly why the marker is a TOOLING one. It takes the plate out of the
        // inspector and off the tools without touching a single write below it. Marked on the plate
        // root and on the manager root, so neither half shows up as a stray branch.
        Slot.GetOrAttachComponent<ImmutableComponent>();
        plate.GetOrAttachComponent<ImmutableComponent>();
        _plate = plate;

        float startHeight = TextSize.Value + PanelPadY;

        var rim = plate.AddSlot(RimSlotName);
        rim.LocalPosition.Value = new float3(0f, 0f, PanelDepth);
        _rimMesh = rim.AttachComponent<Meshes.RoundedQuadRingMesh>();
        _rimMesh.Size.Value = new float2(0.4f, startHeight);
        _rimMesh.CornerRadius.Value = startHeight * PanelRadiusRatio;
        _rimMesh.RimWidth.Value = RimWidth;
        _rimRenderer = AttachRenderer(rim, _rimMesh, theme.PlateMaterial);
        _theme = theme;

        var panel = plate.AddSlot(PanelSlotName);
        panel.LocalPosition.Value = new float3(0f, 0f, PanelDepth);
        _panelMesh = panel.AttachComponent<Meshes.RoundedQuadMesh>();
        _panelMesh.Size.Value = new float2(0.4f, startHeight);
        _panelMesh.CornerRadius.Value = startHeight * PanelRadiusRatio;
        _panelRenderer = AttachRenderer(panel, _panelMesh, theme.PlateMaterial);
        _press = plate.AttachComponent<NameplatePress>();
        _press.Manager = this;
        _press.Panel = _panelMesh;
        ApplyPlateColors();

        var accent = plate.AddSlot(AccentSlotName);
        accent.LocalPosition.Value = new float3(0f, 0f, ChipDepth);
        _accentSlot = accent;
        _accentMesh = accent.AttachComponent<Meshes.RoundedQuadMesh>();
        _accentMesh.Size.Value = new float2(AccentHeight * 4f, AccentHeight);
        _accentMesh.CornerRadius.Value = AccentHeight * 0.5f;
        _accentRenderer = AttachRenderer(accent, _accentMesh, theme.PlateMaterial);
        // The bar and the tab sit on the glass, and both are see-through: told to draw after it, or the
        // glass sorted after them from some angles and dimmed them.
        _accentRenderer.SortingOrder.Value = 1;
        ApplyAccentColor();

        var tab = plate.AddSlot(StaffTabSlotName);
        tab.LocalPosition.Value = new float3(0f, 0f, ChipDepth);
        tab.ActiveSelf.Value = false;
        _staffTab = tab;
        _staffTabMesh = tab.AttachComponent<Meshes.RoundedQuadMesh>();
        _staffTabMesh.Color = NameplateTheme.StaffTabFill;
        _staffTabRenderer = AttachRenderer(tab, _staffTabMesh, theme.PlateMaterial);
        _staffTabRenderer.SortingOrder.Value = 1;
        var word = tab.AddSlot("Word");
        word.LocalPosition.Value = new float3(0f, 0f, IconDepth - ChipDepth);
        _staffTabWord = word.AttachComponent<TextRenderer>();
        if (theme.Font != null)
            _staffTabWord.Font.Target = theme.Font;
        _staffTabWord.HorizontalAlign.Value = TextHorizontalAlignment.Center;
        _staffTabWord.VerticalAlign.Value = TextVerticalAlignment.Middle;
        _staffTabWord.Color.Value = NameplateTheme.StaffTabInk;
        _staffTabWord.OutlineThickness.Value = 0f;
        _staffTabWord.Text.Value = "Nameplate.Staff".AsLocale("STAFF").Resolve();

        var label = plate.AddSlot(LabelSlotName);
        _label = label.AttachComponent<TextRenderer>();
        if (theme.Font != null)
            _label.Font.Target = theme.Font;
        _label.Size.Value = TextSize.Value;
        _label.HorizontalAlign.Value = TextHorizontalAlignment.Center;
        _label.VerticalAlign.Value = TextVerticalAlignment.Middle;
        // A ring in atlas texels, kept faint on purpose: the plate under the name does the contrast work
        // now, and the ring is only there for the range where the plate has faded out from under it. Well
        // under the MSDF field's representable range either way - the shader caps the shift at ~1.4 texels
        // of a pixel-range-4 atlas, and past that the ring swallows the glyph quad whole. -xlinka
        _label.OutlineThickness.Value = 0.6f;

        _badgeRow = plate.AddSlot(BadgeRowSlotName);
        ComposeGroupCard(plate, theme);

        var status = plate.AddSlot(StatusSlotName);
        _statusMesh = status.AttachComponent<Meshes.RoundedQuadMesh>();
        _statusMesh.Size.Value = new float2(StatusSize, StatusSize);
        // Radius at half the side IS a disc, so the dot is geometry now instead of a fully-rounded
        // texture stretched over a quad.
        _statusMesh.CornerRadius.Value = StatusSize * 0.5f;
        AttachRenderer(status, _statusMesh, theme.PlateMaterial);
        ApplyStatusColor();

        _labelDirty = true;
        _groupDirty = true;
        _laidOutFor = new float2(-1f, -1f);
        _cardLaidOutFor = new float2(-1f, -1f);
        ApplyViewDistance();
        Reconcile();
    }

    // The card's pieces are built ONCE and shown or hidden per user. Building them on demand would mean
    // minting slots and a material inside a live plate every time somebody changed the group they wear,
    // and the icon's asset provider is exactly the thing that must survive a hash change rather than be
    // replaced by it. -xlinka
    private void ComposeGroupCard(Slot plate, NameplateTheme? theme)
    {
        var card = plate.AddSlot(GroupCardSlotName);
        card.LocalPosition.Value = new float3(0f, 0f, PanelDepth);
        _groupCardMesh = card.AttachComponent<Meshes.RoundedQuadMesh>();
        _groupCardMesh.Size.Value = new float2(GroupCardHeight, GroupCardHeight);
        _groupCardMesh.CornerRadius.Value = GroupCardHeight * PanelRadiusRatio;
        _groupCardMesh.BottomCornerRadius.Value = GroupCardHeight * PanelRadiusRatio;
        _groupCardRenderer = AttachRenderer(card, _groupCardMesh, theme?.PlateMaterial);
        card.ActiveSelf.Value = false;
        _groupCard = card;

        // The banner's own material, for the same reason the icon has one: the plate material is shared by
        // every plate in the world. Swapped onto the strip once the picture is in.
        var banner = card.AttachComponent<Lumora.Core.Assets.UnlitMaterial>();
        banner.TintColor.Value = new colorHDR(1f, 1f, 1f, BannerAlpha);
        banner.UseVertexColor.Value = false;
        banner.BlendMode.Value = Lumora.Core.Assets.BlendMode.Alpha;
        banner.Culling.Value = Lumora.Core.Assets.Culling.None;
        _groupBannerMaterial = banner;

        var chip = card.AddSlot("Chip");
        chip.LocalPosition.Value = new float3(0f, 0f, StripChipDepth);
        _groupChipMesh = chip.AttachComponent<Meshes.RoundedQuadMesh>();
        AttachRenderer(chip, _groupChipMesh, theme?.PlateMaterial);
        _groupChipMesh.Color = BannerChipFill;
        chip.ActiveSelf.Value = false;
        _groupChipSlot = chip;

        var icon = card.AddSlot("Icon");
        icon.LocalPosition.Value = new float3(0f, 0f, CardMarkDepth);
        _groupIconMesh = icon.AttachComponent<Meshes.RoundedQuadMesh>();
        _groupIconMesh.Size.Value = new float2(GroupIconSize, GroupIconSize);
        _groupIconMesh.CornerRadius.Value = GroupIconSize * ChipRadiusRatio;
        _groupIconMesh.UseVertexColors.Value = false;
        // Its own material, not the shared plate one: the plate material carries no texture on purpose and
        // every plate in the world shares it, so pointing that at one user's icon would put that icon on
        // everybody. Culling and blend match the plate so the card reads the same from behind.
        var material = icon.AttachComponent<Lumora.Core.Assets.UnlitMaterial>();
        material.TintColor.Value = colorHDR.White;
        material.UseVertexColor.Value = false;
        material.BlendMode.Value = Lumora.Core.Assets.BlendMode.Alpha;
        material.Culling.Value = Lumora.Core.Assets.Culling.None;
        _groupIconMaterial = material;
        AttachRenderer(icon, _groupIconMesh, material);
        icon.ActiveSelf.Value = false;
        _groupIconSlot = icon;

        var tagSlot = card.AddSlot("Tag");
        _groupTag = tagSlot.AttachComponent<TextRenderer>();
        if (theme?.Font != null)
            _groupTag.Font.Target = theme.Font;
        _groupTag.Size.Value = TextSize.Value * GroupTagScale;
        _groupTag.HorizontalAlign.Value = TextHorizontalAlignment.Center;
        _groupTag.VerticalAlign.Value = TextVerticalAlignment.Middle;
        tagSlot.ActiveSelf.Value = false;

        var dot = card.AddSlot("Dot");
        dot.LocalPosition.Value = new float3(0f, 0f, CardMarkDepth);
        _groupDotMesh = dot.AttachComponent<Meshes.RoundedQuadMesh>();
        _groupDotMesh.Size.Value = new float2(GroupDotSize, GroupDotSize);
        _groupDotMesh.CornerRadius.Value = GroupDotSize * 0.5f;
        AttachRenderer(dot, _groupDotMesh, theme?.PlateMaterial);
        dot.ActiveSelf.Value = false;
        _groupDotSlot = dot;
    }

    private static MeshRenderer AttachRenderer(Slot slot, Meshes.ProceduralMesh mesh, Lumora.Core.Assets.UnlitMaterial? material)
    {
        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        if (material != null)
            renderer.Material.Target = material;
        // A floating label has no business darkening the floor under it, and shadow passes are what
        // nameplates cost when a room fills up.
        renderer.ShadowCastMode.Value = ShadowCastMode.Off;
        return renderer;
    }

    // UPDATE

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        if (Slot == null || World == null)
            return;

        if (_plate == null || _plate.IsDestroyed)
        {
            _plate = null;
            Compose();
            if (_plate == null)
                return;
        }

        _reconcileCountdown -= delta;
        if (_reconcileCountdown <= 0f)
        {
            _reconcileCountdown = ReconcileInterval;

            if (_user == null || _user.IsDestroyed)
            {
                Unbind();
                Bind();
            }
            else if (_watchedConfig == null || _watchedConfig.IsDestroyed)
            {
                // The config arrives long after a guest joined - it is created on demand on the authority
                // and reaches a guest by state sync. Pick it up when it lands rather than sitting on a
                // dead watch for the rest of the session.
                WatchPermissionConfig();
            }

            Reconcile();
        }

        UpdateVisibility();
        if (!_plateVisible)
            return;

        // A plate behind the viewer is brought up to date now and then rather than every frame; one in view
        // follows every frame, near or far. -xlinka
        var view = AvatarViewLod.Look(Slot.ActiveUserRoot?.Slot);
        _heldDelta += delta;
        if (view.Level == AvatarViewLod.Level.Hidden && !view.RunsThisFrame(World.Time.UpdateIndex))
            return;
        delta = _heldDelta;
        _heldDelta = 0f;

        if (_activity.Tick(Slot.ActiveUserRoot, _user, delta))
            ApplyStatusColor();

        if (_labelDirty)
        {
            _labelDirty = false;
            ApplyLabel();
        }

        if (_badgesDirty)
        {
            _badgesDirty = false;
            RebuildBadges();
        }

        if (_groupDirty)
        {
            _groupDirty = false;
            RebuildGroupCard();
        }
        TryShowBanner();

        Layout();
    }

    // The twice-a-second pass. Everything here costs a tree walk or a registry scan, which is exactly why
    // none of it runs per frame: with twenty people in a room a per-frame component search per plate is
    // the whole nameplate system showing up in a profile for answers that change on the scale of somebody
    // swapping an avatar. -xlinka
    private void Reconcile()
    {
        _hasCustomBadgeDriver = HasCustomBadgeDriver();

        var root = Slot?.ActiveUserRoot;
        var head = root == null || root.IsDestroyed ? null : root.HeadSlot;
        // A hidden avatar hides its plate. The plate hangs under the avatar container, so slot activation
        // already covers the container itself being switched off; this catches the head node going away or
        // being deactivated on its own.
        _avatarShowing = root != null && !root.IsDestroyed
            && head != null && !head.IsDestroyed && head.IsActive;

        // A host can move a role through a path that writes only the gate's session override, which fires
        // no config change on any peer. Re-reading the resolved role here is what makes the plate follow
        // the Session screen as well as the Permissions screen.
        _badgesDirty = true;

        // The world's own group id has no change event this component watches (WorldSettings arrives by
        // state sync like the permission config does), so it is read here and the card only rebuilt when
        // the answer actually moved. Read through RootSlot: World.Configuration ATTACHES the settings
        // component on the authority and a nameplate must not author world state to draw itself. -xlinka
        string hostGroup = World?.RootSlot?.GetComponent<WorldSettings>()?.HostGroupId.Value ?? string.Empty;
        if (!string.Equals(hostGroup, _hostGroupId, StringComparison.Ordinal))
        {
            _hostGroupId = hostGroup;
            _groupDirty = true;
        }
    }

    // The plate is not drawn when: the badge is switched off, the equipped avatar carries its own name
    // badge driver, the avatar itself is hidden, or this is your own plate seen through your own eyes.
    private void UpdateVisibility()
    {
        bool show = _avatarShowing && !_hasCustomBadgeDriver;

        if (show && _equip != null && !_equip.IsDestroyed && !_equip.AutoAddNameBadge.Value)
            show = false;

        var root = Slot?.ActiveUserRoot;
        if (show && HideInFirstPerson.Value && root != null && root.IsLocalUserRoot
            && !UserInputState.FocusedExternalCameraActive)
            show = false;

        // Your own plate is for mirrors and cameras: on the band your eyes skip, unless the view is not
        // behind your eyes at all.
        int band = root != null && root.IsLocalUserRoot && !UserInputState.FocusedExternalCameraActive
            ? RenderLayerOverride.OwnNameplateLayer : RenderLayerOverride.NameplateLayer;
        if (_plateLayer != null && !_plateLayer.IsDestroyed && _plateLayer.Layer.Value != band)
            _plateLayer.Layer.Value = band;

        if (show == _plateVisible)
            return;
        _plateVisible = show;
        if (_plate != null && !_plate.IsDestroyed)
            _plate.ActiveSelf.Value = show;
    }

    private bool HasCustomBadgeDriver()
    {
        var host = _equip?.Slot;
        if (host == null || host.IsDestroyed)
            return false;
        foreach (var driver in host.GetComponentsInChildren<NameBadgeDriver>())
        {
            if (driver != null && !driver.IsDestroyed)
                return true;
        }
        return false;
    }

    private void ApplyLabel()
    {
        if (_label == null || _label.IsDestroyed)
            return;

        string name = _equip?.BadgeText.Value ?? string.Empty;
        if (string.IsNullOrEmpty(name))
            name = _user?.UserName.Value ?? string.Empty;
        if (string.IsNullOrEmpty(name))
            name = "User";

        _label.Text.Value = Clip(name);
        _label.Size.Value = TextSize.Value;

        bool authored = _equip != null && !_equip.IsDestroyed;
        _label.Color.Value = authored ? _equip!.BadgeColor.Value : FallbackText;

        var outline = authored ? _equip!.BadgeOutline.Value : FallbackOutline;
        _label.OutlineColor.Value = new colorHDR(outline.r, outline.g, outline.b, outline.a);

        ApplyFrame();
        MarkLayoutDirty();
    }

    private enum PlateFrame { Plain, Tier, Staff, Animated }

    // Staff wear night glass with light crossing it, a bright living ring, the STAFF tab on the bottom edge
    // and a light crossing the name. A tier the accounts service marks as animated wears its own colour
    // flowing round the ring and along the bar, and the light across the name in that colour. Every other
    // tier wears its colour still, round the ring and on the bar. Staff outrank any tier. All of it is the
    // GPU running off the clock, one material per look shared by every plate in the world, and the label's
    // own text material told how strong; nothing here runs per frame. -xlinka
    private void ApplyFrame()
    {
        var frame = FrameOf(_user);
        bool moving = frame is PlateFrame.Staff or PlateFrame.Animated;

        if (_label != null && !_label.IsDestroyed)
        {
            float shimmer = moving ? 1f : 0f;
            if (_label.Shimmer.Value != shimmer)
                _label.Shimmer.Value = shimmer;
            if (moving)
            {
                var glint = frame == PlateFrame.Staff ? NameplateTheme.StaffGlint : TierGlint(_user!);
                if (!_label.ShimmerColor.Value.Equals(glint))
                    _label.ShimmerColor.Value = glint;
            }
        }

        if (_theme != null && !_theme.IsDestroyed)
        {
            SetMaterial(_rimRenderer, frame switch
            {
                PlateFrame.Staff => _theme.StaffRimMaterial,
                PlateFrame.Animated => _theme.FlowRimMaterial,
                _ => _theme.PlateMaterial,
            });
            SetMaterial(_accentRenderer, frame switch
            {
                PlateFrame.Staff => _theme.StaffAccentMaterial,
                PlateFrame.Animated => _theme.FlowAccentMaterial,
                _ => _theme.PlateMaterial,
            });
            SetMaterial(_panelRenderer, frame == PlateFrame.Staff ? _theme.StaffPanelMaterial : _theme.PlateMaterial);
            SetMaterial(_staffTabRenderer, _theme.StaffAccentMaterial);
        }

        if (_rimMesh != null && !_rimMesh.IsDestroyed)
        {
            float width = frame switch
            {
                PlateFrame.Plain => RimWidth,
                PlateFrame.Tier => TierRimWidth,
                _ => StaffRimWidth,
            };
            if (_rimMesh.RimWidth.Value != width)
                _rimMesh.RimWidth.Value = width;
        }

        // The tab takes the bar's place on a staff plate.
        bool staff = frame == PlateFrame.Staff;
        if (_staffTab != null && !_staffTab.IsDestroyed && _staffTab.ActiveSelf.Value != staff)
        {
            _staffTab.ActiveSelf.Value = staff;
            _tabLaidOutFor = new float2(-1f, -1f);
        }
        if (_accentSlot != null && !_accentSlot.IsDestroyed && _accentSlot.ActiveSelf.Value == staff)
            _accentSlot.ActiveSelf.Value = !staff;

        // Every time, not only on a change of look: an animated tier's ring wears the tier colour, and that
        // can change while the look stays the same.
        _frame = frame;
        ApplyPlateColors();
    }

    private static PlateFrame FrameOf(User? user)
    {
        if (user == null || user.IsDestroyed || user.IsGuest)
            return PlateFrame.Plain;
        if (user.IsStaff.Value)
            return PlateFrame.Staff;
        if (!user.IsSupporter.Value)
            return PlateFrame.Plain;
        return string.Equals(user.SupporterStyle.Value, AnimatedStyle, StringComparison.OrdinalIgnoreCase)
            ? PlateFrame.Animated
            : PlateFrame.Tier;
    }

    // The style word the accounts service sends for a tier whose name tag moves.
    private const string AnimatedStyle = "Animated";

    // The tier's colour lifted toward white, so the light reads as light crossing a name already in colour.
    private static colorHDR TierGlint(User user)
    {
        var tier = TierColor(user);
        return new colorHDR(tier.r + (1f - tier.r) * 0.4f, tier.g + (1f - tier.g) * 0.4f, tier.b + (1f - tier.b) * 0.4f, 1f);
    }

    private static color TierColor(User user)
        => user.SupporterColor.Value.a > 0f ? user.SupporterColor.Value : NameplateTheme.SupporterAccent;

    private static void SetMaterial(MeshRenderer? renderer, Lumora.Core.Assets.IAssetProvider<Lumora.Core.Assets.MaterialAsset>? wanted)
    {
        if (renderer == null || renderer.IsDestroyed || wanted == null)
            return;
        if (!ReferenceEquals(renderer.Material.Target, wanted))
            renderer.Material.Target = wanted;
    }

    // Trailing dots rather than an ellipsis character: the shipping face does not carry every dot glyph a
    // designer would reach for, and a name that ends in a missing-glyph box is worse than one that ends in
    // three periods. Backs off a character if the cut would land inside a surrogate pair. -xlinka
    private static string Clip(string name)
    {
        if (name.Length <= MaxNameChars)
            return name;

        int cut = MaxNameChars - 3;
        if (char.IsHighSurrogate(name[cut - 1]))
            cut--;
        return name.Substring(0, cut).TrimEnd() + "...";
    }

    // The body of the plate is the theme's, not anybody's: the dash tint with a hairline round it. No blur
    // behind it - one frosted panel is a dash, twenty of them over twenty heads is twenty screen-grab and
    // blur passes for a label the size of a postcard, so the glass here is tint only. -xlinka
    private void ApplyPlateColors()
    {
        if (_panelMesh != null && !_panelMesh.IsDestroyed)
            _panelMesh.Color = _pressHovered ? NameplateTheme.PlateHoverFill : NameplateTheme.PlateFill;
        if (_rimMesh != null && !_rimMesh.IsDestroyed)
            _rimMesh.Color = _frame switch
            {
                PlateFrame.Staff => NameplateTheme.StaffRim,
                PlateFrame.Animated or PlateFrame.Tier when _user != null && !_user.IsDestroyed => TierColor(_user),
                _ => _pressHovered ? NameplateTheme.PlateHoverEdge : NameplateTheme.PlateEdge,
            };
    }

    // The accent is the supporter-reward surface: one authored colour living on the EQUIP manager, which
    // replicates, painted into a mesh that does not. It used to be the whole border; a bar under the name
    // says the same thing without wrapping every plate in a colour that fights the one next to it.
    private void ApplyAccentColor()
    {
        if (_accentMesh == null || _accentMesh.IsDestroyed)
            return;

        if (_equip == null || _equip.IsDestroyed)
        {
            _accentMesh.Color = FallbackAccent;
            return;
        }

        var rim = _equip.BadgeRim.Value;
        _accentMesh.Color = rim.a <= 0f || NearlySame(rim, UngrantedAccent) ? FallbackAccent : rim;
    }

    // Loose compare because the value makes a round trip through a save and the wire before it gets here,
    // and a rim that came back a thousandth off the default is still the default.
    private static bool NearlySame(in color a, in color b)
        => MathF.Abs(a.r - b.r) < 0.004f && MathF.Abs(a.g - b.g) < 0.004f
           && MathF.Abs(a.b - b.b) < 0.004f && MathF.Abs(a.a - b.a) < 0.004f;

    private void ApplyStatusColor()
    {
        if (_statusMesh == null || _statusMesh.IsDestroyed)
            return;
        _statusMesh.Color = NameplatePresenceColors.For(_activity.State);
    }

    private void ApplyViewDistance()
    {
        if (_label != null && !_label.IsDestroyed)
            _label.MaxViewDistance.Value = MathF.Max(0f, MaxViewDistance.Value);

        if (_plate == null || _plate.IsDestroyed)
            return;

        float distance = MathF.Max(0f, MaxViewDistance.Value);
        float margin = distance > 0f ? TextRenderer.ViewDistanceFadeMargin : 0f;
        foreach (var renderer in _plate.GetComponentsInChildren<MeshRenderer>())
        {
            if (renderer == null || renderer.IsDestroyed)
                continue;
            if (renderer.MaxViewDistance == distance && renderer.ViewDistanceFadeMargin == margin)
                continue;
            renderer.MaxViewDistance = distance;
            renderer.ViewDistanceFadeMargin = margin;
            renderer.MarkChangeDirty();
        }
    }

    // BADGES

    // The chip's side, off the name rather than a fixed metre count, so the row scales with whatever
    // TextSize the world set instead of towering over the plate at a big one and rattling round it at a
    // small one.
    private float ChipSize => MathF.Max(0.001f, TextSize.Value) * ChipHeightRatio;

    private void RebuildBadges()
    {
        if (_badgeRow == null || _badgeRow.IsDestroyed)
            return;

        _wanted.Clear();
        if (ShowBadges.Value && _user != null && !_user.IsDestroyed)
        {
            var context = new NameplateBadgeContext(World!, _user, Slot?.ActiveUserRoot!);
            NameplateBadgeRegistry.Collect(in context, _wanted);
        }

        // Chip geometry is baked when a chip is built, so a TextSize change has to rebuild the row even
        // when the badge SET is identical. Tracked as a number rather than by clearing what was drawn,
        // because the drawn list is what the compare above is for. -xlinka
        float chip = ChipSize;
        if (SameBadges(_wanted, _drawn) && MathF.Abs(chip - _chipsBuiltFor) < 1e-6f)
            return;
        _chipsBuiltFor = chip;

        _badgeRow.DestroyChildren();

        var theme = NameplateTheme.For(World);
        for (int i = 0; i < _wanted.Count; i++)
            BuildBadge(_badgeRow, _wanted[i], theme);

        _drawn.Clear();
        _drawn.AddRange(_wanted);
        // No placement here: the row rides the plate's top edge, so where it goes depends on the panel
        // height the shaped name produced and Layout is the only thing that knows it.
        MarkLayoutDirty();
        ApplyViewDistance();
    }

    // A chip is a FILLED square in the badge's own colour with its mark cut out of it in dark ink: a
    // letter for the things a letter says (host, role), the icon set the rest of the UI already uses for
    // the things that have a picture (voice). Both branches sit on the same square so the row measures the
    // same whichever a source hands back. Neither colour is chosen here - the badge carries both. -xlinka
    private void BuildBadge(Slot row, in NameplateBadge badge, NameplateTheme? theme)
    {
        var slot = row.AddSlot(BadgeSlotPrefix + badge.Key);
        float size = ChipSize;

        var chip = slot.AddSlot("Chip");
        chip.LocalPosition.Value = new float3(0f, 0f, ChipDepth);
        var chipMesh = chip.AttachComponent<Meshes.RoundedQuadMesh>();
        chipMesh.Size.Value = new float2(size, size);
        chipMesh.CornerRadius.Value = size * ChipRadiusRatio;
        chipMesh.Color = badge.Chip;
        AttachRenderer(chip, chipMesh, theme?.PlateMaterial);

        if (!string.IsNullOrEmpty(badge.IconName))
        {
            // The material is shared per icon kind across the whole world and carries no tint, so the ink
            // goes on the mesh corners instead. Nothing here mints a texture.
            var material = theme?.IconMaterial(badge.IconName);
            if (material == null)
                return;

            var iconSlot = slot.AddSlot("Icon");
            iconSlot.LocalPosition.Value = new float3(0f, 0f, IconDepth);
            var iconMesh = iconSlot.AttachComponent<Meshes.RoundedQuadMesh>();
            iconMesh.Size.Value = new float2(size * IconScale, size * IconScale);
            iconMesh.CornerRadius.Value = 0f;
            iconMesh.Color = badge.Ink;
            AttachRenderer(iconSlot, iconMesh, material);
            return;
        }

        if (string.IsNullOrEmpty(badge.Glyph))
            return;

        var glyphSlot = slot.AddSlot("Glyph");
        var glyph = glyphSlot.AttachComponent<TextRenderer>();
        if (theme?.Font != null)
            glyph.Font.Target = theme.Font;
        // A lone letter (the host's H) is read from across a room, so it is set bold and as large as the chip allows.
        bool single = badge.Glyph.Length == 1;
        glyph.RichText.Value = single;
        glyph.Text.Value = single ? "<b>" + badge.Glyph + "</b>" : badge.Glyph;
        glyph.Size.Value = size * (single ? GlyphScale : PairGlyphScale);
        glyph.Color.Value = badge.Ink;
        glyph.HorizontalAlign.Value = TextHorizontalAlignment.Center;
        glyph.VerticalAlign.Value = TextVerticalAlignment.Middle;
        glyph.MaxViewDistance.Value = MathF.Max(0f, MaxViewDistance.Value);
    }

    // THE GROUP CARD

    private void RebuildGroupCard()
    {
        var card = _groupCard;
        if (card == null || card.IsDestroyed)
            return;

        var user = _user;
        bool here = user != null && !user.IsDestroyed;
        string tag = here ? user!.GroupTag.Value ?? string.Empty : string.Empty;
        string iconHash = here ? user!.GroupIconHash.Value ?? string.Empty : string.Empty;
        string bannerHash = here ? user!.GroupBannerHash.Value ?? string.Empty : string.Empty;
        string groupId = here ? user!.GroupId.Value ?? string.Empty : string.Empty;

        // Nothing to say, no strip. A group id with no tag, icon or banner would draw an empty coloured
        // stub over somebody's head, which is worse than nothing.
        bool active = ShowGroupCard.Value
            && groupId.Length > 0
            && (tag.Length > 0 || iconHash.Length > 0 || bannerHash.Length > 0);

        if (active != _cardActive)
        {
            _cardActive = active;
            card.ActiveSelf.Value = active;
            // Everything stacked over the plate steps over the strip, so it moves when the strip comes or goes.
            MarkLayoutDirty();
        }
        if (!active)
            return;

        var groupColor = user!.GroupColor.Value;
        var fill = CompositeGroupFill(groupColor);
        if (_groupCardMesh != null && !_groupCardMesh.IsDestroyed)
            _groupCardMesh.Color = fill;

        bool banner = bannerHash.Length > 0;
        if (banner)
            PointBannerAt(bannerHash);
        else
            DropBanner();
        if (banner != _bannerWanted)
        {
            _bannerWanted = banner;
            _cardLaidOutFor = new float2(-1f, -1f);
        }
        if (_groupChipSlot != null && !_groupChipSlot.IsDestroyed && _groupChipSlot.ActiveSelf.Value != banner)
            _groupChipSlot.ActiveSelf.Value = banner;

        bool showIcon = iconHash.Length > 0;
        if (showIcon)
            PointIconAt(iconHash);
        if (_groupIconSlot != null && !_groupIconSlot.IsDestroyed && _groupIconSlot.ActiveSelf.Value != showIcon)
            _groupIconSlot.ActiveSelf.Value = showIcon;

        bool showTag = tag.Length > 0;
        if (_groupTag != null && !_groupTag.IsDestroyed)
        {
            if (showTag)
            {
                _groupTag.Text.Value = tag;
                // white on the dark chip over a picture, the plate's own ink rule on a colour strip
                _groupTag.Color.Value = banner ? color.White : InkFor(fill);
                _groupTag.MaxViewDistance.Value = MathF.Max(0f, MaxViewDistance.Value);
            }
            if (_groupTag.Slot != null && _groupTag.Slot.ActiveSelf.Value != showTag)
                _groupTag.Slot.ActiveSelf.Value = showTag;
        }

        // The quiet marker for a group's own staff, and only inside that group's own world. No word and no
        // glyph on purpose: it must not be blatant or be confused with official staff, and
        // a dot in the group's own colour is as far as that goes. -xlinka
        bool showDot = groupId.Length > 0
            && string.Equals(groupId, _hostGroupId, StringComparison.Ordinal)
            && IsGroupStaff(user.GroupRole.Value);
        if (showDot && _groupDotMesh != null && !_groupDotMesh.IsDestroyed)
            _groupDotMesh.Color = color.Lerp(groupColor.a > 0f ? groupColor : fill, color.White, 0.5f);
        if (_groupDotSlot != null && !_groupDotSlot.IsDestroyed && _groupDotSlot.ActiveSelf.Value != showDot)
            _groupDotSlot.ActiveSelf.Value = showDot;

        if (showIcon != _cardIconShown || showTag != _cardTagShown || showDot != _cardDotShown)
        {
            _cardIconShown = showIcon;
            _cardTagShown = showTag;
            _cardDotShown = showDot;
            _cardLaidOutFor = new float2(-1f, -1f);
        }
        ApplyViewDistance();
    }

    // The banner arrives on its own time. Until it does the strip shows the group's colour, and the picture
    // goes on the moment it is in, cropped to cover the strip.
    private void TryShowBanner()
    {
        if (!_bannerWanted || _bannerShown)
            return;
        var texture = _groupBannerProvider?.Asset;
        if (texture == null || texture.Width <= 0 || texture.Height <= 0
            || _groupBannerMaterial == null || _groupBannerMaterial.IsDestroyed
            || _groupCardRenderer == null || _groupCardRenderer.IsDestroyed)
            return;

        FitBanner();
        _groupCardRenderer.Material.Target = _groupBannerMaterial;
        _bannerShown = true;
    }

    // Cover: the picture fills the strip and what overflows is cut evenly from both ends.
    private void FitBanner()
    {
        var texture = _groupBannerProvider?.Asset;
        if (texture == null || texture.Width <= 0 || texture.Height <= 0 || _groupBannerMaterial == null || _groupBannerMaterial.IsDestroyed)
            return;
        float picture = texture.Width / (float)texture.Height;
        var scale = picture > _stripAspect
            ? new float2(_stripAspect / picture, 1f)
            : new float2(1f, picture / _stripAspect);
        _groupBannerMaterial.TextureScale.Value = scale;
        _groupBannerMaterial.TextureOffset.Value = (float2.One - scale) * 0.5f;
    }

    private void DropBanner()
    {
        _cardBannerUrl = string.Empty;
        ShowColourStrip();
    }

    private void ShowColourStrip()
    {
        if (!_bannerShown)
            return;
        _bannerShown = false;
        if (_groupCardRenderer != null && !_groupCardRenderer.IsDestroyed && _theme?.PlateMaterial != null)
            _groupCardRenderer.Material.Target = _theme.PlateMaterial;
    }

    // Retargeted like the icon's, never replaced.
    private void PointBannerAt(string hash)
    {
        var host = _groupCard;
        if (host == null || host.IsDestroyed)
            return;

        string url = Nexus.Cloud.Cdn.ServiceConfig.Current.GetContentUrl(hash);
        if (_groupBannerProvider != null && !_groupBannerProvider.IsDestroyed
            && string.Equals(url, _cardBannerUrl, StringComparison.Ordinal))
        {
            return;
        }

        if (_groupBannerProvider == null || _groupBannerProvider.IsDestroyed)
        {
            var slot = host.AddSlot("BannerAsset");
            slot.ActiveSelf.Value = false;
            _groupBannerProvider = slot.AttachComponent<Lumora.Core.Assets.ImageProvider>();
            if (_groupBannerMaterial != null && !_groupBannerMaterial.IsDestroyed)
                _groupBannerMaterial.Texture.Target = _groupBannerProvider;
        }

        // a new picture shows the colour until it is in, never the last group's picture
        ShowColourStrip();
        _cardBannerUrl = url;
        try
        {
            _groupBannerProvider.URL.Value = new Uri(url);
        }
        catch (UriFormatException)
        {
            _cardBannerUrl = string.Empty;
        }
    }

    private static bool IsGroupStaff(string? role)
        => role is "Moderator" or "Admin" or "Owner";

    // The provider is built once and RETARGETED. Replacing it per hash change would leave a dead asset
    // component on the plate for every group the wearer has ever worn this session.
    private void PointIconAt(string hash)
    {
        var host = _groupCard;
        if (host == null || host.IsDestroyed)
            return;

        string url = Nexus.Cloud.Cdn.ServiceConfig.Current.GetContentUrl(hash);
        if (_groupIconProvider != null && !_groupIconProvider.IsDestroyed
            && string.Equals(url, _cardIconUrl, StringComparison.Ordinal))
        {
            return;
        }

        if (_groupIconProvider == null || _groupIconProvider.IsDestroyed)
        {
            // Its own hidden child so the provider is never something the card's layout has to step over.
            var slot = host.AddSlot("IconAsset");
            slot.ActiveSelf.Value = false;
            _groupIconProvider = slot.AttachComponent<Lumora.Core.Assets.ImageProvider>();
            _groupIconProvider.GenerateMipmaps.Value = false;
            if (_groupIconMaterial != null && !_groupIconMaterial.IsDestroyed)
                _groupIconMaterial.Texture.Target = _groupIconProvider;
        }

        _cardIconUrl = url;
        try
        {
            _groupIconProvider.URL.Value = new Uri(url);
        }
        catch (UriFormatException)
        {
            // A hash the service handed us that does not make a URL is a hash we draw nothing for.
            _cardIconUrl = string.Empty;
        }
    }

    // The group colour laid over the plate's own tint at GroupFillStrength, composited here rather
    // than stacked as a second translucent quad: two alpha layers over the same pixels come out darker
    // than one and the card would not match the plate it is sitting on. A clear group colour (nothing
    // parsed) falls straight back to the plate's own tint. -xlinka
    private static color CompositeGroupFill(in color group)
    {
        var plate = NameplateTheme.PlateFill;
        if (group.a <= 0f)
            return plate;

        float source = GroupFillStrength * group.a;
        float outAlpha = source + plate.a * (1f - source);
        if (outAlpha <= 0f)
            return plate;
        float backing = plate.a * (1f - source);
        return new color(
            (group.r * source + plate.r * backing) / outAlpha,
            (group.g * source + plate.g * backing) / outAlpha,
            (group.b * source + plate.b * backing) / outAlpha,
            outAlpha);
    }

    // White or the plate's own ink, whichever pulls further away from the fill. A group that picked a pale
    // colour gets dark text and one that picked a deep colour gets white, without anybody authoring it.
    private color InkFor(in color fill)
    {
        var plateInk = _equip != null && !_equip.IsDestroyed ? _equip.BadgeColor.Value : FallbackText;
        float fillLuma = Luminance(fill);
        return MathF.Abs(Luminance(color.White) - fillLuma) >= MathF.Abs(Luminance(plateInk) - fillLuma)
            ? color.White
            : plateInk;
    }

    private static float Luminance(in color value)
        => 0.2126f * value.r + 0.7152f * value.g + 0.0722f * value.b;

    // How much room the strip takes off the plate's top edge. Everything that stacks above the plate - the
    // badge row and the status dot - steps over this rather than over the strip's own geometry.
    private float GroupCardOffset => _cardActive ? _cardHeight + GroupCardGap + RowGap : 0f;

    // The strip's size follows the plate's height only, so it is the same strip over every name. Its
    // contents wait on the tag's shaping like the name does.
    private void LayoutGroupCard(float panelWidth, float panelHeight)
    {
        var card = _groupCard;
        if (!_cardActive || card == null || card.IsDestroyed || _groupCardMesh == null || _groupCardMesh.IsDestroyed)
            return;

        // narrower than the plate under it, in a banner's shape, the shape kept within reason on a short name
        float height = panelHeight * BannerHeightRatio;
        float width = System.Math.Clamp(panelWidth * BannerWidthRatio, height * BannerMinAspect, height * BannerAspect);
        _cardHeight = height;

        float chipHeight = height * BannerChipHeightRatio;
        float icon = chipHeight * 0.72f;
        float dot = chipHeight * 0.3f;

        float tagWidth = 0f;
        if (_cardTagShown && _groupTag != null && !_groupTag.IsDestroyed)
        {
            // The tag is sized to the chip, and a long one comes down until it fits the strip with the mark
            // and the dot beside it. A size change reshapes, so the layout waits a frame for the new width.
            float room = width - BannerChipInset * 2f - BannerChipPad * 2f
                - (_cardIconShown ? icon + GroupContentGap : 0f) - (_cardDotShown ? dot + GroupContentGap : 0f);
            float wanted = chipHeight * BannerTagScale;
            var shaped = _groupTag.RenderedSize;
            if (shaped.x <= 0f)
            {
                if (MathF.Abs(_groupTag.Size.Value - wanted) > wanted * 0.01f && _groupTag.Size.Value > wanted)
                    _groupTag.Size.Value = wanted;
                return;   // still shaping; the next update lays it out
            }
            float size = _groupTag.Size.Value;
            float target = MathF.Min(wanted, size * room / shaped.x);
            if (MathF.Abs(size - target) > size * 0.01f)
            {
                _groupTag.Size.Value = target;
                _cardLaidOutFor = new float2(-1f, -1f);
                return;
            }
            tagWidth = shaped.x;
        }

        var signature = new float2(tagWidth + (_bannerWanted ? 1000f : 0f) + width * 10000f, panelHeight);
        if ((signature - _cardLaidOutFor).LengthSquared < 1e-8f)
            return;
        _cardLaidOutFor = signature;

        _groupCardMesh.Size.Value = new float2(width, height);
        _stripAspect = width / height;
        if (_bannerShown)
            FitBanner();
        _groupCardMesh.CornerRadius.Value = height * PanelRadiusRatio;
        _groupCardMesh.BottomCornerRadius.Value = height * PanelRadiusRatio;
        card.LocalPosition.Value = new float3(0f, panelHeight * 0.5f + GroupCardGap + height * 0.5f, 0f);
        if (_groupIconMesh != null && !_groupIconMesh.IsDestroyed)
        {
            _groupIconMesh.Size.Value = new float2(icon, icon);
            _groupIconMesh.CornerRadius.Value = icon * ChipRadiusRatio;
        }
        if (_groupDotMesh != null && !_groupDotMesh.IsDestroyed)
        {
            _groupDotMesh.Size.Value = new float2(dot, dot);
            _groupDotMesh.CornerRadius.Value = dot * 0.5f;
        }

        float content = 0f;
        if (_cardIconShown)
            content += icon;
        if (_cardTagShown)
            content += (content > 0f ? GroupContentGap : 0f) + tagWidth;
        if (_cardDotShown)
            content += (content > 0f ? GroupContentGap : 0f) + dot;

        // Over a picture the mark and tag ride a chip at the strip's left; on a plain colour they sit centred.
        float cursor;
        if (_bannerWanted)
        {
            float chipWidth = MathF.Min(content + BannerChipPad * 2f, width - BannerChipInset * 2f);
            float chipLeft = -width * 0.5f + BannerChipInset;
            if (_groupChipMesh != null && !_groupChipMesh.IsDestroyed)
            {
                _groupChipMesh.Size.Value = new float2(chipWidth, chipHeight);
                _groupChipMesh.CornerRadius.Value = chipHeight * PanelRadiusRatio;
            }
            if (_groupChipSlot != null && !_groupChipSlot.IsDestroyed)
                _groupChipSlot.LocalPosition.Value = new float3(chipLeft + chipWidth * 0.5f, 0f, StripChipDepth);
            cursor = chipLeft + BannerChipPad;
        }
        else
        {
            cursor = -content * 0.5f;
        }

        if (_cardIconShown && _groupIconSlot != null && !_groupIconSlot.IsDestroyed)
        {
            _groupIconSlot.LocalPosition.Value = new float3(cursor + icon * 0.5f, 0f, CardMarkDepth);
            cursor += icon + GroupContentGap;
        }
        if (_cardTagShown && _groupTag?.Slot != null && !_groupTag.Slot.IsDestroyed)
        {
            _groupTag.Slot.LocalPosition.Value = new float3(cursor + tagWidth * 0.5f, 0f, StripTextDepth);
            cursor += tagWidth + GroupContentGap;
        }
        if (_cardDotShown && _groupDotSlot != null && !_groupDotSlot.IsDestroyed)
            _groupDotSlot.LocalPosition.Value = new float3(cursor + dot * 0.5f, 0f, CardMarkDepth);
    }

    private static bool SameBadges(List<NameplateBadge> a, List<NameplateBadge> b)
    {
        if (a.Count != b.Count)
            return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!a[i].Equals(b[i]))
                return false;
        }
        return true;
    }

    // LAYOUT
    // The panel and the badge row are sized off the SHAPED text, not off a guess: TextRenderer fills
    // RenderedSize during meshing, which lands a frame or two after the text is set and again whenever
    // the font atlas finishes loading. Re-laying out only when that number moves is what keeps this off
    // the per-frame bill.

    private void MarkLayoutDirty() => _laidOutFor = new float2(-1f, -1f);

    private void Layout()
    {
        if (_label == null || _label.IsDestroyed || _plate == null || _plate.IsDestroyed)
            return;

        var size = _label.RenderedSize;
        if (size.x <= 0f || size.y <= 0f)
            return;

        float panelHeight = size.y + PanelPadY;

        // The card runs on its own guard: its width waits on the TAG shaping, which lands on a different
        // frame from the name's, and the name not having moved is no reason to leave the card unplaced.
        // The staff tab waits on its word the same way.
        LayoutGroupCard(MathF.Max(size.x, BadgeRowSpan()) + PanelPadX, panelHeight);
        LayoutStaffTab(panelHeight);

        if ((size - _laidOutFor).LengthSquared < 1e-8f)
            return;
        _laidOutFor = size;

        // CHIPS ABOVE, NAME INSIDE. The row is its own line sitting on the plate's top edge, so the plate
        // is drawn round the WIDER of the two rather than round the pair: a long name never shoves the
        // chips sideways and a long chip row never stretches the name's own padding. -xlinka
        float panelWidth = MathF.Max(size.x, BadgeRowSpan()) + PanelPadX;
        var panelSize = new float2(panelWidth, panelHeight);
        float radius = panelHeight * PanelRadiusRatio;
        if (_panelMesh != null && !_panelMesh.IsDestroyed)
        {
            _panelMesh.Size.Value = panelSize;
            _panelMesh.CornerRadius.Value = radius;
        }
        // Same size and same radius as the panel: the band INNER outline is the panel outline, so it
        // hugs the edge at every width the name grows to.
        if (_rimMesh != null && !_rimMesh.IsDestroyed)
        {
            _rimMesh.Size.Value = panelSize;
            _rimMesh.CornerRadius.Value = radius;
        }

        if (_accentMesh != null && !_accentMesh.IsDestroyed && _accentSlot != null && !_accentSlot.IsDestroyed)
        {
            _accentMesh.Size.Value = new float2(MathF.Max(panelWidth * AccentWidthRatio, AccentHeight * 4f),
                AccentHeight);
            _accentSlot.LocalPosition.Value = new float3(0f,
                -panelHeight * 0.5f + AccentInset + AccentHeight * 0.5f, ChipDepth);
        }

        SpreadBadgeRow(panelHeight);
        var labelSlot = _label.Slot;
        if (labelSlot != null && !labelSlot.IsDestroyed)
            labelSlot.LocalPosition.Value = float3.Zero;

        var status = _plate.FindChild(StatusSlotName, recursive: false);
        if (status != null && !status.IsDestroyed)
        {
            status.ActiveSelf.Value = ShowStatus.Value;
            float statusX = -(panelWidth * 0.5f + StatusGap + StatusSize * 0.5f);
            status.LocalPosition.Value = new float3(statusX, GroupCardOffset, 0f);
        }

        var positioner = _plate.GetComponent<PositionAtUser>();
        if (positioner != null && !positioner.IsDestroyed)
            positioner.VerticalOffset.Value = HeightOffset.Value;
    }

    private void LayoutStaffTab(float panelHeight)
    {
        var tab = _staffTab;
        var word = _staffTabWord;
        if (tab == null || tab.IsDestroyed || !tab.ActiveSelf.Value || word == null || word.IsDestroyed
            || _staffTabMesh == null || _staffTabMesh.IsDestroyed)
        {
            return;
        }

        float height = MathF.Max(0.001f, TextSize.Value) * StaffTabHeightRatio;
        float wordSize = height * StaffTabTextRatio;
        if (word.Size.Value != wordSize)
        {
            word.Size.Value = wordSize;
            return;   // reshapes; the next update lays it out
        }
        var shaped = word.RenderedSize;
        if (shaped.x <= 0f)
            return;

        var signature = new float2(shaped.x, panelHeight);
        if ((signature - _tabLaidOutFor).LengthSquared < 1e-8f)
            return;
        _tabLaidOutFor = signature;

        _staffTabMesh.Size.Value = new float2(shaped.x + height * StaffTabPadRatio, height);
        _staffTabMesh.CornerRadius.Value = height * 0.5f;
        tab.LocalPosition.Value = new float3(0f, -panelHeight * 0.5f, ChipDepth);
    }

    // The row is centred over the name, one RowGap of daylight off the plate's top edge, and it steps over
    // the group card when there is one. Slot order is registry order, which is host, role, voice, live by Order.
    private void SpreadBadgeRow(float panelHeight)
    {
        if (_badgeRow == null || _badgeRow.IsDestroyed)
            return;

        float chip = ChipSize;
        _badgeRow.LocalPosition.Value = new float3(0f,
            panelHeight * 0.5f + GroupCardOffset + RowGap + chip * 0.5f, 0f);

        float left = BadgeRowSpan() * -0.5f;
        var children = _badgeRow.Children;
        for (int i = 0; i < children.Count; i++)
            children[i].LocalPosition.Value = new float3(left + chip * 0.5f + i * (chip + BadgeGap), 0f, 0f);
    }

    // What the chip row measures, off the chips that are actually BUILT rather than the list the registry
    // last handed back: the two can disagree for one update while a rebuild is pending, and the panel is
    // sized off this.
    private float BadgeRowSpan()
    {
        int count = _badgeRow == null || _badgeRow.IsDestroyed ? 0 : _badgeRow.Children.Count;
        return count > 0 ? count * ChipSize + (count - 1) * BadgeGap : 0f;
    }

    // TEARDOWN

    private void Unbind()
    {
        if (_user != null && !_user.IsDestroyed)
        {
            if (_userChanged != null)
            {
                _user.UserName.Changed -= _userChanged;
                _user.IsPresent.Changed -= _userChanged;
                _user.IsLive.Changed -= _userChanged;
                _user.Capture.Changed -= _userChanged;
                _user.AccountId.Changed -= _userChanged;
                _user.IsSupporter.Changed -= _userChanged;
                _user.SupporterColor.Changed -= _userChanged;
                _user.SupporterStyle.Changed -= _userChanged;
                _user.IsStaff.Changed -= _userChanged;
                _user.AccountKind.Changed -= _userChanged;
            }
            if (_groupChanged != null)
            {
                _user.GroupId.Changed -= _groupChanged;
                _user.GroupTag.Changed -= _groupChanged;
                _user.GroupName.Changed -= _groupChanged;
                _user.GroupIconHash.Changed -= _groupChanged;
                _user.GroupBannerHash.Changed -= _groupChanged;
                _user.GroupRole.Changed -= _groupChanged;
                _user.GroupColor.Changed -= _groupChanged;
            }
        }
        _userChanged = null;
        _groupChanged = null;
        _user = null;

        if (_equip != null && !_equip.IsDestroyed && _equipChanged != null)
        {
            _equip.BadgeText.Changed -= _equipChanged;
            _equip.BadgeColor.Changed -= _equipChanged;
            _equip.BadgeOutline.Changed -= _equipChanged;
            _equip.BadgeRim.Changed -= _equipChanged;
        }
        _equipChanged = null;
        _equip = null;

        UnwatchPermissionConfig();
        _activity.Reset();
    }

    public override void OnDestroy()
    {
        Unbind();
        base.OnDestroy();
    }
}
