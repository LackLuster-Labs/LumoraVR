using System;
using System.Threading.Tasks;
using LumoraVR.Source.Management;
using LumoraVR.Source.Logging;
using Godot;

namespace LumoraVR.Source.Management.HUD;

/// <summary>
/// Manages the login UI panel for user authentication
/// </summary>
public partial class LoginUI : Control
{
    [Export] public Control LoginPanel;
    [Export] public Control RegisterPanel;
    [Export] public Control TwoFactorPanel;
    [Export] public Control ProfilePanel;

    [Export] public LineEdit UsernameInput;
    [Export] public LineEdit PasswordInput;
    [Export] public Button LoginButton;
    [Export] public Button RegisterTabButton;
    [Export] public Label LoginErrorLabel;

    [Export] public LineEdit RegisterUsernameInput;
    [Export] public LineEdit RegisterEmailInput;
    [Export] public LineEdit RegisterPasswordInput;
    [Export] public LineEdit RegisterConfirmPasswordInput;
    [Export] public Button RegisterButton;
    [Export] public Button LoginTabButton;
    [Export] public Label RegisterErrorLabel;

    [Export] public LineEdit TwoFactorCodeInput;
    [Export] public Button TwoFactorSubmitButton;
    [Export] public Button TwoFactorCancelButton;
    [Export] public Label TwoFactorErrorLabel;

    [Export] public Label ProfileUsernameLabel;
    [Export] public Label ProfileEmailLabel;
    [Export] public Label ProfileMemberSinceLabel;
    [Export] public Label ProfilePatreonStatusLabel;
    [Export] public Button LogoutButton;

    private string _pendingUsername;
    private string _pendingPassword;
    private bool _requires2FA;

    public override void _Ready()
    {
        // Connect signal handlers
        LoginButton.Pressed += OnLoginButtonPressed;
        RegisterButton.Pressed += OnRegisterButtonPressed;
        TwoFactorSubmitButton.Pressed += OnTwoFactorSubmitButtonPressed;
        TwoFactorCancelButton.Pressed += OnTwoFactorCancelPressed;
        LogoutButton.Pressed += OnLogoutButtonPressed;

        LoginTabButton.Pressed += () => SwitchToPanel(LoginPanel);
        RegisterTabButton.Pressed += () => SwitchToPanel(RegisterPanel);

        // Handle input submission
        UsernameInput.TextSubmitted += (_) => PasswordInput.GrabFocus();
        PasswordInput.TextSubmitted += (_) => OnLoginButtonPressed();
        TwoFactorCodeInput.TextSubmitted += (_) => OnTwoFactorSubmitButtonPressed();

        RegisterUsernameInput.TextSubmitted += (_) => RegisterEmailInput.GrabFocus();
        RegisterEmailInput.TextSubmitted += (_) => RegisterPasswordInput.GrabFocus();
        RegisterPasswordInput.TextSubmitted += (_) => RegisterConfirmPasswordInput.GrabFocus();
        RegisterConfirmPasswordInput.TextSubmitted += (_) => OnRegisterButtonPressed();

        // Listen for login status changes
        LoginManager.Instance.OnLoginStatusChanged += OnLoginStatusChanged;

        // Initial state
        UpdateUIState();
    }
    public override void _Notification(int what)
    {
        base._Notification(what);
        switch (what)
        {
            case (int)NotificationPredelete:
                if (LoginManager.Instance != null)
                {
                    LoginManager.Instance.OnLoginStatusChanged -= OnLoginStatusChanged;
                }
                break;
        }
    }

    /// <summary>
    /// Updates the UI based on current login state
    /// </summary>
    public void UpdateUIState()
    {
        var loginManager = LoginManager.Instance;

        if (loginManager == null)
        {
            Logger.Error("LoginManager instance is null");
            return;
        }

        var isLoggedIn = loginManager.IsLoggedIn;

        // Clear any previous errors
        LoginErrorLabel.Text = "";
        RegisterErrorLabel.Text = "";
        TwoFactorErrorLabel.Text = "";

        if (isLoggedIn)
        {
            // User is logged in, show profile panel
            SwitchToPanel(ProfilePanel);
            UpdateProfileInfo();
        }
        else if (_requires2FA)
        {
            // User needs to enter 2FA code
            SwitchToPanel(TwoFactorPanel);
            TwoFactorCodeInput.GrabFocus();
        }
        else
        {
            // User needs to log in
            SwitchToPanel(LoginPanel);
            UsernameInput.GrabFocus();
        }
    }

