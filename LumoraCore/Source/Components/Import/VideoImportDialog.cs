// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.IO;
using Helio.UI;
using Lumora.Core.Logging;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Import;

// Picks the shape of the video before anything is built: a screen, a full sphere, a half sphere, and
// whether the frame holds one eye or two. None of that can be read out of the file - a stereo 360 clip
// is just a wide picture until somebody says otherwise - so it is asked rather than guessed.
//
// The stereo buttons open a second page for the eye order instead of trying to guess it, because
// getting it backwards is not a subtle mistake to look at.
//
// "As Raw File" stays what it was: drop the file in as a grabbable and hand it off. -xlinka
[ComponentCategory("Assets/Import")]
public sealed class VideoImportDialog : ImportDialog
{
    public readonly Sync<VideoProjection> Projection;
    public readonly Sync<StereoLayout> Layout;

    protected override string TitleText => "Video Import";

    public VideoImportDialog()
    {
        Projection = new Sync<VideoProjection>(this, VideoProjection.Flat);
        Layout = new Sync<StereoLayout>(this, StereoLayout.None);
    }

    protected override void OpenRoot(UIBuilder ui)
    {
        var body = SetupSection(ui, "How should this video be imported?", backButton: false);
        SetupGrid(body);
        GridButton(body, "Regular", PresetRegular);
        GridButton(body, "360", Preset360);
        GridButton(body, "180", Preset180);
        GridButton(body, "Stereo", PresetStereoFlat);
        GridButton(body, "Stereo 360", PresetStereo360);
        GridButton(body, "Stereo 180", PresetStereo180);
        GridButton(body, "As Raw File", AsRawFile, BackColor);
    }

    public void PresetRegular()
    {
        Projection.Value = VideoProjection.Flat;
        Layout.Value = StereoLayout.None;
        RunImport();
    }

    public void Preset360()
    {
        Projection.Value = VideoProjection.Sphere360;
        Layout.Value = StereoLayout.None;
        RunImport();
    }

    public void Preset180()
    {
        Projection.Value = VideoProjection.Sphere180;
        Layout.Value = StereoLayout.None;
        RunImport();
    }

    public void PresetStereoFlat()
    {
        Projection.Value = VideoProjection.Flat;
        OpenPage(MenuStereoLayout);
    }

    public void PresetStereo360()
    {
        Projection.Value = VideoProjection.Sphere360;
        OpenPage(MenuStereoLayout);
    }

    public void PresetStereo180()
    {
        Projection.Value = VideoProjection.Sphere180;
        OpenPage(MenuStereoLayout);
    }

    private void MenuStereoLayout(UIBuilder ui)
    {
        var body = SetupSection(ui, "Which half is the left eye?");
        SetupGrid(body);
        GridButton(body, "Horizontal L|R", PickHorizontalLR);
        GridButton(body, "Horizontal R|L", PickHorizontalRL);
        GridButton(body, "Vertical L|R", PickVerticalLR);
        GridButton(body, "Vertical R|L", PickVerticalRL);
    }

    public void PickHorizontalLR() => PickLayout(StereoLayout.Horizontal_LR);
    public void PickHorizontalRL() => PickLayout(StereoLayout.Horizontal_RL);
    public void PickVerticalLR() => PickLayout(StereoLayout.Vertical_LR);
    public void PickVerticalRL() => PickLayout(StereoLayout.Vertical_RL);

    private void PickLayout(StereoLayout layout)
    {
        Layout.Value = layout;
        RunImport();
    }

    public void RunImport()
    {
        if (!CanInteract || RefuseSpawn()) return;

        var handler = ImportHandlers.Video;
        if (handler == null)
        {
            Logger.Warn($"VideoImportDialog: no video pipeline - {Paths.Count} source(s) not imported.");
            ShowUnsupported("Video playback isn't supported yet.\nUse \"As Raw File\" to drop the file into the world.");
            return;
        }

        Logger.Debug($"VideoImportDialog: importing {Paths.Count} source(s) as {Projection.Value}/{Layout.Value}");

        int rowSize = (int)MathF.Max(1f, MathF.Ceiling(MathF.Sqrt(Paths.Count)));
        int index = 0;
        var basePos = Slot.GlobalPosition;
        var baseRot = Slot.GlobalRotation;
        var target = ResolveTargetWorld();

        foreach (var source in Paths)
        {
            var s = target.AddSpawnSlot(target.RootSlot, NameFor(source));
            var offset = UniversalImporter.GridOffset(ref index, rowSize);
            s.GlobalPosition = basePos + baseRot * offset;
            s.GlobalRotation = baseRot;
            s.GlobalScale = float3.One;

            var options = new VideoImportOptions(IsRemote(source), Layout.Value, Projection.Value);
            var captured = source;
            // The user's favourite player when they have one, which the handler then points at the video.
            FavouriteSpawn.Fill<Media.VideoPlayer>(s, FavouriteKind.VideoPlayer, favourite =>
            {
                if (!s.IsDestroyed)
                    _ = handler.ImportAsync(s, captured, options);
            });
        }

        Slot.Destroy();
    }

    // A local:// address resolves to a file on this machine, so it is not a stream even though it
    // parses as a URI.
    private static bool IsRemote(string source)
        => Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && !uri.IsFile
            && !string.Equals(uri.Scheme, "local", StringComparison.OrdinalIgnoreCase);

    private static string NameFor(string source)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            var last = Path.GetFileName(uri.AbsolutePath);
            return string.IsNullOrEmpty(last) ? uri.Host : last;
        }
        var name = Path.GetFileName(source);
        return string.IsNullOrEmpty(name) ? source : name;
    }
}
