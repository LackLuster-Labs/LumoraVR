// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Localization;

namespace Lumora.Core;

// What the favourite inspector and keyboard say: the inventory's answer to picking one, and why one could not
// be used.
public static class FavouriteLocale
{
    public static LocaleText InspectorPicked(string name)
        => "Favourites.Inspector.Picked".AsLocale("\"{0}\" is your favourite inspector. Every inspector you open is this one.", name);

    public static LocaleText KeyboardPicked(string name)
        => "Favourites.Keyboard.Picked".AsLocale("\"{0}\" is your favourite keyboard. It comes up when you type in VR.", name);

    public static LocaleText InspectorDidNotLoad(string name, string why)
        => "Favourites.Inspector.DidNotLoad".AsLocale("Your favourite inspector \"{0}\" did not load: {1}", name, why);

    public static LocaleText InspectorNoLists(string name)
        => "Favourites.Inspector.NoLists".AsLocale("Your favourite inspector \"{0}\" has neither its tree nor its component list any more.", name);

    public static LocaleText KeyboardDidNotLoad(string name, string why)
        => "Favourites.Keyboard.DidNotLoad".AsLocale("Your favourite keyboard \"{0}\" did not load: {1}", name, why);

    public static LocaleText KeyboardNoKeys(string name)
        => "Favourites.Keyboard.NoKeys".AsLocale("Your favourite keyboard \"{0}\" has no keys in it.", name);

    public static LocaleText KeyboardTooBig(string name, int parts, int most)
        => "Favourites.Keyboard.TooBig".AsLocale("Your favourite keyboard \"{0}\" has {1} parts, more than the {2} a keyboard may.", name, parts, most);

    public static readonly LocaleText KeyboardLeftOutTitle = "Favourites.Keyboard.LeftOut.Title".AsLocale("Your keyboard, with less");

    public static LocaleText KeyboardLeftOut(string name, string parts)
        => "Favourites.Keyboard.LeftOut.Text".AsLocale("Only looks and keys come into your own space, so \"{0}\" types without: {1}.", name, parts);
}
