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

            /// <summary>When over 0: distance from the centre back to the board's own back surface, where the back screen goes.</summary>
            public float m_BackDistance;

            /// <summary>Curved ad face: how far the left and right edges sit behind the middle, in meters (0 = flat).</summary>
            public float m_Curve;
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

        /// <summary>
        /// For boards whose ad is on the front only: finds the flat back of the board behind the
        /// ad, so a screen can go there too. That is the outermost back-facing surface that covers a
        /// good part of the ad's outline; surfaces closer in (like the back of the ad plate itself)
        /// are inside the board, and small ones further out are struts.
        /// </summary>
        public static bool TryMeasureBack(PrefabBase prefab, ref Panel panel)
        {
            if (!(prefab is ObjectGeometryPrefab geometryPrefab) || geometryPrefab.m_Meshes == null)
                return false;

            const float kBin = 0.02f;
            const float kMaxDepth = 3f;
            float3 up = new float3(0f, 1f, 0f);
            float3 right = math.normalize(math.cross(up, panel.m_Normal));
            var areaByBin = new Dictionary<int, float>();
            var depthByBin = new Dictionary<int, float>();

            foreach (var meshInfo in geometryPrefab.m_Meshes)
            {
                if (!(meshInfo.m_Mesh is RenderPrefab render) || !render.hasGeometryAsset)
                    continue;

                int adsMaterial = FindAdsMaterial(render);
                Mesh[] meshes = render.ObtainMeshes();
                try
                {
                    if (meshes == null)
                        continue;
                    int material = 0;
                    foreach (var mesh in meshes)
                    {
                        if (!mesh.isReadable)
                        {
                            material += mesh.subMeshCount;
                            continue;
                        }
                        Vector3[] vertices = mesh.vertices;
                        for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++, material++)
                        {
                            if (material == adsMaterial)
                                continue;
                            int[] triangles = mesh.GetTriangles(subMesh);
                            for (int t = 0; t + 2 < triangles.Length; t += 3)
                            {
                                float3 a = meshInfo.m_Position + math.mul(meshInfo.m_Rotation, (float3)vertices[triangles[t]]);
                                float3 b = meshInfo.m_Position + math.mul(meshInfo.m_Rotation, (float3)vertices[triangles[t + 1]]);
                                float3 c = meshInfo.m_Position + math.mul(meshInfo.m_Rotation, (float3)vertices[triangles[t + 2]]);
                                float3 cross = math.cross(b - a, c - a);
                                float area = math.length(cross) * 0.5f;
                                if (area < 1e-6f || math.dot(cross / (area * 2f), panel.m_Normal) > -0.9f)
                                    continue;

                                float3 local = (a + b + c) / 3f - panel.m_Center;
                                float depth = -math.dot(local, panel.m_Normal);
                                if (depth < 0f || depth > kMaxDepth ||
                                    math.abs(math.dot(local, right)) > panel.m_Size.x * 0.45f || math.abs(local.y) > panel.m_Size.y * 0.45f)
                                    continue;

                                int bin = (int)math.round(depth / kBin);
                                areaByBin.TryGetValue(bin, out float binArea);
                                depthByBin.TryGetValue(bin, out float binDepth);
                                areaByBin[bin] = binArea + area;
                                depthByBin[bin] = binDepth + depth * area;
                            }
                        }
                    }
                }
                finally
                {
                    render.ReleaseMeshes();
                }
            }

            // Neighbouring bins count together, so a surface split across a bin edge still qualifies.
            float minArea = 0.3f * panel.m_Size.x * panel.m_Size.y;
            int best = int.MinValue;
            float bestArea = 0f, bestDepth = 0f;
            foreach (int bin in areaByBin.Keys)
            {
                float area = 0f, depth = 0f;
                for (int b = bin - 1; b <= bin + 1; b++)
                {
                    if (areaByBin.TryGetValue(b, out float binArea))
                    {
                        area += binArea;
                        depth += depthByBin[b];
                    }
                }
                if (area >= minArea && bin > best)
                {
                    best = bin;
                    bestArea = area;
                    bestDepth = depth;
                }
            }
            if (best == int.MinValue)
                return false;

            // Weighted depth of the surface, but never in front of its outermost bin's triangles.
            panel.m_BackDistance = math.max(math.max(bestDepth / bestArea, depthByBin[best] / areaByBin[best]), 0.001f);
            panel.m_Faces = ScreenFaces.Both;
            return true;
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
            float curve = MeasureCurve(corners, normals, axis, right, mid.x, size.x * 0.5f);
            if (curve > 0f)
                Mod.log.Info($"Curved ad face: edges {curve:0.000} m behind the middle");
            panel = new Panel
            {
                m_Center = right * mid.x + up * mid.y + axis * mid.z,
                m_Size = size,
                m_Normal = axis,
                m_HalfDepth = (max.z - min.z) * 0.5f,
                m_Faces = backArea > 0.2f * totalArea ? ScreenFaces.Both : ScreenFaces.Front,
                m_Curve = curve,
            };
            return true;
        }

        /// <summary>
        /// Fits the front of the ad face as a parabola across its width: depth = middle - curve * x²
        /// with x from -1 to 1. Returns the curve when the face clearly bends back towards its
        /// edges, else 0 (flat).
        /// </summary>
        private static float MeasureCurve(List<float3> corners, List<float4> normals, float3 axis, float3 right, float midX, float halfWidth)
        {
            if (halfWidth < 0.05f)
                return 0f;
            double sw = 0, st = 0, sd = 0, stt = 0, std = 0, sdd = 0;
            for (int i = 0; i < normals.Count; i++)
            {
                float4 n = normals[i];
                if (math.dot(math.normalize(n.xyz), axis) < 0.3f)
                    continue;
                float3 c = (corners[3 * i] + corners[3 * i + 1] + corners[3 * i + 2]) / 3f;
                float x = (math.dot(c, right) - midX) / halfWidth;
                double t = x * x, d = math.dot(c, axis), w = n.w;
                sw += w;
                st += w * t;
                sd += w * d;
                stt += w * t * t;
                std += w * t * d;
                sdd += w * d * d;
            }
            if (sw <= 0)
                return 0f;
            double meanT = st / sw, meanD = sd / sw;
            double varT = stt / sw - meanT * meanT, varD = sdd / sw - meanD * meanD, cov = std / sw - meanT * meanD;
            if (varT < 1e-4 || varD < 1e-8)
                return 0f;
            double slope = cov / varT;
            double fit = cov * cov / (varT * varD);
            if (slope > -0.02 || fit < 0.7)
                return 0f;
            return (float)math.min(-slope, halfWidth);
        }
    }
}
