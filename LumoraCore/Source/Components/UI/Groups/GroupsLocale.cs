// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Localization;

namespace Lumora.Core.Components.UI;

// Every string the Groups window can put on screen, in one place, so a locale sweep can walk it by
// reflection and prove nothing ships a key with no English behind it.
//
// The wording stays out of the service's vocabulary. Nobody knows what "visibility: Private" means the
// first time they read it, so the chip says what happens instead: Open, Request, Invite only. -xlinka
public static class GroupsLocale
{
    public static readonly LocaleText Title = "Groups.Title".AsLocale("Groups");
    public static readonly LocaleText Loading = "Groups.Loading".AsLocale("Loading");
    public static readonly LocaleText LoadFailed = "Groups.LoadFailed".AsLocale("Could not load that.");
    public static readonly LocaleText ActionFailed = "Groups.ActionFailed".AsLocale("That did not go through.");

    public static readonly LocaleText TabBrowse = "Groups.Tab.Browse".AsLocale("Browse");
    public static readonly LocaleText TabMine = "Groups.Tab.Mine".AsLocale("Mine");
    public static readonly LocaleText TabAbout = "Groups.Tab.About".AsLocale("About");
    public static readonly LocaleText TabPosts = "Groups.Tab.Posts".AsLocale("Posts");
    public static readonly LocaleText TabAdmin = "Groups.Tab.Admin".AsLocale("Admin");
    public static readonly LocaleText NewGroup = "Groups.NewGroup".AsLocale("New group");
    public static readonly LocaleText SignIn = "Groups.SignIn".AsLocale("Sign in");
    public static readonly LocaleText SignedOutBody = "Groups.SignedOut.Body".AsLocale(
        "A group is a shared home for the people you play with: one page, members with roles, announcements and events, and worlds hosted for its members.");
    public static readonly LocaleText SignedOutAfter = "Groups.SignedOut.After".AsLocale(
        "Once you are signed in, this page shows the groups you are in, the invites waiting for you and the public groups you can join.");
    public static readonly LocaleText SearchHint = "Groups.Search.Hint".AsLocale("Search groups");
    public static readonly LocaleText PickGroup = "Groups.PickGroup".AsLocale("Pick a group to see its page.");
    public static readonly LocaleText PickGroupHint = "Groups.PickGroup.Hint".AsLocale("Or start your own with New group.");

    public static readonly LocaleText BrowseEmpty = "Groups.Browse.Empty".AsLocale("No groups to show.");
    public static readonly LocaleText MineEmpty =
        "Groups.Mine.Empty".AsLocale("You are not in a group yet. Browse to find one.");
    public static readonly LocaleText Invites = "Groups.Invites".AsLocale("Invites");
    public static readonly LocaleText Invited = "Groups.Invited".AsLocale("Invited");
    public static readonly LocaleText SectionGroups = "Groups.Section.Groups".AsLocale("Groups");
    public static readonly LocaleText SectionYours = "Groups.Section.Yours".AsLocale("Your groups");
    public static readonly LocaleText Accept = "Groups.Accept".AsLocale("Accept");
    public static readonly LocaleText Decline = "Groups.Decline".AsLocale("Decline");

    public static readonly LocaleText VisibilityOpen = "Groups.Visibility.Open".AsLocale("Open");
    public static readonly LocaleText VisibilityRequest = "Groups.Visibility.Request".AsLocale("Request");
    public static readonly LocaleText VisibilityInvite = "Groups.Visibility.Invite".AsLocale("Invite only");

    public static readonly LocaleText Join = "Groups.Join".AsLocale("Join");
    public static readonly LocaleText RequestToJoin = "Groups.Request".AsLocale("Request");
    public static readonly LocaleText Member = "Groups.Member".AsLocale("Member");
    public static readonly LocaleText Leave = "Groups.Leave".AsLocale("Leave");
    public static readonly LocaleText Represent = "Groups.Represent".AsLocale("Represent this group");
    public static readonly LocaleText Represented = "Groups.Represented".AsLocale("Represented");
    public static readonly LocaleText You = "Groups.You".AsLocale("you");

