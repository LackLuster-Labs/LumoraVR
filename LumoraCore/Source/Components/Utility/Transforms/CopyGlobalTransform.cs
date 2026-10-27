// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Components.Utility;

// Holds this slot on another slot's global transform, channel by channel.
//
// This is a DRIVER: the pose lands through FieldDrive members on this slot's own transform fields, so
// the fields read as driven, are excluded from value sync, and let go the moment the component does.
// SlotMirror is the other shape - a follower that writes a REMOTE slot's fields by hand every frame -
// and the two are not interchangeable: a follower can push a slot it does not own, a driver can only
// hold its own, and only the driver's target is protected from a second writer. -xlinka
[ComponentCategory("Utility/Transforms")]
public class CopyGlobalTransform : Component
{
    public readonly SyncRef<Slot> Source;

    public readonly Sync<bool> CopyPosition;

    public readonly Sync<bool> CopyRotation;

    public readonly Sync<bool> CopyScale;

    // Average the copied scale across the axes, for a source whose parents squash it unevenly.
    public readonly Sync<bool> UniformScale;

    protected readonly FieldDrive<float3> _position;

    protected readonly FieldDrive<floatQ> _rotation;

    protected readonly FieldDrive<float3> _scale;

    public CopyGlobalTransform()
    {
        Source = new SyncRef<Slot>(this);
        CopyPosition = new Sync<bool>(this, true);
        CopyRotation = new Sync<bool>(this, true);
        CopyScale = new Sync<bool>(this, false);
        UniformScale = new Sync<bool>(this, false);
        _position = new FieldDrive<float3>(this) { LocalValueOnly = true };
        _rotation = new FieldDrive<floatQ>(this) { LocalValueOnly = true };
        _scale = new FieldDrive<float3>(this) { LocalValueOnly = true };
    }

    public override void OnStart()
    {
        base.OnStart();
        ApplyChannels();
    }

    public override void OnChanges()
    {
        base.OnChanges();
        ApplyChannels();
    }

    // The channel switches own the LINK, not just the write. A drive that holds a field it never writes
    // still reads as driven everywhere - tinted in the inspector, refused to value sync, and blocking
    // any other driver - which is a worse lie than an off switch. -xlinka
    private void ApplyChannels()
    {
        var slot = Slot;
        if (slot == null || slot.IsDestroyed)
            return;
        SetChannel(_position, CopyPosition.Value, slot.LocalPosition);
        SetChannel(_rotation, CopyRotation.Value, slot.LocalRotation);
        SetChannel(_scale, CopyScale.Value, slot.LocalScale);
    }

    // Every switch off and every link let go now, for a copier that has to stop holding its slot but may not be
    // removed by whoever stops it. Nothing links the channels again until someone turns one back on.
    internal void LetGo()
    {
        CopyPosition.Value = false;
        CopyRotation.Value = false;
        CopyScale.Value = false;
        ApplyChannels();
    }

    internal bool HoldsAnyChannel => CopyPosition.Value || CopyRotation.Value || CopyScale.Value
        || _position.HasTarget || _rotation.HasTarget || _scale.HasTarget;

    private static void SetChannel<T>(FieldDrive<T> drive, bool wanted, IField<T> field)
    {
        if (wanted)
        {
            if (!ReferenceEquals(drive.Target, field))
                drive.DriveTarget(field);
        }
        else if (drive.HasTarget)
        {
            drive.ReleaseLink();
        }
    }

    public override void OnUpdate(float delta)
    {
        var source = Source.Target;
        var slot = Slot;
        if (source == null || source.IsDestroyed || slot == null)
            return;

        var parent = slot.Parent;

        if (_position.IsLinkValid)
        {
            var global = source.GlobalPosition;
            _position.SetValue(parent != null ? parent.GlobalPointToLocal(global) : global);
        }

        if (_rotation.IsLinkValid)
        {
            var global = source.GlobalRotation;
            _rotation.SetValue(parent != null ? parent.GlobalRotationToLocal(global) : global);
        }

        if (_scale.IsLinkValid)
        {
            var global = source.GlobalScale;
            var local = parent != null ? parent.GlobalScaleToLocal(global) : global;
            if (UniformScale.Value)
            {
                float average = (local.x + local.y + local.z) / 3f;
                local = new float3(average, average, average);
            }
            _scale.SetValue(local);
        }
    }
}
