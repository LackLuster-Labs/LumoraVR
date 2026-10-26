// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.Components;

public sealed class SyncIntList : SyncList<Sync<int>>
{
    public event Action<SyncIntList>? OnChanged;

    public SyncIntList(Component owner)
    {
        Parent = owner;
        ElementsAdded += OnElementsAdded;
        ElementsRemoving += OnElementsRemoving;
    }

    public int GetValue(int index, int fallback)
    {
        return index >= 0 && index < Count ? GetElement(index).Value : fallback;
    }

    public void SetValue(int index, int value)
    {
        EnsureMinimumCount(index + 1);
        GetElement(index).Value = value;
    }

    public bool EnsureExactCount(int count, int defaultValue)
    {
        int oldCount = Count;
        base.EnsureExactCount(count);
        for (int i = oldCount; i < Count; i++)
        {
            GetElement(i).Value = defaultValue;
        }

        return oldCount != Count;
    }

    private void OnElementsAdded(SyncElementList<Sync<int>> list, int index, int count)
    {
        for (int i = 0; i < count; i++)
        {
            list[index + i].OnChanged += OnElementChanged;
        }
        OnChanged?.Invoke(this);
    }

    private void OnElementsRemoving(SyncElementList<Sync<int>> list, int index, int count)
    {
        for (int i = 0; i < count; i++)
        {
            list[index + i].OnChanged -= OnElementChanged;
        }
        OnChanged?.Invoke(this);
    }

    private void OnElementChanged(int value)
    {
        OnChanged?.Invoke(this);
    }
}
