using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Lumora.Core.Logging;

namespace Lumora.Core.Physics;

/// <summary>
/// Global physics manager handling physics simulation, collision detection, and raycasting.
/// </summary>
public class PhysicsManager : IDisposable
{
    private readonly List<PhysicsWorld> _physicsWorlds = new List<PhysicsWorld>();
    private readonly object _physicsLock = new object();

    private bool _initialized = false;
    private float _fixedTimeStep = 1f / 60f; // 60 Hz physics
    private float _accumulator = 0f;
    private int _maxSubSteps = 4;

    // Global physics settings
    public Vector3 Gravity { get; set; } = new Vector3(0, -9.81f, 0);
    public float TimeScale { get; set; } = 1f;
    public bool EnablePhysics { get; set; } = true;

    // Statistics
    public int ActiveWorldCount => _physicsWorlds.Count;
    public int TotalBodiesCount { get; private set; }
    public int TotalCollisionsThisFrame { get; private set; }
    public float LastPhysicsTime { get; private set; }

    /// <summary>
    /// Fixed time step for physics simulation.
    /// </summary>
    public float FixedTimeStep
    {
        get => _fixedTimeStep;
        set => _fixedTimeStep = value > 0.001f ? value : 0.001f;
    }

    /// <summary>
    /// Maximum sub-steps per frame to prevent spiral of death.
    /// </summary>
    public int MaxSubSteps
    {
        get => _maxSubSteps;
        set => _maxSubSteps = value > 1 ? value : 1;
    }

    /// <summary>
    /// Initialize the physics manager.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized)
            return;

        // Initialize physics engine settings
        // In a real implementation, this would initialize Bullet, PhysX, or another physics engine

        _initialized = true;

        await Task.CompletedTask;
        Logger.Log($"PhysicsManager: Initialized (FixedTimeStep: {_fixedTimeStep:F3}s, MaxSubSteps: {_maxSubSteps})");
    }

    /// <summary>
    /// Create a new physics world for a game world.
    /// </summary>
    public PhysicsWorld CreatePhysicsWorld(string name)
    {
        var physicsWorld = new PhysicsWorld(name, this);

        lock (_physicsLock)
        {
            _physicsWorlds.Add(physicsWorld);
        }

        Logger.Log($"PhysicsManager: Created physics world '{name}'");
        return physicsWorld;
    }

    /// <summary>
    /// Remove a physics world.
    /// </summary>
    public void RemovePhysicsWorld(PhysicsWorld world)
    {
        if (world == null)
            return;

        lock (_physicsLock)
        {
            _physicsWorlds.Remove(world);
        }

        world.Dispose();
        Logger.Log($"PhysicsManager: Removed physics world '{world.Name}'");
    }

    /// <summary>
    /// Update before world updates (physics preparation).
    /// </summary>
    public void PreWorldUpdate(float deltaTime)
    {
        if (!_initialized || !EnablePhysics)
            return;

        // Prepare physics for this frame
        TotalCollisionsThisFrame = 0;
    }

    /// <summary>
    /// Update after world updates (apply physics results).
    /// </summary>
    public void PostWorldUpdate(float deltaTime)
    {
        if (!_initialized || !EnablePhysics)
            return;

        // Apply physics results back to the world
        lock (_physicsLock)
        {
            foreach (var world in _physicsWorlds)
            {
                world.ApplyPhysicsResults();
            }
        }
    }

    /// <summary>
    /// Fixed update for physics simulation.
    /// </summary>
    public void FixedUpdate(float fixedDelta)
    {
        if (!_initialized || !EnablePhysics)
            return;

        var startTime = DateTime.Now;

        // Accumulate time for fixed timestep
        _accumulator += fixedDelta * TimeScale;

        // Limit accumulator to prevent spiral of death
        float maxAccumulator = _fixedTimeStep * _maxSubSteps;
        _accumulator = _accumulator < maxAccumulator ? _accumulator : maxAccumulator;

        // Simulate physics with fixed timestep
        int steps = 0;
        while (_accumulator >= _fixedTimeStep && steps < _maxSubSteps)
        {
            lock (_physicsLock)
            {
                foreach (var world in _physicsWorlds)
                {
                    world.StepSimulation(_fixedTimeStep);
                }
            }

            _accumulator -= _fixedTimeStep;
            steps++;
        }

        LastPhysicsTime = (float)(DateTime.Now - startTime).TotalSeconds;

        // Update statistics
        TotalBodiesCount = 0;
        lock (_physicsLock)
        {
            foreach (var world in _physicsWorlds)
            {
                TotalBodiesCount += world.RigidBodyCount;
                TotalCollisionsThisFrame += world.CollisionsThisFrame;
            }
        }
    }

    /// <summary>
    /// Dispose of the physics manager.
    /// </summary>
    public void Dispose()
    {
        if (!_initialized)
            return;

        lock (_physicsLock)
        {
            foreach (var world in _physicsWorlds)
            {
                world.Dispose();
            }
            _physicsWorlds.Clear();
        }

        _initialized = false;
        Logger.Log("PhysicsManager: Disposed");
    }
}

