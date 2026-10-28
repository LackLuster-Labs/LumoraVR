// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Localization;

namespace Lumora.Core.Components.UI;

// Every string the People window can put on screen, so a locale sweep can walk it by reflection and prove
// nothing ships a key with no English behind it.
public static class PeopleLocale
{
    public static readonly LocaleText Title = "People.Title".AsLocale("Friends");
    public static readonly LocaleText Loading = "People.Loading".AsLocale("Loading");
    public static readonly LocaleText LoadFailed = "People.LoadFailed".AsLocale("Could not load that.");
    public static readonly LocaleText ActionFailed = "People.ActionFailed".AsLocale("That did not go through.");
    public static readonly LocaleText Back = "People.Back".AsLocale("Back");

    public static readonly LocaleText SignedOutTitle = "People.SignedOut.Title".AsLocale("Friends");
    public static readonly LocaleText SignedOutBody = "People.SignedOut.Body".AsLocale(
        "Friends see where you are, can join you when your world lets them, and can send you messages and things from their inventory.");
    public static readonly LocaleText SignedOutAfter = "People.SignedOut.After".AsLocale(
        "Once you are signed in, your friends, their requests and your chats show up here.");
    public static readonly LocaleText SignIn = "People.SignIn".AsLocale("Sign in");

    public static readonly LocaleText SearchHint = "People.Search.Hint".AsLocale("Search people");
    public static readonly LocaleText SearchShort = "People.Search.Short".AsLocale("Type at least two letters.");
    public static readonly LocaleText Searching = "People.Search.Busy".AsLocale("Searching");
    public static readonly LocaleText SearchNone = "People.Search.None".AsLocale("Nobody goes by that name.");
    public static readonly LocaleText SearchFriends = "People.Search.Friends".AsLocale("Your friends");
    public static readonly LocaleText SearchAccounts = "People.Search.Accounts".AsLocale("Everyone");

    public static readonly LocaleText SectionPinned = "People.Section.Pinned".AsLocale("Pinned");
    public static readonly LocaleText SectionInWorld = "People.Section.InWorld".AsLocale("In a world");
    public static readonly LocaleText SectionOnline = "People.Section.Online".AsLocale("Online");
    public static readonly LocaleText SectionOffline = "People.Section.Offline".AsLocale("Offline");
    public static readonly LocaleText SectionBots = "People.Section.Bots".AsLocale("Bots");

    public static readonly LocaleText You = "People.You".AsLocale("You");
    public static readonly LocaleText YourProfile = "People.YourProfile".AsLocale("Your profile");
    public static readonly LocaleText Requests = "People.Requests".AsLocale("Requests");
    public static readonly LocaleText NoFriends = "People.NoFriends".AsLocale(
        "No friends yet. Search for someone above and send them a request.");
    public static readonly LocaleText PickSomeone = "People.PickSomeone".AsLocale("Pick someone to see their profile.");
    public static readonly LocaleText PickSomeoneHint = "People.PickSomeone.Hint".AsLocale(
        "Or search for anybody by name with the box on the left.");

    public static readonly LocaleText ModeOnline = "People.Mode.Online".AsLocale("Online");
    public static readonly LocaleText ModeAway = "People.Mode.Away".AsLocale("Away");
    public static readonly LocaleText ModeBusy = "People.Mode.Busy".AsLocale("Busy");
    public static readonly LocaleText ModeInvisible = "People.Mode.Invisible".AsLocale("Invisible");
    public static readonly LocaleText ModeOffline = "People.Mode.Offline".AsLocale("Offline");

    public static readonly LocaleText PrivateWorld = "People.Where.Private".AsLocale("In a private world");
    public static readonly LocaleText NotInWorld = "People.Where.Nowhere".AsLocale("Not in a world");
    public static readonly LocaleText LocationHidden = "People.Where.Hidden".AsLocale("Keeps where they are to themselves");
    public static readonly LocaleText OfflineNow = "People.Where.Offline".AsLocale("Offline");
    public static readonly LocaleText NotFriendsWhere = "People.Where.NotFriends".AsLocale(
        "Only friends see where someone is.");
    public static LocaleText InWorld(string world) => "People.Where.In".AsLocale("In {0}", world);
    public static LocaleText LastSeen(string when) => "People.Where.LastSeen".AsLocale("Offline, last seen {0}", when);

