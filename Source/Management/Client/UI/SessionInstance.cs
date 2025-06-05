using LumoraVR.Source.Management;
using Godot;
using System;
using System.Data;
namespace LumoraVR.Source.Management.Client.UI;
public partial class SessionInstance : HBoxContainer
{
    private RichTextLabel _detailsText;
    private RichTextLabel _sessionUsersLabel;
    private string _id;
    private WorldsTab _tab;
    public override void _Ready()
    {
        base._Ready();
        _detailsText = GetNode("%DetailsText") as RichTextLabel;
        _sessionUsersLabel = GetNode("%PlayersText") as RichTextLabel;
    }
    /// <summary>
    /// Updates the session instance with the given session info
    /// </summary>
    /// <param name="info"></param>
    /// <param name="tab"></param>
    internal void UpdateData(SessionInfo info, WorldsTab tab)
    {
        _id = info.SessionIdentifier;
#if DEBUG
        _detailsText.Text = info.SessionIdentifier;
#else
        _detailsText.Text = info.Name; // Changed from SessionName to Name
#endif
        _tab = tab;
    }
    public void OnJoinButtonPressed()
    {
        if (_id is null)
            return;
        _tab?.joinSession(_id); // Fixed method call
    }
    public void OnPortalButtonPressed()
    {
    }
}