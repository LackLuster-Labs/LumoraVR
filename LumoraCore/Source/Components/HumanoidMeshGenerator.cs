using System;
using System.Collections.Generic;
using Lumora.Core.Math;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

/// <summary>
/// Utility class for generating default humanoid mesh data.
/// Creates simple geometric shapes (capsules, cylinders, spheres) with proper bone weights.
/// </summary>
public static class HumanoidMeshGenerator
{
    /// <summary>
    /// Generate a complete humanoid body mesh with proper bone weights.
    /// Creates simple shapes for body parts weighted to a standard humanoid skeleton.
    /// </summary>
    public static void GenerateDefaultHumanoidMesh(
        out float3[] vertices,
        out float3[] normals,
        out float2[] uvs,
        out int[] indices,
        out int4[] boneIndices,
        out float4[] boneWeights)
    {
        var vertexList = new List<float3>();
        var normalList = new List<float3>();
        var uvList = new List<float2>();
        var indexList = new List<int>();
        var boneIndexList = new List<int4>();
        var boneWeightList = new List<float4>();

        // Define bone indices for standard humanoid skeleton
        const int BONE_HIPS = 0;
        const int BONE_SPINE = 1;
        const int BONE_CHEST = 2;
        const int BONE_NECK = 3;
        const int BONE_HEAD = 4;
        const int BONE_LEFT_SHOULDER = 5;
        const int BONE_LEFT_UPPER_ARM = 6;
        const int BONE_LEFT_LOWER_ARM = 7;
        const int BONE_LEFT_HAND = 8;
        const int BONE_RIGHT_SHOULDER = 9;
        const int BONE_RIGHT_UPPER_ARM = 10;
        const int BONE_RIGHT_LOWER_ARM = 11;
        const int BONE_RIGHT_HAND = 12;
        const int BONE_LEFT_UPPER_LEG = 13;
        const int BONE_LEFT_LOWER_LEG = 14;
        const int BONE_LEFT_FOOT = 15;
        const int BONE_RIGHT_UPPER_LEG = 16;
        const int BONE_RIGHT_LOWER_LEG = 17;
        const int BONE_RIGHT_FOOT = 18;

        // Generate body parts
        // Torso (capsule) - weighted to Hips, Spine, Chest
        AddCapsule(vertexList, normalList, uvList, indexList, boneIndexList, boneWeightList,
            new float3(0, 0.6f, 0), 0.2f, 0.7f, 8, 4,
            BONE_HIPS, BONE_SPINE, BONE_CHEST, -1,
            0.3f, 0.4f, 0.3f, 0.0f);

        // Head (sphere) - weighted to Head
        AddSphere(vertexList, normalList, uvList, indexList, boneIndexList, boneWeightList,
            new float3(0, 1.35f, 0), 0.12f, 8, 6,
            BONE_HEAD, -1, -1, -1,
            1.0f, 0.0f, 0.0f, 0.0f);

        // Neck (small cylinder) - weighted to Neck
        AddCylinder(vertexList, normalList, uvList, indexList, boneIndexList, boneWeightList,
            new float3(0, 1.15f, 0), 0.06f, 0.15f, 6,
            BONE_NECK, -1, -1, -1,
            1.0f, 0.0f, 0.0f, 0.0f);

        // Left arm (cylinder) - weighted to LeftUpperArm and LeftLowerArm
        AddCylinder(vertexList, normalList, uvList, indexList, boneIndexList, boneWeightList,
            new float3(-0.35f, 0.75f, 0), 0.04f, 0.5f, 6,
            BONE_LEFT_UPPER_ARM, BONE_LEFT_LOWER_ARM, -1, -1,
            0.5f, 0.5f, 0.0f, 0.0f);

        // Right arm (cylinder) - weighted to RightUpperArm and RightLowerArm
        AddCylinder(vertexList, normalList, uvList, indexList, boneIndexList, boneWeightList,
            new float3(0.35f, 0.75f, 0), 0.04f, 0.5f, 6,
            BONE_RIGHT_UPPER_ARM, BONE_RIGHT_LOWER_ARM, -1, -1,
            0.5f, 0.5f, 0.0f, 0.0f);

        // Left hand (small sphere) - weighted to LeftHand
        AddSphere(vertexList, normalList, uvList, indexList, boneIndexList, boneWeightList,
            new float3(-0.4f, 0.45f, 0), 0.05f, 6, 4,
            BONE_LEFT_HAND, -1, -1, -1,
            1.0f, 0.0f, 0.0f, 0.0f);

        // Right hand (small sphere) - weighted to RightHand
        AddSphere(vertexList, normalList, uvList, indexList, boneIndexList, boneWeightList,
            new float3(0.4f, 0.45f, 0), 0.05f, 6, 4,
            BONE_RIGHT_HAND, -1, -1, -1,
            1.0f, 0.0f, 0.0f, 0.0f);

        // Left leg (cylinder) - weighted to LeftUpperLeg and LeftLowerLeg
        AddCylinder(vertexList, normalList, uvList, indexList, boneIndexList, boneWeightList,
            new float3(-0.1f, 0.05f, 0), 0.05f, 0.8f, 6,
            BONE_LEFT_UPPER_LEG, BONE_LEFT_LOWER_LEG, -1, -1,
            0.5f, 0.5f, 0.0f, 0.0f);

        // Right leg (cylinder) - weighted to RightUpperLeg and RightLowerLeg
        AddCylinder(vertexList, normalList, uvList, indexList, boneIndexList, boneWeightList,
            new float3(0.1f, 0.05f, 0), 0.05f, 0.8f, 6,
            BONE_RIGHT_UPPER_LEG, BONE_RIGHT_LOWER_LEG, -1, -1,
            0.5f, 0.5f, 0.0f, 0.0f);

        // Left foot (small box) - weighted to LeftFoot
        AddBox(vertexList, normalList, uvList, indexList, boneIndexList, boneWeightList,
            new float3(-0.1f, -0.4f, 0.05f), 0.08f, 0.05f, 0.15f,
            BONE_LEFT_FOOT, -1, -1, -1,
            1.0f, 0.0f, 0.0f, 0.0f);

        // Right foot (small box) - weighted to RightFoot
        AddBox(vertexList, normalList, uvList, indexList, boneIndexList, boneWeightList,
            new float3(0.1f, -0.4f, 0.05f), 0.08f, 0.05f, 0.15f,
            BONE_RIGHT_FOOT, -1, -1, -1,
            1.0f, 0.0f, 0.0f, 0.0f);

        // Convert lists to arrays
        vertices = vertexList.ToArray();
        normals = normalList.ToArray();
        uvs = uvList.ToArray();
        indices = indexList.ToArray();
        boneIndices = boneIndexList.ToArray();
        boneWeights = boneWeightList.ToArray();

        AquaLogger.Log($"HumanoidMeshGenerator: Generated mesh with {vertices.Length} vertices, {indices.Length / 3} triangles");
    }

