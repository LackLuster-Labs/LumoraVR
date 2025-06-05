using System.Text.RegularExpressions;

namespace LumoraVR.Source.Helpers;

public static partial class GeneralHelpers
{
    [GeneratedRegex("\\[.+?\\]")]
    private static partial Regex BBCodeRegex();
    /// <summary>
    /// Removes every instance of a BBCode tag from a string.
    /// </summary>
    /// <param name="text">The text to strip BBCode out of.</param>
    /// <returns>A string stripped of BBCode tags.</returns>
    public static string StripBBCode(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }
        return BBCodeRegex().Replace(text, string.Empty);
    }
}