    public static readonly LocaleText CreateName = "Groups.Create.Name".AsLocale("Group name");
    public static readonly LocaleText CreateNameHint = "Groups.Create.NameHint".AsLocale("Name your group");
    public static readonly LocaleText Create = "Groups.Create".AsLocale("Create");
    public static readonly LocaleText Cancel = "Groups.Cancel".AsLocale("Cancel");
    public static readonly LocaleText NameEmpty = "Groups.Create.Empty".AsLocale("A group needs a name.");

    public static readonly LocaleText SectionAbout = "Groups.Section.About".AsLocale("About");
    public static readonly LocaleText SectionDetails = "Groups.Section.Details".AsLocale("Details");
    public static readonly LocaleText SectionInfo = "Groups.Section.Info".AsLocale("Group info");
    public static readonly LocaleText SectionPictures = "Groups.Section.Pictures".AsLocale("Pictures");
    public static readonly LocaleText FieldIcon = "Groups.Field.Icon".AsLocale("Icon");
    public static readonly LocaleText FieldBanner = "Groups.Field.Banner".AsLocale("Banner");
    public static readonly LocaleText IconNone = "Groups.Icon.None".AsLocale("No icon yet");
    public static readonly LocaleText PickIconTitle = "Groups.Pick.Icon".AsLocale("Choose an icon");
    public static readonly LocaleText SectionEvents = "Groups.Section.Events".AsLocale("Upcoming events");
    public static readonly LocaleText SectionAnnouncements =
        "Groups.Section.Announcements".AsLocale("Announcements");
    public static readonly LocaleText SectionMembers = "Groups.Section.Members".AsLocale("Members");
    public static readonly LocaleText SectionRequests = "Groups.Section.Requests".AsLocale("Requests");
    public static readonly LocaleText SectionInvites = "Groups.Section.Invites".AsLocale("Invites");
    public static readonly LocaleText SectionBans = "Groups.Section.Bans".AsLocale("Bans");
    public static readonly LocaleText SectionDanger = "Groups.Section.Danger".AsLocale("Danger");

    public static readonly LocaleText NoBio = "Groups.NoBio".AsLocale("No description yet.");
    public static readonly LocaleText FactRole = "Groups.Fact.Role".AsLocale("Your role");
    public static readonly LocaleText FactStarted = "Groups.Fact.Started".AsLocale("Started");

    public static readonly LocaleText EventsEmpty = "Groups.Events.Empty".AsLocale("Nothing planned.");
    public static readonly LocaleText AnnouncementsEmpty =
        "Groups.Announcements.Empty".AsLocale("Nothing posted yet.");
    public static readonly LocaleText MembersEmpty = "Groups.Members.Empty".AsLocale("No members listed.");
    public static readonly LocaleText RequestsEmpty = "Groups.Requests.Empty".AsLocale("Nobody waiting.");
    public static readonly LocaleText InvitesEmpty = "Groups.Invites.Empty".AsLocale("No invites out.");
    public static readonly LocaleText BansEmpty = "Groups.Bans.Empty".AsLocale("Nobody banned.");

