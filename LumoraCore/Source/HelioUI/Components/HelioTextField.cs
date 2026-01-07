using System;
using Lumora.Core.Math;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Text field content type for validation.
/// </summary>
public enum TextContentType
{
    Standard,
    Password,
    Email,
    Number,
    Integer,
    Alphanumeric
}

/// <summary>
/// Helio text field component.
/// Text input field with placeholder and validation.
/// </summary>
[ComponentCategory("HelioUI/Interaction")]
public class HelioTextField : Component, IHelioInteractable
{
    // ===== VALUE =====

    /// <summary>
    /// Current text content.
    /// </summary>
    public Sync<string> Text { get; private set; }

    /// <summary>
    /// Placeholder text when empty.
    /// </summary>
    public Sync<string> PlaceholderText { get; private set; }

    /// <summary>
    /// Maximum character count (0 = unlimited).
    /// </summary>
    public Sync<int> CharacterLimit { get; private set; }

    /// <summary>
    /// Content type for validation.
    /// </summary>
    public Sync<TextContentType> ContentType { get; private set; }

    // ===== PROPERTIES =====

    /// <summary>
    /// Whether the field accepts input.
    /// </summary>
    public Sync<bool> Interactable { get; private set; }

    /// <summary>
    /// Whether the field is read-only.
    /// </summary>
    public Sync<bool> ReadOnly { get; private set; }

    /// <summary>
    /// Reference to the text display component.
    /// </summary>
    public SyncRef<HelioText> TextComponent { get; private set; }

    /// <summary>
    /// Reference to the placeholder text component.
    /// </summary>
    public SyncRef<HelioText> PlaceholderComponent { get; private set; }

    /// <summary>
    /// Reference to the background panel.
    /// </summary>
    public SyncRef<HelioPanel> Background { get; private set; }

    /// <summary>
    /// Background color when focused.
    /// </summary>
    public Sync<color> FocusedColor { get; private set; }

    /// <summary>
    /// Normal background color.
    /// </summary>
    public Sync<color> NormalColor { get; private set; }

    // ===== EVENTS =====

    /// <summary>
    /// Invoked when the text changes.
    /// </summary>
    public SyncDelegate<Action<string>> OnValueChanged { get; private set; }

    /// <summary>
    /// Invoked when Enter is pressed.
    /// </summary>
    public SyncDelegate<Action<string>> OnSubmit { get; private set; }

    /// <summary>
    /// Invoked when the field gains focus.
    /// </summary>
    public SyncDelegate<Action> OnSelect { get; private set; }

    /// <summary>
    /// Invoked when the field loses focus.
    /// </summary>
    public SyncDelegate<Action> OnDeselect { get; private set; }

    // ===== INTERACTION STATE =====

    private bool _isHovered;
    private bool _isPressed;
    private bool _isFocused;
    private int _caretPosition;

    public bool IsInteractable => Interactable?.Value ?? true;
    public bool IsHovered => _isHovered;
    public bool IsPressed => _isPressed;
    public bool IsFocused => _isFocused;
    public bool IsEditing => _isFocused && !ReadOnly.Value;
    public int CaretPosition => _caretPosition;

    // ===== IHelioInteractable EVENTS =====

    public event Action<HelioPointerEventData> OnPointerEnter;
    public event Action<HelioPointerEventData> OnPointerExit;
    public event Action<HelioPointerEventData> OnPointerDown;
    public event Action<HelioPointerEventData> OnPointerUp;
    public event Action<HelioPointerEventData> OnPointerClick;

    // ===== INITIALIZATION =====

    public override void OnAwake()
    {
        base.OnAwake();

        Text = new Sync<string>(this, "");
        PlaceholderText = new Sync<string>(this, "Enter text...");
        CharacterLimit = new Sync<int>(this, 0);
        ContentType = new Sync<TextContentType>(this, TextContentType.Standard);
        Interactable = new Sync<bool>(this, true);
        ReadOnly = new Sync<bool>(this, false);
        NormalColor = new Sync<color>(this, new color(0.15f, 0.15f, 0.15f, 1f));
        FocusedColor = new Sync<color>(this, new color(0.2f, 0.2f, 0.25f, 1f));

        TextComponent = new SyncRef<HelioText>(this);
        PlaceholderComponent = new SyncRef<HelioText>(this);
        Background = new SyncRef<HelioPanel>(this);

        OnValueChanged = new SyncDelegate<Action<string>>(this);
        OnSubmit = new SyncDelegate<Action<string>>(this);
        OnSelect = new SyncDelegate<Action>(this);
        OnDeselect = new SyncDelegate<Action>(this);

        // Update display when text changes
        Text.OnChanged += _ =>
        {
            UpdateDisplay();
            OnValueChanged?.Invoke(Text.Value);
        };
    }

    // ===== POINTER EVENT HANDLERS =====

    public void HandlePointerEnter(HelioPointerEventData eventData)
    {
        if (!IsInteractable) return;
        _isHovered = true;
        OnPointerEnter?.Invoke(eventData);
    }

    public void HandlePointerExit(HelioPointerEventData eventData)
    {
        _isHovered = false;
        _isPressed = false;
        OnPointerExit?.Invoke(eventData);
    }

