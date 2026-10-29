// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumora.Core.Assets;
using Lumora.Core.Components;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Math;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Photos;

// Who took a photo, from where, through what.
public readonly record struct PhotoFacts(User? TakenBy, string Camera, float3 Position, floatQ Rotation, float FieldOfView);

// A photo as a thing in the world: the picture on a card its own shape, to pick up and keep, knowing who took
// it and where. Kept in the local store first, so the address travels and everyone fetches the picture. Also
// saved to the inventory's Photos folder when the setting says so. A world that will not take new things
// still gets the photo into the inventory, made aside and gone again. -xlinka
public static class PhotoPrint
{
    public const string PhotosFolder = "Photos";

    public static async Task<Slot?> MakeAsync(World world, byte[] image, ImageEncoding encoding, float3 position, floatQ rotation,
        float width, PhotoFacts facts, PhotoShape shape = PhotoShape.Flat, bool inWorld = true)
    {
        await PhotoLibrary.SaveAsync(image, encoding).ConfigureAwait(false);
        var db = Engine.Current?.LocalDB;
        if (db == null || image.Length == 0)
            return null;
        string address = await db.SaveAssetAsync(image, encoding.Extension()).ConfigureAwait(false);
        await WorldContext.ToWorld();
        if (world.IsDisposed)
            return null;

        Slot? slot = null;
        bool aside = false;
        try
        {
            if (!inWorld)
                throw new UnauthorizedAccessException();
            slot = world.AddSpawnSlot(world.RootSlot, "Photo");
        }
        catch (UnauthorizedAccessException)
        {
            slot = world.RootSlot.AddLocalSlot("Photo");
            aside = true;
        }
        slot.GlobalPosition = position;
        slot.GlobalRotation = rotation;
        switch (shape)
        {
            case PhotoShape.Stereo:
                BuildStereo(slot, new Uri(address), width);
                break;
            case PhotoShape.Panorama:
                BuildPanorama(slot, new Uri(address), width);
                break;
            default:
                Build(slot, new Uri(address), width, shape == PhotoShape.Object);
                break;
        }
        Describe(slot, world, facts);

        if (EngineSettings.PhotosToInventory && Inventory.IsSignedIn)
            await KeepAsync(slot, image);
        else if (aside)
            LumoraLogger.Log("Photos: no world or inventory copy was kept; the local photo save was handled separately");
        if (aside && !slot.IsDestroyed)
            slot.Destroy();
        return aside ? null : slot;
    }

    // The card: a quad the picture's shape at the width asked, both faces showing it, to grab and scale. An
    // object on its own keeps its clear background.
    private static void Build(Slot slot, Uri address, float width, bool clear)
    {
        var quad = slot.AttachComponent<QuadMesh>();
        quad.Size.Value = new float2(width, width * 9f / 16f);
        quad.DualSided.Value = false;
        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = quad;
        var picture = slot.AttachComponent<ImageProvider>();
        picture.URL.Value = address;
        var collider = slot.AttachComponent<BoxCollider>();
        collider.Size.Value = new float3(width, width * 9f / 16f, 0.005f);
        var grab = slot.AttachComponent<Grabbable>();
        grab.Scalable.Value = true;
        var size = slot.AttachComponent<TextureSizeDriver>();
        size.Source.Target = picture;
        size.Target.Target = quad;
        size.ColliderTarget.Target = collider;
        size.MaxSide.Value = width;
        var material = slot.AttachComponent<UnlitMaterial>();
        material.Texture.Target = picture;
        material.BlendMode.Value = clear ? BlendMode.Alpha : BlendMode.Opaque;
        material.Culling.Value = clear ? Culling.None : Culling.Back;
        renderer.Material.Target = material;
        // A clear background would show the other face through it, mirrored. -xlinka
        if (!clear)
            OtherFace(slot, quad, material);
    }

    // Unlit draws both faces of a quad whatever its culling says, so from behind the card showed its picture
    // mirrored. The same picture turned half a turn a millimetre behind is the nearer face from that side and
    // reads the right way round. -xlinka
    private static void OtherFace(Slot slot, QuadMesh quad, MaterialProvider material)
    {
        var back = slot.AddSlot("Back");
        back.LocalPosition.Value = new float3(0f, 0f, -0.001f);
        back.LocalRotation.Value = floatQ.AxisAngleRad(float3.Up, MathF.PI);
        var renderer = back.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = quad;
        renderer.Material.Target = material;
    }

