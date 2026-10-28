using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Colossal.Entities;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using LedScreens.Components;
using LedScreens.Media;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using Transform = Game.Objects.Transform;

namespace LedScreens.Systems
{
    /// <summary>
    /// Draws the screen surface of every LED screen prop. The game's batch renderer shares one
    /// material per prefab, so instead each screen gets a quad drawn here with its own texture;
    /// screens showing the same frame share one material property setup.
    /// </summary>
    /// <remarks>
    /// Only the keyword-free HDRP/Unlit variant is guaranteed to exist in the game build (the game
    /// uses it as a plain colour), so the image goes into _UnlitColorMap and screens are drawn
    /// without GPU instancing. Emissive maps or instancing would hit stripped shader variants and
    /// render flat white. Unlit colour isn't affected by camera exposure, so screens keep a
    /// constant brightness day and night, like a real LED panel.
    /// </remarks>
    public partial class LedScreenRenderSystem : GameSystemBase
    {
        private const float kPreloadSeconds = 4f;

        private static readonly int kUnlitColorMap = Shader.PropertyToID("_UnlitColorMap");
        private static readonly int kUnlitColor = Shader.PropertyToID("_UnlitColor");

        /// <summary>Screen placement in prefab-local space, derived from the mesh bounds.</summary>
        private struct ScreenSurface
        {
            public bool m_Valid;
            public ScreenFaces m_Faces;
            public float2 m_Size;
            public float3 m_FrontCenter;
            public float3 m_BackCenter;
            public quaternion m_FrontRotation;
            public quaternion m_BackRotation;
        }

        private readonly struct BatchKey : IEquatable<BatchKey>
        {
            public readonly Texture Texture;
            public readonly int Brightness; // hundredths, so nearly-equal values share a batch

            public BatchKey(Texture texture, int brightness)
            {
                Texture = texture;
                Brightness = brightness;
            }

            public bool Equals(BatchKey other) => ReferenceEquals(Texture, other.Texture) && Brightness == other.Brightness;

            public override bool Equals(object obj) => obj is BatchKey other && Equals(other);

            public override int GetHashCode() => (Texture != null ? Texture.GetHashCode() : 0) * 397 ^ Brightness;
        }

        private sealed class Batch
        {
            public readonly List<Matrix4x4> Matrices = new List<Matrix4x4>();
            public Bounds Bounds;
        }

        private readonly Dictionary<Entity, ScreenSurface> m_Surfaces = new Dictionary<Entity, ScreenSurface>();
        private readonly Dictionary<BatchKey, Batch> m_Batches = new Dictionary<BatchKey, Batch>();
        private readonly Stack<Batch> m_BatchPool = new Stack<Batch>();
        private readonly Plane[] m_FrustumPlanes = new Plane[6];

