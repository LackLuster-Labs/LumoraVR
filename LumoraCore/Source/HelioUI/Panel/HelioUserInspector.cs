using System.Linq;
using Lumora.Core.Math;

namespace Lumora.Core.HelioUI;

/// <summary>
/// User Inspector panel - shows user list on left and user details on right.
/// </summary>
[ComponentCategory("HelioUI/Inspector")]
public class HelioUserInspector : HelioInspectorPanel
{
    public SyncRef<User> ViewUser { get; private set; }

    private SyncRef<Slot> _userListContentRoot;
    private SyncRef<Slot> _detailsContentRoot;
    private SyncRef<HelioText> _headerText;

    public override void OnAwake()
    {
        base.OnAwake();
        ViewUser = new SyncRef<User>(this);
        _userListContentRoot = new SyncRef<Slot>(this);
        _detailsContentRoot = new SyncRef<Slot>(this);
        _headerText = new SyncRef<HelioText>(this);
    }

    public override void OnStart()
    {
        base.OnStart();
        BuildInspector();
    }

    private void BuildInspector()
    {
        var panel = Setup(
            HelioUITheme.AccentPrimary,
            HelioUITheme.AccentPrimary.Lighten(0.1f),
            out var hierarchyHeader,
            out var hierarchyContent,
            out var detailHeader,
            out var detailContent,
            out var detailFooter);

        panel.AddCloseButton();
        panel.AddParentButton();
        panel.Title.Value = "User Inspector";

        var leftUi = new HelioUIBuilder(hierarchyHeader.Slot);
        leftUi.Text("Users", fontSize: 18f, textColor: HelioUITheme.TextPrimary);
        _userListContentRoot.Target = hierarchyContent.Slot;

        var rightUi = new HelioUIBuilder(detailHeader.Slot);
        _headerText.Target = rightUi.Text("User: --", fontSize: 18f, textColor: HelioUITheme.TextPrimary);
        rightUi.FlexibleSpacer();
        rightUi.Button("User", HelioUITheme.AccentCyan, HelioUITheme.TextBlack, OnViewUser);
        rightUi.Button("Copy", HelioUITheme.AccentGreen, HelioUITheme.TextBlack, OnCopyInfo);

        _detailsContentRoot.Target = detailContent.Slot;

        var footerUi = new HelioUIBuilder(detailFooter.Slot);
        footerUi.Button("Refresh", OnRefresh);

        BuildUserList();
    }

    private void BuildUserList()
    {
        var root = _userListContentRoot.Target;
        if (root == null) return;

        foreach (var child in root.Children.ToList())
        {
            child.Destroy();
        }

        var ui = new HelioUIBuilder(root);
        ui.VerticalLayout(spacing: 4f);

        foreach (var user in World?.GetAllUsers() ?? Enumerable.Empty<User>())
        {
            var userName = user.UserName?.Value ?? "Unknown";
            var isHost = user.IsHost ? " [Host]" : "";
            var isLocal = user == World?.LocalUser ? " (You)" : "";

            var btn = ui.Button($"{userName}{isHost}{isLocal}", () => SelectUser(user));
            if (user == World?.LocalUser)
            {
                btn.NormalColor.Value = HelioUITheme.AccentGreen.WithAlpha(0.3f);
            }
        }

        ui.EndLayout();
    }

    private void SelectUser(User user)
    {
        ViewUser.Target = user;
        UpdateDetails();
    }

    private void UpdateDetails()
    {
        var user = ViewUser.Target;
        var root = _detailsContentRoot.Target;
        if (root == null) return;

        foreach (var child in root.Children.ToList())
        {
            child.Destroy();
        }

        if (_headerText.Target != null)
        {
            _headerText.Target.Content.Value = $"User: {user?.UserName?.Value ?? "--"}";
        }

        if (user == null) return;

        var ui = new HelioUIBuilder(root);
        ui.VerticalLayout(spacing: 8f, padding: new float4(8f, 8f, 8f, 8f));

        ui.Text($"Username: {user.UserName.Value}", fontSize: 16f);
        ui.Text($"User ID: {user.UserID.Value}", fontSize: 14f, textColor: HelioUITheme.TextSecondary);
        ui.Text($"Reference ID: {user.ReferenceID}", fontSize: 12f, textColor: HelioUITheme.TextLabel);

        ui.Spacer(12f);

        ui.Text("Status:", fontSize: 16f, textColor: HelioUITheme.AccentCyan);
        ui.Text($"  Is Host: {(user.IsHost ? "Yes" : "No")}", fontSize: 14f);
        ui.Text($"  Is Present: {(user.IsPresent.Value ? "Yes" : "No")}", fontSize: 14f);
        ui.Text($"  Ping: {user.Ping.Value:F0}ms", fontSize: 14f);

        ui.Spacer(12f);

        ui.Text("Network:", fontSize: 16f, textColor: HelioUITheme.AccentPrimary);
        ui.Text($"  Sent: {FormatBytes(user.SentBytes)}", fontSize: 14f);
        ui.Text($"  Received: {FormatBytes(user.ReceivedBytes)}", fontSize: 14f);

        ui.EndLayout();
    }

    private string FormatBytes(ulong bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }

    private void OnViewUser() { }
    private void OnCopyInfo() { }
    private void OnRefresh() { BuildUserList(); UpdateDetails(); }
}
