// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Threading;
using System.Threading.Tasks;
using Helio.UI;
using Helio.UI.Listing;
using Lumora.Core.Assets;
using Lumora.Core.Math;
using Lumora.Core.Photos;
using Lumora.Core.Social;
using Lumora.Nexus.Cloud.Cdn;

namespace Lumora.Core.Components.UI;

// REPORTING SOMEONE
//
// Report sits on the face at the top of anybody's profile but your own. The form takes the person's place in
// the pane: what happened as one of nine, what they did in words, and a picture if there is one, either the
// view right now (filmed by a camera of this machine's, which never sees the dash) or one of your photos.
// Either picture is written out again small before it goes. What was typed stays while the form is backed
// out of, until it is sent or someone else is reported. The session you are in when it opens goes with it.
// -xlinka
public sealed partial class PeopleScreen
{
    private const float ReportPillHeight = 20f;

    private enum ReportStage { Form, Sending, Sent }

    private enum ReportPictureState { None, Taking, Getting, Ready, Failed }

    // Written from the connection's thread, read on the update.
    private sealed class SendProgress : IProgress<float>
    {
        private float _value;

        public float Value => Volatile.Read(ref _value);

        public void Report(float value) => Volatile.Write(ref _value, value);
    }

    private Slot? _pictureHost;
    private Slot? _reportPill;
    private RectTransform? _reportPillRect;
    private Text? _reportPillText;

    private string _reportFor = string.Empty;
    private ReportStage _reportStage;
    private int _reportCategory = -1;
    private string _reportReason = string.Empty;
    private string? _reportWorld;
    private string? _reportSession;
    private string _reportError = string.Empty;

    private ReportPictureState _reportPictureState;
    private ReportImage? _reportImage;
    private bool _reportTaken;
    private string? _reportPhotoHash;
    private ImageProvider? _reportShot;
    private string? _reportShotAddress;
    private bool _reportShotShown;
    private string _reportPictureNote = string.Empty;
    private int _reportTicket;

    private readonly SendProgress _reportProgress = new();
    private float _reportShownProgress = -1f;

    private ReportCategoriesItem? _reportCategoryItem;
    private ReportPictureItem? _reportPictureItem;
    private ReportSendItem? _reportSendItem;

    // THE BUTTON ON THE FACE

    // Across the foot of the profile picture, on a chunk of its own so it draws over the face.
    private void BuildReportPill(Slot face)
    {
        var pill = SettingsUI.Child(face, "Report", new float2(0.5f, 0f), new float2(0.5f, 0f),
            new float2(-36f, -ReportPillHeight * 0.5f), new float2(36f, ReportPillHeight * 0.5f));
        pill.AttachComponent<GraphicChunkRoot>();
        var plate = SettingsUI.Panel(pill, DashTheme.Panel, DashTheme.OutlineStrong, ReportPillHeight * 0.5f);
        var button = pill.AttachComponent<Button>();
        button.Clicked += (_, _) => OpenReport(_personId);
        var hover = Blend(DashTheme.Panel, DashTheme.Negative, 0.3f);
        var fill = button.AddColorDriver(plate.Color, DashTheme.Panel, InteractionColorMode.Direct);
        fill.NormalColor.Value = DashTheme.Panel;
        fill.HighlightColor.Value = hover;
        fill.PressedColor.Value = Blend(DashTheme.Panel, DashTheme.Negative, 0.45f);
        fill.DisabledColor.Value = DashTheme.Panel;
        fill.Apply();
        var glyph = SettingsUI.Child(pill, "Glyph", new float2(0f, 0.5f), new float2(0f, 0.5f), new float2(8f, -5.5f), new float2(19f, 5.5f));
        var image = glyph.AttachComponent<Image>();
        if (_dashboard != null)
            image.Texture.Target = _dashboard.IconProvider("alert");
        image.PreserveAspect.Value = true;
        image.InteractionTarget.Value = false;
        image.Tint.Value = DashTheme.Warning;
        _reportPillText = SettingsUI.Label(pill, "Text", _fonts.Medium, DashTheme.FontLabel, DashTheme.Text, TextHorizontalAlignment.Left,
            float2.Zero, float2.One, new float2(23f, 0f), new float2(-7f, 0f));
        _reportPillText.Ellipsis.Value = false;
        _reportPill = pill;
        _reportPillRect = SettingsUI.Rect(pill);
        pill.ActiveSelf.Value = false;
    }

