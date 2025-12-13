using System;
using System.Collections.Generic;
using Lumora.Core.Assets;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.GodotUI.Wizards;

/// <summary>
/// Godot UI-based User Inspector that loads the UserInspector.tscn scene.
/// Displays user information using native Godot UI.
/// </summary>
[ComponentCategory("GodotUI/Wizards")]
public class GodotUserInspector : GodotUIPanel, IWorldEventReceiver
{
    protected override string DefaultScenePath => LumAssets.UI.UserInspector;
    protected override float2 DefaultSize => new float2(320, 380);

    /// <summary>
    /// Currently selected user to display details for.
    /// </summary>
    public SyncRef<User> SelectedUser { get; private set; } = null!;

    /// <summary>
    /// Event fired when user selection changes.
    /// </summary>
    public event Action<User?>? OnUserSelectionChanged;

    /// <summary>
    /// Event fired when user list needs refresh.
    /// </summary>
    public event Action? OnUserListChanged;

    public override void OnAwake()
    {
        base.OnAwake();

        SelectedUser = new SyncRef<User>(this);
        SelectedUser.OnChanged += _ =>
        {
            OnUserSelectionChanged?.Invoke(SelectedUser.Target);
            NotifyChanged();
        };
    }

    protected override void OnAttach()
    {
        base.OnAttach();
        World?.RegisterEventReceiver(this);

        // Auto-select first user if none selected
        if (SelectedUser.Target == null && World != null)
        {
            var users = World.GetAllUsers();
            if (users.Count > 0)
            {
                SelectedUser.Target = users[0];
            }
        }
    }

    protected override void OnDetach()
    {
        World?.UnregisterEventReceiver(this);
        base.OnDetach();
    }

    public override Dictionary<string, string> GetUIData()
    {
        var data = new Dictionary<string, string>();
        var user = SelectedUser.Target;

        // Title
        data["MainPanel/VBox/Header/HBox/UserTitle"] = user != null
            ? $"User: {user.UserName.Value} ({user.ReferenceID})"
            : "User: Select a user";

        if (user == null) return data;

        // Basic Info
        data["MainPanel/VBox/Content/RightPanel/ScrollContainer/DetailContent/BasicInfo/UserName/Value"] = user.UserName.Value ?? "Unknown";
        data["MainPanel/VBox/Content/RightPanel/ScrollContainer/DetailContent/BasicInfo/UserID/Value"] = user.UserID.Value ?? "N/A";
        data["MainPanel/VBox/Content/RightPanel/ScrollContainer/DetailContent/BasicInfo/MachineID/Value"] = TruncateString(user.MachineID.Value ?? "N/A", 20);

        // Status
        data["MainPanel/VBox/Content/RightPanel/ScrollContainer/DetailContent/Status/HeadDevice/Value"] = FormatHeadDevice(user.HeadDevice.Value);
        data["MainPanel/VBox/Content/RightPanel/ScrollContainer/DetailContent/Status/Platform/Value"] = user.UserPlatform.Value.ToString();
        data["MainPanel/VBox/Content/RightPanel/ScrollContainer/DetailContent/Status/VRActive/Value"] = user.VRActive.Value ? "true" : "false";
        data["MainPanel/VBox/Content/RightPanel/ScrollContainer/DetailContent/Status/Present/Value"] = user.PresentInWorld.Value ? "✓" : "✗";

        // Performance
        data["MainPanel/VBox/Content/RightPanel/ScrollContainer/DetailContent/Performance/FPS/Value"] = $"{user.FPS.Value:F1}";
        data["MainPanel/VBox/Content/RightPanel/ScrollContainer/DetailContent/Performance/Ping/Value"] = $"{user.Ping.Value} ms";

        return data;
    }

    public override Dictionary<string, color> GetUIColors()
    {
        var colors = new Dictionary<string, color>();
        var user = SelectedUser.Target;

        if (user != null)
        {
            colors["MainPanel/VBox/Content/RightPanel/ScrollContainer/DetailContent/Status/Present/Value"] =
                user.PresentInWorld.Value ? new color(0.4f, 0.9f, 0.4f, 1f) : new color(0.9f, 0.4f, 0.4f, 1f);
        }

        return colors;
    }

    private static string TruncateString(string str, int maxLength)
    {
        if (str.Length <= maxLength) return str;
        return str.Substring(0, maxLength - 3) + "...";
    }

    private static string FormatHeadDevice(HeadOutputDevice device)
    {
        return device switch
        {
            HeadOutputDevice.Server => "Server",
            HeadOutputDevice.Screen => "Desktop",
            HeadOutputDevice.VR => "VR",
            HeadOutputDevice.Camera => "Camera",
            _ => device.ToString()
        };
    }

    // IWorldEventReceiver implementation
    public bool HasEventHandler(World.WorldEvent eventType)
    {
        return eventType == World.WorldEvent.OnUserJoined ||
               eventType == World.WorldEvent.OnUserLeft;
    }

    public void OnUserJoined(User user)
    {
        OnUserListChanged?.Invoke();

        // Auto-select first user
        if (SelectedUser.Target == null)
        {
            SelectedUser.Target = user;
        }
    }

    public void OnUserLeft(User user)
    {
        OnUserListChanged?.Invoke();

        // Clear selection if this user left
        if (SelectedUser.Target == user)
        {
            SelectedUser.Target = null;
        }
    }

    public void OnFocusChanged(World.WorldFocus focus) { }
    public void OnWorldDestroy() { }

    /// <summary>
    /// Select a user to display details for.
    /// </summary>
    public void SelectUser(User? user)
    {
        SelectedUser.Target = user;
    }

    /// <summary>
    /// Get all users in the world.
    /// </summary>
    public IReadOnlyList<User> GetUsers()
    {
        if (World == null)
            return Array.Empty<User>();
        return World.GetAllUsers();
    }
}
