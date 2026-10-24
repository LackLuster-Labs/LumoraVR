// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks;

// The shader files the game ships stay loaded for the whole run, once anything has loaded them.
//
// Godot builds a shader's pipelines in the background, and freeing a shader waits for every one of them still
// queued. That free lands on the main thread whoever dropped the shader: a finalizer letting go of a loaded
// file queues it, and the next call into the renderer runs it. Cold, with an avatar's toon passes in the same
// queue, that was a 24 to 44 s freeze of the world update, the GPU idle, blamed on whichever material happened
// to write a value next. A file nothing wears any more was also loaded and compiled again by the next thing to
// want it. Kept here, neither happens. -xlinka
//
// Materials load their files through Load. Files other code loads itself (a module's renderer) are adopted from
// Godot's resource cache a few a frame, so they outlive the renderer that loaded them too. -xlinka
internal static class ShaderFiles
{
    private static readonly string[] Roots = { "res://Shaders", "res://Modules", "res://Assets" };
    private const int AdoptPerFrame = 8;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Shader> Kept = new(StringComparer.Ordinal);
    private static string[]? _shipped;
    private static Task? _listing;
    private static int _next;
    private static bool _adopting;

    // The file's shader, loaded once and kept. Null when there is no such file.
    public static Shader? Load(string path)
    {
        lock (Gate)
        {
            if (Kept.TryGetValue(path, out var kept) && GodotObject.IsInstanceValid(kept))
                return kept;
        }
        if (!ResourceLoader.Exists(path))
            return null;
        var shader = ResourceLoader.Load<Shader>(path);
        if (shader == null)
            return null;
        lock (Gate)
            Kept[path] = shader;
        StartAdopting();
        return shader;
    }

    private static void StartAdopting()
    {
        if (_adopting || global::Godot.Engine.GetMainLoop() is not SceneTree tree)
            return;
        _adopting = true;
        _listing = Task.Run(List);
        tree.ProcessFrame += Adopt;
    }

    private static void List()
    {
        var found = new List<string>();
        foreach (var root in Roots)
            Collect(root, found, 0);
        _shipped = found.ToArray();
    }

    private static void Collect(string folder, List<string> found, int depth)
    {
        if (depth > 12)
            return;
        string[] entries;
        try
        {
            entries = ResourceLoader.ListDirectory(folder);
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"ShaderFiles: could not list {folder}: {ex.Message}");
            return;
        }
        foreach (var entry in entries)
        {
            if (entry.EndsWith('/'))
                Collect(folder + "/" + entry.TrimEnd('/'), found, depth + 1);
            else if (entry.EndsWith(".gdshader", StringComparison.OrdinalIgnoreCase))
                found.Add(folder + "/" + entry);
        }
    }

    private static void Adopt()
    {
        if (_listing is { IsCompleted: false })
            return;
        var shipped = _shipped;
        if (shipped == null || shipped.Length == 0)
            return;
        for (int i = 0; i < AdoptPerFrame && i < shipped.Length; i++)
        {
            _next = (_next + 1) % shipped.Length;
            string path = shipped[_next];
            lock (Gate)
            {
                if (Kept.TryGetValue(path, out var kept) && GodotObject.IsInstanceValid(kept))
                    continue;
            }
            if (!ResourceLoader.HasCached(path) || ResourceLoader.GetCachedRef(path) is not Shader loaded)
                continue;
            lock (Gate)
                Kept[path] = loaded;
        }
    }
}