    private static color Blend(in color a, in color b, float t)
        => new(a.r + (b.r - a.r) * t, a.g + (b.g - a.g) * t, a.b + (b.b - a.b) * t, 1f);

    private void BindReportPill()
    {
        bool show = _pane == PaneView.Person && Reports.CanReport(_personId, SelfId);
        PeopleShow.Set(_reportPill, show);
        if (!show || _reportPillText == null || _reportPillRect == null)
            return;
        string label = ReportLocale.Report.Resolve();
        ListingStyle.SetText(_reportPillText, label);
        float half = MathF.Ceiling(30f + label.Length * DashTheme.FontLabel * 0.58f) * 0.5f;
        SettingsUI.SetOffsets(_reportPillRect, new float2(-half, -ReportPillHeight * 0.5f), new float2(half, ReportPillHeight * 0.5f));
    }

    // OPENING AND LEAVING

    private void OpenReport(string userId)
    {
        if (string.IsNullOrEmpty(userId))
            return;
        if (!SignedIn)
        {
            SignInFromBar();
            return;
        }
        if (!Reports.CanReport(userId, SelfId))
            return;
        FlushTyping();
        if (_pane is not (PaneView.Person or PaneView.Report) || !string.Equals(userId, _personId, StringComparison.Ordinal))
            OpenPerson(userId, PersonTab.Profile);
        if (!string.Equals(_reportFor, userId, StringComparison.Ordinal) || _reportStage == ReportStage.Sent)
            ResetReport(userId);
        LeaveChat();
        (_reportWorld, _reportSession) = Reports.Where(Engine.Current?.WorldManager?.FocusedWorld);
        _pane = PaneView.Report;
        _paneError = null;
        _paneNotice = null;
        _narrowPerson = true;
        _paneDirty = true;
        _headerDirty = true;
        _listDirty = true;
        _body?.ResetToTop();
        ApplyPaneVisibility();
    }

    private void CloseReport()
    {
        if (_pane != PaneView.Report)
            return;
        FlushTyping();
        if (_reportStage == ReportStage.Sent)
            ResetReport(string.Empty);
        _pane = PaneView.Person;
        SetTab(PersonTab.Profile, force: true);
        _paneDirty = true;
        _headerDirty = true;
        _listDirty = true;
        ApplyPaneVisibility();
    }

    private void ResetReport(string userId)
    {
        DropReportPicture();
        _reportFor = userId;
        _reportStage = ReportStage.Form;
        _reportCategory = -1;
        _reportReason = string.Empty;
        _reportError = string.Empty;
        _reportProgress.Report(0f);
    }

    // THE FORM

    private void BuildReportItems()
    {
        _reportCategoryItem = null;
        _reportPictureItem = null;
        _reportSendItem = null;
        if (_reportStage == ReportStage.Sent)
        {
            _bodyItems.AddRoot(new ReportThanksItem("report.thanks") { Body = ReportLocale.ThanksBody(ShownName()).Resolve() });
            return;
        }

        var options = new string[ReportLocale.Categories.Length];
        for (int i = 0; i < options.Length; i++)
            options[i] = ReportLocale.Categories[i].Resolve();
        _reportCategoryItem = new ReportCategoriesItem("report.category", ReportLocale.Category)
        {
            Options = options,
            Columns = ReportCategoriesItem.ColumnsFor(BodyWidth()),
        };
        _bodyItems.AddRoot(_reportCategoryItem);
        _bodyItems.AddRoot(new PeopleFieldItem("report.reason", ReportLocale.Reason)
        {
            Placeholder = ReportLocale.ReasonHint.Resolve(),
            MaxLength = ReportLimits.ReasonMax,
            Multiline = true,
            Read = () => _reportReason,
            Write = text => _reportReason = text ?? string.Empty,
        });
        _reportPictureItem = new ReportPictureItem("report.picture", ReportLocale.Picture);
        _bodyItems.AddRoot(_reportPictureItem);
        if (!string.IsNullOrEmpty(_reportWorld))
            _bodyItems.AddRoot(new PeopleNoteItem("report.where", ReportLocale.Where(_reportWorld!)) { Wrap = true, Height = 34f });
        _reportSendItem = new ReportSendItem("report.send");
        _bodyItems.AddRoot(_reportSendItem);
        PaintReport();
    }

