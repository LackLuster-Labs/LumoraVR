using Godot;
using System;
using System.Collections.Generic;

namespace Lumora.Godot.UI;

/// <summary>
/// Virtual keyboard for VR text input.
/// </summary>
public partial class VRKeyboard : Control
{
    private const string KeyScenePath = "res://Scenes/UI/Input/VRKeyboardKey.tscn";

    // Keyboard layout - each string is a row, | separates keys, special keys in brackets
    private static readonly string[][] KeyboardLayouts = new[]
    {
        // Row 1: Numbers
        new[] { "`", "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-", "=", "[BACK]" },
        // Row 2: QWERTY top
        new[] { "[TAB]", "q", "w", "e", "r", "t", "y", "u", "i", "o", "p", "[", "]", "\\" },
        // Row 3: ASDF middle
        new[] { "[CAPS]", "a", "s", "d", "f", "g", "h", "j", "k", "l", ";", "'", "[ENTER]" },
        // Row 4: ZXCV bottom
        new[] { "[SHIFT]", "z", "x", "c", "v", "b", "n", "m", ",", ".", "/", "[SHIFT]" },
        // Row 5: Space bar
        new[] { "[CTRL]", "[ALT]", "[SPACE]", "[ALT]", "[CTRL]" }
    };

    // Shift variants
    private static readonly Dictionary<string, string> ShiftMap = new()
    {
        { "`", "~" }, { "1", "!" }, { "2", "@" }, { "3", "#" }, { "4", "$" },
        { "5", "%" }, { "6", "^" }, { "7", "&" }, { "8", "*" }, { "9", "(" },
        { "0", ")" }, { "-", "_" }, { "=", "+" }, { "[", "{" }, { "]", "}" },
        { "\\", "|" }, { ";", ":" }, { "'", "\"" }, { ",", "<" }, { ".", ">" },
        { "/", "?" }
    };

    // Base key size
    private const float KeySize = 38f;
    private const float KeySpacing = 3f;

    // Special key widths (multiplier of standard key width)
    private static readonly Dictionary<string, float> SpecialKeyWidths = new()
    {
        { "[BACK]", 1.5f },
        { "[TAB]", 1.3f },
        { "[CAPS]", 1.5f },
        { "[ENTER]", 1.7f },
        { "[SHIFT]", 1.8f },
        { "[SPACE]", 5.0f },
        { "[CTRL]", 1.2f },
        { "[ALT]", 1.2f }
    };

    private static readonly Dictionary<string, string> SpecialKeyLabels = new()
    {
        { "[BACK]", "⌫" },
        { "[TAB]", "⇥" },
        { "[CAPS]", "⇪" },
        { "[ENTER]", "↵" },
        { "[SHIFT]", "⇧" },
        { "[SPACE]", "" },
        { "[CTRL]", "Ctrl" },
        { "[ALT]", "Alt" }
    };

    private LineEdit? _textPreview;
    private Button? _closeButton;
    private HBoxContainer? _row1;
    private HBoxContainer? _row2;
    private HBoxContainer? _row3;
    private HBoxContainer? _row4;
    private HBoxContainer? _row5;

    private PackedScene? _keyScene;
    private readonly List<Button> _allKeys = new();
    private readonly Dictionary<string, Button> _letterKeys = new();

    private bool _shiftActive = false;
    private bool _capsLock = false;
    private string _currentText = "";

    /// <summary>
    /// Fired when text changes.
    /// </summary>
    public event Action<string>? TextChanged;

    /// <summary>
    /// Fired when Enter is pressed.
    /// </summary>
    public event Action<string>? TextSubmitted;

    /// <summary>
    /// Fired when a key is pressed (for audio feedback, etc).
    /// </summary>
    public event Action<string>? KeyPressed;

    /// <summary>
    /// Fired when the close button is pressed.
    /// </summary>
    public event Action? CloseRequested;

    public override void _Ready()
    {
        _textPreview = GetNodeOrNull<LineEdit>("MainMargin/VBox/Header/HeaderMargin/HeaderHBox/TextPreview");
        _closeButton = GetNodeOrNull<Button>("MainMargin/VBox/Header/HeaderMargin/HeaderHBox/CloseButton");
        _row1 = GetNodeOrNull<HBoxContainer>("MainMargin/VBox/KeyboardArea/Row1");
        _row2 = GetNodeOrNull<HBoxContainer>("MainMargin/VBox/KeyboardArea/Row2");
        _row3 = GetNodeOrNull<HBoxContainer>("MainMargin/VBox/KeyboardArea/Row3");
        _row4 = GetNodeOrNull<HBoxContainer>("MainMargin/VBox/KeyboardArea/Row4");
        _row5 = GetNodeOrNull<HBoxContainer>("MainMargin/VBox/KeyboardArea/Row5");

        _keyScene = GD.Load<PackedScene>(KeyScenePath);
        if (_keyScene == null)
        {
            GD.PrintErr($"VRKeyboard: Failed to load key scene from {KeyScenePath}");
            return;
        }

        // Connect close button
        if (_closeButton != null)
        {
            _closeButton.Pressed += OnClosePressed;
        }

        BuildKeyboard();
        GD.Print("VRKeyboard: Initialized");
    }

