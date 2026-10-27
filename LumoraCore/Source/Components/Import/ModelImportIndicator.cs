// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Runtime.CompilerServices;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Components.Import;
using Lumora.Core.Components.UI;
using Lumora.Core.Localization;
using Lumora.Core.Math;
using Helio.UI;
using LumoraMeshes = Lumora.Core.Components.Meshes;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

// The readout that stands in front of an import or a spawn while it happens.
//
// This is the IN-WORLD half, and it starts after the import dialog is done: the dialog asks what you
// want, this says how it is going. It is laid out like the world loading card, on the dash's colours:
// what is happening over the name of the thing, the percent beside it, the stage under that and a thin
// bar. It goes green and holds a moment when the thing is in, red with the reason when it is not, so a
// spawn is something you see happen rather than a line on the dash. It stands in front of the thing
// below the importer's eye line, never over it, so the thing itself stays in view as it arrives.
//
// It stands in the session like the dialog, so the people there see an import under way and how it went.
// Only the importer's machine makes it and drives it, minted as the importer's own: what goes on the wire
// is this slot and a few fields (title, status, the fraction, failed), written at most a few times a
// second. Every machine draws its own plate from them on a local slot, as the nameplates are drawn,
// turned toward its own viewer and easing toward the latest fraction, so nothing per frame is sent.
// Nothing on it answers a pointer.
//
// Threading: the static calls are safe from any thread because none of them touch the data model -
// they write volatile fields on the import's ticket. Only Show marshals, to raise the plate. OnUpdate
// reads the ticket back on the world thread and writes the synced fields only when they changed. -xlinka
[ComponentCategory("Assets/Import")]
public class ModelImportIndicator : Component
{
    // The slot the model imports into. Floating over it means the readout is wherever the model is
    // about to appear, which is in front of the user by construction. -xlinka
    public readonly SyncRef<Slot> Anchor;
    // Who is importing: the readout hangs in front of them while it has nothing to float over, and goes
    // when they leave.
    public readonly SyncRef<User> Importer;
    public readonly Sync<string> Title;
    public readonly Sync<string> Status;
    public readonly Sync<float> Progress;
    public readonly Sync<bool> Failed;
    // What is coming in, by name.
    public readonly Sync<string> Subject;
    public readonly Sync<bool> Done;

    private const float PanelWidth = 0.52f;
    private const float PanelHeight = 0.19f;
    private const float PanelCornerRadius = 0.022f;
    private const float RimWidth = 0.002f;
    private const float PanelDepth = -0.002f;
    private const float Inset = 0.032f;
    private const float BarWidth = PanelWidth - 2f * Inset;
    private const float BarHeight = 0.008f;
    private const float BarHalfWidth = BarWidth * 0.5f;
    private const int MaxSubjectChars = 30;

    // In front of the thing, toward the importer, by this much (never more than most of the way to them),
    // and this far under their eyes.
    private const float StandOff = 0.35f;
    private const float BelowEyes = 0.2f;
    private const float LingerOnDone = 2.5f;
    private const float LingerOnFail = 4f;
    // How often the importer's machine sends what the ticket says. Finishing and failing go at once.
    private const float SyncInterval = 0.25f;

    private static readonly color PanelColor = DashTheme.Panel.WithAlpha(0.94f);
    private static readonly color RimColor = DashTheme.Accent.WithAlpha(0.5f);

    // The import this plate reports on, on the importer's machine only; null everywhere else. Every
    // cross-thread write lands on the ticket, never on the component, so the readout is allowed to be one
    // frame behind and there is no lock here on purpose.
    private ImportTicket? _ticket;
    private float _sinceSync = SyncInterval;

    private float _lingerTimer;
    private System.Collections.Generic.List<MeshProvider>? _followMeshes;
    private float _followFor;
    private float _followPollIn;
    private float _followStill;
    private int _followSettled = -1;
    private float _shownProgress;
    private float _appliedProgress = -1f;
    private string _appliedStatus = null!;
    private string _appliedTitle = null!;
    private string _appliedSubject = null!;
    private int _appliedEnd = -1;
    private bool _placed;