    public static readonly LocaleText Join = "People.Join".AsLocale("Join");
    public static readonly LocaleText JoinMissing = "People.Join.Missing".AsLocale(
        "That session has ended, or isn't open to you.");
    public static readonly LocaleText Message = "People.Message".AsLocale("Message");
    public static readonly LocaleText AddFriend = "People.AddFriend".AsLocale("Add friend");
    public static readonly LocaleText Requested = "People.Requested".AsLocale("Requested");
    public static readonly LocaleText CancelRequest = "People.CancelRequest".AsLocale("Cancel request");
    public static readonly LocaleText Accept = "People.Accept".AsLocale("Accept");
    public static readonly LocaleText Decline = "People.Decline".AsLocale("Decline");
    public static readonly LocaleText Friends = "People.Friends".AsLocale("Friends");
    public static readonly LocaleText WantsToBeFriends = "People.WantsToBeFriends".AsLocale("Asked you");
    public static readonly LocaleText EditProfile = "People.EditProfile".AsLocale("Edit profile");
    public static readonly LocaleText Save = "People.Save".AsLocale("Save");
    public static readonly LocaleText Cancel = "People.Cancel".AsLocale("Cancel");
    public static readonly LocaleText Saved = "People.Saved".AsLocale("Saved.");
    public static readonly LocaleText NothingChanged = "People.NothingChanged".AsLocale("Nothing changed.");

    public static readonly LocaleText More = "People.More".AsLocale("More");
    public static readonly LocaleText Pin = "People.Pin".AsLocale("Pin to the top");
    public static readonly LocaleText Unpin = "People.Unpin".AsLocale("Unpin");
    public static readonly LocaleText Block = "People.Block".AsLocale("Block");
    public static readonly LocaleText Unblock = "People.Unblock".AsLocale("Unblock");
    public static readonly LocaleText RemoveFriend = "People.RemoveFriend".AsLocale("Remove friend");
    public static readonly LocaleText ClearChat = "People.ClearChat".AsLocale("Clear chat on my side");
    public static readonly LocaleText ShowBlocked = "People.ShowBlocked".AsLocale("Blocked accounts");
    public static readonly LocaleText ShowRequests = "People.ShowRequests".AsLocale("Friend requests");
    public static LocaleText BlockTitle(string name) => "People.Block.Title".AsLocale("Block {0}?", name);
    public static readonly LocaleText BlockMessage = "People.Block.Message".AsLocale(
        "They will not be able to find you, message you or ask to be friends. Any friendship and requests between you go.");
    public static LocaleText RemoveTitle(string name) => "People.Remove.Title".AsLocale("Remove {0}?", name);
    public static readonly LocaleText RemoveMessage = "People.Remove.Message".AsLocale(
        "You stop seeing where each other are and the chat closes. Either of you can ask again later.");
    public static LocaleText ClearTitle(string name) => "People.Clear.Title".AsLocale("Clear your chat with {0}?", name);
    public static readonly LocaleText ClearMessage = "People.Clear.Message".AsLocale(
        "Every message so far goes from your side. They keep theirs.");

    public static readonly LocaleText ChipStaff = "People.Chip.Staff".AsLocale("Staff");
    public static readonly LocaleText ChipModerator = "People.Chip.Moderator".AsLocale("Moderator");
    public static readonly LocaleText ChipBot = "People.Chip.Bot".AsLocale("Bot");
    public static LocaleText TheirTime(string time) => "People.TheirTime".AsLocale("{0} their time", time);
    public static readonly LocaleText NoteHint = "People.Note.Hint".AsLocale("Add a private note, only you see it");

    public static readonly LocaleText TabProfile = "People.Tab.Profile".AsLocale("Profile");
    public static readonly LocaleText TabChat = "People.Tab.Chat".AsLocale("Chat");
    public static LocaleText TabChatUnread(int count) => "People.Tab.ChatUnread".AsLocale("Chat ({0})", count);