    private void BuildKeyboard()
    {
        HBoxContainer?[] rows = { _row1, _row2, _row3, _row4, _row5 };

        for (int rowIndex = 0; rowIndex < KeyboardLayouts.Length; rowIndex++)
        {
            var row = rows[rowIndex];
            if (row == null) continue;

            foreach (var keyDef in KeyboardLayouts[rowIndex])
            {
                CreateKey(row, keyDef);
            }
        }
    }

    private void CreateKey(HBoxContainer row, string keyDef)
    {
        if (_keyScene == null) return;

        var key = _keyScene.Instantiate<Button>();
        row.AddChild(key);

        bool isSpecial = keyDef.StartsWith("[") && keyDef.EndsWith("]");
        string displayText = isSpecial
            ? SpecialKeyLabels.GetValueOrDefault(keyDef, keyDef.Trim('[', ']'))
            : keyDef.ToUpper();

        key.Text = displayText;

        // Set key size
        float width = KeySize;
        if (SpecialKeyWidths.TryGetValue(keyDef, out float widthMultiplier))
        {
            width = KeySize * widthMultiplier;
        }
        key.CustomMinimumSize = new Vector2(width, KeySize);

        // Store letter keys for shift updates
        if (!isSpecial && keyDef.Length == 1 && char.IsLetter(keyDef[0]))
        {
            _letterKeys[keyDef] = key;
        }

        // Connect signal
        var capturedKey = keyDef;
        key.Pressed += () => OnKeyPressed(capturedKey);

        _allKeys.Add(key);
    }

    private void OnKeyPressed(string keyDef)
    {
        KeyPressed?.Invoke(keyDef);

        // Handle special keys
        if (keyDef.StartsWith("["))
        {
            HandleSpecialKey(keyDef);
            return;
        }

        // Get the actual character to type
        string charToType = keyDef;

        // Apply shift/caps
        if (_shiftActive || _capsLock)
        {
            if (char.IsLetter(keyDef[0]))
            {
                charToType = keyDef.ToUpper();
            }
            else if (ShiftMap.TryGetValue(keyDef, out var shiftChar))
            {
                charToType = shiftChar;
            }
        }

        // Add character to text
        _currentText += charToType;
        UpdateTextPreview();
        TextChanged?.Invoke(_currentText);

        // Release shift after typing (unless caps lock)
        if (_shiftActive && !_capsLock)
        {
            _shiftActive = false;
            UpdateKeyLabels();
        }
    }

    private void HandleSpecialKey(string keyDef)
    {
        switch (keyDef)
        {
            case "[BACK]":
                if (_currentText.Length > 0)
                {
                    _currentText = _currentText[..^1];
                    UpdateTextPreview();
                    TextChanged?.Invoke(_currentText);
                }
                break;

            case "[ENTER]":
                TextSubmitted?.Invoke(_currentText);
                break;

            case "[SPACE]":
                _currentText += " ";
                UpdateTextPreview();
                TextChanged?.Invoke(_currentText);
                break;

            case "[TAB]":
                _currentText += "\t";
                UpdateTextPreview();
                TextChanged?.Invoke(_currentText);
                break;

            case "[SHIFT]":
                _shiftActive = !_shiftActive;
                UpdateKeyLabels();
                break;

            case "[CAPS]":
                _capsLock = !_capsLock;
                _shiftActive = _capsLock;
                UpdateKeyLabels();
                break;
        }
    }

    private void UpdateKeyLabels()
    {
        bool showUpper = _shiftActive || _capsLock;

        foreach (var (keyChar, button) in _letterKeys)
        {
            button.Text = showUpper ? keyChar.ToUpper() : keyChar.ToLower();
        }

        // Update symbol keys too
        foreach (var key in _allKeys)
        {
            // Find the original key definition
            // For simplicity, we're updating letters directly via _letterKeys
        }
    }

    private void UpdateTextPreview()
    {
        if (_textPreview != null)
        {
            _textPreview.Text = _currentText;
            _textPreview.CaretColumn = _currentText.Length;
        }
    }

    /// <summary>
    /// Get the current text.
    /// </summary>
    public string GetText() => _currentText;

    /// <summary>
    /// Set the current text.
    /// </summary>
    public void SetText(string text)
    {
        _currentText = text ?? "";
        UpdateTextPreview();
    }

    /// <summary>
    /// Clear all text.
    /// </summary>
    public void Clear()
    {
        _currentText = "";
        UpdateTextPreview();
        TextChanged?.Invoke(_currentText);
    }

    /// <summary>
    /// Set placeholder text.
    /// </summary>
    public void SetPlaceholder(string placeholder)
    {
        if (_textPreview != null)
        {
            _textPreview.PlaceholderText = placeholder;
        }
    }

    /// <summary>
    /// Show the keyboard.
    /// </summary>
    public void ShowKeyboard()
    {
        Visible = true;
    }

    /// <summary>
    /// Hide the keyboard.
    /// </summary>
    public void HideKeyboard()
    {
        Visible = false;
    }

    private void OnClosePressed()
    {
        CloseRequested?.Invoke();
        HideKeyboard();
    }
}