    // This machine's own plate, on a local slot.
    private Slot? _plate;
    private TextRenderer? _titleText;
    private TextRenderer? _subjectText;
    private TextRenderer? _percentText;
    private TextRenderer? _statusText;
    private Slot? _progressFill;
    private LumoraMeshes.RoundedQuadMesh? _panelMesh;
    private LumoraMeshes.RoundedQuadRingMesh? _rimMesh;
    private LumoraMeshes.RoundedQuadMesh? _fillMesh;

    // ONE PLATE PER WORLD, ONE TICKET PER IMPORT, AND THE TICKET IS THE TOKEN.
    //
    // A multi-file drop starts one import per file and the last Show takes the plate, so a call has to
    // say which import it speaks for or the first one to finish puts "Done" on somebody else's
    // still-running readout, which is the same lie as hiding early.
    //
    // The state lives on the ticket rather than on the plate, and that is what covers the window between
    // Show returning a token and the plate actually existing on the world thread. A Report landing in
    // there updates its own import instead of painting the plate still up from the last one, and a Hide
    // or a Fail landing in there retires the import before it has a plate, so the plate is born finished
    // or never born at all - rather than appearing afterwards with nothing left alive to hide it. -xlinka
    private sealed class ImportTicket
    {
        public volatile string Status = string.Empty;
        public volatile float Progress;
        public volatile bool Done;
        public volatile bool Failed;
        public volatile bool Dismissed;
        // A later Show took the plate. Ending is not this: an import that ends before its plate is up is
        // still shown, finished.
        public volatile bool Superseded;
        // Where the thing is, once it is somewhere: a spawn has nothing to stand by while it downloads.
        public volatile Slot? Anchor;
        // A thing already in the world whose meshes the plate counts in itself, and what it says then.
        public volatile Slot? Follow;
        public volatile string FollowDone = string.Empty;
        // The heading once it is in ("Spawned" rather than a green "Spawning"); empty keeps the one it had.
        public volatile string DoneTitle = string.Empty;
    }

    private static readonly ConditionalWeakTable<World, ModelImportIndicator> Current = new();
    private static volatile ImportTicket? _active;

    public ModelImportIndicator()
    {
        Anchor = new SyncRef<Slot>(this);
        Importer = new SyncRef<User>(this);
        Title = new Sync<string>(this, string.Empty);
        Status = new Sync<string>(this, string.Empty);
        Progress = new Sync<float>(this, 0f);
        Failed = new Sync<bool>(this, false);
        Subject = new Sync<string>(this, string.Empty);
        Done = new Sync<bool>(this, false);
    }

    public override void OnStart()
    {
        base.OnStart();
        Compose();
        UpdatePosition();
    }