        private CameraUpdateSystem m_CameraUpdateSystem;
        private EntityQuery m_ScreenQuery;
        private Mesh m_Quad;
        private Material m_Material;
        private MaterialPropertyBlock m_Properties;
        private int m_SurfaceSettingsHash;
        private bool m_LoggedFirstDraw;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_CameraUpdateSystem = World.GetOrCreateSystemManaged<CameraUpdateSystem>();
            m_ScreenQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<LedScreen>(),
                    ComponentType.ReadOnly<Transform>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Hidden>(),
                },
            });

            m_Quad = CreateQuad();
            m_Properties = new MaterialPropertyBlock();
            m_Material = CreateMaterial(MediaLibrary.Instance.NoSignal);
        }

        protected override void OnDestroy()
        {
            if (m_Material != null)
                UnityEngine.Object.Destroy(m_Material);
            if (m_Quad != null)
                UnityEngine.Object.Destroy(m_Quad);
            base.OnDestroy();
        }

        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            m_Surfaces.Clear();
        }

        protected override void OnUpdate()
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            var library = MediaLibrary.Instance;
            library.Update(now);

            var settings = Mod.Settings;
            if (m_Material == null || settings == null || m_ScreenQuery.IsEmptyIgnoreFilter)
                return;

            Camera camera = m_CameraUpdateSystem.activeCamera;
            if (camera == null)
                return;

            int surfaceSettingsHash = HashCode(settings.FacesOverride, settings.SurfaceOffset, settings.ExtraInset);
            if (surfaceSettingsHash != m_SurfaceSettingsHash)
            {
                m_SurfaceSettingsHash = surfaceSettingsHash;
                m_Surfaces.Clear();
            }

            GeometryUtility.CalculateFrustumPlanes(camera, m_FrustumPlanes);
            float3 cameraPosition = camera.transform.position;
            float maxDistanceSq = settings.DrawDistance * settings.DrawDistance;
            double time = ScreenMediaResolver.Clock;
            var resolver = ScreenMediaResolver.Instance;

            using (var entities = m_ScreenQuery.ToEntityArray(Allocator.Temp))
            using (var screens = m_ScreenQuery.ToComponentDataArray<LedScreen>(Allocator.Temp))
            using (var transforms = m_ScreenQuery.ToComponentDataArray<Transform>(Allocator.Temp))
            using (var prefabRefs = m_ScreenQuery.ToComponentDataArray<PrefabRef>(Allocator.Temp))
            {
                for (int i = 0; i < screens.Length; i++)
                {
                    var transform = transforms[i];
                    if (math.distancesq(transform.m_Position, cameraPosition) > maxDistanceSq)
                        continue;

                    var surface = GetSurface(prefabRefs[i].m_Prefab, settings);
                    if (!surface.m_Valid)
                        continue;

                    var screen = screens[i];
                    Texture texture = null;
                    float mediaBrightness = 1f;
                    if ((screen.m_Flags & LedScreenFlags.Initialized) != 0)
                    {
                        string media = resolver.Resolve(EntityManager, entities[i], screen, time, out string next, out float timeToNext);
                        var clip = library.GetClip(media, now);
                        if (clip != null)
                        {
                            texture = clip.GetFrame(time * screen.m_Speed + screen.m_TimeOffset);
                            mediaBrightness = clip.BrightnessScale;
                        }
                        // Start loading the next playlist item early so the switch has no gap.
                        if (next != null && timeToNext < kPreloadSeconds)
                            library.GetClip(next, now);
                    }
                    if (texture == null)
                        texture = library.NoSignal;

                    int brightness = (int)math.round(screen.m_Brightness * settings.Brightness * mediaBrightness * 100f);

                    if ((surface.m_Faces & ScreenFaces.Front) != 0)
                        AddFace(transform, surface.m_FrontCenter, surface.m_FrontRotation, surface.m_Size, cameraPosition, texture, brightness);
                    if ((surface.m_Faces & ScreenFaces.Back) != 0)
                        AddFace(transform, surface.m_BackCenter, surface.m_BackRotation, surface.m_Size, cameraPosition, texture, brightness);
                }
            }

            DrawBatches(camera);
        }

        private void AddFace(Transform transform, float3 localCenter, quaternion localRotation, float2 size, float3 cameraPosition, Texture texture, int brightness)
        {
            float3 center = transform.m_Position + math.mul(transform.m_Rotation, localCenter);
            quaternion rotation = math.mul(transform.m_Rotation, localRotation);

            // The quad is visible from its local -Z side; skip faces turned away from the camera.
            float3 normal = math.mul(rotation, new float3(0f, 0f, -1f));
            if (math.dot(normal, cameraPosition - center) <= 0f)
                return;

            float extent = math.max(size.x, size.y);
            var bounds = new Bounds(center, new Vector3(extent, extent, extent));
            if (!GeometryUtility.TestPlanesAABB(m_FrustumPlanes, bounds))
                return;

            var key = new BatchKey(texture, brightness);
            if (!m_Batches.TryGetValue(key, out var batch))
            {
                batch = m_BatchPool.Count > 0 ? m_BatchPool.Pop() : new Batch();
                batch.Bounds = bounds;
                m_Batches.Add(key, batch);
            }
            else
            {
                batch.Bounds.Encapsulate(bounds);
            }

            batch.Matrices.Add(Matrix4x4.TRS(center, rotation, new Vector3(size.x, size.y, 1f)));
        }

        private void DrawBatches(Camera camera)
        {
            int drawn = 0;
            foreach (var pair in m_Batches)
            {
                var batch = pair.Value;
                // Property values are copied when the draw is queued, so the block can be reused.
                m_Properties.Clear();
                m_Properties.SetTexture(kUnlitColorMap, pair.Key.Texture);
                m_Properties.SetColor(kUnlitColor, Color.white * (pair.Key.Brightness / 100f));

                var renderParams = new RenderParams(m_Material)
                {
                    camera = camera,
                    layer = 0,
                    matProps = m_Properties,
                    receiveShadows = false,
                    shadowCastingMode = ShadowCastingMode.Off,
                    worldBounds = batch.Bounds,
                };

                foreach (var matrix in batch.Matrices)
                    Graphics.RenderMesh(renderParams, m_Quad, 0, matrix);

                drawn += batch.Matrices.Count;
                batch.Matrices.Clear();
                m_BatchPool.Push(batch);
            }
            m_Batches.Clear();

            if (drawn > 0 && !m_LoggedFirstDraw)
            {
                m_LoggedFirstDraw = true;
                Mod.log.Info($"First screen draw: {drawn} face(s), camera '{camera.name}' cullingMask={camera.cullingMask:X}");
            }
        }

        private ScreenSurface GetSurface(Entity prefab, Setting settings)
        {
            if (!m_Surfaces.TryGetValue(prefab, out var surface))
            {
                surface = ComputeSurface(prefab, settings);
                m_Surfaces.Add(prefab, surface);
            }
            return surface;
        }

        private ScreenSurface ComputeSurface(Entity prefab, Setting settings)
        {
            if (!EntityManager.TryGetComponent<LedScreenData>(prefab, out var data) ||
                !EntityManager.TryGetComponent<ObjectGeometryData>(prefab, out var geometry))
            {
                return default;
            }

            float3 normal, faceCenter;
            float width, height, halfDepth;
            if (data.m_HasPanel)
            {
                // Measured ad panel: exact, only the debug border applies.
                normal = data.m_PanelNormal;
                faceCenter = data.m_PanelCenter;
                halfDepth = data.m_PanelHalfDepth;
                width = data.m_PanelSize.x * (1f - 2f * settings.ExtraInset);
                height = data.m_PanelSize.y * (1f - 2f * settings.ExtraInset);
            }
            else
            {
                // Fallback: assume the panel is the thin horizontal side of the mesh bounds.
                Bounds3 bounds = geometry.m_Bounds;
                float3 size = bounds.max - bounds.min;
                float3 center = (bounds.min + bounds.max) * 0.5f;
                bool facesZ = size.x >= size.z;
                normal = facesZ ? new float3(0f, 0f, 1f) : new float3(1f, 0f, 0f);
                float panelWidth = facesZ ? size.x : size.z;
                halfDepth = (facesZ ? size.z : size.x) * 0.5f;

                float insetSide = data.m_InsetSide + settings.ExtraInset;
                float insetTop = data.m_InsetTop + settings.ExtraInset;
                float insetBottom = data.m_InsetBottom + settings.ExtraInset;
                width = panelWidth * (1f - 2f * insetSide);
                height = size.y * (1f - insetTop - insetBottom);
                float centerY = bounds.min.y + size.y * insetBottom + height * 0.5f;
                faceCenter = new float3(center.x, centerY, center.z);
            }
            if (width <= 0.01f || height <= 0.01f)
                return default;

            float distance = halfDepth + data.m_SurfaceOffset + settings.SurfaceOffset;
            float3 up = new float3(0f, 1f, 0f);

            var faces = data.m_Faces;
            switch (settings.FacesOverride)
            {
                case ScreenFacesOverride.Front: faces = ScreenFaces.Front; break;
                case ScreenFacesOverride.Back: faces = ScreenFaces.Back; break;
                case ScreenFacesOverride.Both: faces = ScreenFaces.Both; break;
            }

            return new ScreenSurface
            {
                m_Valid = true,
                m_Faces = faces,
                m_Size = new float2(width, height),
                m_FrontCenter = faceCenter + normal * distance,
                m_BackCenter = faceCenter - normal * distance,
                // The quad's visible side is its local -Z, so it looks along -normal to face +normal.
                m_FrontRotation = quaternion.LookRotation(-normal, up),
                m_BackRotation = quaternion.LookRotation(normal, up),
            };
        }

        private static int HashCode(ScreenFacesOverride faces, float offset, float inset)
        {
            unchecked
            {
                return ((int)faces * 397 ^ offset.GetHashCode()) * 397 ^ inset.GetHashCode();
            }
        }

        /// <summary>Unit quad in the XY plane, visible from -Z, with UV (0,0) at the bottom left.</summary>
        private static Mesh CreateQuad()
        {
            var mesh = new Mesh { name = "LedScreens_Quad" };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f),
            };
            mesh.uv = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
            };
            mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
            mesh.tangents = new[] { new Vector4(1f, 0f, 0f, -1f), new Vector4(1f, 0f, 0f, -1f), new Vector4(1f, 0f, 0f, -1f), new Vector4(1f, 0f, 0f, -1f) };
            mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            mesh.RecalculateBounds();
            mesh.UploadMeshData(true);
            return mesh;
        }

        private static Material CreateMaterial(Texture placeholder)
        {
            var shader = Shader.Find("HDRP/Unlit");
            if (shader == null)
            {
                Mod.log.Error("Shader 'HDRP/Unlit' not found; LED screens can't be drawn");
                return null;
            }

            var material = new Material(shader) { name = "LedScreens_Screen" };
            material.SetColor(kUnlitColor, Color.white);
            material.SetTexture(kUnlitColorMap, placeholder);
            HDMaterial.ValidateMaterial(material);
            Mod.log.Info($"Screen material created, keywords: {string.Join(" ", material.shaderKeywords)}");
            return material;
        }
    }
}
