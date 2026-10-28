// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace Lumora.Script;

// What a node search finds, best first. Every word typed has to begin a word of the node's title, keywords,
// route or class name, and words split where letters meet digits, so "vector3" is "vector" and "3" and meets
// Random Vector by its "float3" keyword. A title that is the text typed comes first, then a title that starts
// with it, then a keyword that is the text, then titles holding every word, then titles and keywords, then the
// rest. A keyword never outranks a title that starts with the text: "round" is Round To Int before Format Number.
// The typed text anywhere in the route or the class name still finds the node, as search always did. -xlinka
public static class NodeSearch
{
    private sealed class Entry
    {
        public string Title = string.Empty;
        public string[] TitleWords = Array.Empty<string>();
        public string[] KeywordWords = Array.Empty<string>();
        public string[] OtherWords = Array.Empty<string>();
    }

    private static readonly ConditionalWeakTable<NodeDefinition, Entry> Entries = new();

    public const int NoMatch = -1;

    // Every definition the query finds, best first; equally good ones keep the order they came in.
    public static List<NodeDefinition> Find(IEnumerable<NodeDefinition> definitions, string query)
    {
        var text = Collapse(query);
        var words = Words(text);
        var found = new List<(NodeDefinition Definition, int Rank, int Order)>();
        if (text.Length == 0)
            return new List<NodeDefinition>();
        foreach (var definition in definitions)
        {
            int rank = Rank(definition, text, words);
            if (rank != NoMatch)
                found.Add((definition, rank, found.Count));
        }
        found.Sort((a, b) => a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank) : a.Order.CompareTo(b.Order));
        var ordered = new List<NodeDefinition>(found.Count);
        foreach (var (definition, _, _) in found)
            ordered.Add(definition);
        return ordered;
    }

    // How well one node answers the query, 0 best, or NoMatch.
    public static int Rank(NodeDefinition definition, string query)
    {
        var text = Collapse(query);
        return text.Length == 0 ? NoMatch : Rank(definition, text, Words(text));
    }

    private static int Rank(NodeDefinition definition, string text, string[] words)
    {
        var entry = Entries.GetValue(definition, Make);
        if (string.Equals(entry.Title, text, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (entry.Title.StartsWith(text, StringComparison.OrdinalIgnoreCase))
            return 1;
        foreach (var keyword in definition.Keywords)
        {
            if (string.Equals(Collapse(keyword), text, StringComparison.OrdinalIgnoreCase))
                return 2;
        }
        if (words.Length > 0)
        {
            bool inTitle = true, inKeywords = true, anywhere = true;
            foreach (var word in words)
            {
                bool title = Begins(entry.TitleWords, word);
                bool keyword = title || Begins(entry.KeywordWords, word);
                inTitle &= title;
                inKeywords &= keyword;
                anywhere &= keyword || Begins(entry.OtherWords, word);
            }
            if (inTitle)
                return 3;
            if (inKeywords)
                return 4;
            if (anywhere)
                return 5;
        }
        return definition.Path.Contains(text, StringComparison.OrdinalIgnoreCase)
               || definition.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ? 5 : NoMatch;
    }

    private static bool Begins(string[] words, string word)
    {
        foreach (var candidate in words)
        {
            if (candidate.StartsWith(word, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static Entry Make(NodeDefinition definition)
    {
        int slash = definition.Path.LastIndexOf('/');
        var title = slash >= 0 ? definition.Path[(slash + 1)..] : definition.Path;
        var keywords = new List<string>();
        foreach (var keyword in definition.Keywords)
            keywords.AddRange(Words(keyword));
        var other = new List<string>(Words(slash > 0 ? definition.Path[..slash] : string.Empty));
        other.AddRange(Words(definition.Name));
        foreach (var former in definition.FormerNames)
            other.AddRange(Words(former));
        return new Entry
        {
            Title = title,
            TitleWords = Words(title),
            KeywordWords = keywords.ToArray(),
            OtherWords = other.ToArray(),
        };
    }

    private static string Collapse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts);
    }

    // Lower-case words, split at anything but a letter or digit, where a lower-case letter meets a capital,
    // before the last capital of a run that goes on in lower case, and where letters meet digits:
    // "HTTPRequest2" is "http", "request", "2".
    internal static string[] Words(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<string>();
        var words = new List<string>();
        var word = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (!char.IsLetterOrDigit(c))
            {
                Flush();
                continue;
            }
            if (word.Length > 0)
            {
                char before = text[i - 1];
                bool split = (char.IsLower(before) && char.IsUpper(c))
                    || (char.IsUpper(before) && char.IsUpper(c) && i + 1 < text.Length && char.IsLower(text[i + 1]))
                    || (char.IsLetter(before) && char.IsDigit(c))
                    || (char.IsDigit(before) && char.IsLetter(c));
                if (split)
                    Flush();
            }
            word.Append(char.ToLowerInvariant(c));
        }
        Flush();
        return words.ToArray();

        void Flush()
        {
            if (word.Length == 0)
                return;
            words.Add(word.ToString());
            word.Clear();
        }
    }
}