    // Every machine's own plate. Local, so the theme it borrows (a per-world local singleton: one font,
    // one vertex-coloured unlit material) is in reach, and it costs no font provider or second material.
    private void Compose()
    {
        if (Slot == null || World == null || _plate != null)
            return;

        var theme = NameplateTheme.For(World);

        var plate = Slot.AddLocalSlot("Plate");
        plate.Persistent.Value = false;
        plate.AttachComponent<FaceLocalUser>();
        _plate = plate;

        var panel = plate.AddSlot("Panel");
        panel.LocalPosition.Value = new float3(0f, 0f, PanelDepth);
        _panelMesh = panel.AttachComponent<LumoraMeshes.RoundedQuadMesh>();
        _panelMesh.Size.Value = new float2(PanelWidth, PanelHeight);
        _panelMesh.CornerRadius.Value = PanelCornerRadius;
        _panelMesh.Color = PanelColor;
        AttachRenderer(panel, _panelMesh, theme);

        var rim = plate.AddSlot("Rim");
        rim.LocalPosition.Value = new float3(0f, 0f, PanelDepth + 0.0005f);
        _rimMesh = rim.AttachComponent<LumoraMeshes.RoundedQuadRingMesh>();
        _rimMesh.Size.Value = new float2(PanelWidth, PanelHeight);
        _rimMesh.CornerRadius.Value = PanelCornerRadius;
        _rimMesh.RimWidth.Value = RimWidth;
        _rimMesh.Color = RimColor;
        AttachRenderer(rim, _rimMesh, theme);

        float left = -BarHalfWidth;
        _titleText = AddText(plate, "Title", left, 0.062f, 0.018f, DashTheme.AccentHover, TextHorizontalAlignment.Left, theme);
        _subjectText = AddText(plate, "Subject", left, 0.026f, 0.034f, DashTheme.Text, TextHorizontalAlignment.Left, theme);
        _percentText = AddText(plate, "Percent", -left, 0.040f, 0.044f, DashTheme.Text, TextHorizontalAlignment.Right, theme);
        _statusText = AddText(plate, "Status", left, -0.022f, 0.020f, DashTheme.TextDim, TextHorizontalAlignment.Left, theme);

        var track = plate.AddSlot("BarTrack");
        track.LocalPosition.Value = new float3(0f, -0.062f, 0f);
        var trackMesh = track.AttachComponent<LumoraMeshes.RoundedQuadMesh>();
        trackMesh.Size.Value = new float2(BarWidth, BarHeight);
        trackMesh.CornerRadius.Value = BarHeight * 0.5f;
        trackMesh.Color = DashTheme.SurfaceHover;
        AttachRenderer(track, trackMesh, theme);

        // Left-anchored fill: a centred quad shifted left by half its missing width and x-scaled by the
        // fraction, so it grows from the left edge without re-meshing anything per update.
        var fill = track.AddSlot("BarFill");
        fill.LocalPosition.Value = new float3(-BarHalfWidth, 0f, 0.001f);
        fill.LocalScale.Value = new float3(0.0001f, 1f, 1f);
        _fillMesh = fill.AttachComponent<LumoraMeshes.RoundedQuadMesh>();
        _fillMesh.Size.Value = new float2(BarWidth, BarHeight);
        _fillMesh.CornerRadius.Value = BarHeight * 0.5f;
        _fillMesh.Color = DashTheme.Accent;
        AttachRenderer(fill, _fillMesh, theme);
        _progressFill = fill;
    }

    private static TextRenderer AddText(Slot plate, string name, float x, float y, float size, color ink,
        TextHorizontalAlignment align, NameplateTheme? theme)
    {
        var slot = plate.AddSlot(name);
        slot.LocalPosition.Value = new float3(x, y, 0f);
        var text = slot.AttachComponent<TextRenderer>();
        if (theme?.Font != null)
            text.Font.Target = theme.Font;
        text.Size.Value = size;
        text.Color.Value = ink;
        text.HorizontalAlign.Value = align;
        text.VerticalAlign.Value = TextVerticalAlignment.Middle;
        text.Text.Value = string.Empty;
        return text;
    }

    private static void AttachRenderer(Slot slot, LumoraMeshes.ProceduralMesh mesh, NameplateTheme? theme)
    {
        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        if (theme?.PlateMaterial != null)
            renderer.Material.Target = theme.PlateMaterial;
        // A floating readout has no business darkening the floor under it.
        renderer.ShadowCastMode.Value = ShadowCastMode.Off;
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        if (_ticket != null && !DriveFromTicket(_ticket, delta))
            return;

        UpdatePosition(delta);
        ApplyState(delta);
    }