    public static readonly LocaleText SectionWhere = "People.Section.Where".AsLocale("Where they are");
    public static readonly LocaleText SectionWhereSelf = "People.Section.WhereSelf".AsLocale("Where you are");
    public static readonly LocaleText SectionAbout = "People.Section.About".AsLocale("About");
    public static readonly LocaleText SectionBadges = "People.Section.Badges".AsLocale("Badges");
    public static readonly LocaleText SectionGroups = "People.Section.Groups".AsLocale("Groups");
    public static readonly LocaleText SectionDetails = "People.Section.Details".AsLocale("Details");
    public static readonly LocaleText NoBio = "People.NoBio".AsLocale("Nothing written yet.");
    public static LocaleText LinkReason(string name) => "People.Link.Reason".AsLocale("A link in {0}'s bio.", name);
    public static readonly LocaleText Represented = "People.Represented".AsLocale("Represented");
    public static readonly LocaleText FactLanguages = "People.Fact.Languages".AsLocale("Languages");
    public static readonly LocaleText FactMemberSince = "People.Fact.MemberSince".AsLocale("Member since");
    public static readonly LocaleText FactFriendsSince = "People.Fact.FriendsSince".AsLocale("Friends since");
    public static readonly LocaleText FactTimeZone = "People.Fact.TimeZone".AsLocale("Time zone");
    public static readonly LocaleText FactNone = "People.Fact.None".AsLocale("Not set");

    public static readonly LocaleText RequestsTitle = "People.Requests.Title".AsLocale("Friend requests");
    public static readonly LocaleText RequestsIncoming = "People.Requests.Incoming".AsLocale("Asking you");
    public static readonly LocaleText RequestsOutgoing = "People.Requests.Outgoing".AsLocale("Sent by you");
    public static readonly LocaleText RequestsEmpty = "People.Requests.Empty".AsLocale("No requests waiting.");
    public static LocaleText RequestsWaiting(int count) => count == 1
        ? "People.Requests.One".AsLocale("1 waiting")
        : "People.Requests.Many".AsLocale("{0} waiting", count);
    public static LocaleText AskedAgo(string when) => "People.Requests.Asked".AsLocale("Asked {0}", when);
    public static LocaleText SentAgo(string when) => "People.Requests.Sent".AsLocale("Sent {0}", when);

    public static readonly LocaleText BlockedTitle = "People.Blocked.Title".AsLocale("Blocked accounts");
    public static readonly LocaleText BlockedEmpty = "People.Blocked.Empty".AsLocale("You have not blocked anyone.");
    public static LocaleText BlockedCount(int count) => "People.Blocked.Count".AsLocale("Blocked ({0})", count);
    public static LocaleText BlockedSince(string when) => "People.Blocked.Since".AsLocale("Blocked {0}", when);