    public static readonly LocaleText Post = "Groups.Post".AsLocale("Post");
    public static readonly LocaleText Delete = "Groups.Delete".AsLocale("Delete");
    public static readonly LocaleText AnnouncementHint =
        "Groups.Announcement.Hint".AsLocale("Say something to the group");
    public static readonly LocaleText EventTitle = "Groups.Event.Title".AsLocale("Event");
    public static readonly LocaleText EventTitleHint = "Groups.Event.TitleHint".AsLocale("What is happening");
    public static readonly LocaleText EventWorld = "Groups.Event.World".AsLocale("World");
    public static readonly LocaleText EventWorldHint = "Groups.Event.WorldHint".AsLocale("Where");
    public static readonly LocaleText EventDate = "Groups.Event.Date".AsLocale("Date");
    public static readonly LocaleText EventDateHint = "Groups.Event.DateHint".AsLocale("2026-01-31");
    public static readonly LocaleText EventTime = "Groups.Event.Time".AsLocale("Start");
    public static readonly LocaleText EventTimeHint = "Groups.Event.TimeHint".AsLocale("19:30");
    public static readonly LocaleText EventBadDate =
        "Groups.Event.BadDate".AsLocale("That date and time did not read.");
    public static readonly LocaleText EventNoTitle = "Groups.Event.NoTitle".AsLocale("An event needs a title.");
    public static readonly LocaleText AnnouncementEmpty =
        "Groups.Announcement.Empty".AsLocale("Nothing typed yet.");

    public static readonly LocaleText FieldName = "Groups.Field.Name".AsLocale("Name");
    public static readonly LocaleText FieldTag = "Groups.Field.Tag".AsLocale("Tag");
    public static readonly LocaleText FieldTagHint = "Groups.Field.TagHint".AsLocale("2 to 6 characters");
    public static readonly LocaleText FieldBio = "Groups.Field.Bio".AsLocale("Bio");
    public static readonly LocaleText FieldBioHint = "Groups.Field.BioHint".AsLocale("What this group is for");
    public static readonly LocaleText FieldColor = "Groups.Field.Color".AsLocale("Colour");
    public static readonly LocaleText FieldColorHint = "Groups.Field.ColorHint".AsLocale("#6F5FF0");
    public static readonly LocaleText FieldVisibility = "Groups.Field.Visibility".AsLocale("Who can join");
    public static readonly LocaleText FieldWorldAccess = "Groups.Field.WorldAccess".AsLocale("Who can join its worlds");
    public static readonly LocaleText Save = "Groups.Save".AsLocale("Save");
    public static readonly LocaleText NothingToSave = "Groups.NothingToSave".AsLocale("Nothing changed.");

    public static readonly LocaleText InviteById = "Groups.Invite.ById".AsLocale("Invite by user id");
    public static readonly LocaleText InviteHint = "Groups.Invite.Hint".AsLocale("U-...");
    public static readonly LocaleText Invite = "Groups.Invite".AsLocale("Invite");
    public static readonly LocaleText Withdraw = "Groups.Withdraw".AsLocale("Withdraw");
    public static readonly LocaleText Approve = "Groups.Approve".AsLocale("Approve");
    public static readonly LocaleText Deny = "Groups.Deny".AsLocale("Deny");
    public static readonly LocaleText Kick = "Groups.Kick".AsLocale("Kick");
    public static readonly LocaleText Ban = "Groups.Ban".AsLocale("Ban");
    public static readonly LocaleText Unban = "Groups.Unban".AsLocale("Unban");
    public static readonly LocaleText MakeOwner = "Groups.MakeOwner".AsLocale("Make owner");
    public static readonly LocaleText DeleteGroup = "Groups.DeleteGroup".AsLocale("Delete group");

    public static LocaleText LeaveTitle(string group) => "Groups.Ask.Leave".AsLocale("Leave {0}?", group);
    public static readonly LocaleText LeaveMessage = "Groups.Ask.Leave.Body".AsLocale(
        "Its members, events and announcements are for members only, so they go from here until you are back in.");
    public static LocaleText KickTitle(string name) => "Groups.Ask.Kick".AsLocale("Remove {0} from the group?", name);
    public static readonly LocaleText KickMessage = "Groups.Ask.Kick.Body".AsLocale(
        "They are taken off the member list now. Nothing stops them asking to join again.");
    public static LocaleText BanTitle(string name) => "Groups.Ask.Ban".AsLocale("Ban {0}?", name);
    public static readonly LocaleText BanMessage = "Groups.Ask.Ban.Body".AsLocale(
        "They are taken off the member list, any request or invite of theirs goes with it, and they cannot join again until unbanned.");
    public static LocaleText OwnerTitle(string name) => "Groups.Ask.Owner".AsLocale("Make {0} the owner?", name);
    public static readonly LocaleText OwnerMessage = "Groups.Ask.Owner.Body".AsLocale(
        "The group passes to them. Only the owner can pass it on again.");
    public static LocaleText DeleteTitle(string group) => "Groups.Ask.Delete".AsLocale("Delete {0}?", group);
    public static readonly LocaleText DeleteMessage = "Groups.Ask.Delete.Body".AsLocale(
        "The group goes for everyone in it, with its events and announcements. This cannot be undone.");