    // A picture per eye, side by side: each eye sees its own half on a card one eye's shape.
    private static void BuildStereo(Slot slot, Uri address, float width)
    {
        var quad = slot.AttachComponent<QuadMesh>();
        quad.Size.Value = new float2(width, width * 9f / 16f);
        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = quad;
        var picture = slot.AttachComponent<ImageProvider>();
        picture.URL.Value = address;
        var collider = slot.AttachComponent<BoxCollider>();
        collider.Size.Value = new float3(width, width * 9f / 16f, 0.005f);
        var grab = slot.AttachComponent<Grabbable>();
        grab.Scalable.Value = true;
        var material = slot.AttachComponent<UnlitMaterial>();
        material.Texture.Target = picture;
        material.Culling.Value = Culling.Back;
        material.StereoTextureTransform.Value = true;
        material.LeftEyeTextureScale.Value = new float2(0.5f, 1f);
        material.LeftEyeTextureOffset.Value = float2.Zero;
        material.RightEyeTextureScale.Value = new float2(0.5f, 1f);
        material.RightEyeTextureOffset.Value = new float2(0.5f, 0f);
        renderer.Material.Target = material;
        OtherFace(slot, quad, material);
    }

    // All the way round, on a ball as wide as asked: the picture wraps it, the middle of the picture at its
    // front.
    private static void BuildPanorama(Slot slot, Uri address, float width)
    {
        float radius = width * 0.5f;
        var sphere = slot.AttachComponent<SphereMesh>();
        sphere.Radius.Value = radius;
        sphere.Segments.Value = 48;
        sphere.Rings.Value = 24;
        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = sphere;
        var picture = slot.AttachComponent<ImageProvider>();
        picture.URL.Value = address;
        picture.WrapModeU.Value = TextureWrapMode.Repeat;
        var collider = slot.AttachComponent<SphereCollider>();
        collider.Radius.Value = radius;
        var grab = slot.AttachComponent<Grabbable>();
        grab.Scalable.Value = true;
        var material = slot.AttachComponent<UnlitMaterial>();
        material.Texture.Target = picture;
        material.Culling.Value = Culling.None;
        renderer.Material.Target = material;
    }

    private static void Describe(Slot slot, World world, PhotoFacts facts)
    {
        var details = slot.AttachComponent<PhotoDetails>();
        details.TakenBy.Value = facts.TakenBy?.UserName.Value ?? string.Empty;
        details.TakenById.Value = facts.TakenBy?.UserID.Value ?? string.Empty;
        details.TakenAt.Value = DateTime.UtcNow.ToString("o");
        details.WorldName.Value = Lumora.Core.Management.WorldManager.DisplayName(world);
        details.Camera.Value = facts.Camera;
        details.Position.Value = facts.Position;
        details.Rotation.Value = facts.Rotation;
        details.FieldOfView.Value = facts.FieldOfView;
        var people = new List<User>();
        world.GetAllUsers(people);
        foreach (var user in people)
        {
            if (!user.IsDestroyed && !string.IsNullOrEmpty(user.UserName.Value))
                details.People.Add(user.UserName.Value!);
        }
        slot.Name.Value = $"Photo in {world.Name}";
    }

    // Into the Photos folder, the picture as its card.
    private static async Task KeepAsync(Slot slot, byte[] image)
    {
        string? folder = await Inventory.TopFolderAsync(PhotosFolder).ConfigureAwait(false);
        await WorldContext.ToWorld();
        if (slot.IsDestroyed)
            return;
        var result = await Inventory.SaveItemAsync(slot, slot.Name.Value, folder ?? Inventory.Root, image);
        await WorldContext.ToWorld();
        // A photo is kept on its own, every time; an account that cannot save yet hears it on the inventory and the
        // account card instead of a warning per picture.
        if (!result.Ok && result.Code != AccountEmail.NotVerifiedCode)
            LumoraLogger.Warn($"Photos: not kept in the inventory: {result.Message}");
    }
}