    // The importer's machine: what the ticket says goes out a few times a second, and the end at once.
    // False once the readout has been taken down.
    private bool DriveFromTicket(ImportTicket ticket, float delta)
    {
        if (ticket.Dismissed)
        {
            Slot?.Destroy();
            return false;
        }

        // A replicated readout can only stand by something every machine has.
        var wanted = ticket.Anchor;
        if (wanted != null && !wanted.IsDestroyed && wanted.World == World && !ReferenceEquals(Anchor.Target, wanted)
            && (Slot.IsLocalElement || !wanted.IsLocalElement))
            Anchor.Target = wanted;

        if (ticket.Follow is { } follow && !ticket.Done && !ticket.Failed)
            FollowMeshes(ticket, follow, delta);

        bool ended = ticket.Done || ticket.Failed;
        _sinceSync += delta;
        if (ended || _sinceSync >= SyncInterval)
        {
            _sinceSync = 0f;
            if (!ticket.Failed)
                Progress.Value = System.Math.Clamp(ticket.Progress, 0f, 1f);
            Status.Value = ticket.Status ?? string.Empty;
            Failed.Value = ticket.Failed;
            Done.Value = ticket.Done && !ticket.Failed;
            if (Done.Value && ticket.DoneTitle.Length > 0)
                Title.Value = ticket.DoneTitle;
        }

        if (ended)
        {
            _lingerTimer += delta;
            if (_lingerTimer >= (ticket.Failed ? LingerOnFail : LingerOnDone))
            {
                Slot?.Destroy();
                return false;
            }
        }
        return true;
    }

    // A mesh is asked for only once a renderer takes it, a frame or more after it is attached, so for this
    // long a mesh nothing holds still counts as on its way. Past it nobody is going to ask.
    private const float FollowBindGrace = 3f;
    // No mesh settled either way for this long: the rest are stuck, not slow. The files are on this machine.
    private const float FollowStall = 90f;
    private const float FollowPoll = 0.2f;

    // The importer's machine: the meshes of the thing just put in the world, counted in five times a
    // second, then green, or red with how many never came.
    private void FollowMeshes(ImportTicket ticket, Slot root, float delta)
    {
        if (root.IsDestroyed)
        {
            ticket.Dismissed = true;
            Retire(ticket);
            return;
        }
        _followFor += delta;
        _followPollIn -= delta;
        if (_followPollIn > 0f)
            return;
        _followPollIn = FollowPoll;

        if (_followMeshes == null)
        {
            _followMeshes = new System.Collections.Generic.List<MeshProvider>();
            root.GetComponentsInChildren(_followMeshes);
        }

        int waiting = 0, loaded = 0, failed = 0;
        foreach (var mesh in _followMeshes)
        {
            if (mesh == null || mesh.IsDestroyed)
                continue;
            if (mesh.AssetReferenceCount == 0 && !mesh.IsAssetAvailable)
            {
                if (_followFor <= FollowBindGrace)
                    waiting++;
                continue;
            }
            waiting++;
            if (mesh.IsAssetAvailable)
                loaded++;
            else if (!AssetReadiness.IsPending(mesh))
                failed++;
        }

        int settled = loaded + failed;
        if (settled != _followSettled)
        {
            _followSettled = settled;
            _followStill = 0f;
        }
        else
        {
            _followStill += FollowPoll;
        }

        if (waiting > 0 && settled < waiting && _followStill < FollowStall)
        {
            ticket.Progress = 0.5f + 0.5f * settled / waiting;
            ticket.Status = new ImportProgress(ImportStage.Meshes, ticket.Progress, settled, waiting).Describe().Resolve();
            return;
        }

        int missing = waiting - loaded;
        if (missing > 0)
        {
            ticket.Status = "Import.MeshesMissing".AsLocale("{0} of {1} meshes did not load", missing, waiting).Resolve();
            ticket.Failed = true;
        }
        else
        {
            ticket.Progress = 1f;
            ticket.Status = ticket.FollowDone;
            ticket.Done = true;
        }
        Retire(ticket);
    }