    public static readonly LocaleText ChatNotFriends = "People.Chat.NotFriends".AsLocale(
        "Chat opens once you are friends.");
    public static readonly LocaleText ChatEmpty = "People.Chat.Empty".AsLocale("No messages yet. Say hello.");
    public static readonly LocaleText ChatHint = "People.Chat.Hint".AsLocale("Write a message");
    public static readonly LocaleText ChatOlder = "People.Chat.Older".AsLocale("Earlier messages");
    public static readonly LocaleText ChatSeen = "People.Chat.Seen".AsLocale("Seen");
    public static readonly LocaleText ChatSending = "People.Chat.Sending".AsLocale("Sending…");
    public static readonly LocaleText ChatSent = "People.Chat.Sent".AsLocale("Sent");
    public static readonly LocaleText ChatNotSent = "People.Chat.NotSent".AsLocale("Not sent");
    public static readonly LocaleText ChatRetry = "People.Chat.Retry".AsLocale("Retry");
    public static readonly LocaleText ChatTyping = "People.Chat.Typing".AsLocale("Typing…");
    public static readonly LocaleText ChatMore = "People.Chat.More".AsLocale("Delete…");
    public static readonly LocaleText ChatDeleteTitle = "People.Chat.Delete.Title".AsLocale("Delete this message?");
    public static readonly LocaleText ChatRemoveTitle = "People.Chat.Remove.Title".AsLocale("Remove this message?");
    public static readonly LocaleText ChatDeleteForMe = "People.Chat.Delete.ForMe".AsLocale("Delete for me");
    public static readonly LocaleText ChatDeleteForBoth = "People.Chat.Delete.ForBoth".AsLocale("Delete for both");
    public static readonly LocaleText ChatRemove = "People.Chat.Remove".AsLocale("Remove");
    public static LocaleText ChatDeleteMine(string quote, string name) => "People.Chat.Delete.Mine".AsLocale(
        "{0}\nFor me clears it from your side, on every device. For both takes it back from {1} as well.", quote, name);
    public static LocaleText ChatDeleteMineItem(string quote, string name) => "People.Chat.Delete.MineItem".AsLocale(
        "{0}\nFor both also takes it back from {1}, who can no longer spawn or keep it. Your inventory keeps yours.", quote, name);
    public static LocaleText ChatDeleteTheirs(string quote, string name) => "People.Chat.Delete.Theirs".AsLocale(
        "{0}\nIt goes from your side on every device. {1} keeps theirs.", quote, name);
    public static LocaleText ChatRemoveNotSent(string quote, string name) => "People.Chat.Remove.NotSent".AsLocale(
        "{0}\nIt never reached {1}, so it only goes from here.", quote, name);
    public static LocaleText ChatRemoveSending(string quote, string name) => "People.Chat.Remove.Sending".AsLocale(
        "{0}\nStill on its way to {1}. If it lands, it is taken back from both of you.", quote, name);
    public static readonly LocaleText ChatDeleteFailed = "People.Chat.Delete.Failed".AsLocale(
        "That message could not be deleted, so it is back where it was.");
    public static readonly LocaleText Send = "People.Send".AsLocale("Send");
    public static readonly LocaleText SendItem = "People.SendItem".AsLocale("Send something from your inventory");
    public static readonly LocaleText Spawn = "People.Spawn".AsLocale("Spawn");
    public static readonly LocaleText Keep = "People.Keep".AsLocale("Keep");
    public static readonly LocaleText Kept = "People.Kept".AsLocale("Kept");
    public static LocaleText KeptNotice(string name) => "People.KeptNotice".AsLocale("\"{0}\" is in your inventory.", name);
    public static readonly LocaleText Open = "People.Open".AsLocale("Open");
    public static readonly LocaleText Expired = "People.Expired".AsLocale("Expired");
    public static readonly LocaleText Today = "People.Today".AsLocale("Today");
    public static readonly LocaleText Yesterday = "People.Yesterday".AsLocale("Yesterday");
    public static readonly LocaleText JustNow = "People.Ago.Now".AsLocale("just now");
    public static readonly LocaleText YesterdayAgo = "People.Ago.Yesterday".AsLocale("yesterday");
    public static LocaleText MinutesAgo(int count) => "People.Ago.Minutes".AsLocale("{0} min ago", count);
    public static LocaleText HoursAgo(int count) => "People.Ago.Hours".AsLocale("{0} h ago", count);
    public static LocaleText DaysAgo(int count) => "People.Ago.Days".AsLocale("{0} days ago", count);
    public static LocaleText Spawned(string name) => "People.Spawned".AsLocale("\"{0}\" is in front of you.", name);
    public static readonly LocaleText SpawnRefused = "People.Spawn.Refused".AsLocale("This world does not let you spawn items.");
    public static LocaleText SpawnFailed(string name) => "People.Spawn.Failed".AsLocale("Couldn't load \"{0}\".", name);
    public static readonly LocaleText NotUploaded = "People.Item.NotUp".AsLocale(
        "That is still going up to your inventory. Send it once it has synced.");

