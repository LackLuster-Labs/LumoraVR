using LumoraVR.Source.Management.Client.UI;
using Godot;
using System;
using LumoraVR.Source.Management.Data;
namespace LumoraVR.Source.Management.Client.UI;
public partial class WorldEntry : PanelContainer
{
    private WorldsTab _tab;
    private string _worldid;
    /// <summary>
    /// Assigns the world metadata to the entry
    /// This updates the world name label and the id called by the details button
    /// In the future, this will also update the player count and image
    /// </summary>
    /// <param name="tab"></param>
    /// <param name="worldid"></param>
    public void assignEvent(WorldsTab tab, string worldid)
    {
        _worldid = worldid;
        _tab = tab;
        tab.SessionListUpdated += Tab_SessionListUpdated;
        if (tab.Sessions.TryGetValue(worldid, out var worldEntry))
            GetNode<RichTextLabel>("%WorldNameLabel").Text = $"[center]{worldEntry.WorldName}[/center]";
    }
    /// <summary>
    /// Should be called when the session list is updated
    /// Currently, this is only used to check if the world still exists
    /// </summary>
    private void Tab_SessionListUpdated()
    {
        if (!_tab?.Sessions.ContainsKey(_worldid) ?? true)
        {
            if (_tab is not null)
                _tab.SessionListUpdated -= Tab_SessionListUpdated;
            this.QueueFree();
        }
    }
    public void OnDetailsButtonPressed()
    {
        _tab?.LoadWorldInfo(_worldid);
    }
}
