// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Helio.UI.Layout;
using Lumora.Core.Components.UI.Worlds;
using Lumora.Core.Math;
using Lumora.Nexus.Cloud.Cdn;

namespace Lumora.Core.Components.UI;

// DELETE REQUESTS: one panel over the inventory for one group, opened from the amber row under its root. Every
// request waiting there, newest first: what it is, where it sits, who asked and when, and Approve or Deny. Approve
// deletes it for good; Deny keeps it and clears the request. -xlinka
public sealed partial class InventoryScreen
{
    private const float RequestsWidth = 600f;
    private const float RequestsHeight = 470f;
    private const float RequestRow = 52f;
    private const int RequestsPerPage = 6;

    private Slot? _requestsRoot;
    private Slot? _requestsBody;
    private bool _requestsOpen;
    private string _requestsGroup = string.Empty;
    private string _requestsGroupName = string.Empty;
    private List<InventoryDeleteRequest>? _requests;
    private string? _requestsProblem;
    private bool _requestsBusy;
    private int _requestsPage;
    private int _requestsSerial;

    private void BuildRequestsPanel(Slot root)
    {
        _requestsRoot = root.AddSlot("Requests");
        BrowserParts.Fill(_requestsRoot.AttachComponent<RectTransform>());
        _requestsRoot.AttachComponent<IgnoreLayout>();
        _requestsRoot.ActiveSelf.Value = false;

        var backdrop = _requestsRoot.AddSlot("Backdrop");
        BrowserParts.Fill(backdrop.AttachComponent<RectTransform>());
        backdrop.AttachComponent<GraphicChunkRoot>().OverlayLevel = 1;
        backdrop.AttachComponent<Button>().Clicked += (_, _) => CloseRequests();
        backdrop.AttachComponent<Image>().Tint.Value = DashTheme.ModalScrim;

        var panel = _requestsRoot.AddSlot("Panel");
        var panelRect = panel.AttachComponent<RectTransform>();
        panelRect.AnchorMin.Value = new float2(0.5f, 0.5f);
        panelRect.AnchorMax.Value = new float2(0.5f, 0.5f);
        panelRect.OffsetMin.Value = new float2(-RequestsWidth * 0.5f, -RequestsHeight * 0.5f);
        panelRect.OffsetMax.Value = new float2(RequestsWidth * 0.5f, RequestsHeight * 0.5f);
        panel.AttachComponent<GraphicChunkRoot>().OverlayLevel = 2;
        BrowserParts.Panel(panel, DashTheme.Panel, DashTheme.RadiusCard, DashTheme.Warning.WithAlpha(0.45f), 1.5f);
        panel.AttachComponent<Button>();

        var band = panel.AddSlot("Band");
        BrowserParts.PinTop(band.AttachComponent<RectTransform>(), 0f, 4f, DashTheme.RadiusCard);
        BrowserParts.Panel(band, DashTheme.Warning.WithAlpha(0.85f), 2f);

        _requestsBody = BrowserParts.Child(panel, "Body");
    }

    // The requests waiting under a root of the side tree.
    private void OpenRequests(int rootIndex)
    {
        if (rootIndex < 0 || rootIndex >= _roots.Count || !_roots[rootIndex].IsGroup)
            return;
        OpenRequests(_roots[rootIndex].Place.OwnerId!, _roots[rootIndex].Place.Name);
    }

    private void OpenRequests(string groupId, string groupName)
    {
        if (_requestsRoot == null)
            return;
        if (_carried.HasValue)
            CancelCarry();
        CloseDialog();
        CloseShare();
        _requestsGroup = groupId;
        _requestsGroupName = groupName;
        _requestsOpen = true;
        _requests = null;
        _requestsProblem = null;
        _requestsPage = 0;
        _requestsRoot.ActiveSelf.Value = true;
        LoadRequests();
    }

    private void CloseRequests()
    {
        if (!_requestsOpen || _requestsRoot == null)
            return;
        _requestsOpen = false;
        _requestsSerial++;
        _requestsRoot.ActiveSelf.Value = false;
        MarkDirty();
        RefreshGroupStates();
        Reload();
    }