/// <summary>
/// Represents a physics world instance.
/// </summary>
public class PhysicsWorld : IDisposable
{
    private readonly List<RigidBody> _rigidBodies = new List<RigidBody>();
    private readonly List<CollisionPair> _collisionPairs = new List<CollisionPair>();
    private readonly PhysicsManager _manager;

    public string Name { get; }
    public int RigidBodyCount => _rigidBodies.Count;
    public int CollisionsThisFrame { get; private set; }
    public Vector3 Gravity { get; set; }

    public PhysicsWorld(string name, PhysicsManager manager)
    {
        Name = name;
        _manager = manager;
        Gravity = manager.Gravity;
    }

    /// <summary>
    /// Add a rigid body to the physics world.
    /// </summary>
    public void AddRigidBody(RigidBody body)
    {
        if (body == null || _rigidBodies.Contains(body))
            return;

        _rigidBodies.Add(body);
        body.World = this;
    }

    /// <summary>
    /// Remove a rigid body from the physics world.
    /// </summary>
    public void RemoveRigidBody(RigidBody body)
    {
        if (body == null)
            return;

        _rigidBodies.Remove(body);
        body.World = null;
    }

    /// <summary>
    /// Step the physics simulation.
    /// </summary>
    public void StepSimulation(float timeStep)
    {
        CollisionsThisFrame = 0;
        _collisionPairs.Clear();

        // Apply gravity and forces
        foreach (var body in _rigidBodies)
        {
            if (body.IsKinematic || body.Mass <= 0)
                continue;

            // Apply gravity
            body.ApplyForce(Gravity * body.Mass);

            // Integrate velocity
            body.LinearVelocity += (body.TotalForce / body.Mass) * timeStep;

            // Apply damping
            body.LinearVelocity *= (1f - body.LinearDamping * timeStep);
            body.AngularVelocity *= (1f - body.AngularDamping * timeStep);

            // Clear forces for next frame
            body.TotalForce = Vector3.Zero;
        }

        // Simple collision detection (broad phase)
        for (int i = 0; i < _rigidBodies.Count - 1; i++)
        {
            for (int j = i + 1; j < _rigidBodies.Count; j++)
            {
                if (CheckCollision(_rigidBodies[i], _rigidBodies[j]))
                {
                    _collisionPairs.Add(new CollisionPair(_rigidBodies[i], _rigidBodies[j]));
                    CollisionsThisFrame++;
                }
            }
        }

        // Resolve collisions
        foreach (var pair in _collisionPairs)
        {
            ResolveCollision(pair.BodyA, pair.BodyB);
        }

        // Update positions
        foreach (var body in _rigidBodies)
        {
            if (body.IsKinematic)
                continue;

            body.Position += body.LinearVelocity * timeStep;

            // Simple rotation integration
            var angularSpeed = body.AngularVelocity.Length();
            if (angularSpeed > 0.001f)
            {
                var axis = body.AngularVelocity / angularSpeed;
                var angle = angularSpeed * timeStep;
                body.Rotation = Quaternion.Multiply(body.Rotation, Quaternion.CreateFromAxisAngle(axis, angle));
            }
        }
    }

    /// <summary>
    /// Simple AABB collision detection.
    /// </summary>
    private bool CheckCollision(RigidBody a, RigidBody b)
    {
        if (!a.EnableCollision || !b.EnableCollision)
            return false;

        // Simple AABB check for demonstration
        var minA = a.Position - a.ColliderSize * 0.5f;
        var maxA = a.Position + a.ColliderSize * 0.5f;
        var minB = b.Position - b.ColliderSize * 0.5f;
        var maxB = b.Position + b.ColliderSize * 0.5f;

        return minA.X <= maxB.X && maxA.X >= minB.X &&
               minA.Y <= maxB.Y && maxA.Y >= minB.Y &&
               minA.Z <= maxB.Z && maxA.Z >= minB.Z;
    }

    /// <summary>
    /// Simple collision resolution.
    /// </summary>
    private void ResolveCollision(RigidBody a, RigidBody b)
    {
        // Simple elastic collision response
        if (a.IsKinematic && b.IsKinematic)
            return;

        Vector3 normal = Vector3.Normalize(b.Position - a.Position);
        float relativeVelocity = Vector3.Dot(b.LinearVelocity - a.LinearVelocity, normal);

        if (relativeVelocity > 0)
            return; // Objects moving apart

        float restitution = a.Restitution < b.Restitution ? a.Restitution : b.Restitution;
        float impulse = (1 + restitution) * relativeVelocity;

        if (!a.IsKinematic)
        {
            float massA = a.Mass > 0 ? a.Mass : 1f;
            float massB = b.IsKinematic ? float.MaxValue : (b.Mass > 0 ? b.Mass : 1f);
            impulse /= 1f / massA + 1f / massB;
            a.LinearVelocity += impulse * normal / massA;
        }

        if (!b.IsKinematic)
        {
            float massA = a.IsKinematic ? float.MaxValue : (a.Mass > 0 ? a.Mass : 1f);
            float massB = b.Mass > 0 ? b.Mass : 1f;
            impulse /= 1f / massA + 1f / massB;
            b.LinearVelocity -= impulse * normal / massB;
        }
    }

