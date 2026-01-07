using Godot;
using Lumora.Core.Components;
using Lumora.Core.Math;
using System;

namespace Aquamarine.Godot.Hooks;

/// <summary>
/// Hook for Light component → Godot Light3D.
/// Platform light hook for Godot.
/// </summary>
public class LightHook : ComponentHook<Light>
{
    private Node3D _lightContainer;
    private Light3D _light;

    public Light3D GodotLight => _light;

    public override void Initialize()
    {
        base.Initialize();

        _lightContainer = new Node3D();
        _lightContainer.Name = "LightContainer";
        attachedNode.AddChild(_lightContainer);

        CreateLight(Owner.Type.Value);
    }

    private void CreateLight(LightType type)
    {
        if (_light != null && GodotObject.IsInstanceValid(_light))
        {
            _light.QueueFree();
        }

        switch (type)
        {
            case LightType.Directional:
                _light = new DirectionalLight3D();
                _light.Name = "DirectionalLight";
                break;

            case LightType.Point:
                _light = new OmniLight3D();
                _light.Name = "PointLight";
                break;

            case LightType.Spot:
                _light = new SpotLight3D();
                _light.Name = "SpotLight";
                break;

            default:
                throw new ArgumentException($"Unknown light type: {type}");
        }

        _lightContainer.AddChild(_light);
    }

    public override void ApplyChanges()
    {
        if (Owner.Type.GetWasChangedAndClear())
        {
            CreateLight(Owner.Type.Value);
        }

        color lightColor = Owner.LightColor.Value;
        _light.LightColor = new Color(lightColor.r, lightColor.g, lightColor.b, lightColor.a);

        _light.LightEnergy = Owner.Intensity.Value;

        if (_light is OmniLight3D omni)
        {
            omni.OmniRange = Owner.Range.Value;
        }
        else if (_light is SpotLight3D spot)
        {
            spot.SpotRange = Owner.Range.Value;
            spot.SpotAngle = Owner.SpotAngle.Value;
        }

        UpdateShadows();

        _light.Visible = Owner.Enabled.Value;
    }

    private void UpdateShadows()
    {
        switch (Owner.Shadows.Value)
        {
            case ShadowType.None:
                _light.ShadowEnabled = false;
                break;

            case ShadowType.Hard:
                _light.ShadowEnabled = true;
                break;

            case ShadowType.Soft:
                _light.ShadowEnabled = true;
                break;
        }

        _light.ShadowOpacity = 1f - Owner.ShadowStrength.Value;
        _light.ShadowBias = Owner.ShadowBias.Value;
        _light.ShadowNormalBias = Owner.ShadowNormalBias.Value;
    }

    public override void Destroy(bool destroyingWorld)
    {
        if (!destroyingWorld && _lightContainer != null && GodotObject.IsInstanceValid(_lightContainer))
        {
            _lightContainer.QueueFree();
        }
        _lightContainer = null;
        _light = null;

        base.Destroy(destroyingWorld);
    }
}
