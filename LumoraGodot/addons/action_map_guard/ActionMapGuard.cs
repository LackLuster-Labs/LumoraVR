// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

#if TOOLS
using System;
using Godot;

namespace Lumora.Source.Godot.Editor;

// The editor reloads a resource when its file changes on disk (a pull, an IDE save, another tool), and in
// this engine build a reloaded OpenXR action map keeps only every second interaction profile: the reload
// hands the map the fresh copy's own profile list, and taking each profile out of that copy shifts the list
// under the loop. The map is marked edited too, so the next scene save (Ctrl+S, Save All, Save & Quit)
// writes the halved map back. That is how Touch, WMR, HP, Samsung, Huawei, the Frame and the trackers left
// the map in October. This sets the map again from the file, with copied lists, right after such a
// reload. -xlinka
[Tool]
public partial class ActionMapGuard : EditorPlugin
{
    private EditorFileSystem? _files;

    public override void _EnterTree()
    {
        _files = EditorInterface.Singleton.GetResourceFilesystem();
        _files.ResourcesReload += OnResourcesReload;
    }

    public override void _ExitTree()
    {
        if (_files != null)
            _files.ResourcesReload -= OnResourcesReload;
        _files = null;
    }

    private static string MapPath
        => ResourceUid.EnsurePath(ProjectSettings.GetSetting("xr/openxr/default_action_map", "res://openxr_action_map.tres").AsString());

    private void OnResourcesReload(string[] resources)
    {
        // Deferred, so it runs after the editor's own reload whichever handler is called first.
        if (Array.IndexOf(resources, MapPath) >= 0)
            Callable.From(Repair).CallDeferred();
    }

    private static void Repair()
    {
        var path = MapPath;
        if (!ResourceLoader.HasCached(path) || ResourceLoader.GetCachedRef(path) is not OpenXRActionMap held)
            return;
        var file = ResourceLoader.Load<OpenXRActionMap>(path, null, ResourceLoader.CacheMode.Ignore);
        if (file == null || held.GetInteractionProfileCount() == file.GetInteractionProfileCount())
            return;

        int kept = held.GetInteractionProfileCount();
        // Both lists from the one load, so every binding points at an action in the map's own sets.
        held.ActionSets = file.ActionSets.Duplicate();
        held.InteractionProfiles = file.InteractionProfiles.Duplicate();
        GD.Print($"Action map guard: the editor's reload of {path} kept {kept} of {held.GetInteractionProfileCount()} interaction profiles; all of them are back.");
    }
}
#endif
