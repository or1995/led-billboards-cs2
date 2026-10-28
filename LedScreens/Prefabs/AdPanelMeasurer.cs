using System;
using System.Collections.Generic;
using System.Text;
using Game.Prefabs;
using LedScreens.Components;
using Unity.Mathematics;
using UnityEngine;

namespace LedScreens.Prefabs
{
    /// <summary>
    /// Finds the ad face of a vanilla billboard. Their meshes have a separate material whose
    /// surface asset is named "*_Ads*"; the triangles of that submesh are exactly the ad panel.
    /// </summary>
    public static class AdPanelMeasurer
    {
        public struct Panel
        {
            public float3 m_Center;
            public float2 m_Size;
            public float3 m_Normal;
            public float m_HalfDepth;
            public ScreenFaces m_Faces;
        }

        public static bool TryMeasure(PrefabBase prefab, out Panel panel)
        {
            panel = default;
            if (!(prefab is ObjectGeometryPrefab geometryPrefab) || geometryPrefab.m_Meshes == null)
                return false;

            var corners = new List<float3>();
            var normals = new List<float4>(); // xyz = area-weighted normal, w = area

            foreach (var meshInfo in geometryPrefab.m_Meshes)
            {
                if (!(meshInfo.m_Mesh is RenderPrefab render) || !render.hasGeometryAsset)
                    continue;

                int adsMaterial = FindAdsMaterial(render);
                if (adsMaterial < 0)
                    continue;

                // ObtainMeshes is reference counted; always pair it with ReleaseMeshes.
                Mesh[] meshes = render.ObtainMeshes();
                try
                {
                    if (!TryFindSubMesh(meshes, adsMaterial, out var mesh, out int subMesh))
                        continue;
                    if (!mesh.isReadable)
                    {
                        Mod.log.Warn($"{prefab.name}: mesh '{mesh.name}' is not readable");
                        continue;
                    }
                    CollectTriangles(mesh, subMesh, meshInfo.m_Position, meshInfo.m_Rotation, corners, normals);
                }
                finally
                {
                    render.ReleaseMeshes();
                }
            }

            return corners.Count >= 3 && TryBuildPanel(corners, normals, out panel);
        }

        /// <summary>One line per mesh with its materials and components, to see what a prefab is made of.</summary>
        public static string Describe(PrefabBase prefab)
        {
            var sb = new StringBuilder();
            sb.Append(prefab.name).Append(" components: ");
            foreach (var component in prefab.components)
                sb.Append(component.GetType().Name).Append(' ');

            if (prefab is ObjectGeometryPrefab geometryPrefab && geometryPrefab.m_Meshes != null)
            {
                foreach (var meshInfo in geometryPrefab.m_Meshes)
                {
                    if (!(meshInfo.m_Mesh is RenderPrefab render))
                        continue;
                    sb.Append("\n  mesh ").Append(render.name).Append(" materials: ");
                    for (int i = 0; i < render.materialCount; i++)
                        sb.Append(render.GetSurfaceAsset(i)?.name ?? "null").Append(' ');
                    sb.Append("components: ");
                    foreach (var component in render.components)
                        sb.Append(component.GetType().Name).Append(' ');
                }
            }
            return sb.ToString();
        }

        private static int FindAdsMaterial(RenderPrefab render)
        {
            for (int i = 0; i < render.materialCount; i++)
            {
                var surface = render.GetSurfaceAsset(i);
                if (surface != null && surface.name != null && surface.name.IndexOf("_Ads", StringComparison.OrdinalIgnoreCase) >= 0)
                    return i;
            }
            return -1;
        }

        /// <summary>Material indices run across all meshes of the geometry, like RenderPrefab.ObtainMesh.</summary>
        private static bool TryFindSubMesh(Mesh[] meshes, int materialIndex, out Mesh mesh, out int subMesh)
        {
            mesh = null;
            subMesh = 0;
            if (meshes == null)
                return false;
            foreach (var candidate in meshes)
            {
                if (materialIndex < candidate.subMeshCount)
                {
                    mesh = candidate;
                    subMesh = materialIndex;
                    return true;
                }
                materialIndex -= candidate.subMeshCount;
            }
            return false;
        }

        private static void CollectTriangles(Mesh mesh, int subMesh, float3 offset, quaternion rotation, List<float3> corners, List<float4> normals)
        {
            Vector3[] vertices = mesh.vertices;
            int[] triangles = mesh.GetTriangles(subMesh);
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                float3 a = offset + math.mul(rotation, (float3)vertices[triangles[t]]);
                float3 b = offset + math.mul(rotation, (float3)vertices[triangles[t + 1]]);
                float3 c = offset + math.mul(rotation, (float3)vertices[triangles[t + 2]]);
                float3 cross = math.cross(b - a, c - a);
                float area = math.length(cross) * 0.5f;
                if (area < 1e-6f)
                    continue;
                corners.Add(a);
                corners.Add(b);
                corners.Add(c);
                normals.Add(new float4(cross * 0.5f, area));
            }
        }

        private static bool TryBuildPanel(List<float3> corners, List<float4> normals, out Panel panel)
        {
            panel = default;

            // Panel axis: sum horizontal normals, flipped to agree with the largest triangle,
            // so front and back faces reinforce each other instead of cancelling out.
            float4 reference = normals[0];
            foreach (var n in normals)
            {
                if (n.w > reference.w)
                    reference = n;
            }
            float3 referenceDir = new float3(reference.x, 0f, reference.z);
            float3 axisSum = float3.zero;
            foreach (var n in normals)
            {
                float3 h = new float3(n.x, 0f, n.z);
                axisSum += math.dot(h, referenceDir) >= 0f ? h : -h;
            }
            if (math.lengthsq(axisSum) < 1e-8f)
                return false; // horizontal panel, not a screen
            float3 axis = math.normalize(axisSum);

            // Triangles facing along +axis are the front, against it the back.
            float frontArea = 0f, backArea = 0f, totalArea = 0f;
            foreach (var n in normals)
            {
                float facing = math.dot(math.normalize(n.xyz), axis);
                if (facing > 0.5f)
                    frontArea += n.w;
                else if (facing < -0.5f)
                    backArea += n.w;
                totalArea += n.w;
            }

            // Prefer looking out of the side that has the most ad area as "front".
            if (backArea > frontArea)
            {
                axis = -axis;
                (frontArea, backArea) = (backArea, frontArea);
            }

            float3 up = new float3(0f, 1f, 0f);
            float3 right = math.normalize(math.cross(up, axis));
            float3 min = new float3(float.MaxValue), max = new float3(float.MinValue);
            foreach (var p in corners)
            {
                var local = new float3(math.dot(p, right), p.y, math.dot(p, axis));
                min = math.min(min, local);
                max = math.max(max, local);
            }

            float2 size = new float2(max.x - min.x, max.y - min.y);
            if (size.x < 0.1f || size.y < 0.1f)
                return false;

            float3 mid = (min + max) * 0.5f;
            panel = new Panel
            {
                m_Center = right * mid.x + up * mid.y + axis * mid.z,
                m_Size = size,
                m_Normal = axis,
                m_HalfDepth = (max.z - min.z) * 0.5f,
                m_Faces = backArea > 0.2f * totalArea ? ScreenFaces.Both : ScreenFaces.Front,
            };
            return true;
        }
    }
}