    private void LoadRequests()
    {
        _requestsBusy = true;
        RebuildRequests();
        int serial = ++_requestsSerial;
        string group = _requestsGroup;
        Await(System.Threading.Tasks.Task.Run(() => Inventory.DeleteRequestsAsync(group)), answer =>
        {
            if (serial != _requestsSerial || !_requestsOpen)
                return;
            _requestsBusy = false;
            _requests = answer.requests;
            _requestsProblem = answer.problem;
            _requestCounts[group] = answer.requests?.Count ?? 0;
            RebuildRequests();
        });
    }

    private void Decide(InventoryDeleteRequest request, bool approve)
    {
        if (_requestsBusy)
            return;
        _requestsBusy = true;
        _requestsProblem = null;
        RebuildRequests();
        string group = _requestsGroup;
        Await(System.Threading.Tasks.Task.Run(() => approve
            ? Inventory.ApproveDeleteAsync(group, request)
            : Inventory.DropDeleteRequestAsync(group, request.Id, request.Name, mine: false)), result =>
        {
            if (!_requestsOpen)
                return;
            SetStatus(result.Message, result.Ok ? DashTheme.TextDim : DashTheme.Warning);
            if (!result.Ok)
            {
                _requestsBusy = false;
                _requestsProblem = result.Message;
                RebuildRequests();
                return;
            }
            LoadRequests();
            Reload();
        });
    }

    private void RebuildRequests()
    {
        var body = _requestsBody;
        if (body == null || body.IsDestroyed)
            return;
        body.DestroyChildren();

        var title = PinnedTop(body, "Title", 18f, 28f);
        _parts.Label(title, "Delete requests", DashTheme.FontHeading, DashTheme.Text, BrowserParts.Weight.Bold);
        var sub = PinnedTop(body, "Sub", 46f, 18f);
        _parts.Label(sub, $"{_requestsGroupName}: its builders asked for these to go. Approve deletes for good; Deny keeps them.",
            DashTheme.FontSmall, DashTheme.TextDim, BrowserParts.Weight.Regular);

        float y = 78f;
        var requests = _requests ?? new List<InventoryDeleteRequest>();
        if (_requests == null)
        {
            var wait = PinnedTop(body, "Wait", y, 20f);
            _parts.Label(wait, _requestsProblem ?? "Asking the group…", DashTheme.FontSmall,
                _requestsProblem != null ? DashTheme.Warning : DashTheme.TextDim, BrowserParts.Weight.Regular);
        }
        else if (requests.Count == 0)
        {
            var none = PinnedTop(body, "None", y, 20f);
            _parts.Label(none, "Nothing is waiting. Everything asked for has been decided.", DashTheme.FontSmall, DashTheme.TextDim,
                BrowserParts.Weight.Regular);
        }
        int pages = System.Math.Max(1, (requests.Count + RequestsPerPage - 1) / RequestsPerPage);
        _requestsPage = System.Math.Clamp(_requestsPage, 0, pages - 1);
        int first = _requestsPage * RequestsPerPage;
        for (int i = first; i < System.Math.Min(requests.Count, first + RequestsPerPage); i++)
        {
            AddRequestRow(body, requests[i], y);
            y += RequestRow;
        }

        var foot = body.AddSlot("Foot");
        BrowserParts.PinBottom(foot.AttachComponent<RectTransform>(), 16f, DashTheme.ControlHeight, ShareInset);
        var done = _parts.AddButton(foot, "Done", 84f, DashTheme.ControlHeight, DashTheme.Accent, DashTheme.AccentHover, DashTheme.AccentPressed,
            DashTheme.OnAccent, BrowserParts.Weight.Semibold, CloseRequests);
        BrowserParts.PinRight(done.Root.GetComponent<RectTransform>()!, 0f, 84f, 0f);
        var line = foot.AddSlot("Line");
        var lineRect = line.AttachComponent<RectTransform>();
        BrowserParts.Fill(lineRect);
        lineRect.OffsetMax.Value = new float2(-100f, 0f);
        string state = _requestsProblem ?? (_requestsBusy && _requests != null ? "Working…"
            : requests.Count == 1 ? "1 request waiting" : $"{requests.Count} requests waiting");
        _parts.Label(line, pages > 1 ? $"{state} · page {_requestsPage + 1} of {pages}" : state, DashTheme.FontSmall,
            _requestsProblem != null ? DashTheme.Warning : DashTheme.TextDim, BrowserParts.Weight.Regular);
        if (pages > 1)
        {
            var next = _parts.AddButton(foot, "›", 36f, DashTheme.ControlHeight, DashTheme.Surface, DashTheme.SurfaceHover, DashTheme.SurfacePressed,
                DashTheme.Text, BrowserParts.Weight.Semibold, () => { _requestsPage = (_requestsPage + 1) % pages; RebuildRequests(); });
            BrowserParts.PinRight(next.Root.GetComponent<RectTransform>()!, 92f, 36f, 0f);
        }
    }

