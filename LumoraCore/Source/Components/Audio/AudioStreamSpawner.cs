// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Lumora.Core.Audio;
using Lumora.Core.Math;
using Lumora.Core.Networking.Streams;

namespace Lumora.Core.Components.Audio;

// Puts one of the local user's input devices into the world as an item.
//
// One per person: spawning again destroys whatever they had out. The stream itself is reused rather
// than re-minted, because a stream belongs to its owner for the life of the session and churning them
// would leave a trail of dead entries in the user's stream table. -xlinka
public static class AudioStreamSpawner
{
    // Distinct from the voice stream's name on purpose: name-based lookup is how peers agree which
    // stream is which, and a device stream is not a voice.
    public const string StreamName = "AudioStream";

    public static AudioStreamController? SpawnForLocalUser(World world, int bitrate, AudioInput? input)
    {
        if (world?.RootSlot == null)
            return null;

        var localUser = world.LocalUser;
        if (localUser == null)
            return null;

        DestroyExisting(world, localUser);

        var stream = localUser.GetStreamOrAdd<OpusStream>(StreamName);
        if (stream == null)
            return null;

        // Stereo, because what goes into this is a device and not a mouth.
        stream.Channels.Value = 2;
        // A spawned stream is somebody sharing what their machine is playing. Stereo at 128 kbit was
        // already right; the codec was still in speech mode underneath it, which is what made shared
        // music sound thin and smeared however high the bitrate went. -xlinka
        stream.Content.Value = Lumora.Audio.OpusContent.Music;
        stream.BitRate.Value = SettingsCatalog.DefaultStreamBitrateKbps.ClampInt(bitrate) * 1000;

        float3 position;
        floatQ rotation;
        var userRoot = localUser.Root;
        if (userRoot?.HeadSlot != null)
        {
            // -Z (Backward) is the view direction in our head convention; +Z is behind the user.
            position = userRoot.HeadPosition + userRoot.HeadRotation * (float3.Backward * 1.0f);
            rotation = userRoot.HeadRotation;
        }
        else
        {
            position = new float3(0f, 1f, 1f);
            rotation = floatQ.Identity;
        }

        using var own = world.EnterSpawnAllocation();
        var slot = world.RootSlot.AddSlot("Audio Stream");
        slot.GlobalPosition = position;
        slot.GlobalRotation = rotation;
        // A live feed off one person's machine: it means nothing in a saved world, and it goes when they do.
        slot.Persistent.Value = false;
        slot.AttachComponent<Lumora.Core.Components.Utility.DestroyWhenUserLeaves>().TargetUser.Target = localUser;

        // An item, like everything else somebody puts in a room: it has a root, it has a name, and a
        // hand can take hold of it and move it. It had none of that, so it sat wherever it spawned and
        // could not be picked up or put anywhere. -xlinka
        var root = slot.GetOrAttachComponent<ObjectRoot>();
        root.ObjectName.Value = input != null
            ? $"{localUser.UserName.Value} - {input.Name}"
            : $"{localUser.UserName.Value} Audio Stream";

        var grab = slot.GetOrAttachComponent<Lumora.Core.Components.Grabbable>();
        grab.AllowGrab.Value = true;
        grab.Scalable.Value = true;

        var source = slot.AttachComponent<UserAudioStream>();
        source.Stream.Target = stream;
        // Raw, not filtered: the gate and the normalizer are shaped for speech.
        source.UseFilteredData.Value = false;
        // Not a voice, so the microphone mute and the host's silencing do not reach it.
        source.IsVoiceCapture.Value = false;
        source.PreferredInput = input;

        var controller = slot.AttachComponent<AudioStreamController>();
        controller.Stream.Target = stream;
        controller.Source.Target = source;
        controller.DeviceName.Value = input?.Name ?? string.Empty;
        controller.DeviceKind.Value = input?.InputType ?? AudioInputType.Unknown;
        controller.Configure();
        return controller;
    }

    private static void DestroyExisting(World world, User localUser)
    {
        var found = new List<AudioStreamController>();
        world.RootSlot.GetComponentsInChildren(found);
        for (int i = 0; i < found.Count; i++)
        {
            var controller = found[i];
            if (controller == null || controller.IsDestroyed)
                continue;
            var owner = controller.Owner;
            if (owner != null && ReferenceEquals(owner, localUser))
                controller.Slot?.Destroy();
        }
    }
}