    /// <summary>
    /// Apply physics results back to game objects.
    /// </summary>
    public void ApplyPhysicsResults()
    {
        foreach (var body in _rigidBodies)
        {
            body.ApplyToTransform();
        }
    }

    /// <summary>
    /// Raycast through the physics world.
    /// </summary>
    public RaycastHit? Raycast(Vector3 origin, Vector3 direction, float maxDistance = float.MaxValue)
    {
        RaycastHit? closestHit = null;
        float closestDistance = maxDistance;

        foreach (var body in _rigidBodies)
        {
            if (!body.EnableCollision)
                continue;

            // Simple ray-AABB intersection
            var hit = RayAABBIntersection(origin, direction, body, maxDistance);
            if (hit.HasValue && hit.Value.Distance < closestDistance)
            {
                closestHit = hit;
                closestDistance = hit.Value.Distance;
            }
        }

        return closestHit;
    }

    /// <summary>
    /// Simple ray-AABB intersection test.
    /// </summary>
    private RaycastHit? RayAABBIntersection(Vector3 origin, Vector3 direction, RigidBody body, float maxDistance)
    {
        var min = body.Position - body.ColliderSize * 0.5f;
        var max = body.Position + body.ColliderSize * 0.5f;

        float tMin = 0f;
        float tMax = maxDistance;

        for (int i = 0; i < 3; i++)
        {
            float invDir = 1f / GetComponent(direction, i);
            float t0 = (GetComponent(min, i) - GetComponent(origin, i)) * invDir;
            float t1 = (GetComponent(max, i) - GetComponent(origin, i)) * invDir;

            if (invDir < 0)
                (t0, t1) = (t1, t0);

            tMin = tMin > t0 ? tMin : t0;
            tMax = tMax < t1 ? tMax : t1;

            if (tMax < tMin)
                return null;
        }

        var point = origin + direction * tMin;
        var normal = Vector3.Normalize(point - body.Position);

        return new RaycastHit
        {
            Body = body,
            Point = point,
            Normal = normal,
            Distance = tMin
        };
    }

    private float GetComponent(Vector3 v, int index)
    {
        return index switch
        {
            0 => v.X,
            1 => v.Y,
            2 => v.Z,
            _ => 0f
        };
    }

    public void Dispose()
    {
        _rigidBodies.Clear();
        _collisionPairs.Clear();
    }

    private struct CollisionPair
    {
        public RigidBody BodyA;
        public RigidBody BodyB;

        public CollisionPair(RigidBody a, RigidBody b)
        {
            BodyA = a;
            BodyB = b;
        }
    }
}

/// <summary>
/// Represents a rigid body in the physics simulation.
/// </summary>
public class RigidBody
{
    public PhysicsWorld World { get; set; }

    // Transform
    public Vector3 Position { get; set; }
    public Quaternion Rotation { get; set; } = Quaternion.Identity;

    // Physics properties
    public float Mass { get; set; } = 1f;
    public bool IsKinematic { get; set; }
    public Vector3 LinearVelocity { get; set; }
    public Vector3 AngularVelocity { get; set; }
    public Vector3 TotalForce { get; set; }

    // Collision
    public bool EnableCollision { get; set; } = true;
    public Vector3 ColliderSize { get; set; } = Vector3.One;
    public float Restitution { get; set; } = 0.5f; // Bounciness
    public float Friction { get; set; } = 0.5f;

    // Damping
    public float LinearDamping { get; set; } = 0.1f;
    public float AngularDamping { get; set; } = 0.1f;

    // User data
    public object UserData { get; set; }

    /// <summary>
    /// Apply a force to the rigid body.
    /// </summary>
    public void ApplyForce(Vector3 force)
    {
        if (!IsKinematic)
            TotalForce += force;
    }

    /// <summary>
    /// Apply an impulse to the rigid body.
    /// </summary>
    public void ApplyImpulse(Vector3 impulse)
    {
        if (!IsKinematic && Mass > 0)
            LinearVelocity += impulse / Mass;
    }

    /// <summary>
    /// Apply torque to the rigid body.
    /// </summary>
    public void ApplyTorque(Vector3 torque)
    {
        if (!IsKinematic)
            AngularVelocity += torque;
    }

    /// <summary>
    /// Apply physics results to the game object transform.
    /// </summary>
    public void ApplyToTransform()
    {
        // This would update the actual game object transform
        // In implementation, this would sync with the component system
    }
}

/// <summary>
/// Result of a raycast operation.
/// </summary>
public struct RaycastHit
{
    public RigidBody Body { get; set; }
    public Vector3 Point { get; set; }
    public Vector3 Normal { get; set; }
    public float Distance { get; set; }
}