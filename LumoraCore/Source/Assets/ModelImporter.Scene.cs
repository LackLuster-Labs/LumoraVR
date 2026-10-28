// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Components;
using Lumora.Core.Logging;
using Lumora.Core.Math;

namespace Lumora.Core.Assets;

public static partial class ModelImporter
{
    // The lights and cameras a file carries, taken out of the parse as plain data and built once the model has
    // its final size.
    //
    // Each sits on the node the file hangs it from, aimed by the file's own direction and up for it (glTF aims
    // down the node's -Z, FBX down -Y or +X), turned through the component's RotationOffset so the node keeps
    // its transform. -xlinka
    private sealed class SceneExtras
    {
        private sealed record LightData(string Name, LightType Type, color Color, float Brightness, float3 Position,
            float3 Direction, float3 Up, float ConeFull);

        private sealed record CameraData(string Name, float3 Position, float3 Direction, float3 Up, float HorizontalFov,
            float Aspect, float Near, float Far);

        private readonly List<LightData> _lights = new();
        private readonly List<CameraData> _cameras = new();
        private bool _physicalUnits;
        private int _skippedLights;

        public bool IsEmpty => _lights.Count == 0 && _cameras.Count == 0;

        public static SceneExtras? Capture(Assimp.Scene scene, ModelImportSettings settings, string hint)
        {
            if (!settings.ImportLights && !settings.ImportCameras)
                return null;

            // glTF is the one format with units: candela for point and spot, lux for the sun, and its spot cone
            // and lens angles arrive as half and full angles where every other format hands the opposite.
            bool gltf = hint is ".glb" or ".gltf";
            var extras = new SceneExtras { _physicalUnits = gltf };

            if (settings.ImportLights && scene.HasLights)
            {
                foreach (var l in scene.Lights)
                {
                    LightType type;
                    switch (l.LightType)
                    {
                        case Assimp.LightSourceType.Directional: type = LightType.Directional; break;
                        case Assimp.LightSourceType.Point: type = LightType.Point; break;
                        case Assimp.LightSourceType.Spot: type = LightType.Spot; break;
                        default:
                            // Ambient and area lights have nothing here to become.
                            extras._skippedLights++;
                            continue;
                    }
                    var c = l.ColorDiffuse;
                    float brightness = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
                    var tint = brightness > 0f ? new color(c.X / brightness, c.Y / brightness, c.Z / brightness, 1f) : new color(1f, 1f, 1f, 1f);
                    float cone = gltf ? l.AngleOuterCone * 2f : l.AngleOuterCone;
                    extras._lights.Add(new LightData(l.Name ?? "", type, tint, brightness, V(l.Position), V(l.Direction), V(l.Up), cone));
                }
            }

            if (settings.ImportCameras && scene.HasCameras)
            {
                foreach (var cam in scene.Cameras)
                {
                    float hfov = gltf ? cam.FieldOfview : cam.FieldOfview * 2f;
                    extras._cameras.Add(new CameraData(cam.Name ?? "", V(cam.Position), V(cam.Direction), V(cam.Up), hfov,
                        cam.AspectRatio, cam.ClipPlaneNear, cam.ClipPlaneFar));
                }
            }
            return extras;
        }