    // One request: the thing on the left (its kind, its name, where it sits), who asked and when under it, the
    // two decisions on the right.
    private void AddRequestRow(Slot body, InventoryDeleteRequest request, float y)
    {
        var row = PinnedTop(body, "Request", y, RequestRow - 8f);
        BrowserParts.Panel(row, DashTheme.Surface.WithAlpha(0.7f), DashTheme.RadiusControl, DashTheme.Outline, 1f);
        bool folder = request.TargetKind == "folder";

        var chip = _parts.AddChip(row, folder ? "Folder" : "Item", folder ? DashTheme.Accent.WithAlpha(0.8f) : DashTheme.IconBlue.WithAlpha(0.8f),
            DashTheme.OnAccent, 54f);
        var chipRect = chip.Root.GetComponent<RectTransform>()!;
        chipRect.AnchorMin.Value = new float2(0f, 0.5f);
        chipRect.AnchorMax.Value = new float2(0f, 0.5f);
        chipRect.OffsetMin.Value = new float2(10f, -DashTheme.ChipHeight * 0.5f);
        chipRect.OffsetMax.Value = new float2(64f, DashTheme.ChipHeight * 0.5f);

        var name = row.AddSlot("Name");
        var nameRect = name.AttachComponent<RectTransform>();
        nameRect.AnchorMin.Value = new float2(0f, 0.5f);
        nameRect.AnchorMax.Value = new float2(1f, 1f);
        nameRect.OffsetMin.Value = new float2(74f, 0f);
        nameRect.OffsetMax.Value = new float2(-190f, -3f);
        _parts.Label(name, BrowserParts.Truncate(request.Name, 30), DashTheme.FontBody, DashTheme.Text, BrowserParts.Weight.Semibold);

        var who = row.AddSlot("Who");
        var whoRect = who.AttachComponent<RectTransform>();
        whoRect.AnchorMin.Value = new float2(0f, 0f);
        whoRect.AnchorMax.Value = new float2(1f, 0.5f);
        whoRect.OffsetMin.Value = new float2(74f, 3f);
        whoRect.OffsetMax.Value = new float2(-190f, 0f);
        _parts.Label(who, $"asked by {request.RequestedByName} · {Ago(request.RequestedAt)}{(request.FolderId == null ? " · at the top" : string.Empty)}",
            DashTheme.FontSmall, DashTheme.TextDim, BrowserParts.Weight.Regular);

        var approve = _parts.AddButton(row, "Approve", 86f, 30f, GroupPaint.DangerFill, GroupPaint.DangerHover, GroupPaint.DangerHover,
            DashTheme.Negative, BrowserParts.Weight.Semibold, () => Decide(request, approve: true));
        var approveRect = approve.Root.GetComponent<RectTransform>()!;
        approveRect.AnchorMin.Value = new float2(1f, 0.5f);
        approveRect.AnchorMax.Value = new float2(1f, 0.5f);
        approveRect.OffsetMin.Value = new float2(-96f, -15f);
        approveRect.OffsetMax.Value = new float2(-10f, 15f);
        approve.SetEnabled(!_requestsBusy);
        var deny = _parts.AddButton(row, "Deny", 76f, 30f, DashTheme.Surface, DashTheme.SurfaceHover, DashTheme.SurfacePressed,
            DashTheme.Text, BrowserParts.Weight.Semibold, () => Decide(request, approve: false));
        var denyRect = deny.Root.GetComponent<RectTransform>()!;
        denyRect.AnchorMin.Value = new float2(1f, 0.5f);
        denyRect.AnchorMax.Value = new float2(1f, 0.5f);
        denyRect.OffsetMin.Value = new float2(-180f, -15f);
        denyRect.OffsetMax.Value = new float2(-104f, 15f);
        deny.SetEnabled(!_requestsBusy);
    }

    private static string Ago(DateTime at)
    {
        var span = DateTime.UtcNow - (at.Kind == DateTimeKind.Utc ? at : at.ToUniversalTime());
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} h ago";
        return at.ToLocalTime().ToString("d MMM HH:mm");
    }
}
