// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Math;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks;

// Applies a gradient sky to the Godot WorldEnvironment, gated on FocusManager
// focus. Worlds aren't unloaded when the user switches between them, so the
// hook also lives across switches. We attach our gradient only while our world
// is the focused one and restore the previous env when focus moves away. - xlinka
public sealed class GradientSkyboxHook : ComponentHook<GradientSkybox>
{
    private const string ShaderPath = "res://Shaders/GradientSkybox.gdshader";

    private WorldEnvironment _worldEnvironment;
    private global::Godot.Environment _previousEnvironment;
    private bool _ownsWorldEnvironment;
    private global::Godot.Environment _environment;
    private Sky _sky;
    private ShaderMaterial _skyMaterial;
    private FocusManager _focusManager;
    private bool _gradientActive;

    public static IHook<GradientSkybox> Constructor() => new GradientSkyboxHook();

    public override void Initialize()
    {
        base.Initialize();

        _worldEnvironment = FindWorldEnvironment();
        if (_worldEnvironment == null)
        {
            _worldEnvironment = new WorldEnvironment { Name = "WorldEnvironment" };
            attachedNode.GetTree().Root.AddChild(_worldEnvironment);
            _ownsWorldEnvironment = true;
        }

        // Snapshot once: this is whatever the WE shows when our world isn't
        // focused (bootstrap default, or another hook's env if we're stacked). - xlinka
        _previousEnvironment = _worldEnvironment.Environment;

        _environment = _previousEnvironment?.Duplicate() as global::Godot.Environment ?? new global::Godot.Environment();
        _sky = new Sky();
        _skyMaterial = new ShaderMaterial();

        if (ResourceLoader.Exists(ShaderPath))
        {
            _skyMaterial.Shader = GD.Load<Shader>(ShaderPath);
        }
        else
        {
            LumoraLogger.Warn($"GradientSkyboxHook: Sky shader not found at {ShaderPath}");
        }

        _sky.SkyMaterial = _skyMaterial;
        _environment.BackgroundMode = global::Godot.Environment.BGMode.Sky;
        _environment.Sky = _sky;
        // Sky still drives ambient color, but reflections are explicitly killed
        // so the bright sun disc inside the shader doesn't mirror onto glossy
        // floors as a hard hotspot. Specular highlights now come only from real
        // lights (DirectionalLight, etc.), not from the sky. - xlinka
        _environment.AmbientLightSource = global::Godot.Environment.AmbientSource.Sky;
        _environment.ReflectedLightSource = global::Godot.Environment.ReflectionSource.Disabled;

        ApplyChanges();

        _focusManager = Lumora.Core.Engine.Current?.FocusManager;
        if (_focusManager != null)
        {
            _focusManager.OnFocusedWorldChanged += OnFocusedWorldChanged;
            if (_focusManager.FocusedWorld == Owner.World)
            {
                ApplyGradient();
            }
        }
        else
        {
            // No focus manager available (single-world boot path), apply directly.
            ApplyGradient();
        }
    }

    public override void ApplyChanges()
    {
        if (_environment == null || _skyMaterial == null)
        {
            return;
        }

        SetColor("top_color", Owner.TopColor.Value);
        SetColor("horizon_color", Owner.HorizonColor.Value);
        SetColor("bottom_color", Owner.BottomColor.Value);
        SetColor("sun_color", Owner.SunColor.Value);

        var sunDirection = Owner.SunDirection.Value;
        _skyMaterial.SetShaderParameter("sun_direction", new Vector3(sunDirection.x, sunDirection.y, sunDirection.z));
        _skyMaterial.SetShaderParameter("sun_size", Owner.SunSize.Value);
        _skyMaterial.SetShaderParameter("sun_intensity", Owner.SunIntensity.Value);
        _skyMaterial.SetShaderParameter("sun_glow_power", Owner.SunGlowPower.Value);

        _environment.AmbientLightEnergy = Owner.AmbientEnergy.Value;
    }

    private void OnFocusedWorldChanged(World oldWorld, World newWorld)
    {
        if (newWorld == Owner.World)
        {
            ApplyGradient();
        }
        else if (oldWorld == Owner.World)
        {
            RestorePreviousEnv();
        }
    }

    private void ApplyGradient()
    {
        if (_gradientActive || _environment == null) return;
        if (_worldEnvironment == null || !GodotObject.IsInstanceValid(_worldEnvironment)) return;

        _gradientActive = true;
        _worldEnvironment.Environment = _environment;
    }

    private void RestorePreviousEnv()
    {
        if (!_gradientActive) return;
        _gradientActive = false;

        if (_worldEnvironment == null || !GodotObject.IsInstanceValid(_worldEnvironment)) return;

        // Only stomp the env if it's still ours. If another hook has taken over
        // since (someone else's world got focus first), leave it alone. - xlinka
        if (_worldEnvironment.Environment != _environment) return;

        _worldEnvironment.Environment = _previousEnvironment;
    }

    private WorldEnvironment FindWorldEnvironment()
    {
        return attachedNode.GetTree()?.Root?.FindChild("WorldEnvironment", true, false) as WorldEnvironment;
    }

    private void SetColor(string uniform, color value)
    {
        _skyMaterial.SetShaderParameter(uniform, new Color(value.r, value.g, value.b, value.a));
    }

    public override void Destroy(bool destroyingWorld)
    {
        if (_focusManager != null)
        {
            _focusManager.OnFocusedWorldChanged -= OnFocusedWorldChanged;
            _focusManager = null;
        }

        RestorePreviousEnv();

        if (_ownsWorldEnvironment && _worldEnvironment != null && GodotObject.IsInstanceValid(_worldEnvironment))
        {
            _worldEnvironment.QueueFree();
        }

        _environment?.Dispose();
        _sky?.Dispose();
        _skyMaterial?.Dispose();

        _worldEnvironment = null;
        _previousEnvironment = null;
        _environment = null;
        _sky = null;
        _skyMaterial = null;

        base.Destroy(destroyingWorld);
    }
}
