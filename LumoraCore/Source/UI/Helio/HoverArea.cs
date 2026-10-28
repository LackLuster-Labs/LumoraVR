// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Math;

namespace Helio.UI;

// Says whether a pointer is over its rect, whatever inside it takes the pointer: a panel that shows its controls
// while someone points anywhere on it, its buttons included. It is not an interactable, so it never takes a hover
// or a press off anything under it. The canvas tells it which of its pointers are over it as each pointer's scan
// walks past. -xlinka
[ComponentCategory("UI/Helio/Interaction")]
public sealed class HoverArea : UIComponent
{
    [NonPersistent]
    public readonly Sync<bool> IsHovering;

    private readonly HashSet<int> _pointers = new();

    public HoverArea()
    {
        IsHovering = new Sync<bool>(this, false);
        IsHovering.MarkUiState();
    }

    public bool Covers(in float2 point)
        => !IsDestroyed && Enabled.Value && RectTransform is { } rect && rect.LocalComputeRect.Contains(point);

    internal void PointerEntered(int pointer)
    {
        if (_pointers.Add(pointer))
            Refresh();
    }

    internal void PointerLeft(int pointer)
    {
        if (_pointers.Remove(pointer))
            Refresh();
    }

    public override void OnDuplicate()
    {
        base.OnDuplicate();
        _pointers.Clear();
        IsHovering.Value = false;
    }

    // Everyone sees a panel someone points at, so the flag is shared. Where this machine's user may not write it
    // they still see their own hover.
    private void Refresh()
    {
        if (IsDestroyed)
            return;
        bool over = _pointers.Count > 0;
        if (IsHovering.Value == over)
            return;
        if (IsHovering.IsWriteAllowedQuietly())
            IsHovering.Value = over;
        else
            IsHovering.SetValueSilently(over);
    }
}
