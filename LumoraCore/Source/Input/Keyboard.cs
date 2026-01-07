using System.Collections.Generic;

namespace Lumora.Core.Input;

/// <summary>
/// Keyboard input device.
/// </summary>
public class Keyboard : InputDevice
{
    private HashSet<Key> _pressedKeys = new HashSet<Key>();
    private HashSet<Key> _justPressedKeys = new HashSet<Key>();
    private HashSet<Key> _justReleasedKeys = new HashSet<Key>();
    private string _typedText = string.Empty;

    public Keyboard()
    {
    }

    /// <summary>
    /// Check if a key is currently pressed.
    /// </summary>
    public bool IsKeyPressed(Key key)
    {
        return _pressedKeys.Contains(key);
    }

    /// <summary>
    /// Check if a key was just pressed this frame.
    /// </summary>
    public bool IsKeyJustPressed(Key key)
    {
        return _justPressedKeys.Contains(key);
    }

    /// <summary>
    /// Check if a key was just released this frame.
    /// </summary>
    public bool IsKeyJustReleased(Key key)
    {
        return _justReleasedKeys.Contains(key);
    }

    /// <summary>
    /// Get text typed this frame.
    /// </summary>
    public string GetTypedText()
    {
        return _typedText;
    }

    /// <summary>
    /// Update keyboard state from driver.
    /// </summary>
    public void UpdateFromDriver(HashSet<Key> newPressedKeys, string typedText)
    {
        _justPressedKeys.Clear();
        _justReleasedKeys.Clear();

        // Find newly pressed keys
        foreach (var key in newPressedKeys)
        {
            if (!_pressedKeys.Contains(key))
            {
                _justPressedKeys.Add(key);
            }
        }

        // Find newly released keys
        foreach (var key in _pressedKeys)
        {
            if (!newPressedKeys.Contains(key))
            {
                _justReleasedKeys.Add(key);
            }
        }

        _pressedKeys = new HashSet<Key>(newPressedKeys);
        _typedText = typedText ?? string.Empty;
    }

    /// <summary>
    /// Clear frame-specific input.
    /// </summary>
    public void ClearFrameInput()
    {
        _justPressedKeys.Clear();
        _justReleasedKeys.Clear();
        _typedText = string.Empty;
    }
}