    // The live state into the form's items. Nothing here changes a row's height, so a rebind shows it.
    private void PaintReport()
    {
        if (_reportCategoryItem is { } categories)
        {
            categories.Selected = _reportCategory;
            categories.Hint = (_reportCategory >= 0 && _reportCategory < ReportLocale.CategoryHints.Length
                ? ReportLocale.CategoryHints[_reportCategory]
                : ReportLocale.CategoryNone).Resolve();
        }
        if (_reportPictureItem is { } picture)
        {
            picture.Picture = _reportShot != null && !_reportShot.IsDestroyed ? _reportShot : _pictures?.Get(_reportPhotoHash);
            picture.Has = picture.Picture != null;
            picture.Busy = _reportPictureState is ReportPictureState.Taking or ReportPictureState.Getting || _reportStage != ReportStage.Form;
            picture.StatusTint = _reportPictureState == ReportPictureState.Failed ? DashTheme.Negative
                : _reportPictureState == ReportPictureState.None ? DashTheme.TextMuted : DashTheme.TextDim;
            picture.Status = _reportPictureState switch
            {
                ReportPictureState.Taking => ReportLocale.Taking.Resolve(),
                ReportPictureState.Getting => ReportLocale.Getting.Resolve(),
                ReportPictureState.Ready when _reportImage != null => (_reportTaken
                    ? ReportLocale.Taken(PictureSize(_reportImage))
                    : ReportLocale.Chosen(PictureSize(_reportImage))).Resolve(),
                ReportPictureState.Failed => _reportPictureNote,
                _ => ReportLocale.PictureNone.Resolve(),
            };
        }
        if (_reportSendItem is { } send)
        {
            send.Sending = _reportStage == ReportStage.Sending;
            send.Progress = _reportProgress.Value;
            send.Error = _reportError;
        }
    }

    private static string PictureSize(ReportImage image)
        => image.Width + " × " + image.Height + ", " + PeopleFormat.Bytes(image.Bytes.LongLength);

    private void RepaintReport()
    {
        if (_pane != PaneView.Report)
            return;
        PaintReport();
        _body?.RefreshValues();
        MarkDirty();
    }

    // Each frame while the form shows: a taken picture landing in its preview, and how far the sending is.
    private void TickReport()
    {
        if (_pane != PaneView.Report)
            return;
        // A window resized narrower or wider lays the categories out again: their rows are as tall as the
        // number of lines they take at the width the pane has once it has settled.
        if (_reportCategoryItem is { } categories && ReportCategoriesItem.ColumnsFor(BodyWidth()) != categories.Columns)
            _paneDirty = true;
        bool repaint = false;
        if (_reportShot != null && !_reportShotShown && PeoplePictureCache.Ready(_reportShot))
        {
            _reportShotShown = true;
            repaint = true;
        }
        if (_reportStage == ReportStage.Sending)
        {
            float share = _reportProgress.Value;
            if (MathF.Abs(share - _reportShownProgress) >= 0.01f)
            {
                _reportShownProgress = share;
                repaint = true;
            }
        }
        if (repaint)
            RepaintReport();
    }

