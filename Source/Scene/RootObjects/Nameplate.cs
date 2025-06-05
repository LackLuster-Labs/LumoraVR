using Godot;
using System;

namespace LumoraVR.Source.Scene.UI
{
    public partial class Nameplate : Node3D
    {
        private SubViewport _viewport;
        private Sprite3D _sprite3D;
        private Label _label;
        private float _maxWidth = 1.0f; // Max width in 3D units

        public override void _Ready()
        {
            _viewport = GetNode<SubViewport>("SubViewport");
            _sprite3D = GetNode<Sprite3D>("Sprite3D");

            // Fixed path - using GetNodeOrNull to avoid crashing if not found
            _label = GetNodeOrNull<Label>("SubViewport/NameplateContainer/MarginContainer/Label");

            // Fallback method to find the label by searching children
            if (_label == null)
            {
                // Search for the Label within the viewport's children
                foreach (var child in _viewport.GetChildren())
                {
                    if (child is Control container)
                    {
                        // Try to find a label in this container or its children
                        var foundLabel = FindLabelInChildren(container);
                        if (foundLabel != null)
                        {
                            _label = foundLabel;
                            break;
                        }
                    }
                }
            }

            // If we still can't find a label, log the error and create one
            if (_label == null)
            {
                GD.PrintErr("Could not find Label in Nameplate. Creating a fallback label.");
                CreateFallbackLabel();
            }

            // High res viewport
            _viewport.Size = new Vector2I(512, 128);

            // Set up the viewport texture
            _sprite3D.Texture = _viewport.GetTexture();

            // Force fixed scale
            _sprite3D.PixelSize = 0.002f; // Smaller pixel size since we're using higher res
            UpdateSpriteSize();
        }

        // Helper method to recursively find a Label in children
        private Label FindLabelInChildren(Node node)
        {
            if (node is Label label)
                return label;

            foreach (var child in node.GetChildren())
            {
                var result = FindLabelInChildren(child);
                if (result != null)
                    return result;
            }

            return null;
        }

        // Create a fallback label if we can't find the original
        private void CreateFallbackLabel()
        {
            var container = new Control();
            container.Name = "NameplateContainer";
            _viewport.AddChild(container);
            container.SetAnchorsPreset(Control.LayoutPreset.FullRect);

            var panel = new Panel();
            panel.Name = "Panel";
            container.AddChild(panel);
            panel.SetAnchorsPreset(Control.LayoutPreset.FullRect);

            var marginContainer = new MarginContainer();
            marginContainer.Name = "MarginContainer";
            panel.AddChild(marginContainer);
            marginContainer.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            marginContainer.AddThemeConstantOverride("margin_left", 32);
            marginContainer.AddThemeConstantOverride("margin_top", 16);
            marginContainer.AddThemeConstantOverride("margin_right", 32);
            marginContainer.AddThemeConstantOverride("margin_bottom", 16);

            _label = new Label();
            _label.Name = "Label";
            marginContainer.AddChild(_label);
            _label.Text = "Player";
            _label.HorizontalAlignment = HorizontalAlignment.Center;
            _label.VerticalAlignment = VerticalAlignment.Center;
            _label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.7f));
            _label.AddThemeConstantOverride("outline_size", 10);
            _label.AddThemeFontSizeOverride("font_size", 72);
        }

        public void SetText(string newText)
        {
            if (_label != null)
            {
                _label.Text = newText;
                // Clamp the sprite scale after setting text
                ClampScale();
            }
            else
            {
                GD.PrintErr("Cannot set text: Label is null in Nameplate");
            }
        }

        private void ClampScale()
        {
            if (_sprite3D != null)
            {
                // Keep height at 1.0, clamp width
                Vector3 currentScale = _sprite3D.Scale;
                _sprite3D.Scale = new Vector3(
                    Mathf.Min(currentScale.X, _maxWidth),
                    1,
                    1
                );
            }
        }

        private void UpdateSpriteSize()
        {
            if (_viewport != null && _sprite3D != null)
            {
                Vector2 viewportSize = _viewport.Size;
                _sprite3D.Scale = new Vector3(
                    viewportSize.X / viewportSize.Y,
                    1,
                    1
                );
                ClampScale();
            }
        }

        public void SetVisible(bool visible)
        {
            Visible = visible;
        }
    }
}
