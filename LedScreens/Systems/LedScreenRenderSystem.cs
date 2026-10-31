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

        // Faces smaller than this on screen are skipped: the vanilla panel underneath looks the same.
        private const float kMinPixels = 1f;

        // Screens out of view check their media every this many frames (staggered): media that is
        // already loaded stays loaded, but they don't start loads nobody would see.
        private const int kHiddenRefreshFrames = 30;

        private static readonly int kUnlitColorMap = Shader.PropertyToID("_UnlitColorMap");
        private static readonly int kUnlitColor = Shader.PropertyToID("_UnlitColor");
        private static readonly int kUnlitColorMapST = Shader.PropertyToID("_UnlitColorMap_ST");

        // Round screens are drawn slightly inside the measured face so no corner of the image shows past its rim.
        private const float kRoundInset = 0.98f;

        // Order of the faces in ScreenSurface, matching the ScreenFaces bits.
        private static readonly ScreenFaces[] kFaceBits = { ScreenFaces.Front, ScreenFaces.Back, ScreenFaces.Left, ScreenFaces.Right };

        /// <summary>Screen placement in prefab-local space, derived from the mesh bounds.</summary>
        private struct ScreenSurface
        {
            public bool m_Valid;
            public ScreenFaces m_Faces;
            public bool m_Round;
            public float2 m_Size;

            /// <summary>Front and back faces bend back this far at their left and right edges (0 = flat).</summary>
            public float m_Curve;

            /// <summary>Per face (front, back, left, right): centre and rotation in prefab space.</summary>
            public float3[] m_Centers;
            public quaternion[] m_Rotations;
        }

        private readonly struct BatchKey : IEquatable<BatchKey>
        {
            public readonly Texture Texture;
            public readonly int Brightness; // hundredths, so nearly-equal values share a batch
            public readonly bool Round;
            public readonly bool Curved;

            public BatchKey(Texture texture, int brightness, bool round, bool curved)
            {
                Texture = texture;
                Brightness = brightness;
                Round = round;
                Curved = curved;
            }

            public bool Equals(BatchKey other) => ReferenceEquals(Texture, other.Texture) && Brightness == other.Brightness && Round == other.Round && Curved == other.Curved;

            public override bool Equals(object obj) => obj is BatchKey other && Equals(other);

            public override int GetHashCode() => ((Texture != null ? Texture.GetHashCode() : 0) * 397 ^ Brightness) * 4 + (Round ? 1 : 0) + (Curved ? 2 : 0);
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
        private Mesh m_Disc;
        private Mesh m_CurvedQuad;
        private Material m_Material;
        private MaterialPropertyBlock m_Properties;
        private int m_SurfaceSettingsHash;
        private bool m_LoggedFirstDraw;
        private int m_Frame;
        private readonly VisibleFace[] m_VisibleFaces = new VisibleFace[4];

        /// <summary>A face that passed culling, in world space.</summary>
        private struct VisibleFace
        {
            public float3 m_Center;
            public quaternion m_Rotation;
            public Bounds m_Bounds;
        }

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
            m_Disc = CreateDisc(64);
            m_CurvedQuad = CreateCurvedQuad(24);
            m_Properties = new MaterialPropertyBlock();
            m_Material = CreateMaterial(MediaLibrary.Instance.NoSignal);
        }

        protected override void OnDestroy()
        {
            if (m_Material != null)
                UnityEngine.Object.Destroy(m_Material);
            if (m_Quad != null)
                UnityEngine.Object.Destroy(m_Quad);
            if (m_Disc != null)
                UnityEngine.Object.Destroy(m_Disc);
            if (m_CurvedQuad != null)
                UnityEngine.Object.Destroy(m_CurvedQuad);
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
            // Pixels per metre at 1 m distance, for skipping faces too small to see.
            float pixelScale = camera.pixelHeight / (2f * math.tan(math.radians(camera.fieldOfView) * 0.5f));
            m_Frame = unchecked(m_Frame + 1);
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

                    int visibleMask = 0;
                    for (int f = 0; f < kFaceBits.Length; f++)
                    {
                        if ((surface.m_Faces & kFaceBits[f]) != 0 &&
                            TryCullFace(transform, surface.m_Centers[f], surface.m_Rotations[f], surface.m_Size, cameraPosition, pixelScale, out m_VisibleFaces[f]))
                        {
                            visibleMask |= 1 << f;
                        }
                    }
                    if (visibleMask == 0 && unchecked((uint)(entities[i].Index + m_Frame)) % kHiddenRefreshFrames != 0)
                        continue;

                    var screen = screens[i];
                    if (visibleMask == 0)
                    {
                        if ((screen.m_Flags & LedScreenFlags.Initialized) != 0)
                            library.Touch(resolver.Resolve(EntityManager, entities[i], screen, time, out _, out _), ScreenMediaResolver.Effect(screen), now);
                        continue;
                    }

                    Texture texture = null;
                    float mediaBrightness = 1f;
                    if ((screen.m_Flags & LedScreenFlags.Initialized) != 0)
                    {
                        string media = resolver.Resolve(EntityManager, entities[i], screen, time, out string next, out float timeToNext);
                        var effect = ScreenMediaResolver.Effect(screen);
                        var clip = library.GetClip(media, effect, now);
                        if (clip != null)
                        {
                            texture = clip.GetFrame(time * screen.m_Speed + screen.m_TimeOffset);
                            mediaBrightness = clip.BrightnessScale;
                        }
                        // Start loading the next playlist item early so the switch has no gap.
                        if (next != null && timeToNext < kPreloadSeconds)
                            library.GetClip(next, effect, now);
                    }
                    if (texture == null)
                        texture = library.NoSignal;

                    int brightness = (int)math.round(screen.m_Brightness * settings.Brightness * mediaBrightness * 100f);

                    for (int f = 0; f < kFaceBits.Length; f++)
                    {
                        if ((visibleMask & (1 << f)) != 0)
                            AddFace(m_VisibleFaces[f], surface.m_Size, surface.m_Round, f < 2 ? surface.m_Curve : 0f, texture, brightness);
                    }
                }
            }

            DrawBatches(camera);
        }

        /// <summary>World placement of a face, or false when it faces away, is too small to see or is off screen.</summary>
        private bool TryCullFace(Transform transform, float3 localCenter, quaternion localRotation, float2 size, float3 cameraPosition, float pixelScale, out VisibleFace face)
        {
            face = default;
            float3 center = transform.m_Position + math.mul(transform.m_Rotation, localCenter);
            quaternion rotation = math.mul(transform.m_Rotation, localRotation);

            // The quad is visible from its local -Z side; skip faces turned away from the camera.
            float3 toCamera = cameraPosition - center;
            float3 normal = math.mul(rotation, new float3(0f, 0f, -1f));
            if (math.dot(normal, toCamera) <= 0f)
                return false;

            float extent = math.max(size.x, size.y);
            if (extent * pixelScale < kMinPixels * math.length(toCamera))
                return false;

            var bounds = new Bounds(center, new Vector3(extent, extent, extent));
            if (!GeometryUtility.TestPlanesAABB(m_FrustumPlanes, bounds))
                return false;

            face = new VisibleFace { m_Center = center, m_Rotation = rotation, m_Bounds = bounds };
            return true;
        }

        private void AddFace(in VisibleFace face, float2 size, bool round, float curve, Texture texture, int brightness)
        {
            var bounds = face.m_Bounds;
            bool curved = curve > 0f && !round;
            var key = new BatchKey(texture, brightness, round, curved);
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

            // The curved mesh bends back by 1 at its edges; the z scale makes that the face's own curve.
            batch.Matrices.Add(Matrix4x4.TRS(face.m_Center, face.m_Rotation, new Vector3(size.x, size.y, curved ? curve : 1f)));
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
                m_Properties.SetVector(kUnlitColorMapST, pair.Key.Round ? CoverSquare(pair.Key.Texture) : new Vector4(1f, 1f, 0f, 0f));
                Mesh mesh = pair.Key.Round ? m_Disc : pair.Key.Curved ? m_CurvedQuad : m_Quad;

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
                    Graphics.RenderMesh(renderParams, mesh, 0, matrix);

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
            float backDistance = 0f;
            if (data.m_HasPanel)
            {
                // Measured ad panel: exact, only the debug border applies.
                normal = data.m_PanelNormal;
                faceCenter = data.m_PanelCenter;
                halfDepth = data.m_PanelHalfDepth;
                backDistance = data.m_PanelBackDistance;
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
            if (data.m_Round)
            {
                float diameter = math.min(width, height) * kRoundInset;
                width = height = diameter;
            }
            if (width <= 0.01f || height <= 0.01f)
                return default;

            float offset = data.m_SurfaceOffset + settings.SurfaceOffset;
            float distance = halfDepth + offset;
            // Boards with the ad on the front only get their back screen on the board's own back.
            float backDistanceTotal = (backDistance > 0f ? backDistance : halfDepth) + offset;
            float3 up = new float3(0f, 1f, 0f);

            var faces = data.m_Faces;
            switch (settings.FacesOverride)
            {
                case ScreenFacesOverride.Front: faces = ScreenFaces.Front; break;
                case ScreenFacesOverride.Back: faces = ScreenFaces.Back; break;
                case ScreenFacesOverride.Both: faces = ScreenFaces.Both; break;
            }

            // Cubes: the side faces are as far from the centre as the front, at right angles.
            float3 right = math.normalize(math.cross(up, normal));
            return new ScreenSurface
            {
                m_Valid = true,
                m_Faces = faces,
                m_Round = data.m_Round,
                m_Size = new float2(width, height),
                m_Curve = data.m_HasPanel ? data.m_PanelCurve : 0f,
                m_Centers = new[]
                {
                    faceCenter + normal * distance,
                    faceCenter - normal * backDistanceTotal,
                    faceCenter - right * distance,
                    faceCenter + right * distance,
                },
                // The quad's visible side is its local -Z, so it looks along -dir to face dir.
                m_Rotations = new[]
                {
                    quaternion.LookRotation(-normal, up),
                    quaternion.LookRotation(normal, up),
                    quaternion.LookRotation(right, up),
                    quaternion.LookRotation(-right, up),
                },
            };
        }

        private static int HashCode(ScreenFacesOverride faces, float offset, float inset)
        {
            unchecked
            {
                return ((int)faces * 397 ^ offset.GetHashCode()) * 397 ^ inset.GetHashCode();
            }
        }

        /// <summary>
        /// Like the unit quad, but bent back (towards +Z) like the curved front of a sign: z = (2x)²,
        /// so 0 in the middle and 1 at the left and right edges. The draw matrix scales z to the real depth.
        /// </summary>
        private static Mesh CreateCurvedQuad(int columns)
        {
            var mesh = new Mesh { name = "LedScreens_CurvedQuad" };
            var vertices = new Vector3[(columns + 1) * 2];
            var uv = new Vector2[vertices.Length];
            var normals = new Vector3[vertices.Length];
            var tangents = new Vector4[vertices.Length];
            for (int i = 0; i <= columns; i++)
            {
                float u = i / (float)columns;
                float x = u - 0.5f;
                float z = 4f * x * x;
                vertices[i * 2] = new Vector3(x, -0.5f, z);
                vertices[i * 2 + 1] = new Vector3(x, 0.5f, z);
                uv[i * 2] = new Vector2(u, 0f);
                uv[i * 2 + 1] = new Vector2(u, 1f);
                normals[i * 2] = normals[i * 2 + 1] = Vector3.back;
                tangents[i * 2] = tangents[i * 2 + 1] = new Vector4(1f, 0f, 0f, -1f);
            }
            var triangles = new int[columns * 6];
            for (int i = 0; i < columns; i++)
            {
                int v = i * 2;
                // Same winding as the flat quad: bottom-left, top-left, bottom-right; top-left, top-right, bottom-right.
                triangles[i * 6] = v;
                triangles[i * 6 + 1] = v + 1;
                triangles[i * 6 + 2] = v + 2;
                triangles[i * 6 + 3] = v + 1;
                triangles[i * 6 + 4] = v + 3;
                triangles[i * 6 + 5] = v + 2;
            }
            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.normals = normals;
            mesh.tangents = tangents;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            mesh.UploadMeshData(true);
            return mesh;
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

        /// <summary>Unit disc in the XY plane, visible from -Z, with UVs like the quad's.</summary>
        private static Mesh CreateDisc(int segments)
        {
            var vertices = new Vector3[segments + 1];
            var uv = new Vector2[segments + 1];
            var normals = new Vector3[segments + 1];
            var tangents = new Vector4[segments + 1];
            var triangles = new int[segments * 3];
            uv[0] = new Vector2(0.5f, 0.5f);
            for (int i = 0; i <= segments; i++)
            {
                normals[i] = Vector3.back;
                tangents[i] = new Vector4(1f, 0f, 0f, -1f);
                if (i == 0)
                    continue;
                float angle = (i - 1) * 2f * Mathf.PI / segments;
                var p = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * 0.5f;
                vertices[i] = p;
                uv[i] = p + new Vector2(0.5f, 0.5f);
            }
            for (int i = 0; i < segments; i++)
            {
                // Clockwise seen from -Z, like the quad.
                triangles[i * 3] = 0;
                triangles[i * 3 + 1] = 1 + (i + 1) % segments;
                triangles[i * 3 + 2] = 1 + i;
            }

            var mesh = new Mesh { name = "LedScreens_Disc" };
            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.normals = normals;
            mesh.tangents = tangents;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            mesh.UploadMeshData(true);
            return mesh;
        }

        /// <summary>Texture scale/offset that crops the middle square of the image, so a round screen isn't squashed.</summary>
        private static Vector4 CoverSquare(Texture texture)
        {
            if (texture == null || texture.width <= 0 || texture.height <= 0)
                return new Vector4(1f, 1f, 0f, 0f);
            float aspect = (float)texture.width / texture.height;
            return aspect >= 1f
                ? new Vector4(1f / aspect, 1f, (1f - 1f / aspect) * 0.5f, 0f)
                : new Vector4(1f, aspect, 0f, (1f - aspect) * 0.5f);
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