    // ===== MESH PRIMITIVE GENERATORS =====

    private static void AddSphere(List<float3> vertices, List<float3> normals, List<float2> uvs,
        List<int> indices, List<int4> boneIndices, List<float4> boneWeights,
        float3 center, float radius, int segments, int rings,
        int bone0, int bone1, int bone2, int bone3,
        float weight0, float weight1, float weight2, float weight3)
    {
        int startVertex = vertices.Count;

        // Generate sphere vertices
        for (int ring = 0; ring <= rings; ring++)
        {
            float phi = (float)System.Math.PI * ring / rings;
            for (int seg = 0; seg <= segments; seg++)
            {
                float theta = 2.0f * (float)System.Math.PI * seg / segments;

                float x = radius * (float)(System.Math.Sin(phi) * System.Math.Cos(theta));
                float y = radius * (float)System.Math.Cos(phi);
                float z = radius * (float)(System.Math.Sin(phi) * System.Math.Sin(theta));

                float3 pos = center + new float3(x, y, z);
                float3 normal = new float3(x, y, z).Normalized;

                vertices.Add(pos);
                normals.Add(normal);
                uvs.Add(new float2((float)seg / segments, (float)ring / rings));
                boneIndices.Add(new int4(bone0, bone1, bone2, bone3));
                boneWeights.Add(new float4(weight0, weight1, weight2, weight3));
            }
        }

        // Generate sphere indices
        for (int ring = 0; ring < rings; ring++)
        {
            for (int seg = 0; seg < segments; seg++)
            {
                int current = startVertex + ring * (segments + 1) + seg;
                int next = current + segments + 1;

                indices.Add(current);
                indices.Add(next);
                indices.Add(current + 1);

                indices.Add(current + 1);
                indices.Add(next);
                indices.Add(next + 1);
            }
        }
    }

