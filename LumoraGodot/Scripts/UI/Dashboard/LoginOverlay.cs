using Godot;
using System;
using Lumora.CDN;

namespace Lumora.Godot.UI;

/// <summary>
/// Login overlay for Lumora authentication.
/// Shows as a centered popup with username/password fields.
/// </summary>
public partial class LoginOverlay : Control
{
    // UI elements
    private LineEdit? _usernameInput;
    private LineEdit? _passwordInput;
    private LineEdit? _twoFactorInput;
    private VBoxContainer? _twoFactorSection;
    private CheckBox? _rememberMe;
    private Label? _errorLabel;
    private Button? _loginButton;
    private Button? _cancelButton;

    // State
    private bool _isLoggingIn;
    private bool _needs2FA;

    // Events
    public event Action? OnLoginSuccess;
    public event Action? OnCancel;

    // Reference to LumoraClient (should be set externally or via singleton)
    private LumoraClient? _client;

    public override void _Ready()
    {
        // Get UI elements
        _usernameInput = GetNodeOrNull<LineEdit>("CenterContainer/LoginPanel/Margin/VBox/Form/UsernameSection/UsernameInput");
        _passwordInput = GetNodeOrNull<LineEdit>("CenterContainer/LoginPanel/Margin/VBox/Form/PasswordSection/PasswordInput");
        _twoFactorInput = GetNodeOrNull<LineEdit>("CenterContainer/LoginPanel/Margin/VBox/Form/TwoFactorSection/TwoFactorInput");
        _twoFactorSection = GetNodeOrNull<VBoxContainer>("CenterContainer/LoginPanel/Margin/VBox/Form/TwoFactorSection");
        _rememberMe = GetNodeOrNull<CheckBox>("CenterContainer/LoginPanel/Margin/VBox/Form/RememberMe");
        _errorLabel = GetNodeOrNull<Label>("CenterContainer/LoginPanel/Margin/VBox/Form/ErrorLabel");
        _loginButton = GetNodeOrNull<Button>("CenterContainer/LoginPanel/Margin/VBox/Buttons/LoginButton");
        _cancelButton = GetNodeOrNull<Button>("CenterContainer/LoginPanel/Margin/VBox/Buttons/CancelButton");

        // Connect signals
        _loginButton?.Connect("pressed", Callable.From(OnLoginPressed));
        _cancelButton?.Connect("pressed", Callable.From(OnCancelPressed));

        // Handle enter key in input fields
        _usernameInput?.Connect("text_submitted", Callable.From<string>((_) => FocusPassword()));
        _passwordInput?.Connect("text_submitted", Callable.From<string>((_) => OnLoginPressed()));
        _twoFactorInput?.Connect("text_submitted", Callable.From<string>((_) => OnLoginPressed()));

        // Focus username field
        _usernameInput?.GrabFocus();

        GD.Print("LoginOverlay: Initialized");
    }

    /// <summary>
    /// Set the LumoraClient instance to use for authentication.
    /// </summary>
    public void SetClient(LumoraClient client)
    {
        _client = client;
    }

    private void FocusPassword()
    {
        _passwordInput?.GrabFocus();
    }

    private async void OnLoginPressed()
    {
        if (_isLoggingIn) return;

        var username = _usernameInput?.Text?.Trim() ?? "";
        var password = _passwordInput?.Text ?? "";
        var remember = _rememberMe?.ButtonPressed ?? false;

        // Validate inputs
        if (string.IsNullOrEmpty(username))
        {
            ShowError("Please enter your username");
            _usernameInput?.GrabFocus();
            return;
        }

        if (string.IsNullOrEmpty(password))
        {
            ShowError("Please enter your password");
            _passwordInput?.GrabFocus();
            return;
        }

        // If 2FA is shown, validate code
        if (_needs2FA)
        {
            var code = _twoFactorInput?.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(code) || code.Length != 6)
            {
                ShowError("Please enter your 6-digit code");
                _twoFactorInput?.GrabFocus();
                return;
            }
        }

        // Check if client is available
        if (_client == null)
        {
            ShowError("Authentication service not available");
            return;
        }

        // Start login
        _isLoggingIn = true;
        HideError();
        SetLoginButtonState(true);

        try
        {
            GD.Print($"LoginOverlay: Attempting login for '{username}'");

            // Get 2FA code if visible
            string? twoFactorCode = _needs2FA ? (_twoFactorInput?.Text?.Trim()) : null;

            var result = await _client.SignIn(username, password, remember, twoFactorCode);

            if (result.Success && result.Data != null)
            {
                GD.Print("LoginOverlay: Login successful");

                // Clear fields
                ClearInputs();

                // Notify success
                OnLoginSuccess?.Invoke();
            }
            else
            {
                // Check if 2FA is required
                if (result.Message?.Contains("2FA", StringComparison.OrdinalIgnoreCase) == true ||
                    result.Message?.Contains("two-factor", StringComparison.OrdinalIgnoreCase) == true ||
                    result.Message?.Contains("verification code", StringComparison.OrdinalIgnoreCase) == true)
                {
                    Show2FAInput();
                }
                else
                {
                    ShowError(result.Message ?? "Login failed. Please check your credentials.");
                }
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"LoginOverlay: Login error - {ex.Message}");
            ShowError("An error occurred. Please try again.");
        }
        finally
        {
            _isLoggingIn = false;
            SetLoginButtonState(false);
        }
    }

    private void OnCancelPressed()
    {
        GD.Print("LoginOverlay: Cancel pressed");
        ClearInputs();
        OnCancel?.Invoke();
    }

    private void Show2FAInput()
    {
        _needs2FA = true;
        if (_twoFactorSection != null)
        {
            _twoFactorSection.Visible = true;
            _twoFactorInput?.GrabFocus();
        }
        if (_loginButton != null)
            _loginButton.Text = "Verify";
    }

    private void ShowError(string message)
    {
        if (_errorLabel != null)
        {
            _errorLabel.Text = message;
            _errorLabel.Visible = true;
        }
    }

    private void HideError()
    {
        if (_errorLabel != null)
            _errorLabel.Visible = false;
    }

    private void SetLoginButtonState(bool isLoading)
    {
        if (_loginButton != null)
        {
            _loginButton.Disabled = isLoading;
            _loginButton.Text = isLoading ? "Signing in..." : (_needs2FA ? "Verify" : "Sign In");
        }
    }

    private void ClearInputs()
    {
        if (_usernameInput != null) _usernameInput.Text = "";
        if (_passwordInput != null) _passwordInput.Text = "";
        if (_twoFactorInput != null) _twoFactorInput.Text = "";
        if (_twoFactorSection != null) _twoFactorSection.Visible = false;
        _needs2FA = false;
        if (_loginButton != null) _loginButton.Text = "Sign In";
        HideError();
    }

    /// <summary>
    /// Show the login overlay.
    /// </summary>
    public new void Show()
    {
        Visible = true;
        _usernameInput?.GrabFocus();
    }

    /// <summary>
    /// Hide the login overlay.
    /// </summary>
    public new void Hide()
    {
        Visible = false;
        ClearInputs();
    }
}
