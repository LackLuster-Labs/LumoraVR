using Godot;
using System;

namespace Lumora.Godot.UI;

/// <summary>
/// World card UI component for displaying world information.
/// </summary>
public partial class WorldCard : Button
{
    private Label? _titleLabel;
    private Label? _hostLabel;
    private Label? _userCountLabel;
    private TextureRect? _thumbnailRect;
    private Label? _noImageLabel;

    public string WorldId { get; set; } = "";
    public string WorldName { get; set; } = "";
    public string HostName { get; set; } = "";
    public int UserCount { get; set; }

    public event Action<WorldCard>? CardPressed;

    public override void _Ready()
    {
        _titleLabel = GetNodeOrNull<Label>("VBox/InfoPanel/VBox/Title");
        _hostLabel = GetNodeOrNull<Label>("VBox/InfoPanel/VBox/Host");
        _userCountLabel = GetNodeOrNull<Label>("VBox/ThumbnailContainer/UserCountBadge/HBox/Count");
        _thumbnailRect = GetNodeOrNull<TextureRect>("VBox/ThumbnailContainer/Thumbnail");
        _noImageLabel = GetNodeOrNull<Label>("VBox/ThumbnailContainer/NoImageLabel");

        Pressed += OnPressed;
    }

    private void OnPressed()
    {
        CardPressed?.Invoke(this);
    }

    public void SetWorldData(string id, string name, string host, int userCount)
    {
        WorldId = id;
        WorldName = name;
        HostName = host;
        UserCount = userCount;

        if (_titleLabel != null) _titleLabel.Text = name;
        if (_hostLabel != null) _hostLabel.Text = $"Host: {host}";
        if (_userCountLabel != null) _userCountLabel.Text = userCount.ToString();
    }

    public void SetThumbnail(Texture2D? thumbnail)
    {
        if (_thumbnailRect != null)
        {
            _thumbnailRect.Texture = thumbnail;
        }
        if (_noImageLabel != null)
        {
            _noImageLabel.Visible = thumbnail == null;
        }
    }
}
