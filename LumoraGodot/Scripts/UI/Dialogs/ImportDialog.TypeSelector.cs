// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using System;
using System.Collections.Generic;
using Lumora.Core.Assets;

namespace Lumora.Godot.UI;

/// <summary>
/// Partial class: option button creation and extension-based filtering.
/// </summary>
public partial class ImportDialog
{
    private static readonly string[] ModelImportExtensions = ModelImporter.SupportedExtensions;
    private static readonly string[] AvatarImportExtensions = new[]
    {
        ".vrm",
        ".glb",
        ".gltf",
        ".fbx",
        ".dae"
    };

    private static readonly (ImportType type, string label, string[] extensions)[] ImportOptions =
    {
        (ImportType.ImageTexture, "Image / Texture",   new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tga" }),
        (ImportType.Model3D,      "3D Model",          ModelImportExtensions),
        (ImportType.Avatar,       "Avatar",            AvatarImportExtensions),
        (ImportType.Shader,       "Custom Shader",     new[] { ".gdshader" }),
        (ImportType.RawFile,      "Raw File",          Array.Empty<string>()),
    };

    /// <summary>
    /// Create option buttons that match the given file extension.
    /// Falls back to all options when only one type (Raw File) would match.
    /// </summary>
    private void CreateOptionButtonsForFile(string extension)
    {
        if (_optionsList == null) return;

        ClearOptionButtons();

        var matching = new List<(ImportType type, string label)>();
        foreach (var option in ImportOptions)
        {
            // Raw File is always available.
            if (option.type == ImportType.RawFile)
            {
                matching.Add((option.type, option.label));
                continue;
            }

            foreach (var ext in option.extensions)
            {
                if (ext.Equals(extension, StringComparison.OrdinalIgnoreCase))
                {
                    matching.Add((option.type, option.label));
                    break;
                }
            }
        }

        // If only Raw File matched, show everything.
        if (matching.Count <= 1)
        {
            CreateAllOptionButtons();
            return;
        }

        foreach (var (type, label) in matching)
            CreateOptionButton(type, label);
    }

    private void CreateAllOptionButtons()
    {
        ClearOptionButtons();
        foreach (var option in ImportOptions)
            CreateOptionButton(option.type, option.label);
    }

    private void CreateOptionButton(ImportType type, string label)
    {
        if (_optionsList == null) return;

        Button button = _optionButtonScene != null
            ? _optionButtonScene.Instantiate<Button>()
            : new Button { CustomMinimumSize = new Vector2(0, 36) };

        button.Text = label;
        _optionsList.AddChild(button);

        var capturedType = type;
        button.Connect("pressed", Callable.From(() => OnOptionSelected(capturedType)));
        _optionButtons.Add(button);
    }

    private void ClearOptionButtons()
    {
        foreach (var btn in _optionButtons)
            btn.QueueFree();
        _optionButtons.Clear();
    }
}