    /// <summary>
    /// Updates the profile panel with user information
    /// </summary>
    private void UpdateProfileInfo()
    {
        var loginManager = LoginManager.Instance;
        if (!loginManager.IsLoggedIn) return;

        var username = loginManager.GetCurrentUsername();
        var profile = loginManager.GetUserProfile();

        if (profile != null)
        {
            // Get the name color - use the Patreon tier color if available, otherwise use the specific name color
            Color nameColor = Colors.White; // Default white

            if (profile.PatreonData != null && profile.PatreonData.IsActiveSupporter && !string.IsNullOrEmpty(profile.PatreonData.TierColor))
            {
                nameColor = StringToColor(profile.PatreonData.TierColor);
            }
            else if (!string.IsNullOrEmpty(profile.NameColor) && profile.NameColor != "#FFFFFF")
            {
                nameColor = StringToColor(profile.NameColor);
            }

            // Apply the color to the username
            ProfileUsernameLabel.Text = username;
            ProfileUsernameLabel.AddThemeColorOverride("font_color", nameColor);

            // Format registration date
            var formattedDate = profile.RegistrationDate.ToString("MMMM d, yyyy");
            ProfileMemberSinceLabel.Text = $"Member since: {formattedDate}";

            // Display Patreon status if available
            if (profile.PatreonData != null && profile.PatreonData.IsActiveSupporter)
            {
                // Use tier name if available, otherwise use dollar amount
                string tierName = !string.IsNullOrEmpty(profile.PatreonData.TierName) ?
                    profile.PatreonData.TierName :
                    $"${profile.PatreonData.LastTierCents / 100}";

                // Create patron display string
                ProfilePatreonStatusLabel.Text = $"Patreon: {tierName}";
                ProfilePatreonStatusLabel.AddThemeColorOverride("font_color", nameColor);
                ProfilePatreonStatusLabel.Visible = true;
            }
            else
            {
                ProfilePatreonStatusLabel.Text = "Not a Patreon supporter";
                ProfilePatreonStatusLabel.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
                ProfilePatreonStatusLabel.Visible = true;
            }
        }
        else
        {
            // Fallback for when profile isn't available
            ProfileUsernameLabel.Text = username;
            ProfileMemberSinceLabel.Text = "Member since: Unknown";
            ProfilePatreonStatusLabel.Visible = false;
        }
    }

    // Helper to convert hex color string to Godot Color
    private Color StringToColor(string hexColor)
    {
        if (string.IsNullOrEmpty(hexColor)) return Colors.White;

        try
        {
            // Remove # if present
            if (hexColor.StartsWith("#"))
                hexColor = hexColor.Substring(1);

            // Parse hex values
            if (hexColor.Length == 6)
            {
                int r = Convert.ToInt32(hexColor.Substring(0, 2), 16);
                int g = Convert.ToInt32(hexColor.Substring(2, 2), 16);
                int b = Convert.ToInt32(hexColor.Substring(4, 2), 16);
                return new Color(r / 255f, g / 255f, b / 255f);
            }
        }
        catch
        {
            // Fallback to default on parsing error
        }

        return Colors.White; // Default color
    }

    /// <summary>
    /// Switches to the specified panel and hides all others
    /// </summary>
    private void SwitchToPanel(Control panel)
    {
        LoginPanel.Visible = panel == LoginPanel;
        RegisterPanel.Visible = panel == RegisterPanel;
        TwoFactorPanel.Visible = panel == TwoFactorPanel;
        ProfilePanel.Visible = panel == ProfilePanel;
    }

    /// <summary>
    /// Called when the login button is pressed
    /// </summary>
    private async void OnLoginButtonPressed()
    {
        var username = UsernameInput.Text;
        var password = PasswordInput.Text;

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            LoginErrorLabel.Text = "Please enter both username and password";
            return;
        }

