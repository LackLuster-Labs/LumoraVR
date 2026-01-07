using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Lumora.Core.Math;
using Lumora.Core.Input;
using Lumora.Core.Logging;

namespace Lumora.Core.Components;

/// <summary>
/// Handles clipboard import functionality - paste files, images, and text into the world.
/// </summary>
[ComponentCategory("Assets/Import")]
public class ClipboardImporter : Component
{
    /// <summary>
    /// Whether to spawn imports in front of the user
    /// </summary>
    public Sync<bool> SpawnInFrontOfUser { get; private set; }

    /// <summary>
    /// Distance in front of user to spawn imports
    /// </summary>
    public Sync<float> SpawnDistance { get; private set; }

    private bool CanImport
    {
        get
        {
            if (World == null || !World.IsAuthority)
                return false;
            
            if (World.State != World.WorldState.Running)
                return false;
                
            return true;
        }
    }

    public override void OnAwake()
    {
        base.OnAwake();

        SpawnInFrontOfUser = new Sync<bool>(this, true);
        SpawnDistance = new Sync<float>(this, 2.0f);

        // Initialize sync members created in OnAwake
        InitializeNewSyncMembers();
    }

    public override void OnStart()
    {
        base.OnStart();
        
        // Note: File drop events would be handled by the platform layer (Godot)
        Logger.Log("ClipboardImporter: Started - ready for clipboard operations");
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        
        if (!CanImport)
            return;

        var inputInterface = Engine.Current?.InputInterface;
        if (inputInterface == null)
            return;

        // Check for Ctrl+V paste
        var keyboard = inputInterface.GetKeyboardDriver() as Keyboard;
        if (keyboard != null)
        {
            bool ctrlPressed = keyboard.IsKeyPressed(Key.LeftControl) || keyboard.IsKeyPressed(Key.RightControl);
            bool vPressed = keyboard.IsKeyJustPressed(Key.V);
            
            if (ctrlPressed && vPressed)
            {
                HandleClipboardPaste();
            }
        }
    }

    public override void OnDestroy()
    {
        base.OnDestroy();
    }

    private void HandleClipboardPaste()
    {
        Logger.Log("ClipboardImporter: Handling clipboard paste");

        // For now, create a simple text import since we don't have clipboard API implemented
        // TODO: Implement actual clipboard reading when platform clipboard interface is available
        ImportText("Pasted content from clipboard");
    }

    private void ImportFiles(List<string> files)
    {
        if (files == null || files.Count == 0)
            return;

        Logger.Log($"ClipboardImporter: Importing {files.Count} items");

        foreach (var file in files)
        {
            ImportSingleItem(file);
        }
    }

    private void ImportSingleItem(string item)
    {
        try
        {
            // Determine what type of import this is
            if (File.Exists(item))
            {
                ImportFile(item);
            }
            else if (Directory.Exists(item))
            {
                ImportDirectory(item);
            }
            else if (Uri.TryCreate(item, UriKind.Absolute, out var uri))
            {
                ImportUrl(uri);
            }
            else
            {
                ImportText(item);
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"ClipboardImporter: Error importing item '{item}': {ex.Message}");
        }
    }

    private void ImportFile(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        var extension = Path.GetExtension(filePath).ToLower();
        
        Logger.Log($"ClipboardImporter: Importing file '{fileName}' with extension '{extension}'");
        
        var importSlot = CreateImportSlot($"Import: {fileName}");
        
        // TODO: Handle different file types
        // - Images: .png, .jpg, .jpeg, .gif, .bmp
        // - 3D Models: .fbx, .obj, .gltf, .glb
        // - Audio: .wav, .mp3, .ogg
        // - Text: .txt, .json, .xml
        
        // For now, just create a placeholder with TextRenderer
        var textComp = importSlot.AttachComponent<TextRenderer>();
        textComp.Text.Value = $"Imported file: {fileName}\nType: {extension}\nPath: {filePath}";
        
        SetupImportedObject(importSlot);
    }

    private void ImportDirectory(string dirPath)
    {
        var dirName = Path.GetFileName(dirPath);
        Logger.Log($"ClipboardImporter: Importing directory '{dirName}'");
        
        var importSlot = CreateImportSlot($"Import: {dirName}");
        
        // TODO: Recursively import directory contents
        var textComp = importSlot.AttachComponent<TextRenderer>();
        textComp.Text.Value = $"Imported directory: {dirName}\nPath: {dirPath}";
        
        SetupImportedObject(importSlot);
    }

    private void ImportUrl(Uri uri)
    {
        Logger.Log($"ClipboardImporter: Importing URL '{uri}'");
        
        var importSlot = CreateImportSlot($"Import: URL");
        
        // TODO: Handle different URL types
        // - Web pages: Create web panel
        // - Media URLs: Download and import
        // - Session/World URLs: Create session interface
        
        var textComp = importSlot.AttachComponent<TextRenderer>();
        textComp.Text.Value = $"Imported URL: {uri}";
        
        SetupImportedObject(importSlot);
    }

    private void ImportText(string text)
    {
        if (text.Length > 1000)
            text = text.Substring(0, 1000) + "...";
            
        Logger.Log($"ClipboardImporter: Importing text ({text.Length} chars)");
        
        var importSlot = CreateImportSlot("Import: Text");
        
        var textComp = importSlot.AttachComponent<TextRenderer>();
        textComp.Text.Value = text;
        textComp.Size.Value = 0.1f;
        
        SetupImportedObject(importSlot);
    }

    private Slot CreateImportSlot(string name)
    {
        var importSlot = World.RootSlot.AddSlot(name);
        
        if (SpawnInFrontOfUser.Value)
        {
            PositionInFrontOfUser(importSlot);
        }
        
        return importSlot;
    }

    private void PositionInFrontOfUser(Slot slot)
    {
        var localUser = World.LocalUser;
        if (localUser != null)
        {
            // Try to find user's head position from UserRoot component
            var userSlots = World.FindSlotsByTag("UserRoot");
            foreach (var userSlot in userSlots)
            {
                var userRoot = userSlot.GetComponent<UserRoot>();
                if (userRoot?.ActiveUser == localUser)
                {
                    var headSlot = userRoot.HeadSlot;
                    if (headSlot != null)
                    {
                        var forward = headSlot.GlobalRotation * float3.Forward;
                        var spawnPosition = headSlot.GlobalPosition + forward * SpawnDistance.Value;
                        
                        slot.GlobalPosition = spawnPosition;
                        slot.LookAt(headSlot.GlobalPosition, float3.Up);
                        return;
                    }
                }
            }
        }
        
        // Fallback position
        slot.GlobalPosition = new float3(0, 1.5f, 2);
    }

    private static void SetupImportedObject(Slot slot)
    {
        // Make the object temporary (not persistent)
        slot.Persistent.Value = false;

        Logger.Log($"ClipboardImporter: Setup completed for '{slot.SlotName.Value}'");
    }
}