    public static readonly LocaleText Storage = "Groups.Storage".AsLocale("Storage");
    public static readonly LocaleText StorageLocked = "Groups.Storage.Locked".AsLocale(
        "Group storage is locked: the owner's support lapsed, so no new uploads. Existing content stays.");

    public static LocaleText StorageGrace(string when) => "Groups.Storage.Grace".AsLocale(
        "The owner's support has lapsed. Group storage stays usable until {0}, then it locks.", when);

    public static LocaleText Members(int count) => count == 1
        ? "Groups.Members.One".AsLocale("1 member")
        : "Groups.Members.Count".AsLocale("{0} members", count);

    public static LocaleText StoragePool(string used, string quota, string allocated)
        => "Groups.Storage.Pool".AsLocale("{0} of {1} used, {2} allocated", used, quota, allocated);

    public static LocaleText Used(string value) => "Groups.Storage.Used".AsLocale("{0} used", value);

    public static readonly LocaleText ShareEven = "Groups.Pool.ShareEven".AsLocale("This group's share, split evenly");
    public static readonly LocaleText SharePinned = "Groups.Pool.SharePinned".AsLocale("This group's share, set by you");
    public static readonly LocaleText SplitEvenly = "Groups.Pool.SplitEvenly".AsLocale("Split evenly");
    public static readonly LocaleText ShareNone = "Groups.Pool.None".AsLocale(
        "Your tier comes with no group storage, so there is no share to set.");
    public static readonly LocaleText ShareLocked = "Groups.Pool.Locked".AsLocale(
        "Your group storage is locked, so there is no share to hand out until your support resumes.");

    public static LocaleText OwnerPoolOne(string total, string used) => "Groups.Pool.OwnerOne".AsLocale(
        "Your group storage is {0}, and this is your only group. {1} used.", total, used);

    public static LocaleText OwnerPool(string total, int groups, string used) => "Groups.Pool.Owner".AsLocale(
        "Your group storage is {0}, shared by your {1} groups. {2} used across them.", total, groups, used);

    public static LocaleText HandedOut(string value) => "Groups.Pool.HandedOut".AsLocale("{0} handed to members", value);

    public static readonly LocaleText HostEventWorld = "Groups.Event.HostWorld".AsLocale("Host");
    public static readonly LocaleText EventLive = "Groups.Event.Live".AsLocale("Live");

    public static LocaleText HostedBy(string name) => "Groups.Event.Host".AsLocale("Hosted by {0}", name);

    public static LocaleText PostedBy(string name, string when)
        => "Groups.Announcement.By".AsLocale("{0}, {1}", name, when);

    public static LocaleText BannedFor(string reason, string when)
        => "Groups.Ban.Reason".AsLocale("{0}, {1}", reason, when);

    public static LocaleText BannedOn(string when) => "Groups.Ban.When".AsLocale("Banned {0}", when);

    public static LocaleText InvitedOn(string when) => "Groups.Invited.When".AsLocale("Invited {0}", when);

    public static LocaleText AskedOn(string when) => "Groups.Asked.When".AsLocale("Asked {0}", when);

    public static LocaleText JoinedOn(string when) => "Groups.Joined.When".AsLocale("Joined {0}", when);
}
