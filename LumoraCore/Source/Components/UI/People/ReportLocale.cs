// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Net;
using Lumora.Core.Localization;
using Lumora.Nexus.Cloud.Cdn;

namespace Lumora.Core.Components.UI;

// Every string the report form and the Report buttons can put on screen.
public static class ReportLocale
{
    public static readonly LocaleText Report = "Report.Button".AsLocale("Report");
    public static readonly LocaleText SignInToReport = "Report.SignIn".AsLocale("Sign in to report");
    public static LocaleText Title(string name) => "Report.Title".AsLocale("Report {0}", name);
    public static readonly LocaleText Sub = "Report.Sub".AsLocale("Only the moderators see it.");

    public static readonly LocaleText Category = "Report.Category".AsLocale("What happened");
    public static readonly LocaleText CategoryNone = "Report.Category.None".AsLocale("Pick the one that fits best.");
    public static readonly LocaleText Harassment = "Report.Category.Harassment".AsLocale("Harassment");
    public static readonly LocaleText HarassmentHint = "Report.Category.Harassment.Hint".AsLocale(
        "Bullying, threats, or following someone who wants to be left alone.");
    public static readonly LocaleText Hate = "Report.Category.Hate".AsLocale("Hate");
    public static readonly LocaleText HateHint = "Report.Category.Hate.Hint".AsLocale("Attacking people for who they are.");
    public static readonly LocaleText Sexual = "Report.Category.Sexual".AsLocale("Sexual content");
    public static readonly LocaleText SexualHint = "Report.Category.Sexual.Hint".AsLocale(
        "Sexual behaviour or content aimed at people who didn't ask for it.");
    public static readonly LocaleText Underage = "Report.Category.Underage".AsLocale("Underage");
    public static readonly LocaleText UnderageHint = "Report.Category.Underage.Hint".AsLocale("Seems too young to be here.");
    public static readonly LocaleText Impersonation = "Report.Category.Impersonation".AsLocale("Impersonation");
    public static readonly LocaleText ImpersonationHint = "Report.Category.Impersonation.Hint".AsLocale("Pretending to be someone else.");
    public static readonly LocaleText Cheating = "Report.Category.Cheating".AsLocale("Cheating");
    public static readonly LocaleText CheatingHint = "Report.Category.Cheating.Hint".AsLocale(
        "Exploits, crashing people, or breaking worlds on purpose.");
    public static readonly LocaleText Spam = "Report.Category.Spam".AsLocale("Spam");
    public static readonly LocaleText SpamHint = "Report.Category.Spam.Hint".AsLocale("Ads, scams or flooding the chat.");
    public static readonly LocaleText SelfHarm = "Report.Category.SelfHarm".AsLocale("Self-harm");
    public static readonly LocaleText SelfHarmHint = "Report.Category.SelfHarm.Hint".AsLocale(
        "Talking about hurting themselves. If someone is in danger right now, call your local emergency number.");
    public static readonly LocaleText Other = "Report.Category.Other".AsLocale("Something else");
    public static readonly LocaleText OtherHint = "Report.Category.Other.Hint".AsLocale("Anything else a moderator should look at.");

    public static readonly LocaleText Reason = "Report.Reason".AsLocale("What they did");
    public static readonly LocaleText ReasonHint = "Report.Reason.Hint".AsLocale(
        "What happened, and when. The more a moderator knows, the better.");

    public static readonly LocaleText Picture = "Report.Picture".AsLocale("A picture, if you have one");
    public static readonly LocaleText Take = "Report.Picture.Take".AsLocale("Take a picture now");
    public static readonly LocaleText Choose = "Report.Picture.Choose".AsLocale("Choose from my photos");
    public static readonly LocaleText Remove = "Report.Picture.Remove".AsLocale("Remove");
    public static readonly LocaleText PictureNone = "Report.Picture.None".AsLocale("No picture. One helps a moderator see what happened.");
    public static readonly LocaleText Taking = "Report.Picture.Taking".AsLocale("Taking the picture");
    public static readonly LocaleText Getting = "Report.Picture.Getting".AsLocale("Getting the photo");
    public static LocaleText Taken(string size) => "Report.Picture.Taken".AsLocale("Your view just now, without this window  ·  {0}", size);
    public static LocaleText Chosen(string size) => "Report.Picture.Chosen".AsLocale("From your photos  ·  {0}", size);
    public static readonly LocaleText TakeFailed = "Report.Picture.Failed".AsLocale("Couldn't take the picture.");
    public static readonly LocaleText ReadFailed = "Report.Picture.ReadFailed".AsLocale("Couldn't read that photo.");
    public static readonly LocaleText NoWorld = "Report.Picture.NoWorld".AsLocale("There's no world to take a picture of.");
    public static readonly LocaleText PickTitle = "Report.Pick.Title".AsLocale("Choose a photo");