        private static float3 V(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

        // On the world thread, after the rescale. fileUnit is metres per file unit as the file declares it.
        public void Build(Slot model, Dictionary<string, Slot> nameToSlot, float fileUnit)
        {
            if (model == null || model.IsDestroyed)
                return;
            // World metres per file unit, and per metre the file meant.
            float worldPerUnit = MathF.Abs(model.GlobalScale.x);
            float worldPerMetre = worldPerUnit / (fileUnit > 0f ? fileUnit : 1f);

            foreach (var data in _lights)
            {
                var light = Host(model, nameToSlot, data.Name, data.Position, "Light").AttachComponent<Light>();
                light.Type.Value = data.Type;
                light.LightColor.Value = data.Color;
                light.RotationOffset.Value = Aim(data.Direction, data.Up);
                // An imported lamp casts no shadow until somebody says it should: a model with a dozen lamps
                // would otherwise add six shadow renders for every one of them.
                light.Shadows.Value = ShadowType.None;
                if (data.Type == LightType.Directional)
                {
                    light.Intensity.Value = System.Math.Clamp(_physicalUnits ? data.Brightness / LuxPerIntensity : data.Brightness, 0.05f, 8f);
                    continue;
                }

                // A lamp is drawn with the falloff its file was lit with, inverse square, and its brightness becomes
                // how far it reaches: Range is where it has fallen to a twenty-fifth. Candela for glTF; anything
                // else carries no unit, and watts-like numbers are what exporters write there.
                light.Falloff.Value = LightFalloff.Inverse;
                light.Intensity.Value = 1f;
                float reach = (_physicalUnits ? RangePerRootCandela : RangePerRootWatt) * MathF.Sqrt(MathF.Max(0f, data.Brightness));
                light.Range.Value = System.Math.Clamp(reach, 0.25f, 200f) * worldPerMetre;
                if (data.Type == LightType.Spot)
                    light.SpotAngle.Value = System.Math.Clamp(data.ConeFull * 180f / MathF.PI, 1f, 179f);
            }

            foreach (var data in _cameras)
            {
                var camera = Host(model, nameToSlot, data.Name, data.Position, "Camera").AttachComponent<Camera>();
                camera.RotationOffset.Value = Aim(data.Direction, data.Up);
                // The component's angle is the vertical one; files hand the horizontal one and the frame's shape.
                float aspect = data.Aspect > 0.01f ? data.Aspect : 16f / 9f;
                float hfov = data.HorizontalFov > 0f ? data.HorizontalFov : 60f * MathF.PI / 180f;
                float vfov = 2f * MathF.Atan(MathF.Tan(hfov * 0.5f) / aspect);
                camera.FieldOfView.Value = System.Math.Clamp(vfov * 180f / MathF.PI, 1f, 179f);
                if (data.Near > 0f)
                    camera.NearClip.Value = MathF.Max(0.001f, data.Near * worldPerUnit);
                if (data.Far > data.Near && data.Far > 0f)
                    camera.FarClip.Value = MathF.Max(camera.NearClip.Value * 2f, data.Far * worldPerUnit);
            }

            Logger.Debug($"ModelImporter: {_lights.Count} light(s), {_cameras.Count} camera(s) on '{model.SlotName.Value}'"
                + (_skippedLights > 0 ? $", {_skippedLights} ambient/area light(s) left out" : ""));
        }

        // A sun of strength 3 in the usual modelling tool exports as 2049 lux and lands near the Grid sun (1.9); a
        // 1000 W bulb exports as 54351 cd and reaches 10 m, and the same bulb through FBX (as 1000) reaches 10 m too.
        private const float LuxPerIntensity = 1000f;
        private const float RangePerRootCandela = 0.0429f;
        private const float RangePerRootWatt = 0.316f;

        // The file's node for it, or a child of it when the file puts it somewhere off the node's origin.
        private static Slot Host(Slot model, Dictionary<string, Slot> nameToSlot, string name, float3 position, string kind)
        {
            if (!nameToSlot.TryGetValue(name, out var node) || node == null || node.IsDestroyed)
                node = model;
            bool offset = position.LengthSquared > 1e-10f;
            if (!offset && node != model && node.GetComponent<Light>() == null && node.GetComponent<Camera>() == null)
                return node;
            var slot = node.AddSlot(string.IsNullOrEmpty(name) ? kind : $"{name} {kind}");
            slot.LocalPosition.Value = position;
            return slot;
        }

        // The turn taking the component's -Z onto direction with +Y as near up as it will go. A zero direction (a
        // point light) aims nowhere and turns nothing.
        private static floatQ Aim(float3 direction, float3 up)
        {
            if (direction.LengthSquared < 1e-10f)
                return floatQ.Identity;
            var forward = direction.Normalized;
            var back = -forward;
            var upHint = up.LengthSquared > 1e-10f ? up.Normalized : float3.Up;
            if (MathF.Abs(float3.Dot(upHint, back)) > 0.999f)
                upHint = MathF.Abs(back.y) < 0.9f ? float3.Up : float3.Right;
            var right = float3.Cross(upHint, back).Normalized;
            var trueUp = float3.Cross(back, right);
            return FromBasis(right, trueUp, back);
        }

        // Columns x, y, z of a rotation matrix to its quaternion.
        private static floatQ FromBasis(float3 x, float3 y, float3 z)
        {
            float m00 = x.x, m01 = y.x, m02 = z.x;
            float m10 = x.y, m11 = y.y, m12 = z.y;
            float m20 = x.z, m21 = y.z, m22 = z.z;
            float trace = m00 + m11 + m22;
            float qx, qy, qz, qw;
            if (trace > 0f)
            {
                float s = MathF.Sqrt(trace + 1f) * 2f;
                qw = 0.25f * s;
                qx = (m21 - m12) / s;
                qy = (m02 - m20) / s;
                qz = (m10 - m01) / s;
            }
            else if (m00 > m11 && m00 > m22)
            {
                float s = MathF.Sqrt(1f + m00 - m11 - m22) * 2f;
                qw = (m21 - m12) / s;
                qx = 0.25f * s;
                qy = (m01 + m10) / s;
                qz = (m02 + m20) / s;
            }
            else if (m11 > m22)
            {
                float s = MathF.Sqrt(1f + m11 - m00 - m22) * 2f;
                qw = (m02 - m20) / s;
                qx = (m01 + m10) / s;
                qy = 0.25f * s;
                qz = (m12 + m21) / s;
            }
            else
            {
                float s = MathF.Sqrt(1f + m22 - m00 - m11) * 2f;
                qw = (m10 - m01) / s;
                qx = (m02 + m20) / s;
                qy = (m12 + m21) / s;
                qz = 0.25f * s;
            }
            return new floatQ(qx, qy, qz, qw).Normalized;
        }
    }
}