    private static void AddCylinder(List<float3> vertices, List<float3> normals, List<float2> uvs,
        List<int> indices, List<int4> boneIndices, List<float4> boneWeights,
        float3 center, float radius, float height, int segments,
        int bone0, int bone1, int bone2, int bone3,
        float weight0, float weight1, float weight2, float weight3)
    {
        int startVertex = vertices.Count;
        float halfHeight = height * 0.5f;

        // Generate cylinder vertices
        for (int i = 0; i <= segments; i++)
        {
            float angle = 2.0f * (float)System.Math.PI * i / segments;
            float x = radius * (float)System.Math.Cos(angle);
            float z = radius * (float)System.Math.Sin(angle);
            float3 normal = new float3(x, 0, z).Normalized;

            // Bottom
            vertices.Add(center + new float3(x, -halfHeight, z));
            normals.Add(normal);
            uvs.Add(new float2((float)i / segments, 0));
            boneIndices.Add(new int4(bone0, bone1, bone2, bone3));
            boneWeights.Add(new float4(weight0, weight1, weight2, weight3));

            // Top
            vertices.Add(center + new float3(x, halfHeight, z));
            normals.Add(normal);
            uvs.Add(new float2((float)i / segments, 1));
            boneIndices.Add(new int4(bone0, bone1, bone2, bone3));
            boneWeights.Add(new float4(weight0, weight1, weight2, weight3));
        }

        // Generate cylinder indices
        for (int i = 0; i < segments; i++)
        {
            int current = startVertex + i * 2;
            int next = current + 2;

            indices.Add(current);
            indices.Add(next);
            indices.Add(current + 1);

            indices.Add(current + 1);
            indices.Add(next);
            indices.Add(next + 1);
        }
    }

    private static void AddCapsule(List<float3> vertices, List<float3> normals, List<float2> uvs,
        List<int> indices, List<int4> boneIndices, List<float4> boneWeights,
        float3 center, float radius, float height, int segments, int rings,
        int bone0, int bone1, int bone2, int bone3,
        float weight0, float weight1, float weight2, float weight3)
    {
        // Simplified: Use cylinder for now (can be enhanced later)
        AddCylinder(vertices, normals, uvs, indices, boneIndices, boneWeights,
            center, radius, height, segments,
            bone0, bone1, bone2, bone3,
            weight0, weight1, weight2, weight3);
    }

    private static void AddBox(List<float3> vertices, List<float3> normals, List<float2> uvs,
        List<int> indices, List<int4> boneIndices, List<float4> boneWeights,
        float3 center, float width, float height, float depth,
        int bone0, int bone1, int bone2, int bone3,
        float weight0, float weight1, float weight2, float weight3)
    {
        int startVertex = vertices.Count;
        float hw = width * 0.5f;
        float hh = height * 0.5f;
        float hd = depth * 0.5f;

        // Define 8 corners
        float3[] corners = new float3[]
        {
            center + new float3(-hw, -hh, -hd),
            center + new float3(hw, -hh, -hd),
            center + new float3(hw, hh, -hd),
            center + new float3(-hw, hh, -hd),
            center + new float3(-hw, -hh, hd),
            center + new float3(hw, -hh, hd),
            center + new float3(hw, hh, hd),
            center + new float3(-hw, hh, hd),
        };

        // Define 6 faces (each face has 4 vertices)
        int[,] faceIndices = new int[,]
        {
            {0, 1, 2, 3}, // Front
			{5, 4, 7, 6}, // Back
			{4, 0, 3, 7}, // Left
			{1, 5, 6, 2}, // Right
			{3, 2, 6, 7}, // Top
			{4, 5, 1, 0}  // Bottom
		};

        float3[] faceNormals = new float3[]
        {
            new float3(0, 0, -1), // Front
			new float3(0, 0, 1),  // Back
			new float3(-1, 0, 0), // Left
			new float3(1, 0, 0),  // Right
			new float3(0, 1, 0),  // Top
			new float3(0, -1, 0)  // Bottom
		};

        for (int face = 0; face < 6; face++)
        {
            int faceStart = vertices.Count;

            for (int i = 0; i < 4; i++)
            {
                vertices.Add(corners[faceIndices[face, i]]);
                normals.Add(faceNormals[face]);
                uvs.Add(new float2(i % 2, i / 2));
                boneIndices.Add(new int4(bone0, bone1, bone2, bone3));
                boneWeights.Add(new float4(weight0, weight1, weight2, weight3));
            }

            // Two triangles per face
            indices.Add(faceStart + 0);
            indices.Add(faceStart + 1);
            indices.Add(faceStart + 2);

            indices.Add(faceStart + 0);
            indices.Add(faceStart + 2);
            indices.Add(faceStart + 3);
        }
    }
}