    private void ApplyState(float delta)
    {
        // 0 under way, 1 in, 2 failed.
        int end = Failed.Value ? 2 : Done.Value ? 1 : 0;
        if (end != _appliedEnd)
        {
            _appliedEnd = end;
            color tone = end == 2 ? DashTheme.Negative : end == 1 ? DashTheme.Positive : DashTheme.Accent;
            if (_rimMesh != null && !_rimMesh.IsDestroyed)
                _rimMesh.Color = end == 0 ? RimColor : tone.WithAlpha(0.8f);
            if (_fillMesh != null && !_fillMesh.IsDestroyed)
                _fillMesh.Color = tone;
            if (_titleText != null && !_titleText.IsDestroyed)
                _titleText.Color.Value = end == 0 ? DashTheme.AccentHover : tone;
            if (_statusText != null && !_statusText.IsDestroyed)
                _statusText.Color.Value = end == 0 ? DashTheme.TextDim : tone;
            if (end == 2 && _percentText != null && !_percentText.IsDestroyed)
                _percentText.Text.Value = string.Empty;
        }

        string title = Title.Value ?? string.Empty;
        if (title != _appliedTitle && _titleText != null && !_titleText.IsDestroyed)
        {
            _appliedTitle = title;
            _titleText.Text.Value = title;
        }

        string subject = Subject.Value ?? string.Empty;
        if (subject != _appliedSubject && _subjectText != null && !_subjectText.IsDestroyed)
        {
            _appliedSubject = subject;
            _subjectText.Text.Value = subject.Length > MaxSubjectChars ? subject.Substring(0, MaxSubjectChars - 1) + "…" : subject;
        }

        string status = Status.Value ?? string.Empty;
        if (status != _appliedStatus && _statusText != null && !_statusText.IsDestroyed)
        {
            _appliedStatus = status;
            _statusText.Text.Value = status;
        }

        // The fraction arrives a few times a second; the bar eases toward it here, on each machine.
        float target = System.Math.Clamp(Progress.Value, 0f, 1f);
        _shownProgress = delta > 0f && _appliedProgress >= 0f
            ? _shownProgress + (target - _shownProgress) * System.Math.Min(1f, delta * 8f)
            : target;
        if (System.Math.Abs(target - _shownProgress) < 0.002f)
            _shownProgress = target;

        if (System.Math.Abs(_shownProgress - _appliedProgress) > 0.001f)
        {
            _appliedProgress = _shownProgress;
            var fill = _progressFill;
            if (fill != null && !fill.IsDestroyed)
            {
                float scale = System.Math.Max(0.0001f, _shownProgress);
                fill.LocalScale.Value = new float3(scale, 1f, 1f);
                fill.LocalPosition.Value = new float3(BarHalfWidth * (_shownProgress - 1f), 0f, 0.001f);
            }
            if (_appliedEnd != 2 && _percentText != null && !_percentText.IsDestroyed)
                _percentText.Text.Value = $"{(int)MathF.Round(_shownProgress * 100f)}%";
        }
    }

    // Every machine places its own plate: in front of the thing, toward the importer and under their eye
    // line, or in front of the importer while there is nothing to stand by yet.
    private void UpdatePosition(float delta = 0f)
    {
        var anchor = Anchor.Target;
        var head = Importer.Target?.Root?.HeadSlot;
        bool hasHead = head != null && !head.IsDestroyed;

        float3 target;
        if (anchor != null && !anchor.IsDestroyed)
        {
            var at = anchor.GlobalPosition;
            if (hasHead)
            {
                var eye = head!.GlobalPosition;
                var toward = new float3(eye.x - at.x, 0f, eye.z - at.z);
                float reach = toward.Length;
                var step = reach > 0.01f ? toward * (System.Math.Min(StandOff, reach * 0.6f) / reach) : float3.Zero;
                target = new float3(at.x + step.x, eye.y - BelowEyes, at.z + step.z);
            }
            else
            {
                target = at + float3.Up * 0.3f;
            }
        }
        else if (hasHead)
        {
            // The way a head faces is its Backward, as everything else that places things in front of you has it.
            var forward = head!.GlobalRotation * float3.Backward;
            var flat = new float3(forward.x, 0f, forward.z);
            flat = flat.LengthSquared > 1e-4f ? flat.Normalized : float3.Forward;
            target = head.GlobalPosition + flat * 0.8f - float3.Up * BelowEyes;
        }
        else
        {
            // Nothing to hang off yet. Retry next frame rather than snapping to the world origin, which
            // is what "just place it somewhere" looks like from inside the world. -xlinka
            return;
        }

        var plate = _plate;
        if (plate == null || plate.IsDestroyed)
            return;

        // Facing is FaceLocalUser's job on the plate; this only carries position, so the readout does not
        // fight the billboard.
        if (delta > 0f && _placed)
            plate.GlobalPosition = float3.Lerp(plate.GlobalPosition, target, System.Math.Min(1f, delta * 6f));
        else
            plate.GlobalPosition = target;
        _placed = true;
    }

