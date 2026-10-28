// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Lumora.Nexus.Cloud.Cdn;

// A folder merge's answer: the folder that holds everything now, and each folder that went into another (none when
// there was no folder of the same name and it was a plain move).
public record FolderMergeResult
{
    [JsonPropertyName("folder")] public FolderSummary? Folder { get; init; }
    [JsonPropertyName("folded")] public List<FoldedFolderInfo> Folded { get; init; } = new();
}

public record FoldedFolderInfo
{
    [JsonPropertyName("from")] public string From { get; init; } = "";
    [JsonPropertyName("into")] public string Into { get; init; } = "";
}
