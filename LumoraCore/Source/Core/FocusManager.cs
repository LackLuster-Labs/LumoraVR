// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core;

/// <summary>
/// Manages focus between the active world and userspace world.
/// </summary>
public class FocusManager
{
    private World _focusedWorld;
    private World _previousFocusedWorld;
    private World _userspaceWorld;

    /// <summary>
    /// The currently focused world (main world user is in).
    /// </summary>
    public World FocusedWorld
    {
        get => _focusedWorld;
        private set
        {
            if (_focusedWorld == value) return;

            var oldWorld = _focusedWorld;
            // Only record real worlds as "previous"; null transitions don't count
            // so closing the current focused world can still find a sensible target. - xlinka
            if (oldWorld != null)
            {
                _previousFocusedWorld = oldWorld;
            }
            _focusedWorld = value;

            OnFocusedWorldChanged?.Invoke(oldWorld, value);
            LumoraLogger.Log($"FocusManager: Focused world changed from '{oldWorld?.WorldName.Value ?? "none"}' to '{value?.WorldName.Value ?? "none"}'");
        }
    }

    /// <summary>
    /// The world that was focused immediately before the current one. Used as a
    /// natural "back" target when closing the focused world.
    /// </summary>
    public World PreviousFocusedWorld => _previousFocusedWorld;

    /// <summary>
    /// The userspace world (overlay UI, dashboard, settings).
    /// Always rendered on top of focused world.
    /// </summary>
    public World UserspaceWorld
    {
        get => _userspaceWorld;
        set
        {
            if (_userspaceWorld == value) return;

            _userspaceWorld = value;
            LumoraLogger.Log($"FocusManager: Userspace world set to '{value?.WorldName.Value ?? "none"}'");
        }
    }

    /// <summary>
    /// Event triggered when the focused world changes.
    /// </summary>
    public event Action<World, World> OnFocusedWorldChanged;

    /// <summary>
    /// Switch focus to a different world.
    /// </summary>
    public void SwitchToWorld(World world)
    {
        if (world == null)
        {
            LumoraLogger.Warn("FocusManager: Cannot switch to null world");
            return;
        }

        if (world == _userspaceWorld)
        {
            LumoraLogger.Warn("FocusManager: Cannot switch focus to userspace world (it's always an overlay)");
            return;
        }

        FocusedWorld = world;
    }

    /// <summary>
    /// Get all worlds that should be updated.
    /// Returns: [FocusedWorld, UserspaceWorld] (if both exist).
    /// </summary>
    public World[] GetActiveWorlds()
    {
        if (_focusedWorld != null && _userspaceWorld != null)
        {
            return new[] { _focusedWorld, _userspaceWorld };
        }
        else if (_focusedWorld != null)
        {
            return new[] { _focusedWorld };
        }
        else if (_userspaceWorld != null)
        {
            return new[] { _userspaceWorld };
        }
        else
        {
            return Array.Empty<World>();
        }
    }

    /// <summary>
    /// Check if a world is currently focused.
    /// </summary>
    public bool IsFocused(World world)
    {
        return _focusedWorld == world;
    }

    /// <summary>
    /// Check if a world is the userspace world.
    /// </summary>
    public bool IsUserspace(World world)
    {
        return _userspaceWorld == world;
    }
}