    public static readonly LocaleText EditSectionPicture = "People.Edit.Picture".AsLocale("Picture");
    public static readonly LocaleText EditSectionBanner = "People.Edit.Banner".AsLocale("Banner");
    public static readonly LocaleText EditSectionStatus = "People.Edit.Status".AsLocale("Status");
    public static readonly LocaleText EditSectionAbout = "People.Edit.About".AsLocale("About you");
    public static readonly LocaleText EditSectionLanguages = "People.Edit.Languages".AsLocale("Languages");
    public static readonly LocaleText EditSectionZone = "People.Edit.Zone".AsLocale("Time zone");
    public static readonly LocaleText EditSectionLocation = "People.Edit.Location".AsLocale("Who sees where you are");
    public static readonly LocaleText ChooseFromInventory = "People.Edit.Choose".AsLocale("Choose from inventory");
    public static readonly LocaleText Remove = "People.Edit.Remove".AsLocale("Remove");
    public static readonly LocaleText EditStatusText = "People.Edit.StatusText".AsLocale("Status line");
    public static readonly LocaleText StatusTextHint = "People.Edit.StatusHint".AsLocale("What you are up to");
    public static readonly LocaleText BioHint = "People.Edit.BioHint".AsLocale("A few words about you");
    public static readonly LocaleText BioMarkdown = "People.Edit.BioMarkdown".AsLocale(
        "**bold**   *italic*   ~~strike~~   `code`   - list   [words](https://...)");
    public static readonly LocaleText Preview = "People.Edit.Preview".AsLocale("How it reads");
    public static readonly LocaleText PreviewEmpty = "People.Edit.PreviewEmpty".AsLocale("Your bio shows here the way others see it.");
    public static readonly LocaleText EditSectionPronouns = "People.Edit.Pronouns".AsLocale("Pronouns");
    public static readonly LocaleText PronounsNone = "People.Edit.PronounsNone".AsLocale("Not shown");
    public static readonly LocaleText PronounsPick = "People.Edit.PronounsPick".AsLocale("Choose");
    public static readonly LocaleText PronounsHint = "People.Edit.PronounsHint".AsLocale(
        "Shown beside your name. Pick a common set or type your own, like xe/xem.");
    public static readonly LocaleText PronounsBad = "People.Edit.PronounsBad".AsLocale(
        "Two or three words of letters split by /, like she/her or xe/xem/xyr, at most 32 characters.");
    public static readonly LocaleText PickPronouns = "People.Edit.PickPronouns".AsLocale("Your pronouns");
    public static readonly LocaleText PronounsClear = "People.Edit.PronounsClear".AsLocale("Don't show any");
    public static readonly LocaleText AddLanguage = "People.Edit.AddLanguage".AsLocale("Add");
    public static readonly LocaleText LanguagesFull = "People.Edit.LanguagesFull".AsLocale("Five is the most a profile shows.");
    public static readonly LocaleText ChooseZone = "People.Edit.ChooseZone".AsLocale("Choose");
    public static readonly LocaleText ZoneLocal = "People.Edit.ZoneLocal".AsLocale("Use this computer's");
    public static readonly LocaleText PickLanguage = "People.Edit.PickLanguage".AsLocale("Add a language");
    public static readonly LocaleText PickZone = "People.Edit.PickZone".AsLocale("Your time zone");
    public static readonly LocaleText PrivacyFriends = "People.Privacy.Friends".AsLocale("Friends");
    public static readonly LocaleText PrivacyNobody = "People.Privacy.Nobody".AsLocale("Nobody");
    public static readonly LocaleText PrivacyHint = "People.Privacy.Hint".AsLocale(
        "Friends see the world you are in and can join when it lets them in. Nobody keeps it to you.");
    public static readonly LocaleText PictureNone = "People.Edit.PictureNone".AsLocale("No picture yet");
    public static readonly LocaleText BannerNone = "People.Edit.BannerNone".AsLocale("No banner yet");
    public static readonly LocaleText PicturePicked = "People.Edit.Picked".AsLocale("Picked, save to keep it");
    public static readonly LocaleText AnimatedPicked = "People.Edit.AnimatedPicked".AsLocale(
        "Animated: supporters only, it will not save");
    public static readonly LocaleText AnimatedPerk = "People.Edit.AnimatedPerk".AsLocale(
        "Animated pictures and banners are a supporter perk. Support Lumora on Patreon at any tier, with this account's email address, and they will play. Until then, choose a still picture.");
    public static readonly LocaleText SupportPatreon = "People.Edit.SupportPatreon".AsLocale("Support Lumora on Patreon");
    public static readonly LocaleText PatreonReason = "People.Edit.PatreonReason".AsLocale(
        "Lumora's Patreon page. Join with this account's email address and your pictures will play.");
    public static readonly LocaleText PictureCheck = "People.Edit.PictureCheck".AsLocale(
        "The picture could not be checked just now. Try again in a moment.");

    public static readonly LocaleText PickPictureTitle = "People.Pick.Picture".AsLocale("Choose a picture");
    public static readonly LocaleText PickBannerTitle = "People.Pick.Banner".AsLocale("Choose a banner");
    public static readonly LocaleText PickItemTitle = "People.Pick.Item".AsLocale("Send from your inventory");
    public static readonly LocaleText PickPictureHint = "People.Pick.PictureHint".AsLocale(
        "Photos and saves with a picture. Take a photo to add one.");
    public static readonly LocaleText PickItemHint = "People.Pick.ItemHint".AsLocale(
        "Anything saved and synced. They can spawn it for 30 days.");
    public static readonly LocaleText PickEmpty = "People.Pick.Empty".AsLocale("Nothing here to choose.");
    public static readonly LocaleText Up = "People.Pick.Up".AsLocale("Up");
    public static readonly LocaleText Inventory = "People.Pick.Inventory".AsLocale("Inventory");
}
