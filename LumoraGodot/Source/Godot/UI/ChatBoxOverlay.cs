// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using Lumora.Core.Components.Chat;
using Lumora.Godot.Hooks;
using LumoraEngine = Lumora.Core.Engine;

namespace Lumora.Source.Godot.UI;

// The chat box's capture laid over the window in the bottom left corner, the same way the dash's capture is
// laid over all of it: a canvas item composites after the 3D frame with no camera in the path, so the box
// cannot trail or swim against the view. It is sized off the window height, so it reads the same at any
// resolution, and hides outright in VR. -xlinka
public partial class ChatBoxOverlay : CanvasLayer
{
    // Under the world-loading line (99), the dash composite (100) and the cursor (101).
    private const int OverlayLayer = 98;
    private const string CompositeShader = "res://Shaders/ChatBoxScreen.gdshader";
    private const float DesignHeight = 1080f;
    // A Deck's 7-inch screen holds 800 lines: the box goes on at its design size there, so its smallest text
    // is 13 pixels tall rather than 9.
    private const float HandheldDesignHeight = 800f;
    private const float Margin = 18f;

    private TextureRect _view = null!;
    private ShaderMaterial _material = null!;
    private Texture2D? _boundTexture;
    private float _appliedOpacity = -1f;

    public override void _Ready()
    {
        Layer = OverlayLayer;
        Visible = false;

        _material = new ShaderMaterial { Shader = GD.Load<Shader>(CompositeShader) };
        _view = new TextureRect
        {
            Name = "ChatView",
            StretchMode = TextureRect.StretchModeEnum.Scale,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Material = _material,
        };
        AddChild(_view);
    }

    // Called once per frame from the runner, next to the dash overlay's tick.
    public void Tick(LumoraEngine? engine)
    {
        var box = DesktopChatBox.LocalInstance;
        var texture = ResolveTexture(engine, box);
        bool show = texture != null;

        // Re-read every tick, as the dash overlay does: a rebuilt viewport hands back a new texture.
        if (!ReferenceEquals(_boundTexture, texture))
        {
            _boundTexture = texture;
            _view.Texture = texture;
        }

        if (Visible != show)
            Visible = show;
        if (!show)
            return;

        Place(box!);
        float opacity = box!.Opacity;
        if (opacity != _appliedOpacity)
        {
            _appliedOpacity = opacity;
            _material.SetShaderParameter("opacity", opacity);
        }
    }

    private static Texture2D? ResolveTexture(LumoraEngine? engine, DesktopChatBox? box)
    {
        if (engine == null || box == null || box.IsDestroyed || !box.IsShown)
            return null;

        var input = engine.InputInterface;
        if (input == null || input.IsVRActive)
            return null;

        var capture = box.RenderTextureSource?.Asset;
        if (capture?.Hook is not IGodotTexture hook || !hook.IsValid)
            return null;

        return hook.GodotTexture2D;
    }

    private void Place(DesktopChatBox box)
    {
        var viewport = GetViewport();
        if (viewport == null)
            return;

        // The visible rect is in canvas units, which the window's stretch already scales to its pixels, so a
        // share of its height is the same share of the window's at any size.
        var window = viewport.GetVisibleRect().Size;
        float design = Lumora.Core.SteamDeckPlatform.IsSteamDeck ? HandheldDesignHeight : DesignHeight;
        float scale = Mathf.Clamp(window.Y / design, 0.3f, 3f);
        var canvas = box.CanvasSize;
        var size = new Vector2(canvas.x * scale, canvas.y * scale);
        var position = new Vector2(Margin * scale, window.Y - Margin * scale - size.Y);
        if (_view.Size != size)
            _view.Size = size;
        if (_view.Position != position)
            _view.Position = position;
        // The cursor's ray finds the box by where it is on the window (ChatBoxPortal).
        if (window.X > 0f && window.Y > 0f)
            box.SetScreenRect(position.X / window.X, position.Y / window.Y, size.X / window.X, size.Y / window.Y);
    }
}