    public static LocaleText Where(string world) => "Report.Where".AsLocale("Goes with it: the world you're in, {0}.", world);

    public static readonly LocaleText Send = "Report.Send".AsLocale("Send report");
    public static readonly LocaleText Cancel = "Report.Cancel".AsLocale("Cancel");
    public static LocaleText Sending(int percent) => "Report.Sending".AsLocale("Sending  {0}%", percent);
    public static readonly LocaleText Waiting = "Report.Waiting".AsLocale("Sent, waiting for the server");
    public static readonly LocaleText NeedCategory = "Report.Need.Category".AsLocale("Pick what happened first.");
    public static readonly LocaleText NeedReason = "Report.Need.Reason".AsLocale("Say what happened first.");
    public static readonly LocaleText WaitPicture = "Report.Need.Picture".AsLocale("Wait for the picture to finish.");

    public static readonly LocaleText Bad = "Report.Refused.Bad".AsLocale(
        "The report wasn't accepted. Check what you wrote and the picture, then send it again.");
    public static readonly LocaleText Gone = "Report.Refused.Gone".AsLocale("That account doesn't exist any more.");
    public static readonly LocaleText Self = "Report.Refused.Self".AsLocale("You can't report yourself.");
    public static readonly LocaleText TooMany = "Report.Refused.TooMany".AsLocale(
        "You've sent a lot of reports this hour. Wait a while, then try again.");
    public static readonly LocaleText TooBig = "Report.Refused.TooBig".AsLocale("The picture is too big to send. Try another one.");
    public static readonly LocaleText SignedOut = "Report.Refused.SignedOut".AsLocale("You're not signed in any more. Sign in, then send it again.");
    public static readonly LocaleText Offline = "Report.Refused.Offline".AsLocale(
        "Couldn't reach the server. Check your connection and try again.");
    public static readonly LocaleText Failed = "Report.Refused.Failed".AsLocale("The report didn't go through. Try again in a moment.");

    public static readonly LocaleText ThanksTitle = "Report.Thanks.Title".AsLocale("Thanks for telling us");
    public static LocaleText ThanksBody(string name) => "Report.Thanks.Body".AsLocale("Your report about {0} is with the moderators.", name);
    public static readonly LocaleText Done = "Report.Done".AsLocale("Back to profile");

    // The categories as the form shows them, in the order ReportCategories.All sends them.
    public static readonly LocaleText[] Categories = { Harassment, Hate, Sexual, Underage, Impersonation, Cheating, Spam, SelfHarm, Other };
    public static readonly LocaleText[] CategoryHints =
        { HarassmentHint, HateHint, SexualHint, UnderageHint, ImpersonationHint, CheatingHint, SpamHint, SelfHarmHint, OtherHint };

    // What the form says when the service turns a report back.
    public static LocaleText Refusal(ApiResponse answer) => answer.Status switch
    {
        HttpStatusCode.BadRequest => Bad,
        HttpStatusCode.NotFound => Gone,
        HttpStatusCode.Conflict => Self,
        HttpStatusCode.TooManyRequests => TooMany,
        HttpStatusCode.RequestEntityTooLarge => TooBig,
        HttpStatusCode.Unauthorized => SignedOut,
        HttpStatusCode.ServiceUnavailable or HttpStatusCode.RequestTimeout or HttpStatusCode.BadGateway
            or HttpStatusCode.GatewayTimeout => Offline,
        _ => Failed,
    };
}
