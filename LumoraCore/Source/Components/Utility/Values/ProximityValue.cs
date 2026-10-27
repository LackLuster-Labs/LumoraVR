// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.Components.Utility;

// Drives a field with one value while the user at this machine is within Distance of this slot and another past
// it: a panel that opens up as someone walks over and folds away behind them. Measured from the part of the user
// Point names. Every machine drives its own answer, so two people at different distances each see their own.
// With no user here yet the field takes Far. -xlinka
[ComponentCategory("Utility/Values")]
[ComponentGenericTypes(GenericTypeGroup.Values)]
public class ProximityValue<T> : Component
{
    public readonly Sync<UserRoot.UserNode> Point;

    // Metres in the world, whatever the slot's scale.
    public readonly Sync<float> Distance;

    public readonly Sync<T> Near;

    public readonly Sync<T> Far;

    public readonly FieldDrive<T> Target;

    public static bool IsValidGenericType => DrivenValueTypes.IsPrimitive(typeof(T));

    public ProximityValue()
    {
        Point = new Sync<UserRoot.UserNode>(this, UserRoot.UserNode.View);
        Distance = new Sync<float>(this, 1f);
        Near = new Sync<T>(this, SyncCoder.GetDefault<T>());
        Far = new Sync<T>(this, SyncCoder.GetDefault<T>());
        Target = new FieldDrive<T>(this) { LocalValueOnly = true };
    }

    public override void OnUpdate(float delta)
    {
        if (!Target.IsLinkValid || Slot == null)
            return;
        var root = World?.LocalUser?.Root;
        bool near = false;
        if (root != null && !root.IsDestroyed)
        {
            var node = Point.Value == UserRoot.UserNode.None ? UserRoot.UserNode.View : Point.Value;
            float reach = Distance.Value;
            near = (root.GetGlobalPosition(node) - Slot.GlobalPosition).LengthSquared < reach * reach;
        }
        Target.SetValue(near ? Near.Value : Far.Value);
    }
}