    // Out in the world where the importer may bring things in, minted as theirs; a world that will not take
    // what they bring gets a readout on their machine alone, said so in the log.
    private static Slot MakeSlot(World world, out bool local)
    {
        var refusal = ImportDialog.SpawnRefusal(world);
        local = refusal != null;
        if (local)
            LumoraLogger.Warn($"ModelImportIndicator: {refusal} The import readout is shown only on this machine.");
        var slot = local ? world.RootSlot.AddLocalSlot("Import Indicator") : world.AddSpawnSlot(world.RootSlot, "Import Indicator");
        slot.Persistent.Value = false;
        return slot;
    }

    // STATIC API - safe from any thread

    // The returned token identifies THIS import. Pass it back to Report/Hide/Fail/Dismiss so a call
    // that arrives after another import has taken the plate is dropped rather than applied to it.
    public static object Show(World world, Slot? anchor, LocaleText title, string? subject = null)
    {
        var ticket = new ImportTicket();
        if (world == null)
            return ticket;

        // Published before the hop, so a Report or a Fail from the importer's threads reaches THIS import
        // from the moment its caller holds the token. It reaches the ticket, not whatever plate happens to
        // be up, which is why publishing early is safe here.
        if (_active is { } prior && !ReferenceEquals(prior, ticket))
            prior.Superseded = true;
        _active = ticket;
        world.RunSynchronously(() =>
        {
            try
            {
                // Superseded by a later Show, or already cancelled: no plate to raise. The plate that is
                // up stays up, because whoever superseded us clears it when they raise theirs.
                if (ticket.Superseded || ticket.Dismissed)
                    return;

                if (Current.TryGetValue(world, out var previous) && !previous.IsDestroyed)
                    previous.Slot?.Destroy();
                Current.Remove(world);

                var slot = MakeSlot(world, out bool local);
                using (local ? null : world.EnterSpawnAllocation())
                {
                    var indicator = slot.AttachComponent<ModelImportIndicator>();
                    indicator._ticket = ticket;
                    indicator.Importer.Target = world.LocalUser;
                    indicator.Title.Value = title.IsEmpty ? string.Empty : title.Resolve();
                    indicator.Subject.Value = subject ?? string.Empty;
                    // A replicated readout can only point at something every machine has.
                    if (anchor != null && !anchor.IsDestroyed && anchor.World == world && (local || !anchor.IsLocalElement))
                    {
                        indicator.Anchor.Target = anchor;
                        slot.GlobalPosition = anchor.GlobalPosition;
                    }
                    Current.AddOrUpdate(world, indicator);
                }
            }
            catch (Exception ex)
            {
                LumoraLogger.Error($"ModelImportIndicator.Show: {ex.Message}");
            }
        });
        return ticket;
    }

    // A token from another Show still names its own import, so it is answered rather than dropped: its
    // plate is gone but its ticket is not, and writing there cannot touch the live one. No token at all
    // means "whichever import is current", which is what the template callers pass.
    private static ImportTicket? Resolve(object? token)
        => token as ImportTicket ?? (token == null ? _active : null);

    // Retire the ticket so a later call with no token cannot land on a finished import.
    private static void Retire(ImportTicket ticket)
    {
        if (ReferenceEquals(_active, ticket))
            _active = null;
    }

