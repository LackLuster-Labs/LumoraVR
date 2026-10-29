// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Text;
using Lumora.Script;

namespace Lumora.Core.Components.Scripting;

// The same short explanation in the browser and on placed headers. Wrap it for the pointer label
// here so other tooltips keep their own formatting. -xlinka
internal static class ScriptNodeHelp
{
    private const int LineLength = 52;

    public static string For(NodeDefinition? node)
    {
        if (node == null)
            return string.Empty;
        int slash = node.Path.LastIndexOf('/');
        string title = slash >= 0 ? node.Path[(slash + 1)..] : node.Path;
        if (string.IsNullOrWhiteSpace(node.Description))
            return title;

        var text = new StringBuilder(title);
        text.Append('\n');
        int column = 0;
        foreach (string word in node.Description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (column > 0)
            {
                if (column + 1 + word.Length > LineLength)
                {
                    text.Append('\n');
                    column = 0;
                }
                else
                {
                    text.Append(' ');
                    column++;
                }
            }
            text.Append(word);
            column += word.Length;
        }
        return text.ToString();
    }
}
