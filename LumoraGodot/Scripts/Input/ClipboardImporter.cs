// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Components;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Import;
using Lumora.Core.Localization;
using Lumora.Core.Math;
using Lumora.Source.Godot.UI;
using LumoraMeshes = Lumora.Core.Components.Meshes;

namespace Lumora.Godot.Input;

public partial class ClipboardImporter : Node
{
    private static readonly HashSet<string> ClipboardModelExtensions = new(ModelImporter.SupportedExtensions, StringComparer.OrdinalIgnoreCase);

    // The same live-media schemes AssetHelper knows, spelled as prefixes because a pasted string is
    // classified before anything tries to parse it as a URI.
    private static readonly string[] StreamingUrlPrefixes =
    {
        "rtsp://", "rtmp://", "rtp://", "mms://", "srt://", "udp://",
    };

    // Windows clipboard P/Invoke for file drops
    private const uint CF_HDROP = 15;

    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder lpszFile, uint cch);

    private LocalDB _localDB = null!;
    private Slot _targetSlot = null!;
    private Camera3D _camera = null!;
    private Lumora.Core.Engine _engine = null!;

    public event Action<string, Slot> OnAssetImported = null!;

    public override void _Ready()
    {
        Lumora.Core.Logging.Logger.Debug("ClipboardImporter: Ready");

        GetTree().Root.FilesDropped += OnFilesDropped;
    }

    public override void _ExitTree()
    {
        GetTree().Root.FilesDropped -= OnFilesDropped;
        base._ExitTree();
    }

    // Routes through UniversalImporter so the matching import dialog (Image/Model/
    // Video/Folder) appears at the user's view, rather than auto-importing silently.
    // - xlinka
    private async void OnFilesDropped(string[] files)
    {
        Lumora.Core.Logging.Logger.Debug($"ClipboardImporter: {files.Length} file(s) dropped");

        var world = _engine?.WorldManager?.FocusedWorld;
        if (world == null)
        {
            GD.PrintErr("ClipboardImporter: No focused world for drop");
            return;
        }

        var (pos, rot) = GetSpawnPose();
        foreach (var filePath in files)
        {
            var normalized = NormalizeFilePath(filePath);
            Lumora.Core.Logging.Logger.Debug($"ClipboardImporter: Processing dropped file: {normalized}");
            UniversalImporter.Import(normalized, world, pos, rot);
        }
        await Task.CompletedTask;
    }

    private (float3 position, floatQ rotation) GetSpawnPose()
    {
        if (_camera != null)
        {
            var camPos = _camera.GlobalPosition;
            var camFwd = -_camera.GlobalTransform.Basis.Z; // Godot camera looks down -Z
            // FLATTEN to horizontal so an up/down gaze can't tilt the model or shove it above/below eye-line (that's
            // the "spawns above me / lying back" bug), spawn ~1.5 m ahead + a touch low, and face the user with a
            // PURE YAW (+Z back toward the camera). floatQ.LookRotation is broken for facing (inverse-row builder ->
            // edge-on/sideways pose), so use AxisAngle like the working FaceLocalUser / ImportDialog.Executor. -xlinka
            var flat = new float3(camFwd.X, 0f, camFwd.Z);
            flat = flat.LengthSquared < 1e-6f ? new float3(0f, 0f, 1f) : flat.Normalized;
            var spawn = new float3(camPos.X + flat.x * 1.5f, camPos.Y - 0.5f, camPos.Z + flat.z * 1.5f);
            var rot = floatQ.AxisAngle(float3.Up, System.MathF.Atan2(-flat.x, -flat.z));
            return (spawn, rot);
        }
        return (float3.Zero, floatQ.Identity);
    }

    public void Initialize(LocalDB localDB, Slot targetSlot, Camera3D camera)
    {
        _localDB = localDB;
        _targetSlot = targetSlot;
        _camera = camera;
    }

    public void SetEngine(Lumora.Core.Engine engine)
    {
        _engine = engine;
        RegisterImportHandlers();
    }

    private void RegisterImportHandlers()
    {
        ImportHandlers.Image = new ImageHandler(this);
        ImportHandlers.Model = new ModelHandler(this);
        ImportHandlers.Video = new VideoHandler(this);
        ImportHandlers.Raw = new RawFileHandler(this);
        // Bridge the real OS clipboard into the core's world-side Ctrl+V trigger
        // (Lumora.Core.Components.ClipboardImporter). That core component owns the
        // keystroke now, so this node no longer self-triggers paste in _Input. - xlinka
        ImportHandlers.Clipboard = new ClipboardBridge(this);
        Lumora.Core.Logging.Logger.Debug("ClipboardImporter: Registered Image/Model/Video/Raw import + clipboard bridge");
    }

    private sealed class ClipboardBridge : IClipboardPasteHandler
    {
        private readonly ClipboardImporter _owner;
        public ClipboardBridge(ClipboardImporter owner) { _owner = owner; }
        public void Paste() => _owner.HandlePaste();
    }

    private sealed class ImageHandler : IImageImportHandler
    {
        private readonly ClipboardImporter _owner;
        public ImageHandler(ClipboardImporter owner) { _owner = owner; }
        public Task ImportAsync(Slot slot, string path) => _owner.PopulateImageSlotAsync(slot, path);
    }

    private sealed class ModelHandler : IModelImportHandler
    {
        private readonly ClipboardImporter _owner;
        public ModelHandler(ClipboardImporter owner) { _owner = owner; }
        public Task ImportAsync(Slot slot, string path, ModelImportRequest request)
        {
            if (request.Settings != null)
                return _owner.PopulateModelSlotAsync(slot, path, request.Settings.IsAvatarImport, request.Settings, exactSettings: true);
            // The dialog's answers drive the import, a .vrm's included: ToSettings keeps the avatar preset under
            // them, so a plain avatar import is what it always was. - xlinka
            var settings = request.ToSettings(path);
            return _owner.PopulateModelSlotAsync(slot, path, settings.IsAvatarImport, settings, exactSettings: true);
        }
    }

    private sealed class VideoHandler : IVideoImportHandler
    {
        private readonly ClipboardImporter _owner;
        public VideoHandler(ClipboardImporter owner) { _owner = owner; }
        public Task ImportAsync(Slot slot, string source, VideoImportOptions options)
            => _owner.PopulateVideoSlotAsync(slot, source, options);
    }

    private sealed class RawFileHandler : IRawFileImportHandler
    {
        private readonly ClipboardImporter _owner;
        public RawFileHandler(ClipboardImporter owner) { _owner = owner; }

        // A file nothing opens still lands as something to find and pick up.
        private static Task Label(Slot slot, string path)
        {
            if (slot != null && !slot.IsDestroyed)
                slot.World?.RunSynchronously(() =>
                {
                    if (!slot.IsDestroyed)
                        Lumora.Core.Components.Import.UniversalImporter.SpawnFileLabel(slot, Path.GetFileName(path) ?? path);
                });
            return Task.CompletedTask;
        }
        public Task ImportAsync(Slot slot, string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp" or ".tga" or ".tif" or ".tiff"
                    => _owner.PopulateImageSlotAsync(slot, path),
                ".gdshader"
                    => _owner.PopulateShaderSlotAsync(slot, path),
                _ when ModelImporter.IsSupportedFormat(path)
                    => _owner.PopulateModelSlotAsync(slot, path, isAvatar: ext == ".vrm"),
                _ => Label(slot, path),
            };
        }
    }

    private Slot GetTargetSlot()
    {
        if (_targetSlot != null)
            return _targetSlot;

        return _engine?.WorldManager?.FocusedWorld?.RootSlot!;
    }

    public void SetTargetSlot(Slot slot)
    {
        _targetSlot = slot;
    }

    // Ctrl+V is owned by the core userspace ClipboardImporter component, which
    // checks the focused world and calls HandlePaste through the registered
    // clipboard bridge. We deliberately don't self-trigger paste here to avoid a
    // double import. Drag & drop (OnFilesDropped) is still handled directly. - xlinka

    // files copied with Ctrl+C in Explorer land here
    private List<string> GetClipboardFiles()
    {
        var files = new List<string>();

        // Only works on Windows
        if (OS.GetName() != "Windows")
            return files;

        try
        {
            if (!IsClipboardFormatAvailable(CF_HDROP))
                return files;

            if (!OpenClipboard(IntPtr.Zero))
                return files;

            try
            {
                IntPtr hDrop = GetClipboardData(CF_HDROP);
                if (hDrop == IntPtr.Zero)
                    return files;

                uint fileCount = DragQueryFile(hDrop, 0xFFFFFFFF, null!, 0);

                for (uint i = 0; i < fileCount; i++)
                {
                    uint size = DragQueryFile(hDrop, i, null!, 0) + 1;
                    var sb = new StringBuilder((int)size);
                    DragQueryFile(hDrop, i, sb, size);
                    files.Add(sb.ToString());
                }
            }
            finally
            {
                CloseClipboard();
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"ClipboardImporter: Error getting clipboard files: {ex.Message}");
        }

        return files;
    }

    public async void HandlePaste()
    {
        try { await HandlePasteAsync(); }
        catch (Exception ex) { GD.PrintErr($"ClipboardImporter: Paste failed: {ex.Message}"); }
    }

    private async Task HandlePasteAsync()
    {
        Lumora.Core.Logging.Logger.Debug("ClipboardImporter: Handling paste...");

        var clipboardFiles = GetClipboardFiles();
        var clipboardText = DisplayServer.ClipboardGet();
        if (clipboardFiles.Count == 0)
            clipboardFiles = Lumora.Core.Input.ClipboardFileList.Parse(clipboardText, OperatingSystem.IsWindows());
        if (clipboardFiles.Count > 0)
        {
            Lumora.Core.Logging.Logger.Debug($"ClipboardImporter: Found {clipboardFiles.Count} file(s) in clipboard");
            foreach (var filePath in clipboardFiles)
            {
                Lumora.Core.Logging.Logger.Debug($"ClipboardImporter: Processing clipboard file: {filePath}");
                await HandleFilePath(filePath);
            }
            return;
        }

        if (DisplayServer.ClipboardHasImage())
        {
            Lumora.Core.Logging.Logger.Debug("ClipboardImporter: Found image in clipboard");
            await HandleClipboardImage();
            return;
        }

        if (string.IsNullOrEmpty(clipboardText))
        {
            Lumora.Core.Logging.Logger.Debug("ClipboardImporter: Clipboard is empty");
            return;
        }

        Lumora.Core.Logging.Logger.Debug($"ClipboardImporter: Clipboard text, {clipboardText.Length} chars");

        var contentType = DetectContentType(clipboardText);

        switch (contentType)
        {
            case ClipboardContentType.FilePath:
                await HandleFilePath(clipboardText.Trim());
                break;

            case ClipboardContentType.Url:
                await HandleUrl(clipboardText.Trim());
                break;

            case ClipboardContentType.Text:
                await HandleText(clipboardText);
                break;

            default:
                Lumora.Core.Logging.Logger.Debug("ClipboardImporter: Unknown content type");
                break;
        }
    }

    private enum ClipboardContentType
    {
        Unknown,
        FilePath,
        Url,
        Text
    }

    private ClipboardContentType DetectContentType(string content)
    {
        content = NormalizeFilePath(content);

        if (IsFilePath(content))
        {
            return ClipboardContentType.FilePath;
        }

        if (content.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            content.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return ClipboardContentType.Url;
        }

        // Live media protocols. There is no file behind one of these, so pasting one used to land in
        // the text branch and become a floating string instead of a stream. -xlinka
        foreach (var scheme in StreamingUrlPrefixes)
        {
            if (content.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                return ClipboardContentType.Url;
        }

        return ClipboardContentType.Text;
    }

    private bool IsFilePath(string content)
    {
        content = NormalizeFilePath(content);

        // Windows paths
        if (content.Length >= 3 && char.IsLetter(content[0]) && content[1] == ':' && (content[2] == '\\' || content[2] == '/'))
        {
            return File.Exists(content) || Directory.Exists(content);
        }

        // Unix paths
        if (content.StartsWith("/") || content.StartsWith("~"))
        {
            return File.Exists(content) || Directory.Exists(content);
        }

        foreach (var ext in ClipboardModelExtensions)
        {
            if (content.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return File.Exists(content);
            }
        }

        var otherExtensions = new[] { ".png", ".jpg", ".jpeg", ".webp", ".tif", ".tiff", ".gdshader" };
        foreach (var ext in otherExtensions)
        {
            if (content.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return File.Exists(content);
            }
        }

        return false;
    }

    private static string NormalizeFilePath(string content)
    {
        return content?.Trim().Trim('"') ?? string.Empty;
    }

    private Task HandleFilePath(string filePath)
    {
        filePath = NormalizeFilePath(filePath);
        Lumora.Core.Logging.Logger.Debug($"ClipboardImporter: Handling file path: {filePath}");

        if (!File.Exists(filePath) && !Directory.Exists(filePath))
        {
            GD.PrintErr($"ClipboardImporter: Path not found: {filePath}");
            return Task.CompletedTask;
        }

        var world = _engine?.WorldManager?.FocusedWorld;
        if (world == null)
        {
            GD.PrintErr("ClipboardImporter: No focused world for paste");
            return Task.CompletedTask;
        }

        var (pos, rot) = GetSpawnPose();
        UniversalImporter.Import(filePath, world, pos, rot);
        return Task.CompletedTask;
    }

    // Single chokepoint for any clipboard/url/text path that ends up as a file.
    // Routes through UniversalImporter so the matching import dialog appears
    // instead of doing a silent in-place attach. - xlinka
    private Task AutoImport(string filePath)
    {
        var world = _engine?.WorldManager?.FocusedWorld;
        if (world == null)
        {
            GD.PrintErr("ClipboardImporter: No focused world for auto import");
            return Task.CompletedTask;
        }

        var (pos, rot) = GetSpawnPose();
        UniversalImporter.Import(filePath, world, pos, rot);
        return Task.CompletedTask;
    }

    private Task HandleClipboardImage()
    {
        var image = DisplayServer.ClipboardGetImage();
        if (image == null || image.IsEmpty())
        {
            GD.PrintErr("ClipboardImporter: Failed to get image from clipboard");
            return Task.CompletedTask;
        }

        return ImportClipboardImage(image);
    }

    // The importer takes a file, so the picture is written out as a PNG first, and that is seconds of
    // encoding for a big one: a 4096 square held the main thread five seconds, inside the world update
    // that read Ctrl+V. It is written on a worker now and handed to the importer in the world's own
    // update once it is on disk. -xlinka
    public async Task ImportClipboardImage(Image image)
    {
        var world = _engine?.WorldManager?.FocusedWorld;
        if (world == null)
        {
            GD.PrintErr("ClipboardImporter: No focused world for clipboard image");
            return;
        }

        var tempDir = OS.GetUserDataDir() + "/tmp";
        // To the millisecond: two pastes inside one second are now two encodes at once, and one name for
        // both had them writing the same file.
        var tempPath = tempDir + "/clipboard_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png";
        var (pos, rot) = GetSpawnPose();

        Error error;
        try
        {
            error = await Task.Run(() =>
            {
                Directory.CreateDirectory(tempDir);
                return image.SavePng(tempPath);
            });
        }
        catch (Exception ex)
        {
            GD.PrintErr($"ClipboardImporter: Failed to save clipboard image: {ex.Message}");
            return;
        }
        finally
        {
            image.Dispose();
        }
        if (error != Error.Ok)
        {
            GD.PrintErr($"ClipboardImporter: Failed to save clipboard image: {error}");
            return;
        }

        Lumora.Core.Logging.Logger.Debug($"ClipboardImporter: Saved clipboard image to {tempPath}");
        if (world.IsDestroyed)
            return;
        world.RunSynchronously(() =>
        {
            if (!world.IsDestroyed)
                UniversalImporter.Import(tempPath, world, pos, rot);
        });
    }

    // Attach quad+texture+grabbable to an existing slot. The dialog flow creates
    // the slot ahead of time (at the user-chosen spawn pose); this method only
    // populates it. - xlinka
    public async Task PopulateImageSlotAsync(Slot imageSlot, string filePath)
    {
        Lumora.Core.Logging.Logger.Debug($"ClipboardImporter: Populating image slot: {filePath}");

        // Read before anything is attached: an animated picture gets a frame player beside its material.
        bool animated = await Task.Run(() => FrameSheet.IsAnimatedFile(filePath));

        string localUri = null!;
        if (_localDB != null)
        {
            localUri = await _localDB.ImportLocalAssetAsync(filePath, LocalDB.ImportLocation.Copy);
        }

        // TWO-SIDED VIA THE MATERIAL, NOT VIA A SECOND QUAD.
        //
        // QuadMesh.DualSided builds a SECOND quad rotated 180 degrees about up, which is exactly
        // coplanar with the first. Two overlapping surfaces at identical depth is the classic setup for
        // the picture flickering between two versions of itself as the viewer moves, and the second one
        // is UV-mirrored so the two states look like two different images. It also doubles the geometry
        // for something the material can do for free. -xlinka
        var quadMesh = imageSlot.AttachComponent<LumoraMeshes.QuadMesh>();
        quadMesh.Size.Value = new float2(1.0f, 1.0f);
        quadMesh.DualSided.Value = false;

        var meshRenderer = imageSlot.AttachComponent<MeshRenderer>();
        meshRenderer.Mesh.Target = quadMesh;

        var imageProvider = imageSlot.AttachComponent<ImageProvider>();
        var imageUri = new Uri(localUri ?? filePath);
        imageProvider.URL.Value = imageUri;

        var collider = imageSlot.AttachComponent<BoxCollider>();
        collider.Size.Value = new float3(1f, 1f, 0.02f);

        imageSlot.AttachComponent<Grabbable>();

        var sizeDriver = imageSlot.AttachComponent<TextureSizeDriver>();
        sizeDriver.Source.Target = imageProvider;
        sizeDriver.Target.Target = quadMesh;
        sizeDriver.ColliderTarget.Target = collider;

        var material = imageSlot.AttachComponent<UnlitMaterial>();
        material.Texture.Target = imageProvider;
        material.BlendMode.Value = BlendMode.Transparent;
        material.Culling.Value = Culling.None;
        meshRenderer.Material.Target = material;

        // A GIF or animated WebP plays on the quad, on the session clock for everyone in the world. A still gets
        // none of this. -xlinka
        if (animated)
        {
            var player = imageSlot.AttachComponent<Lumora.Core.Components.Utility.TextureFramePlayer>();
            player.Texture.Target = imageProvider;
            player.Scale.DriveTarget(material.TextureScale);
            player.Offset.DriveTarget(material.TextureOffset);
        }

        Lumora.Core.Logging.Logger.Debug($"ClipboardImporter: Image populated with visual components from {localUri ?? filePath}");
        OnAssetImported?.Invoke(filePath, imageSlot);

        QueueTextureVariants(localUri, imageProvider.IsNormalMap.Value);
        DropOwnTemp(filePath, localUri);
    }

    // A pasted picture, a downloaded file or pasted shader text sat in a temp file of ours only so it could be
    // imported. Once the store has it (sealed), the plain copy goes. A file the user dropped is never touched.
    private void DropOwnTemp(string filePath, string? localUri)
    {
        if (string.IsNullOrEmpty(localUri) || string.IsNullOrEmpty(filePath))
            return;
        try
        {
            string full = Path.GetFullPath(filePath);
            string ownTmp = Path.GetFullPath(OS.GetUserDataDir() + "/tmp") + Path.DirectorySeparatorChar;
            string? dbTmp = _localDB == null ? null : Path.GetDirectoryName(Path.GetFullPath(_localDB.GetTempFilePath())) + Path.DirectorySeparatorChar;
            if (full.StartsWith(ownTmp, StringComparison.OrdinalIgnoreCase)
                || (dbTmp != null && full.StartsWith(dbTmp, StringComparison.OrdinalIgnoreCase)))
                File.Delete(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
    }

    // A video is never downloaded first: the player streams a URL as it plays, and a local file is
    // copied into the local store the way an image is so that the ADDRESS travels and the other peers
    // can fetch it. A path handed straight to the provider would only ever play on this machine.
    // - xlinka
    public async Task PopulateVideoSlotAsync(Slot videoSlot, string source, VideoImportOptions options)
    {
        if (videoSlot == null || videoSlot.IsDestroyed)
        {
            GD.PrintErr($"ClipboardImporter: video import target for {source} is gone");
            return;
        }

        Lumora.Core.Logging.Logger.Debug($"ClipboardImporter: Populating video slot: {source} ({options.Projection}/{options.Layout})");

        Uri.TryCreate(source, UriKind.Absolute, out var parsed);
        bool isLocalFile = parsed == null || parsed.IsFile;

        string localUri = null!;
        if (isLocalFile && _localDB != null)
        {
            var path = parsed != null && parsed.IsFile ? parsed.LocalPath : source;
            if (File.Exists(path))
            {
                localUri = await _localDB.ImportLocalAssetAsync(path, LocalDB.ImportLocation.Copy);
            }
        }

        if (videoSlot.IsDestroyed)
            return;

        // A favourite player may already be in the slot; it gets the video instead of a new one. -xlinka
        var player = videoSlot.GetComponentInChildren<Lumora.Core.Components.Media.VideoPlayer>()
            ?? videoSlot.AttachComponent<Lumora.Core.Components.Media.VideoPlayer>();
        player.Projection.Value = options.Projection;
        player.StereoLayout.Value = options.Layout;

        if (!string.IsNullOrEmpty(localUri))
        {
            player.SetSource(new Uri(localUri), stream: false);
        }
        else if (parsed != null && !isLocalFile)
        {
            player.SetSource(parsed, options.Stream);
        }
        else
        {
            player.SetSource(source);
        }

        Lumora.Core.Logging.Logger.Debug($"ClipboardImporter: Video populated from {localUri ?? source}");
        OnAssetImported?.Invoke(source, videoSlot);
    }

    // Deliberately not awaited. Decoding a large image and filtering four mip chains off it is
    // hundreds of milliseconds of CPU that would otherwise be added to how long the user waits for
    // their picture to appear, for a benefit that is entirely about LATER loads. The material binds
    // the base asset immediately and keeps using it; the next load of this texture - this session
    // or a future one - is the first that can pick up a variant. -xlinka
    private static void QueueTextureVariants(string? localUri, bool isNormalMap)
    {
        var db = Lumora.Core.Engine.Current?.LocalDB;
        if (db == null || string.IsNullOrEmpty(localUri))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await TextureVariantStore.GenerateAsync(db, localUri!, isNormalMap).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Variants are an optimization; a failure here must never make the import look broken.
                GD.PrintErr($"ClipboardImporter: texture variant generation failed for {localUri}: {ex.Message}");
            }
        });
    }

    // An import is not over when the last component is attached. Every MeshProvider the importer leaves
    // behind still has a file to read and decode, and that tail is where the time and the memory go - so
    // this holds the readout and this task open across it, and rides the TARGET SLOT's lifetime while it
    // waits: delete the model, or leave the world, and the next world hop cancels instead of dropping
    // chunks onto a corpse. -xlinka
    public async Task PopulateModelSlotAsync(Slot slot, string filePath, bool isAvatar, ModelImportSettings? settings = null, bool exactSettings = false)
    {
        Lumora.Core.Logging.Logger.Debug($"ClipboardImporter: Populating model slot as {(isAvatar ? "avatar" : "3D model")}: {filePath}");

        var importWorld = slot?.World;
        if (slot == null || slot.IsDestroyed || importWorld == null)
        {
            GD.PrintErr($"ClipboardImporter: import target for {filePath} is gone before the import started");
            return;
        }

        // Opened here rather than left to the caller so every entry point (dialog, raw-file route, drop)
        // gets the same owner. It has to happen before the first await: the async builder rewinds the
        // execution context around the prologue, so the scope reaches this method's continuations only.
        WorldContext.Enter(importWorld, slot);

        // Show an IN-WORLD progress indicator (3D, in front of the user), not a flat screen
        // overlay. It's non-modal: the game stays interactive while it loads (freeze fix) and the user sees a
        // floating title + percent + progress bar in the world. Driven straight off the importer's progress. -xlinka
        var title = isAvatar
            ? "Import.Title.Avatar".AsLocale("Importing avatar")
            : "Import.Title.Model".AsLocale("Importing model");
        var token = ModelImportIndicator.Show(importWorld, slot, title, System.IO.Path.GetFileName(filePath));
        // The importer calls Complete when its last chunk lands, which is not a finish the meshes have
        // reached yet. Hold that back to the mesh band so the bar cannot read 100% while nine files are
        // still decoding - that readout is exactly what told the user "imported" before the machine died.
        var progress = new Progress<ImportProgress>(u => ModelImportIndicator.Report(
            u.Stage == ImportStage.Complete ? new ImportProgress(ImportStage.Meshes, MeshWaitStart) : u, token));
        bool finished = false;
        try
        {
            ModelImportResult result;
            if (isAvatar && !exactSettings)
            {
                // Avatar import uses fixed avatar settings (IK/rig); the dialog's material/texture
                // options aren't surfaced for avatars yet, so keep the avatar preset. - xlinka
                result = await ModelImporter.ImportAvatarAsync(filePath, slot, _localDB, progress);
            }
            else
            {
                result = await ModelImporter.ImportModelAsync(filePath, slot, settings, _localDB, progress);
            }
            if (!result.Success)
            {
                // The readout says why and stays up long enough to read it. Without this the plate
                // just vanished and a failed import looked identical to a finished one. -xlinka
                finished = true;
                ModelImportIndicator.Fail(result.ErrorMessage, token);
                GD.PrintErr($"ClipboardImporter: Model import failed: {result.ErrorMessage}");
                return;
            }

            // Assets alone have nothing drawing them, so nothing loads them: there is no tail to wait out.
            var (loaded, failedMeshes) = settings?.AssetsOnly == true ? (0, 0) : await AwaitMeshesAsync(result.RootSlot, token);
            finished = true;
            if (failedMeshes > 0)
            {
                var reason = $"{failedMeshes} of {loaded + failedMeshes} meshes failed to load";
                ModelImportIndicator.Fail(reason, token);
                GD.PrintErr($"ClipboardImporter: {reason} for {filePath}");
                return;
            }

            ModelImportIndicator.Hide(token, default, isAvatar
                ? "Import.Done.Avatar".AsLocale("Avatar imported")
                : "Import.Done.Model".AsLocale("Model imported"));
            OnAssetImported?.Invoke(filePath, result.RootSlot);
        }
        catch (OperationCanceledException)
        {
            // The target slot was destroyed or its world went away. Nothing to finish, nobody to tell.
            finished = true;
            ModelImportIndicator.Dismiss(token);
            Lumora.Core.Logging.Logger.Log($"ClipboardImporter: import of {filePath} cancelled - its target is gone");
        }
        finally
        {
            if (!finished)
                ModelImportIndicator.Fail(null!, token);
        }
    }

    // Where the readout sits once the importer is done attaching and the mesh files are still loading.
    private const float MeshWaitStart = 0.95f;

    // No mesh has resolved either way for this long: stop waiting and call the rest failed. The files are
    // local and already baked, so a minute and a half of no movement is a stuck load, not a slow one, and
    // hanging the readout there forever is the same silence this whole change is about. -xlinka
    private const long MeshStallMs = 90_000;

    // A provider only starts loading once a renderer's AssetRef resolves and takes a reference, which
    // lands a frame or more after the component is attached. Until this is up, an unreferenced provider
    // counts as outstanding - treating it as "nothing is loading it" on the first poll would declare the
    // whole import finished before a single mesh had been asked for. -xlinka
    private const long MeshBindGraceMs = 3_000;

    // Returns (loaded, failed). Polls one frame at a time instead of subscribing: the counts are a handful
    // of field reads on a handful of providers, and the world hop is also the cancellation point that
    // notices the target going away. -xlinka
    private static async Task<(int loaded, int failed)> AwaitMeshesAsync(Slot rootSlot, object token)
    {
        // Resumes on the world thread, where reading the data model is legal, and throws once the owner
        // is gone.
        await WorldContext.NextUpdate();

        if (rootSlot == null || rootSlot.IsDestroyed)
            return (0, 0);

        var providers = new List<MeshProvider>();
        rootSlot.GetComponentsInChildren(providers);
        if (providers.Count == 0)
            return (0, 0);

        int settled = -1;
        long started = System.Environment.TickCount64;
        long stallSince = started;
        bool warnedUnbound = false;

        while (true)
        {
            bool graceOver = System.Environment.TickCount64 - started > MeshBindGraceMs;
            int waiting = 0;
            int loaded = 0;
            int failed = 0;
            int unbound = 0;

            foreach (var provider in providers)
            {
                if (provider == null || provider.IsDestroyed)
                    continue;

                if (provider.AssetReferenceCount == 0 && !provider.IsAssetAvailable)
                {
                    // Past the grace period nothing is ever going to ask for this one, so waiting on it
                    // would hang the readout on a mesh no renderer wants.
                    if (graceOver)
                    {
                        unbound++;
                        continue;
                    }
                    waiting++;
                    continue;
                }

                waiting++;
                if (provider.IsAssetAvailable)
                    loaded++;
                else if (!AssetReadiness.IsPending(provider))
                    failed++; // load ran and gave up; it is never arriving
            }

            if (unbound > 0 && !warnedUnbound)
            {
                warnedUnbound = true;
                GD.PrintErr($"ClipboardImporter: {unbound} mesh provider(s) have no renderer holding them; not waiting on those");
            }

            if (waiting == 0)
                return (loaded, failed);

            int done = loaded + failed;
            if (done != settled)
            {
                settled = done;
                stallSince = System.Environment.TickCount64;
            }

            ModelImportIndicator.Report(new ImportProgress(
                ImportStage.Meshes,
                MeshWaitStart + (1f - MeshWaitStart) * done / waiting,
                done, waiting), token);

            if (done >= waiting)
                return (loaded, failed);

            if (System.Environment.TickCount64 - stallSince > MeshStallMs)
            {
                GD.PrintErr($"ClipboardImporter: {waiting - done} mesh(es) stopped making progress; giving up on them");
                return (loaded, waiting - loaded);
            }

            await WorldContext.NextUpdate();
        }
    }

    private async Task HandleUrl(string url)
    {
        Lumora.Core.Logging.Logger.Debug($"ClipboardImporter: Handling URL: {url}");

        // A video link is PLAYED, not fetched. A service page has no file behind it at all, a live
        // protocol has no end to download, and a two hour mp4 would be a two gigabyte wait before the
        // first frame. Straight to the dialog, same placement a dropped file gets. -xlinka
        Uri.TryCreate(url, UriKind.Absolute, out var parsed);
        if (AssetHelper.IsVideoStreamingService(parsed)
            || AssetHelper.IsStreamingProtocol(parsed)
            || AssetHelper.IdentifyClass(url) == AssetClass.Video)
        {
            var videoWorld = _engine?.WorldManager?.FocusedWorld;
            if (videoWorld == null)
            {
                GD.PrintErr("ClipboardImporter: No focused world for video URL");
                return;
            }
            var (videoPos, videoRot) = GetSpawnPose();
            UniversalImporter.Import(url, videoWorld, videoPos, videoRot);
            return;
        }

        var extension = GetExtensionFromUrl(url);

        if (string.IsNullOrEmpty(extension))
        {
            await FetchAndImport(url);
            return;
        }

        var tempPath = await DownloadFile(url);
        if (!string.IsNullOrEmpty(tempPath))
        {
            _ = extension;
            await AutoImport(tempPath);
        }
    }

    private string GetExtensionFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var path = uri.AbsolutePath;
            var extension = Path.GetExtension(path).ToLowerInvariant();

        var knownExtensions = new List<string>(ModelImporter.SupportedExtensions)
        {
            ".png",
            ".jpg",
            ".jpeg",
            ".webp",
            ".tif",
            ".tiff",
            ".gdshader",
            ".mp3",
            ".ogg",
            ".oga",
            ".wav"
        };
        foreach (var ext in knownExtensions)
        {
            if (extension == ext) return ext;
        }
        }
        catch { }

        return null!;
    }

    private async Task<string> DownloadFile(string url)
    {
        Lumora.Core.Logging.Logger.Debug($"ClipboardImporter: Downloading file from {url}");

        try
        {
            var httpRequest = new HttpRequest();
            AddChild(httpRequest);

            var tempDir = OS.GetUserDataDir() + "/tmp";
            Directory.CreateDirectory(tempDir);
            var tempPath = tempDir + "/" + Guid.NewGuid().ToString("N") + Path.GetExtension(url);

            httpRequest.DownloadFile = tempPath;

            var tcs = new TaskCompletionSource<string>();

            httpRequest.RequestCompleted += (result, responseCode, headers, body) =>
            {
                httpRequest.QueueFree();

                if (result == (long)HttpRequest.Result.Success && File.Exists(tempPath))
                {
                    tcs.SetResult(tempPath);
                }
                else
                {
                    GD.PrintErr($"ClipboardImporter: Download failed with code {responseCode}");
                    tcs.SetResult(null!);
                }
            };

            var error = httpRequest.Request(url);
            if (error != Error.Ok)
            {
                httpRequest.QueueFree();
                GD.PrintErr($"ClipboardImporter: Failed to start request: {error}");
                return null!;
            }

            return await tcs.Task;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"ClipboardImporter: Exception downloading file: {ex.Message}");
            return null!;
        }
    }

    private async Task FetchAndImport(string url)
    {
        var tempPath = await DownloadFile(url);
        if (!string.IsNullOrEmpty(tempPath))
        {
            var extension = DetectFileTypeFromHeader(tempPath);
            if (!string.IsNullOrEmpty(extension))
            {
                await AutoImport(tempPath);
            }
        }
    }

    private string DetectFileTypeFromHeader(string filePath)
    {
        try
        {
            using var file = File.OpenRead(filePath);
            var header = new byte[12];
            var bytesToRead = (int)System.Math.Min(header.Length, file.Length);
            file.ReadExactly(header, 0, bytesToRead);

            // PNG
            if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
                return ".png";

            // JPEG
            if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
                return ".jpg";

            // WebP
            if (header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
                header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50)
                return ".webp";

            // TIFF, either byte order
            if ((header[0] == 0x49 && header[1] == 0x49 && header[2] == 0x2A && header[3] == 0x00) ||
                (header[0] == 0x4D && header[1] == 0x4D && header[2] == 0x00 && header[3] == 0x2A))
                return ".tif";

            // GLB (binary GLTF)
            if (header[0] == 0x67 && header[1] == 0x6C && header[2] == 0x54 && header[3] == 0x46)
                return ".glb";
        }
        catch { }

        return null!;
    }

    private async Task HandleText(string text)
    {
        Lumora.Core.Logging.Logger.Debug($"ClipboardImporter: Handling text content (length: {text.Length})");

        if (IsGdShaderText(text))
        {
            await ImportShaderText(text);
            return;
        }

        // Could create a text label or note in the world
    }

    private static bool IsGdShaderText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return text.Contains("shader_type", StringComparison.OrdinalIgnoreCase);
    }

    private async Task ImportShaderText(string shaderText)
    {
        var targetSlot = GetTargetSlot();
        if (targetSlot == null)
        {
            GD.PrintErr("ClipboardImporter: No target slot available for shader import");
            return;
        }

        var tempPath = _localDB?.GetTempFilePath(".gdshader");
        if (string.IsNullOrEmpty(tempPath))
        {
            var tempDir = OS.GetUserDataDir() + "/tmp";
            Directory.CreateDirectory(tempDir);
            tempPath = Path.Combine(tempDir, $"clipboard_shader_{DateTime.Now:yyyyMMdd_HHmmss}.gdshader");
        }

        await File.WriteAllTextAsync(tempPath, shaderText);
        await ImportShader(tempPath, targetSlot);
    }

    private async Task ImportShader(string filePath, Slot targetSlot)
    {
        var shaderName = Path.GetFileNameWithoutExtension(filePath);
        if (string.IsNullOrWhiteSpace(shaderName)) shaderName = "CustomShader";
        var rootSlot = targetSlot.AddSlot($"{shaderName}_ShaderWorkbench");
        PositionInFrontOfCamera(rootSlot);
        await PopulateShaderSlotAsync(rootSlot, filePath);
    }

    public async Task PopulateShaderSlotAsync(Slot rootSlot, string filePath)
    {
        Lumora.Core.Logging.Logger.Debug($"ClipboardImporter: Populating shader workbench: {filePath}");

        string localUri = null!;
        if (_localDB != null)
        {
            localUri = await _localDB.ImportLocalAssetAsync(filePath, LocalDB.ImportLocation.Copy);
        }

        var sourceSlot = rootSlot.AddSlot("ShaderSource");
        var shaderProvider = sourceSlot.AttachComponent<ShaderSourceProvider>();
        shaderProvider.URL.Value = new Uri(localUri ?? filePath);

        var materialSlot = rootSlot.AddSlot("Material");
        var material = materialSlot.AttachComponent<CustomShaderMaterial>();
        material.Shader.Target = shaderProvider;

        var sphereSlot = rootSlot.AddSlot("PreviewSphere");
        sphereSlot.LocalPosition.Value = new float3(-0.35f, 0f, 0f);

        var sphereMesh = sphereSlot.AttachComponent<LumoraMeshes.SphereMesh>();
        sphereMesh.Radius.Value = 0.3f;
        sphereMesh.Segments.Value = 32;
        sphereMesh.Rings.Value = 16;

        var meshRenderer = sphereSlot.AttachComponent<MeshRenderer>();
        meshRenderer.Mesh.Target = sphereMesh;
        meshRenderer.Material.Target = material;

        sphereSlot.AttachComponent<Grabbable>();
        var sphereCollider = sphereSlot.AttachComponent<SphereCollider>();
        sphereCollider.Radius.Value = 0.3f;

        Lumora.Core.Logging.Logger.Debug($"ClipboardImporter: Shader material created with local URI {localUri ?? filePath}");
        OnAssetImported?.Invoke(filePath, rootSlot);
        DropOwnTemp(filePath, localUri);
    }

    private void PositionInFrontOfCamera(Slot slot, float distance = 2.0f)
    {
        if (slot == null)
            return;

        if (_camera != null)
        {
            var camPos = _camera.GlobalPosition;
            var camFwd = -_camera.GlobalTransform.Basis.Z; // Godot camera looks down -Z
            var flat = new float3(camFwd.X, 0f, camFwd.Z);
            flat = flat.LengthSquared < 1e-6f ? new float3(0f, 0f, 1f) : flat.Normalized;
            slot.GlobalPosition = new float3(camPos.X + flat.x * distance, camPos.Y, camPos.Z + flat.z * distance);

            // Face the user via a pure yaw, pointing the content's +Z back at the camera. Our content front is +Z
            // (FaceLocalUser/Slot.Forward), and floatQ.LookRotation is broken for facing (inverse-row builder ->
            // edge-on), so use AxisAngle - not LookRotation, and not the old "-Z front" assumption. -xlinka
            var toUser = new float3(-flat.x, 0f, -flat.z);
            slot.GlobalRotation = floatQ.AxisAngle(float3.Up, System.MathF.Atan2(toUser.x, toUser.z));
            return;
        }

        slot.GlobalPosition = GetImportSpawnPosition(slot.Parent, distance);
    }

    private float3 GetImportSpawnPosition(Slot targetSlot, float distance)
    {
        if (_camera != null)
        {
            var spawnPosition = _camera.GlobalPosition + (-_camera.GlobalTransform.Basis.Z * distance);
            return new float3(spawnPosition.X, spawnPosition.Y, spawnPosition.Z);
        }

        var origin = targetSlot?.GlobalPosition ?? float3.Zero;
        return origin + new float3(0f, 0f, -distance);
    }
}
