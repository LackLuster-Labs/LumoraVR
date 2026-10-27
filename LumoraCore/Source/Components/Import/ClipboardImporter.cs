// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components.Import;
using Lumora.Core.Input;
using Lumora.Core.Logging;
using System;
using Helio.UI;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Components.UI;
using Lumora.Core.Math;
using Lumora.Core.Persistence;

namespace Lumora.Core.Components;

// Userspace Ctrl+V trigger. Detects the local paste keystroke while a focused world
// is running and hands off to the platform clipboard bridge, which reads the
// real OS clipboard and routes its contents through the import pipeline. The core
// has no clipboard access of its own, so without a registered bridge a paste is a
// no-op - it never fabricates placeholder content. - xlinka
[ComponentCategory("Assets/Import")]
[UserspaceOnly]
public class ClipboardImporter : Component
{
    private bool _warnedNoBridge;
    private SavedGraph? _copiedObject;
    private string? _copyToken;
    private float3 _copiedScale;
    private floatQ _copiedRotation;

    private bool CanImport
    {
        get
        {
            if (!IsExecutionAllowed || Engine.Current?.WorldManager?.FocusedWorld is not { IsDestroyed: false } focused)
                return false;

            if (focused.State != World.WorldState.Running)
                return false;

            return true;
        }
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        if (!CanImport || TextInput.Focused != null || DashWindowPanel.TakesKeys)
            return;

        var actions = Engine.Current?.InputInterface?.Actions;
        if (actions?.TextFocusHeld == true) return;
        if (actions?.Editing.Copy.Pressed == true) CopyObject();

        // Paste is an action like anything else, so a focused text field takes the keyboard out of
        // play before this sees it; ctrl+V typed into a name box never reaches the importer.
        if (Engine.Current?.InputInterface?.Actions?.Editing.Paste.Pressed == true)
        {
            HandleClipboardPaste();
        }
    }

    private void HandleClipboardPaste()
    {
        if (PasteObject()) return;
        var bridge = ImportHandlers.Clipboard;
        if (bridge == null)
        {
            // Honest no-op: the core can't read the OS clipboard, and faking a
            // "pasted content" label would lie about success. The platform layer
            // registers the bridge at startup; if it didn't, paste does nothing.
            if (!_warnedNoBridge)
            {
                Logger.Warn("ClipboardImporter: no platform clipboard bridge registered; Ctrl+V paste is unavailable.");
                _warnedNoBridge = true;
            }
            return;
        }

        Logger.Debug("ClipboardImporter: routing Ctrl+V to platform clipboard");
        bridge.Paste();
    }

    private void CopyObject()
    {
        var world = Engine.Current?.WorldManager?.FocusedWorld;
        var root = world?.LocalUser?.Root?.Slot;
        var clipboard = Engine.Current?.InputInterface?.ClipboardText;
        if (root == null || clipboard == null) return;
        Slot? selected = null;
        foreach (var hand in root.GetComponentsInChildren<HandTool>())
        {
            if (hand.Side.Value != Chirality.Right) continue;
            if (hand.Grabber is { } grabber)
                foreach (var held in grabber.GrabbedObjects)
                    if (held is Component component) { selected = component.Slot; break; }
            selected ??= (hand.ActiveToolItem.Target as DevToolItem)?.SelectedSlot.Target;
        }
        if (selected == null || selected.IsDestroyed || selected == world!.RootSlot
            || selected.GetComponent<UserRoot>() != null || selected.GetComponent<ToolItem>() is { IsEquipped: true }
            || DuplicateBlock.Blocks(selected)) return;
        // A copy on the clipboard is a copy: an account's seal keeps it to its owner.
        if (Lumora.Core.Security.ItemSeals.RefusesHere(selected, Lumora.Core.Security.SealRule.SaveCopy)) return;
        try
        {
            // A snapshot, so moving or deleting the original cannot change what was copied. Assets
            // travel with it; local file paths and private UI services never enter the clipboard.
            var graph = selected.SaveObject(DependencyHandling.CollectAssets, saveNonPersistent: true);
            string token = "lumora-clipboard://object/" + Guid.NewGuid().ToString("N");
            clipboard.SetText(token);
            _copiedObject = graph;
            _copyToken = token;
            _copiedScale = selected.GlobalScale;
            _copiedRotation = selected.GlobalRotation;
        }
        catch (Exception ex) { Logger.Warn($"Clipboard: object copy failed: {ex.Message}"); }
    }

    private bool PasteObject()
    {
        if (_copiedObject == null || _copyToken == null
            || Engine.Current?.InputInterface?.ClipboardText?.GetText() != _copyToken) return false;
        var world = Engine.Current?.WorldManager?.FocusedWorld;
        if (ImportDialog.SpawnRefusal(world) is { } refusal) { Logger.Warn(refusal); return true; }
        Slot? slot = null;
        try
        {
            var into = world!;
            slot = into.AddSpawnSlot(into.RootSlot, "Pasted object");
            // A big paste goes in over several frames (Slot.LoadObjectStaged), placed as soon as it has a place.
            var rotation = _copiedRotation;
            var scale = _copiedScale;
            var pasted = slot;
            slot.LoadObjectStaged(_copiedObject.Root, null, placed =>
            {
                var head = into.LocalUser?.Root?.HeadSlot;
                placed.GlobalPosition = head != null ? head.GlobalPosition + head.Forward * -1.5f : float3.Zero;
                placed.GlobalRotation = rotation;
                placed.GlobalScale = scale;
            }, loaded =>
            {
                // Not a cloud record: another account's sealed things in it do not come in this way.
                if (loaded != null)
                    Lumora.Core.Security.ItemSeals.DropForeignOnLoad(loaded);
                if (loaded != null && !loaded.IsDestroyed)
                    UndoManager.ForLocalUser(into)?.Record(SlotExistenceUndoBatch.Created(into, new[] { loaded }, UndoLocale.Duplicate));
                else if (!pasted.IsDestroyed)
                    pasted.Destroy();
            });
        }
        catch (Exception ex)
        {
            slot?.Destroy();
            Logger.Warn($"Clipboard: object paste failed: {ex.Message}");
        }
        return true;
    }
}
