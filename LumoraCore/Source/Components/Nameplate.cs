using System;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.Components;

/// <summary>
/// User nameplate that displays above the user's head.
/// Pill-shaped with transparent background, drop shadow, and rim glow.
/// Rim glow color indicates user status (white = normal, grey = not logged in, colored = patreon tier).
/// </summary>
[ComponentCategory("Users")]
public class Nameplate : ImplementableComponent
{
    /// <summary>
    /// Reference to the user this nameplate belongs to.
    /// </summary>
    public readonly SyncRef<User> TargetUser = new();

    /// <summary>
    /// Username text to display.
    /// </summary>
    public readonly Sync<string> DisplayName = new();

    /// <summary>
    /// Rim glow color based on user status.
    /// White = normal logged in, Grey = not logged in, Colored = patreon tier.
    /// </summary>
    public readonly Sync<color> RimColor = new();

    /// <summary>
    /// Whether the user is logged in (affects rim color).
    /// </summary>
    public readonly Sync<bool> IsLoggedIn = new();

    /// <summary>
    /// Patreon tier color hex (e.g. "#00FF00" for Enthusiast).
    /// Empty string means no patreon tier.
    /// </summary>
    public readonly Sync<string> PatreonColorHex = new();

    /// <summary>
    /// Size of the nameplate in world units.
    /// </summary>
    public readonly Sync<float2> Size = new();

    /// <summary>
    /// Vertical offset above the head slot.
    /// </summary>
    public readonly Sync<float> HeadOffset = new();

    /// <summary>
    /// Whether the nameplate should billboard (always face camera).
    /// </summary>
    public readonly Sync<bool> Billboard = new();

    // Patreon tier colors
    public static readonly color ColorNotLoggedIn = new color(0.5f, 0.5f, 0.5f, 1f); // Grey
    public static readonly color ColorNormal = new color(1f, 1f, 1f, 1f); // White
    public static readonly color ColorEnthusiast = new color(0f, 1f, 0f, 1f); // Green #00FF00
    public static readonly color ColorCollaborator = new color(0.255f, 0.412f, 0.882f, 1f); // Royal Blue #4169E1
    public static readonly color ColorInsider = new color(0.58f, 0f, 0.827f, 1f); // Purple #9400D3
    public static readonly color ColorVisionary = new color(1f, 0.843f, 0f, 1f); // Gold #FFD700

    public override void OnInit()
    {
        base.OnInit();

        // Set default values
        RimColor.Value = ColorNormal;
        IsLoggedIn.Value = true;
        Size.Value = new float2(0.45f, 0.12f); // 45cm x 12cm default
        HeadOffset.Value = 0.3f; // 30cm above head
        Billboard.Value = true;

        // Subscribe to change events
        TargetUser.OnChanged += _ => UpdateFromUser();
        IsLoggedIn.OnChanged += _ => UpdateRimColor();
        PatreonColorHex.OnChanged += _ => UpdateRimColor();
        DisplayName.OnChanged += _ => NotifyChanged();
        RimColor.OnChanged += _ => NotifyChanged();
        Size.OnChanged += _ => NotifyChanged();
    }

    private User _subscribedUser;

    /// <summary>
    /// Initialize the nameplate for a specific user.
    /// Called on authority when creating the nameplate.
    /// Clients receive TargetUser via sync and UpdateFromUser handles subscription.
    /// </summary>
    public void Initialize(User user)
    {
        TargetUser.Target = user;
        Logging.Logger.Log($"Nameplate: Initialized for user '{user?.UserName?.Value ?? "(null)"}' RefID={user?.ReferenceID}");
        // UpdateFromUser is called via TargetUser.OnChanged
    }

    private void OnUserNameChanged(IChangeable _)
    {
        UpdateFromUser();
    }

    private void UpdateFromUser()
    {
        var user = TargetUser.Target;
        if (user == null)
        {
            // Unsubscribe from previous user if any
            if (_subscribedUser != null)
            {
                _subscribedUser.UserName.Changed -= OnUserNameChanged;
                _subscribedUser = null;
            }
            DisplayName.Value = "";
            return;
        }

        // Subscribe to username changes if not already subscribed
        // This handles both Initialize() calls (authority) and sync receives (client)
        if (_subscribedUser != user)
        {
            if (_subscribedUser != null)
            {
                _subscribedUser.UserName.Changed -= OnUserNameChanged;
            }
            _subscribedUser = user;
            user.UserName.Changed += OnUserNameChanged;
            Logging.Logger.Log($"Nameplate: Subscribed to UserName changes for '{user.UserName.Value ?? "(null)"}' RefID={user.ReferenceID}");
        }

        var newName = user.UserName.Value;
        if (string.IsNullOrEmpty(newName))
        {
            newName = "Unknown";
        }

        // Only update and log if name actually changed
        if (DisplayName.Value != newName)
        {
            Logging.Logger.Log($"Nameplate: DisplayName changed from '{DisplayName.Value}' to '{newName}'");
            DisplayName.Value = newName;
        }

        // Check login status from user
        IsLoggedIn.Value = !string.IsNullOrEmpty(user.UserID.Value);

        // Get patreon color from user metadata (if available)
        // For now, we'll use empty string until patreon data is synced
        // PatreonColorHex will be set by the server/cloud when user data is fetched

        UpdateRimColor();
    }

    private void UpdateRimColor()
    {
        if (!IsLoggedIn.Value)
        {
            RimColor.Value = ColorNotLoggedIn;
            return;
        }

        var hexColor = PatreonColorHex.Value;
        if (string.IsNullOrEmpty(hexColor) || hexColor == "#FFFFFF")
        {
            RimColor.Value = ColorNormal;
            return;
        }

        // Parse hex color
        RimColor.Value = ParseHexColor(hexColor);
    }

    /// <summary>
    /// Set the patreon tier color directly.
    /// </summary>
    public void SetPatreonColor(string hexColor)
    {
        PatreonColorHex.Value = hexColor ?? "";
    }

    /// <summary>
    /// Parse a hex color string to color.
    /// </summary>
    private static color ParseHexColor(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return ColorNormal;

        hex = hex.TrimStart('#');
        if (hex.Length != 6) return ColorNormal;

        try
        {
            int r = Convert.ToInt32(hex.Substring(0, 2), 16);
            int g = Convert.ToInt32(hex.Substring(2, 2), 16);
            int b = Convert.ToInt32(hex.Substring(4, 2), 16);
            return new color(r / 255f, g / 255f, b / 255f, 1f);
        }
        catch
        {
            return ColorNormal;
        }
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        // Update display name if user changes it
        var user = TargetUser.Target;
        if (user != null && DisplayName.Value != user.UserName.Value)
        {
            DisplayName.Value = user.UserName.Value ?? "";
        }

        UpdateFollowTransform(user);
    }

    private void UpdateFollowTransform(User user)
    {
        if (Slot == null) return;

        var userRoot = user?.UserRootRef.Target;
        var headSlot = userRoot?.HeadSlot ?? Slot.Parent;
        if (headSlot == null) return;

        var desiredPos = headSlot.GlobalPosition + float3.Up * HeadOffset.Value;
        Slot.GlobalPosition = desiredPos;
        Slot.GlobalRotation = floatQ.Identity;
    }

    public override void OnDestroy()
    {
        // Unsubscribe from user events
        if (_subscribedUser != null)
        {
            _subscribedUser.UserName.Changed -= OnUserNameChanged;
            _subscribedUser = null;
        }

        base.OnDestroy();
    }
}
