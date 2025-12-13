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
    public SyncRef<User> TargetUser { get; private set; } = null!;

    /// <summary>
    /// Username text to display.
    /// </summary>
    public Sync<string> DisplayName { get; private set; } = null!;

    /// <summary>
    /// Rim glow color based on user status.
    /// White = normal logged in, Grey = not logged in, Colored = patreon tier.
    /// </summary>
    public Sync<color> RimColor { get; private set; } = null!;

    /// <summary>
    /// Whether the user is logged in (affects rim color).
    /// </summary>
    public Sync<bool> IsLoggedIn { get; private set; } = null!;

    /// <summary>
    /// Patreon tier color hex (e.g. "#00FF00" for Enthusiast).
    /// Empty string means no patreon tier.
    /// </summary>
    public Sync<string> PatreonColorHex { get; private set; } = null!;

    /// <summary>
    /// Size of the nameplate in world units.
    /// </summary>
    public Sync<float2> Size { get; private set; } = null!;

    /// <summary>
    /// Vertical offset above the head slot.
    /// </summary>
    public Sync<float> HeadOffset { get; private set; } = null!;

    /// <summary>
    /// Whether the nameplate should billboard (always face camera).
    /// </summary>
    public Sync<bool> Billboard { get; private set; } = null!;

    // Patreon tier colors
    public static readonly color ColorNotLoggedIn = new color(0.5f, 0.5f, 0.5f, 1f); // Grey
    public static readonly color ColorNormal = new color(1f, 1f, 1f, 1f); // White
    public static readonly color ColorEnthusiast = new color(0f, 1f, 0f, 1f); // Green #00FF00
    public static readonly color ColorCollaborator = new color(0.255f, 0.412f, 0.882f, 1f); // Royal Blue #4169E1
    public static readonly color ColorInsider = new color(0.58f, 0f, 0.827f, 1f); // Purple #9400D3
    public static readonly color ColorVisionary = new color(1f, 0.843f, 0f, 1f); // Gold #FFD700

    public override void OnAwake()
    {
        base.OnAwake();
        InitializeSyncMembers();
    }

    private void InitializeSyncMembers()
    {
        TargetUser = new SyncRef<User>(this);
        DisplayName = new Sync<string>(this, "");
        RimColor = new Sync<color>(this, ColorNormal);
        IsLoggedIn = new Sync<bool>(this, true);
        PatreonColorHex = new Sync<string>(this, "");
        Size = new Sync<float2>(this, new float2(0.3f, 0.08f)); // 30cm x 8cm default
        HeadOffset = new Sync<float>(this, 0.25f); // 25cm above head
        Billboard = new Sync<bool>(this, true);

        TargetUser.OnChanged += _ => UpdateFromUser();
        IsLoggedIn.OnChanged += _ => UpdateRimColor();
        PatreonColorHex.OnChanged += _ => UpdateRimColor();
        DisplayName.OnChanged += _ => NotifyChanged();
        RimColor.OnChanged += _ => NotifyChanged();
        Size.OnChanged += _ => NotifyChanged();
    }

    /// <summary>
    /// Initialize the nameplate for a specific user.
    /// </summary>
    public void Initialize(User user)
    {
        TargetUser.Target = user;
        UpdateFromUser();
    }

    private void UpdateFromUser()
    {
        var user = TargetUser.Target;
        if (user == null)
        {
            DisplayName.Value = "";
            return;
        }

        DisplayName.Value = user.UserName.Value ?? "Unknown";

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
    }
}