    public void HandlePointerDown(HelioPointerEventData eventData)
    {
        if (!IsInteractable) return;
        _isPressed = true;
        OnPointerDown?.Invoke(eventData);
    }

    public void HandlePointerUp(HelioPointerEventData eventData)
    {
        bool wasPressed = _isPressed;
        _isPressed = false;
        OnPointerUp?.Invoke(eventData);

        // Focus on click
        if (wasPressed && _isHovered && IsInteractable)
        {
            Focus();
            OnPointerClick?.Invoke(eventData);
        }
    }

    // ===== FOCUS MANAGEMENT =====

    /// <summary>
    /// Give focus to this text field.
    /// </summary>
    public void Focus()
    {
        if (ReadOnly.Value) return;

        _isFocused = true;
        _caretPosition = Text.Value?.Length ?? 0;
        UpdateDisplay();
        OnSelect?.Invoke();
    }

    /// <summary>
    /// Remove focus from this text field.
    /// </summary>
    public void Unfocus()
    {
        _isFocused = false;
        UpdateDisplay();
        OnDeselect?.Invoke();
    }

    // ===== TEXT INPUT =====

    /// <summary>
    /// Handle character input.
    /// </summary>
    public void InsertCharacter(char c)
    {
        if (!_isFocused || ReadOnly.Value) return;

        // Validate character based on content type
        if (!IsValidCharacter(c)) return;

        string current = Text.Value ?? "";

        // Check character limit
        if (CharacterLimit.Value > 0 && current.Length >= CharacterLimit.Value)
            return;

        // Insert at caret
        string newText = current.Insert(_caretPosition, c.ToString());
        Text.Value = newText;
        _caretPosition++;
    }

    /// <summary>
    /// Handle text input (paste, etc).
    /// </summary>
    public void InsertText(string text)
    {
        if (!_isFocused || ReadOnly.Value || string.IsNullOrEmpty(text)) return;

        foreach (char c in text)
        {
            InsertCharacter(c);
        }
    }

    /// <summary>
    /// Handle backspace.
    /// </summary>
    public void Backspace()
    {
        if (!_isFocused || ReadOnly.Value) return;

        string current = Text.Value ?? "";
        if (_caretPosition > 0 && current.Length > 0)
        {
            Text.Value = current.Remove(_caretPosition - 1, 1);
            _caretPosition--;
        }
    }

    /// <summary>
    /// Handle delete key.
    /// </summary>
    public void Delete()
    {
        if (!_isFocused || ReadOnly.Value) return;

        string current = Text.Value ?? "";
        if (_caretPosition < current.Length)
        {
            Text.Value = current.Remove(_caretPosition, 1);
        }
    }

    /// <summary>
    /// Handle enter/submit.
    /// </summary>
    public void Submit()
    {
        OnSubmit?.Invoke(Text.Value);
    }

    /// <summary>
    /// Move caret left.
    /// </summary>
    public void MoveCaretLeft()
    {
        if (_caretPosition > 0)
            _caretPosition--;
    }

    /// <summary>
    /// Move caret right.
    /// </summary>
    public void MoveCaretRight()
    {
        int len = Text.Value?.Length ?? 0;
        if (_caretPosition < len)
            _caretPosition++;
    }

    // ===== VALIDATION =====

    private bool IsValidCharacter(char c)
    {
        switch (ContentType.Value)
        {
            case TextContentType.Number:
                return char.IsDigit(c) || c == '.' || c == '-';
            case TextContentType.Integer:
                return char.IsDigit(c) || c == '-';
            case TextContentType.Alphanumeric:
                return char.IsLetterOrDigit(c);
            case TextContentType.Email:
                return char.IsLetterOrDigit(c) || c == '@' || c == '.' || c == '_' || c == '-';
            default:
                return true;
        }
    }

    // ===== DISPLAY =====

    private void UpdateDisplay()
    {
        // Update text component
        var textComp = TextComponent?.Target;
        if (textComp != null)
        {
            string displayText = Text.Value ?? "";
            if (ContentType.Value == TextContentType.Password)
            {
                displayText = new string('•', displayText.Length);
            }
            textComp.Content.Value = displayText;
        }

        // Update placeholder visibility
        var placeholder = PlaceholderComponent?.Target;
        if (placeholder != null)
        {
            bool showPlaceholder = string.IsNullOrEmpty(Text.Value);
            var placeholderColor = placeholder.Color.Value;
            placeholderColor.a = showPlaceholder ? 0.5f : 0f;
            placeholder.Color.Value = placeholderColor;
        }

        // Update background color
        var background = Background?.Target;
        if (background != null)
        {
            background.BackgroundColor.Value = _isFocused
                ? (FocusedColor?.Value ?? new color(0.2f, 0.2f, 0.25f, 1f))
                : (NormalColor?.Value ?? new color(0.15f, 0.15f, 0.15f, 1f));
        }
    }

    /// <summary>
    /// Set the text value programmatically.
    /// </summary>
    public void SetText(string text)
    {
        Text.Value = text ?? "";
        _caretPosition = Text.Value.Length;
    }

    /// <summary>
    /// Clear the text field.
    /// </summary>
    public void Clear()
    {
        Text.Value = "";
        _caretPosition = 0;
    }
}
