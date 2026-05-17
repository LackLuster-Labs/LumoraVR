// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Godot;
using Lumora.Core;
using LumoraParticleSystem = Lumora.Core.Components.ParticleSystem;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks;

/// <summary>
/// CPU-simulated engine particle renderer backed by a Godot MultiMesh draw call.
/// </summary>
public sealed partial class ParticleSystemHook : ComponentHook<LumoraParticleSystem>
{
    private const string ShaderPath = "res://Shaders/EngineParticle.gdshader";
    private const int HardMaxParticles = 4096;

    private ParticleProcessNode _processNode;
    private MultiMeshInstance3D _instance;
    private MultiMesh _multiMesh;
    private global::Godot.SphereMesh _particleMesh;
    private ShaderMaterial _material;
    private Particle[] _particles = Array.Empty<Particle>();
    private int _activeCount;
    private uint _rngState;
    private float _emissionAccumulator;
    private float _burstTimer;

    public static IHook<LumoraParticleSystem> Constructor() => new ParticleSystemHook();

    public override void Initialize()
    {
        base.Initialize();

        _rngState = NormalizeSeed(Owner.Seed.Value);

        _processNode = new ParticleProcessNode(this)
        {
            Name = "ParticleSystem"
        };
        attachedNode.AddChild(_processNode);

        _particleMesh = new global::Godot.SphereMesh
        {
            Radius = 1.0f,
            Height = 2.0f,
            RadialSegments = 8,
            Rings = 4
        };

        _material = new ShaderMaterial();
        if (ResourceLoader.Exists(ShaderPath))
        {
            _material.Shader = GD.Load<Shader>(ShaderPath);
        }
        else
        {
            LumoraLogger.Warn($"ParticleSystemHook: Particle shader not found at {ShaderPath}");
        }

        _multiMesh = new MultiMesh
        {
            Mesh = _particleMesh
        };

        _instance = new MultiMeshInstance3D
        {
            Name = "ParticleMultiMesh",
            Multimesh = _multiMesh,
            MaterialOverride = _material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        _processNode.AddChild(_instance);

        ApplyChanges();
    }

    public override void ApplyChanges()
    {
        if (_multiMesh == null || _instance == null)
        {
            return;
        }

        if (Owner.Seed.GetWasChangedAndClear())
        {
            _rngState = NormalizeSeed(Owner.Seed.Value);
            _activeCount = 0;
            _emissionAccumulator = 0f;
            _burstTimer = 0f;
        }

        EnsureCapacity();
        ApplyBounds();

        var renderQueue = NormalizeRenderQueue(Owner.RenderQueue.Value);
        _material.RenderPriority = renderQueue;
        _instance.SortingOffset = renderQueue;
        _instance.Visible = Owner.Enabled.Value;
        _material.SetShaderParameter("emission_strength", Owner.EmissionStrength.Value);
    }

    private void EnsureCapacity()
    {
        int maxParticles = Math.Clamp(Owner.MaxParticles.Value, 1, HardMaxParticles);
        if (_particles.Length == maxParticles)
        {
            return;
        }

        var oldParticles = _particles;
        int oldActive = _activeCount;
        _particles = new Particle[maxParticles];
        _activeCount = Math.Min(oldActive, maxParticles);

        for (int i = 0; i < _activeCount; i++)
        {
            _particles[i] = oldParticles[i];
        }

        _multiMesh.InstanceCount = 0;
        _multiMesh.TransformFormat = MultiMesh.TransformFormatEnum.Transform3D;
        _multiMesh.UseColors = true;
        _multiMesh.InstanceCount = maxParticles;
        _multiMesh.VisibleInstanceCount = _activeCount;
    }

    private void ApplyBounds()
    {
        var extents = Owner.EmitterExtents.Value;
        float height = Math.Max(Owner.InitialSpeed.Value * Owner.Lifetime.Value * 1.8f, 1f);
        _instance.CustomAabb = new Aabb(
            new Vector3(-extents.x, -0.25f, -extents.z),
            new Vector3(extents.x * 2f, height, extents.z * 2f));
    }

    internal void Step(float delta)
    {
        if (_multiMesh == null || _particles.Length == 0)
        {
            return;
        }

        if (!Owner.Enabled.Value)
        {
            _multiMesh.VisibleInstanceCount = 0;
            return;
        }

        float dt = Math.Clamp(delta, 0f, 0.05f);
        UpdateParticles(dt);
        EmitParticles(dt);
        WriteInstances();
    }

    private void UpdateParticles(float dt)
    {
        for (int i = _activeCount - 1; i >= 0; i--)
        {
            var particle = _particles[i];
            particle.Age += dt;

            if (particle.Age >= particle.Lifetime)
            {
                _particles[i] = _particles[_activeCount - 1];
                _activeCount--;
                continue;
            }

            particle.Velocity.Y += Owner.Gravity.Value * dt;
            particle.Position += particle.Velocity * dt;
            _particles[i] = particle;
        }
    }

    private void EmitParticles(float dt)
    {
        float emissionRate = Math.Max(0f, Owner.EmissionRate.Value);
        _emissionAccumulator += emissionRate * dt;

        while (_emissionAccumulator >= 1f)
        {
            SpawnParticle();
            _emissionAccumulator -= 1f;
        }

        float burstInterval = Owner.BurstInterval.Value;
        if (burstInterval <= 0f || Owner.BurstCount.Value <= 0)
        {
            return;
        }

        _burstTimer += dt;
        while (_burstTimer >= burstInterval)
        {
            int burstCount = Math.Clamp(Owner.BurstCount.Value, 0, 64);
            for (int i = 0; i < burstCount; i++)
            {
                SpawnParticle();
            }

            _burstTimer -= burstInterval;
        }
    }

    private void SpawnParticle()
    {
        if (_activeCount >= _particles.Length)
        {
            return;
        }

        var extents = Owner.EmitterExtents.Value;
        float angle = Next01() * MathF.PI * 2f;
        float radius = MathF.Sqrt(Next01());
        var position = new Vector3(
            MathF.Cos(angle) * extents.x * radius,
            Owner.SpawnHeight.Value,
            MathF.Sin(angle) * extents.z * radius);

        float speed = Owner.InitialSpeed.Value + (Next01() * 2f - 1f) * Owner.SpeedVariance.Value;
        float spread = Owner.Spread.Value;
        var velocity = new Vector3(
            (Next01() * 2f - 1f) * spread,
            Math.Max(0.02f, speed),
            (Next01() * 2f - 1f) * spread);

        float lifetime = Math.Max(0.05f, Owner.Lifetime.Value + (Next01() * 2f - 1f) * Owner.LifetimeVariance.Value);
        float sizeJitter = 0.78f + Next01() * 0.44f;

        _particles[_activeCount++] = new Particle
        {
            Position = position,
            Velocity = velocity,
            Age = 0f,
            Lifetime = lifetime,
            StartSize = Math.Max(0.001f, Owner.StartSize.Value * sizeJitter),
            EndSize = Math.Max(0.001f, Owner.EndSize.Value * sizeJitter),
            StartColor = ToGodotColor(Owner.StartColor.Value),
            EndColor = ToGodotColor(Owner.EndColor.Value)
        };
    }

    private void WriteInstances()
    {
        for (int i = 0; i < _activeCount; i++)
        {
            var particle = _particles[i];
            float life = Math.Clamp(particle.Age / particle.Lifetime, 0f, 1f);
            float birth = SmoothStep(0f, 0.16f, life);
            float death = 1f - SmoothStep(0.74f, 1f, life);
            float pop = 1f + MathF.Sin(Math.Clamp(life / 0.22f, 0f, 1f) * MathF.PI) * 0.48f;
            float size = Lerp(particle.StartSize, particle.EndSize, life) * birth * death * pop;
            var color = Lerp(particle.StartColor, particle.EndColor, life);
            color.A *= birth * death;

            var transform = Transform3D.Identity;
            transform.Basis = Basis.Identity.Scaled(new Vector3(size, size, size));
            transform.Origin = particle.Position;

            _multiMesh.SetInstanceTransform(i, transform);
            _multiMesh.SetInstanceColor(i, color);
        }

        _multiMesh.VisibleInstanceCount = _activeCount;
    }

    private float Next01()
    {
        _rngState ^= _rngState << 13;
        _rngState ^= _rngState >> 17;
        _rngState ^= _rngState << 5;
        return (_rngState & 0x00FFFFFF) / 16777216f;
    }

    private static uint NormalizeSeed(int seed)
    {
        return seed == 0 ? 1u : unchecked((uint)seed);
    }

    private static int NormalizeRenderQueue(int renderQueue)
    {
        return renderQueue < 0 ? 0 : Math.Clamp(renderQueue, -128, 127);
    }

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        float t = Math.Clamp((value - edge0) / Math.Max(edge1 - edge0, 0.0001f), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static float Lerp(float a, float b, float t)
    {
        return a + (b - a) * t;
    }

    private static Color Lerp(Color a, Color b, float t)
    {
        return new Color(
            Lerp(a.R, b.R, t),
            Lerp(a.G, b.G, t),
            Lerp(a.B, b.B, t),
            Lerp(a.A, b.A, t));
    }

    private static Color ToGodotColor(Lumora.Core.Math.colorHDR color)
    {
        return new Color(color.r, color.g, color.b, color.a);
    }

    public override void Destroy(bool destroyingWorld)
    {
        if (!destroyingWorld && _processNode != null && GodotObject.IsInstanceValid(_processNode))
        {
            _processNode.QueueFree();
        }

        _processNode = null;
        _instance = null;
        _multiMesh = null;
        _particleMesh = null;
        _material = null;
        _particles = Array.Empty<Particle>();
        _activeCount = 0;

        base.Destroy(destroyingWorld);
    }

    private struct Particle
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public float Age;
        public float Lifetime;
        public float StartSize;
        public float EndSize;
        public Color StartColor;
        public Color EndColor;
    }

    private sealed partial class ParticleProcessNode : Node3D
    {
        private readonly ParticleSystemHook _hook;

        public ParticleProcessNode(ParticleSystemHook hook)
        {
            _hook = hook;
        }

        public override void _PhysicsProcess(double delta)
        {
            _hook?.Step((float)delta);
        }
    }
}
