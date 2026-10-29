// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Components;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Components.UI;
using Lumora.Core.Input;
using Lumora.Core.Logging;
using Lumora.Core.Math;

namespace Lumora.Core.Templates
{
    // Userspace is the private overlay world every panel that follows the user lives in: the dash, the
    // pointer rig, the virtual keyboard.
    public static class Userspace
    {
        // The slot every userspace panel hangs off. Pointers restrict themselves to this subtree while
        // any of those panels is up, which is what keeps the world behind them unclickable while both
        // the dash and the keyboard stay hittable. Null until the world is built. -xlinka
        public static Slot? LocalRoot { get; private set; }

        public static World SetupUserspace(Engine engine)
        {
            Logger.Debug("Userspace: Setting up userspace world");

            var world = World.CreateUserspace(engine, (w) =>
            {
                Logger.Debug("Userspace: Initializing userspace world");

                var userspaceRoot = w.RootSlot.AddSlot("UserspaceRoot");
                LocalRoot = userspaceRoot;
                userspaceRoot.AddSlot("Clipboard Import").AttachComponent<ClipboardImporter>();

                // Userspace dashboard root. UserspaceDashboard owns the open
                // state and positions the Helio dash surface in front of the
                // focused user's view when shown.
                var dashboardSlot = userspaceRoot.AddSlot("UserspaceDashboard");
                var dashboard = dashboardSlot.AttachComponent<UserspaceDashboard>();
                dashboard.Close();

                // The virtual keyboard, parked inactive. It watches the focused text field itself and
                // puts itself in front of you in VR when one appears, so nothing has to open it. It sits
                // beside the dash rather than inside it because the field being typed into is just as
                // often on a world panel or an inspector. -xlinka
                SpawnKeyboard(userspaceRoot);

                // The hand menu's overlay shaders, drawn unseen once so its first open does not compile them.
                userspaceRoot.AddSlot("Overlay Warm-Up").AttachComponent<OverlayShaderWarmUp>();

                // The focused world's chat in the corner of a screen. It follows whichever world has focus.
                userspaceRoot.AddSlot("Chat Box").AttachComponent<Components.Chat.DesktopChatBox>();

                // The radial context menu lives per-user in the game world
                // (built by AvatarAssembler, opened by HandTool). Items
                // come from ContextMenuItemSource components at open time.
                Logger.Debug("Userspace: Userspace world initialized");
            });

            // The local user only exists AFTER LocalWorld returns - the init callback above runs
            // before the host user is created - so the pointer rig, which binds a UserRoot to that
            // user, is built here rather than in the callback. -xlinka
            BuildPointerRig(world);

            // Register with WorldManager as userspace world
            // (UserspaceWorld setter automatically calls PrivateOverlayWorld which adds to managed worlds)
            engine.WorldManager.UserspaceWorld = world;
            // A favourite keyboard known before this space was, from a sign in that came first.
            FavouriteKeyboard.Refresh();

            Logger.Log("Userspace: Userspace setup complete");
            return world;
        }

        // The built-in keyboard, parked hidden. Also what comes back when a favourite keyboard goes away.
        public static Slot SpawnKeyboard(Slot userspaceRoot)
        {
            var keyboardSlot = userspaceRoot.AddSlot("Virtual Keyboard");
            keyboardSlot.ActiveSelf.Value = false;
            keyboardSlot.AttachComponent<VrKeyboard>();
            return keyboardSlot;
        }

        // Userspace-owned pointer rig: a stripped proxy avatar that lives in the overlay world and
        // owns the dash pointer outright. It has a UserRoot, a head node, and two controller-tracked
        // hands, each carrying a pointer - and nothing else (no avatar mesh, no camera, no
        // locomotion). Because the rig belongs to userspace and never to a game world, the dash
        // keeps a working cursor even right after you delete the world you were standing in, which
        // used to take the borrowed avatar laser down with it. On desktop the right hand aims off
        // the platform free-cursor ray; in VR both hands aim off their tracked controllers. -xlinka
        private static void BuildPointerRig(World world)
        {
            var userspaceRoot = world.RootSlot.FindChild("UserspaceRoot");
            if (userspaceRoot == null)
            {
                Logger.Error("Userspace: UserspaceRoot missing, cannot build pointer rig");
                return;
            }

            if (world.LocalUser == null)
            {
                Logger.Error("Userspace: LocalUser missing, cannot build pointer rig");
                return;
            }

            var rigSlot = userspaceRoot.AddSlot("PointerUser");
            var userRoot = rigSlot.AttachComponent<UserRoot>();
            userRoot.Initialize(world.LocalUser);
            // Bind explicitly as well: a local world reports no network authority, so the
            // authority-gated bind inside Initialize may not fire. This makes it deterministic so
            // the controller positioners resolve their UserRoot and actually track. -xlinka
            world.LocalUser.Root = userRoot;

            var bodyNodes = rigSlot.AddSlot("Body Nodes");

            // Head node tracks the HMD so the cursor billboards toward the viewer in VR and the rig
            // has a head to sit relative to. No HeadOutput here, so it never spawns a camera. -xlinka
            var head = bodyNodes.AddSlot("Head");
            head.LocalPosition.Value = new float3(0f, 1.6f, 0f);
            var headPositioner = head.AttachComponent<TrackedDevicePositioner>();
            headPositioner.AutoBodyNode.Value = BodyNode.Head;

            BuildPointerHand(bodyNodes, Chirality.Left);
            BuildPointerHand(bodyNodes, Chirality.Right);

            // Keep the rig overlaying the focused user so the VR controller rays line up with the
            // dash. When nothing is focused (you just deleted the world you were in) it holds its
            // last pose - exactly what keeps the dash pointable through the gap. -xlinka
            rigSlot.AttachComponent<UserspaceViewMirror>();
        }

        private static void BuildPointerHand(Slot bodyNodes, Chirality side)
        {
            bool isLeft = side == Chirality.Left;

            var controller = bodyNodes.AddSlot(isLeft ? "LeftController" : "RightController");
            var positioner = controller.AttachComponent<TrackedDevicePositioner>();
            positioner.AutoBodyNode.Value = isLeft ? BodyNode.LeftController : BodyNode.RightController;

            var pointer = controller.AttachComponent<UserspacePointer>();
            pointer.Side.Value = side;

            // A hand that can point can also pick up userspace items (the keyboard); the pointer
            // drives it off the grab action, and the dash itself refuses grabs so only things that
            // opt in ride the hand.
            controller.AttachComponent<Grabber>();

            // And poke them: the keyboard's keys and the dash surface take a fingertip through the
            // same source every world canvas does. No probe on this rig, so it reads the tracked
            // hand's index tip, or the controller when nothing feeds fingers.
            var touch = controller.AttachComponent<Components.Touch.CanvasTouchSource>();
            touch.Hand.Value = side;
            touch.HandRoot.Target = controller;
        }
    }
}
