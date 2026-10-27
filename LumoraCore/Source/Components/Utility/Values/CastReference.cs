// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Components.Utility;

// Drives a reference of one kind with a reference of another, when what the source holds is that kind too: the
// texture a player shows onto everything that wants its transport, which is the same video. Anything the source
// holds that is not a TTo drives null. With WriteBack, something written to the target is taken back into the
// source when it fits there, so a picker on either side moves both.
[ComponentCategory("Utility/Values")]
[DefaultUpdateOrder(-100)]
public class CastReference<TFrom, TTo> : Component
    where TFrom : class, IWorldElement
    where TTo : class, IWorldElement
{
    public readonly SyncRef<TFrom> Source;

    public readonly DriveRef<TTo> Target;

    public readonly Sync<bool> WriteBack;

    public CastReference()
    {
        Source = new SyncRef<TFrom>(this);
        Target = new DriveRef<TTo>(this) { LocalValueOnly = true };
        WriteBack = new Sync<bool>(this, false);
    }

    public override void OnAwake()
    {
        base.OnAwake();
        Target.RefSetHook = TargetWritten;
    }

    public override void OnUpdate(float delta)
    {
        if (!Target.IsLinkValid)
            return;
        var wanted = Source.Target as TTo;
        if (!ReferenceEquals(Target.Target?.Target, wanted))
            Target.SetValue(wanted);
    }

    private void TargetWritten(SyncRef<TTo> reference, TTo value)
    {
        if (!WriteBack.Value)
            return;
        if (value == null)
            Source.Target = null!;
        else if (value is TFrom from)
            Source.Target = from;
    }
}