    public static void Report(in ImportProgress progress, object? token = null)
    {
        var ticket = Resolve(token);
        if (ticket == null)
            return;

        if (progress.Fraction >= 0f)
            ticket.Progress = progress.Fraction;

        var text = progress.Describe();
        ticket.Status = text.IsEmpty ? string.Empty : text.Resolve();
    }

    // The thing it reports on exists now: the plate goes and stands by it.
    public static void SetAnchor(object? token, Slot anchor)
    {
        var ticket = Resolve(token);
        if (ticket != null && anchor != null)
            ticket.Anchor = anchor;
    }

    // The thing is in the world: the plate stands by it, counts its meshes in itself and finishes on its
    // own, saying done.
    public static void Follow(object? token, Slot root, LocaleText done, LocaleText doneTitle = default)
    {
        var ticket = Resolve(token);
        if (ticket == null || root == null)
            return;
        ticket.DoneTitle = doneTitle.IsEmpty ? string.Empty : doneTitle.Resolve();
        ticket.Anchor = root;
        ticket.Progress = System.Math.Max(ticket.Progress, 0.5f);
        ticket.Status = new ImportProgress(ImportStage.Meshes, 0.5f).Describe().Resolve();
        ticket.FollowDone = (done.IsEmpty ? new ImportProgress(ImportStage.Complete, 1f).Describe() : done).Resolve();
        ticket.Follow = root;
    }

    // Finished cleanly: the bar completes and turns green, and the plate holds a couple of seconds so the
    // eye gets to see it land instead of the readout just blinking out.
    public static void Hide(object? token = null, LocaleText done = default, LocaleText doneTitle = default)
    {
        var ticket = Resolve(token);
        if (ticket == null)
            return;

        ticket.DoneTitle = doneTitle.IsEmpty ? string.Empty : doneTitle.Resolve();
        ticket.Progress = 1f;
        ticket.Status = (done.IsEmpty ? new ImportProgress(ImportStage.Complete, 1f).Describe() : done).Resolve();
        ticket.Done = true;
        Retire(ticket);
    }

    // Failed: say WHY, in the world, for long enough to read, then go. A silent disappearance is
    // indistinguishable from the import having worked and the model being somewhere else. -xlinka
    public static void Fail(string reason, object? token = null)
    {
        var ticket = Resolve(token);
        if (ticket == null)
            return;

        var label = string.IsNullOrWhiteSpace(reason)
            ? "Import.Failed".AsLocale("Import failed")
            : "Import.FailedReason".AsLocale("Import failed: {0}", reason);
        Fail(label, ticket);
    }

    // Failed, in the caller's own words: a spawn that did not happen is not an import that failed.
    public static void Fail(LocaleText message, object? token)
    {
        var ticket = Resolve(token);
        if (ticket == null)
            return;
        ticket.Status = message.Resolve();
        ticket.Failed = true;
        Retire(ticket);
    }

    // Cancelled: whatever the readout was reporting on is gone (the target slot was deleted, or the
    // world was left), so there is nothing to call finished and nobody to read a failure. Take the
    // plate away rather than leaving it hanging in front of the user at "Meshes 3 of 9". -xlinka
    public static void Dismiss(object? token = null)
    {
        var ticket = Resolve(token);
        if (ticket == null)
            return;

        // The plate reads this on its own update and destroys itself; a plate that does not exist yet
        // reads it in Show and is never raised.
        ticket.Dismissed = true;
        Retire(ticket);
    }

    // The importer gone, the readout goes with them, the way their dialogs do.
    public override void OnUserLeft(User user) => ImportDialog.TakeDownForLeaver(this, Importer, user);

    public override void OnDestroy()
    {
        if (World != null && Current.TryGetValue(World, out var current) && ReferenceEquals(current, this))
            Current.Remove(World);
        if (_plate != null && !_plate.IsDestroyed)
            _plate.Destroy();
        base.OnDestroy();
    }
}