    // A change of whether there is an error at all moves the rows under it, so the page is built again.
    private void SetReportError(string text)
    {
        bool reshape = text.Length > 0 != _reportError.Length > 0;
        _reportError = text;
        if (reshape)
            _paneDirty = true;
        else
            RepaintReport();
    }

    private void PickReportCategory(int index)
    {
        if (_reportStage != ReportStage.Form || index < 0 || index >= ReportCategories.All.Count)
            return;
        _reportCategory = index;
        if (_reportError.Length > 0)
            SetReportError(string.Empty);
        RepaintReport();
    }

    // THE PICTURE

    private bool PictureBusy => _reportPictureState is ReportPictureState.Taking or ReportPictureState.Getting;

    private void TakeReportPicture()
    {
        if (_reportStage != ReportStage.Form || PictureBusy)
            return;
        FlushTyping();
        DropReportPicture();
        var target = Engine.Current?.WorldManager?.FocusedWorld;
        if (target == null || target.IsDestroyed || target.IsDisposed || target.IsUserspace)
        {
            PictureFailed(ReportLocale.NoWorld.Resolve());
            return;
        }
        int ticket = _reportTicket;
        _reportPictureState = ReportPictureState.Taking;
        RepaintReport();
        var ui = World;
        _ = Task.Run(async () =>
        {
            ReportImage? image = null;
            string? address = null;
            try
            {
                image = await ReportPicture.TakeAsync(target).ConfigureAwait(false);
                var db = Engine.Current?.LocalDB;
                if (image != null && db != null)
                    address = await db.SaveAssetAsync(image.Bytes, image.Encoding.Extension()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logging.Logger.Warn($"Reports: the picture failed: {ex.Message}");
            }
            OnUi(ui, () =>
            {
                if (IsDestroyed || ticket != _reportTicket)
                {
                    ForgetShot(address);
                    return;
                }
                if (image == null)
                {
                    ForgetShot(address);
                    PictureFailed(ReportLocale.TakeFailed.Resolve());
                    return;
                }
                _reportImage = image;
                _reportTaken = true;
                _reportPictureState = ReportPictureState.Ready;
                if (!string.IsNullOrEmpty(address))
                    ShowShot(address!);
                RepaintReport();
            });
        });
    }

    private void ChooseReportPhoto()
    {
        if (_reportStage != ReportStage.Form || PictureBusy)
            return;
        OpenPicker(PickPurpose.Report);
    }

    // A photo picked out of the inventory: shown straight away by its hash, fetched and made fit to send
    // behind it.
    private void UseReportPhoto(string hash)
    {
        DropReportPicture();
        int ticket = _reportTicket;
        _reportPhotoHash = hash;
        _reportPictureState = ReportPictureState.Getting;
        RepaintReport();
        var ui = World;
        _ = Task.Run(async () =>
        {
            ReportImage? image = null;
            try
            {
                var client = Engine.Current?.CDNClient;
                var fetched = client != null ? await client.FetchContent(hash).ConfigureAwait(false) : null;
                if (fetched is { Success: true, Data: { Length: > 0 } bytes })
                    image = ReportPicture.Prepare(bytes);
            }
            catch (Exception ex)
            {
                Logging.Logger.Warn($"Reports: the photo could not be read: {ex.Message}");
            }
            OnUi(ui, () =>
            {
                if (IsDestroyed || ticket != _reportTicket)
                    return;
                if (image == null)
                {
                    _reportPhotoHash = null;
                    PictureFailed(ReportLocale.ReadFailed.Resolve());
                    return;
                }
                _reportImage = image;
                _reportTaken = false;
                _reportPictureState = ReportPictureState.Ready;
                RepaintReport();
            });
        });
    }

    private void RemoveReportPicture()
    {
        if (_reportStage != ReportStage.Form)
            return;
        DropReportPicture();
        RepaintReport();
    }

    private void PictureFailed(string note)
    {
        _reportPictureState = ReportPictureState.Failed;
        _reportPictureNote = note;
        RepaintReport();
    }

    // Lets go of any picture, and of one still on its way.
    private void DropReportPicture()
    {
        _reportTicket++;
        _reportImage = null;
        _reportTaken = false;
        _reportPhotoHash = null;
        _reportPictureState = ReportPictureState.None;
        _reportPictureNote = string.Empty;
        DropShot();
    }

    // The taken picture is previewed from the local store, the one place an image provider reads bytes this
    // machine made; it is taken out again as soon as the report lets go of it.
    private void ShowShot(string address)
    {
        Uri url;
        try
        {
            url = new Uri(address);
        }
        catch (UriFormatException)
        {
            ForgetShot(address);
            return;
        }
        var host = _pictureHost ?? Slot;
        _reportShot = host.AddSlot("Report Picture").AttachComponent<ImageProvider>();
        _reportShot.GenerateMipmaps.Value = false;
        _reportShot.URL.Value = url;
        _reportShotAddress = address;
        _reportShotShown = false;
    }

    private void DropShot()
    {
        var shot = _reportShot;
        var address = _reportShotAddress;
        _reportShot = null;
        _reportShotAddress = null;
        _reportShotShown = false;
        if (shot != null && !shot.IsDestroyed)
            shot.Slot.Destroy();
        ForgetShot(address);
    }

    private static void ForgetShot(string? address)
    {
        var db = Engine.Current?.LocalDB;
        if (db != null && !string.IsNullOrEmpty(address))
            _ = db.DeleteAssetAsync(address!);
    }

    // SENDING

    private async void SendReport()
    {
        if (_reportStage != ReportStage.Form || _pane != PaneView.Report)
            return;
        FlushTyping();
        string target = _reportFor;
        string reason = _reportReason.Trim();
        string problem = !SignedIn ? ReportLocale.SignedOut.Resolve()
            : !Reports.CanReport(target, SelfId) ? ReportLocale.Self.Resolve()
            : _reportCategory < 0 ? ReportLocale.NeedCategory.Resolve()
            : reason.Length == 0 ? ReportLocale.NeedReason.Resolve()
            : PictureBusy ? ReportLocale.WaitPicture.Resolve()
            : string.Empty;
        if (problem.Length > 0)
        {
            SetReportError(problem);
            return;
        }
        if (reason.Length > ReportLimits.ReasonMax)
            reason = reason[..ReportLimits.ReasonMax];

        var image = _reportImage;
        var report = new ReportSubmission
        {
            TargetUserId = target,
            Category = ReportCategories.All[_reportCategory],
            Reason = reason,
            WorldName = _reportWorld,
            SessionId = _reportSession,
            Image = image?.Bytes,
            ImageType = image?.ContentType,
            ImageName = image?.FileName,
        };
        _reportStage = ReportStage.Sending;
        _reportProgress.Report(0f);
        _reportShownProgress = -1f;
        if (_reportError.Length > 0)
            SetReportError(string.Empty);
        RepaintReport();

        var world = World;
        ApiResponse<ReportReceipt> answer;
        try
        {
            answer = await Reports.SendAsync(report, _reportProgress);
        }
        catch (Exception ex)
        {
            answer = ApiResponse<ReportReceipt>.FromException(ex);
        }
        OnUi(world, () =>
        {
            if (IsDestroyed || _reportStage != ReportStage.Sending || !string.Equals(target, _reportFor, StringComparison.Ordinal))
                return;
            if (answer.Success)
            {
                Logging.Logger.Log($"Reports: report {answer.Data?.ReportId} sent");
                DropReportPicture();
                _reportStage = ReportStage.Sent;
                _reportError = string.Empty;
                _paneDirty = true;
                _headerDirty = true;
                _body?.ResetToTop();
                return;
            }
            Logging.Logger.Warn($"Reports: the report was refused ({(int)answer.Status}): {answer.Message}");
            _reportStage = ReportStage.Form;
            SetReportError(ReportLocale.Refusal(answer).Resolve());
            RepaintReport();
        });
    }
}