        // Disable the login button to prevent multiple attempts
        LoginButton.Disabled = true;
        LoginErrorLabel.Text = "Logging in...";

        var result = await LoginManager.Instance.LoginAsync(username, password);

        if (result.Requires2FA)
        {
            _requires2FA = true;
            _pendingUsername = username;
            _pendingPassword = password;
            UpdateUIState();
        }
        else if (result.Success)
        {
            // Login successful, UI will update through the event handler
            PasswordInput.Text = "";
        }
        else
        {
            // Login failed
            LoginErrorLabel.Text = result.Error;
            PasswordInput.Text = "";
        }

        LoginButton.Disabled = false;
    }

    /// <summary>
    /// Called when the register button is pressed
    /// </summary>
    private async void OnRegisterButtonPressed()
    {
        var username = RegisterUsernameInput.Text;
        var email = RegisterEmailInput.Text;
        var password = RegisterPasswordInput.Text;
        var confirmPassword = RegisterConfirmPasswordInput.Text;

        // Basic validation
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(email) ||
            string.IsNullOrEmpty(password) || string.IsNullOrEmpty(confirmPassword))
        {
            RegisterErrorLabel.Text = "All fields are required";
            return;
        }

        if (password != confirmPassword)
        {
            RegisterErrorLabel.Text = "Passwords do not match";
            return;
        }

        // Simple email validation
        if (!email.Contains("@") || !email.Contains("."))
        {
            RegisterErrorLabel.Text = "Invalid email address";
            return;
        }

        // Disable the register button to prevent multiple attempts
        RegisterButton.Disabled = true;
        RegisterErrorLabel.Text = "Registering...";

        var response = await LoginManager.Instance.RegisterAsync(username, email, password);

        if (response.Success)
        {
            RegisterErrorLabel.Text = "Registration successful! You can now log in.";

            // Clear the fields
            RegisterUsernameInput.Text = "";
            RegisterEmailInput.Text = "";
            RegisterPasswordInput.Text = "";
            RegisterConfirmPasswordInput.Text = "";

            // Switch to the login panel after a brief delay
            await Task.Delay(1500);
            SwitchToPanel(LoginPanel);
            UsernameInput.Text = username;
            PasswordInput.GrabFocus();
        }
        else
        {
            RegisterErrorLabel.Text = response.Message;
        }

        RegisterButton.Disabled = false;
    }

    /// <summary>
    /// Called when the 2FA submit button is pressed
    /// </summary>
    private async void OnTwoFactorSubmitButtonPressed()
    {
        var code = TwoFactorCodeInput.Text;

        if (string.IsNullOrEmpty(code))
        {
            TwoFactorErrorLabel.Text = "Please enter the 2FA code";
            return;
        }

        TwoFactorSubmitButton.Disabled = true;
        TwoFactorErrorLabel.Text = "Verifying...";

        var result = await LoginManager.Instance.LoginAsync(_pendingUsername, _pendingPassword, code);

        if (result.Success)
        {
            // Login successful with 2FA, UI will update through the event handler
            _requires2FA = false;
            _pendingUsername = null;
            _pendingPassword = null;
            TwoFactorCodeInput.Text = "";
        }
        else
        {
            // 2FA verification failed
            TwoFactorErrorLabel.Text = result.Error;
            TwoFactorCodeInput.Text = "";
        }

        TwoFactorSubmitButton.Disabled = false;
    }

    /// <summary>
    /// Called when the 2FA cancel button is pressed
    /// </summary>
    private void OnTwoFactorCancelPressed()
    {
        // Reset 2FA state and return to login screen
        _requires2FA = false;
        _pendingUsername = null;
        _pendingPassword = null;
        TwoFactorCodeInput.Text = "";
        TwoFactorErrorLabel.Text = "";
        SwitchToPanel(LoginPanel);
    }

    /// <summary>
    /// Called when the logout button is pressed
    /// </summary>
    private void OnLogoutButtonPressed()
    {
        LoginManager.Instance.Logout();
        // UI will update through the event handler
    }

    /// <summary>
    /// Event handler for login status changes
    /// </summary>
    private void OnLoginStatusChanged(bool isLoggedIn)
    {
        CallDeferred(nameof(UpdateUIState));
    }
}
