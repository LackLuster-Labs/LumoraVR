using LumoraVR.Source.Input;
using LumoraVR.Source.Management;
using LumoraVR.Source.Logging;
using System.Collections.Generic;
using Godot;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using LumoraVR.Source.Helpers;
namespace LumoraVR.Source.Management.HUD;
/// <summary>
/// Enhanced main menu with account login functionality
/// </summary>
public partial class MainMenu : Control
{
    [Export] public Button CloseButton;
    private static readonly Dictionary<string, Node> Tabs = new();
    [Signal]
    /// <summary> Signal emitted when the active tab is changed </summary>
    public delegate void TabChangedEventHandler(string name);
    /// <summary>
    /// Returns the tab with the given name
    /// </summary>
    /// <param name="name"></param>
    /// <returns></returns>
    public Node GetTab(string name)
    {
        return Tabs.TryGetValue(name, out var tab) ? tab : default;
    }
    /// <summary>
    /// Changes the active tab to the one with the given name
    /// </summary>
    /// <param name="name"></param>
    public void ChangeTab(string name)
    {
        Node node = GetNode("%MainPanelContent");
        if (node is PanelContainer)
        {
            for (int i = 0; i < node.GetChildCount(); i++)
            {
                node.RemoveChild(node.GetChild(i));
            }
            node.AddChild(GetTab(name));
            EmitSignal(SignalName.TabChanged, name);
        }
    }
    /// <summary>
    /// Registers a node as a tab with the given name
    /// </summary>
    /// <param name="name"></param>
    /// <param name="tab"></param>
    public void AddTab(string name, Node tab)
    {
        Tabs.Add(name, tab);
    }
    /// <summary>
    /// Adds a tab with the given name by resource URI
    /// </summary>
    /// <param name="name"></param>
    /// <param name="resourceURI"></param>
    public void AddTab(string name, string resourceURI)
    {
        try
        {
            PackedScene packed = ResourceLoader.Load<PackedScene>(resourceURI);
            Node tab = packed.Instantiate();
            Tabs.Add(name, tab);
        }
        catch (System.Exception e)
        {
            GD.PrintErr("Failed to load tab: " + name + " from URI: " + resourceURI);
            GD.PrintErr(e.Message);
        }
    }
    private void LoadTabs()
    {
        // load the worlds tab 
        AddTab("Worlds", "res://Scenes/UI/Manu/WorldTab/WorldsTab.tscn");
        AddTab("Login", "res://Scenes/UI/login_ui.tscn");
    }
    /// <summary>
    /// Reinstantiates all default tabs
    /// </summary>
    public void ReloadTabs()
    {
        foreach (var item in Tabs)
        {
            Tabs.Remove(item.Key);
            item.Value.QueueFree();
        }
        LoadTabs();
    }
    // We'll need to reference the account button in the sidebar
    [Export] public Button AccountButton;

    // Reference main content area to hide when showing login UI
    [Export] public Control MainPanel;

    public override void _Ready()
    {
        base._Ready();
        CloseButton.Pressed += ToggleMenu;
        Visible = false;
        LoadTabs();
        ChangeTab("Worlds");
        // Find the account button by path if not explicitly set
        if (AccountButton is null)
        {
            AccountButton = GetNode<Button>("%AccountsButton");
        }
    }
    public override void _EnterTree()
    {
        base._EnterTree();
        // Listen for login status changes to update the account button text
        if (LoginManager.Instance is not null)
        {
            LoginManager.Instance.OnLoginStatusChanged += UpdateAccountButtonText;
        }
        // shitty workaround do it later
        else
        {
            Task.Delay(1000).ContinueWith((task) =>
            {
                if (LoginManager.Instance is not null)
                {
                    LoginManager.Instance.OnLoginStatusChanged += UpdateAccountButtonText;
                    this.RunOnNodeAsync(() => { UpdateAccountButtonText(LoginManager.Instance.IsLoggedIn); });
                }
                else { Debugger.Break(); }
            });
        }
    }
    // Clean up out of tree nodes when the main menu is removed
    public override void _Notification(int what)
    {
        base._Notification(what);
        if (what == NotificationPredelete)
        {
            foreach (var item in Tabs)
            {
                Tabs.Remove(item.Key);
                item.Value.QueueFree();
            }
        }
    }

    public override void _ExitTree()
    {
        // Clean up event listener
        if (LoginManager.Instance is not null)
        {
            LoginManager.Instance.OnLoginStatusChanged -= UpdateAccountButtonText;
        }

        base._ExitTree();
    }

    /// <summary>
    /// Shows or hides the main menu
    /// </summary>
    void ToggleMenu()
    {
        InputManager.MovementLocked = !InputManager.MovementLocked;
        Visible = InputManager.MovementLocked;

    }

    /// <summary>
    /// Shows the login UI when the account button is pressed
    /// </summary>
    void ShowLoginUI()
    {
        if (!LoginManager.Instance?.IsLoggedIn ?? true)
        {
            ChangeTab("Login");
            return;
        }
        //fix this
        ChangeTab("Login");
    }

    /// <summary>
    /// Hides the login UI and shows the main content
    /// </summary>
    public void HideLoginUI()
    {
        Node login = GetTab("Login");
        login.GetParent().RemoveChild(login);
    }

    /// <summary>
    /// Updates the account button text based on login status
    /// </summary>
    public void UpdateAccountButtonText(bool isLoggedIn)
    {
        if (AccountButton is null) return;

        // If we have a RichTextLabelAutoSizeNode as a child of the button, update its text
        var textNode = AccountButton.GetNode<Control>("RichTextLabelAutoSizeNode");
        if (textNode is null) return;

        var textLabel = textNode.GetNode<RichTextLabel>("AccountsButtonLabel");
        if (textLabel is null) return;

        // Update the text based on login status
        if (isLoggedIn && LoginManager.Instance != null)
        {
            var username = LoginManager.Instance.GetCurrentUsername();
            textLabel.Text = $"[center]{username} 👤[/center]";
        }
        else
        {
            textLabel.Text = "[center]Account 💻[/center]";
        }
    }
}
