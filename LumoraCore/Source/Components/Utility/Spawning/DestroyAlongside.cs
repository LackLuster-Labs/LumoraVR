// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Components.Utility;

// Takes another slot or component with it when it goes, for a part that has left its object's hierarchy but
// belongs to it all the same: a lid set down beside its bottle goes when the bottle does. Done by the
// authority, and not while the world itself is closing.
//
// The authority does it for whoever made this, not as itself. A guest pointing one at somebody else's thing
// and deleting its own item took that thing down too: the host ran the destroy, and the host may. A user's
// one takes only what that user could take down by hand, and goes quiet once they have left. -xlinka
[ComponentCategory("Utility/Spawning")]
public sealed class DestroyAlongside : Component
{
    public readonly SyncRef<IWorldElement> Target;

    public DestroyAlongside()
    {
        Target = new SyncRef<IWorldElement>(this);
    }

    public override void OnDestroy()
    {
        if (World is { IsAuthority: true, IsDestroyed: false, IsDisposed: false } world)
        {
            byte madeIn = ReferenceID.GetUserByte();
            bool worlds = madeIn == RefIDConstants.AUTHORITY_BYTE || world.LocalUser?.HoldsByte(madeIn) == true;
            var author = worlds ? null : world.UserHoldingByte(madeIn);
            var permissions = world.DataModelPermissions;
            using (worlds ? permissions?.EnterAuthoredContentScope(this) : permissions?.EnterActor(author))
            {
                switch (Target.Target)
                {
                    case Slot { IsDestroyed: false, IsRootSlot: false } slot when worlds || (author != null && slot.MayBeDestroyedBy(author)):
                        slot.Destroy();
                        break;
                    case Component { IsDestroyed: false } component when !ReferenceEquals(component, this)
                        && (worlds || (author != null && component.Slot is { } owner && owner.MayRemoveComponentBy(component, author))):
                        component.Destroy();
                        break;
                }
            }
        }
        base.OnDestroy();
    }
}
