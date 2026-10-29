// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;

namespace Lumora.Core.Components.Avatar;

// Destroys its slot tree when the avatar object it belongs to is dequipped.
// Attach to default-filled pieces (head sphere, hand visuals) so equipping a
// real avatar automatically removes them instead of leaving them parented
// under the manager. - xlinka
[ComponentCategory("Users/Avatar")]
public class DiscardOnDequip : Component, IAvatarEquipReceiver
{
    private bool _wasEquipped;

    public void OnPreEquip(AvatarSocket slot) { }

    public void OnEquip(AvatarSocket slot) { }

    public void OnDequip(AvatarSocket slot)
    {
        if (World?.IsAuthority != true)
            return;

        Slot?.RunSynchronously(() =>
        {
            if (Slot != null && !Slot.IsDestroyed)
            {
                using (World?.DataModelPermissions?.EnterSystemBypass())
                    Slot.Destroy();
            }
        });
    }

    // OnDequip runs only on the machine doing the equip, and only the authority may destroy the piece, so a
    // guest's equip used to leave it lying in the world. The authority watches for it coming off instead. -xlinka
    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        if (World?.IsAuthority != true || Slot == null || Slot.IsDestroyed)
            return;

        bool equipped = PieceEquipped();
        if (_wasEquipped && !equipped)
        {
            using (World.DataModelPermissions?.EnterSystemBypass())
                Slot.Destroy();
            return;
        }
        _wasEquipped = equipped;
    }

    // On while its socket still names it. A socket the wearer gave to their avatar's own part at the same moment
    // the authority filled it leaves the piece pointing at a socket that holds something else. -xlinka
    private bool PieceEquipped()
    {
        foreach (var component in Slot.Components)
        {
            if (component is IAvatarEquippable equippable && equippable.IsEquipped
                && ReferenceEquals(equippable.CurrentSocket?.Equipped.Target, equippable))
                return true;
        }
        return false;
    }
}
