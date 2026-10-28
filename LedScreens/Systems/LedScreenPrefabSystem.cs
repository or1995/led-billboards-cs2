using System;
using System.Collections.Generic;
using System.Linq;
using Colossal.Mathematics;
using Game;
using Game.Prefabs;
using Game.SceneFlow;
using LedScreens.Components;
using LedScreens.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace LedScreens.Systems
{
    /// <summary>
    /// Clones vanilla billboard props into LED screen props once the vanilla prefabs are loaded,
    /// and puts them into their own "LED Screens" tab next to the other prop categories.
    /// </summary>
    public partial class LedScreenPrefabSystem : GameSystemBase
    {
        public const string kCategoryName = "LedScreens";

        public sealed class ScreenTemplate
        {
            public ScreenTemplate(string source, string name, string displayName, ScreenFaces faces, float insetSide, float insetTop, float insetBottom,
                PoleMount pole = null, string[] legacyNames = null)
            {
                Source = source;
                Name = name;
                DisplayName = displayName;
                Faces = faces;
                InsetSide = insetSide;
                InsetTop = insetTop;
                InsetBottom = insetBottom;
                Pole = pole;
                LegacyNames = legacyNames ?? Array.Empty<string>();
            }

            public string Source { get; }
            public string Name { get; }
            public string DisplayName { get; }
            public ScreenFaces Faces { get; }
            public float InsetSide { get; }
            public float InsetTop { get; }
            public float InsetBottom { get; }

            /// <summary>When set, the panel (or a grid of panels) is raised onto poles.</summary>
            public PoleMount Pole { get; }

            /// <summary>Earlier names of this prefab; saves that use them load as this prefab.</summary>
            public string[] LegacyNames { get; }
        }

        /// <summary>
        /// A screen raised on poles: a grid of copies of the source panel with one continuous
        /// image across all of them, and poles whose thickness suits the size of the screen.
        /// </summary>
        public sealed class PoleMount
        {
            public PoleMount(int columns, int rows, int poles, float poleDiameter, float elevation)
            {
                Columns = columns;
                Rows = rows;
                Poles = poles;
                PoleDiameter = poleDiameter;
                Elevation = elevation;
            }

            public int Columns { get; }
            public int Rows { get; }
            public int Poles { get; }

            /// <summary>Wanted pole thickness in meters; the closest vanilla pole is used.</summary>
            public float PoleDiameter { get; }

            /// <summary>Height of the bottom of the panels above the ground, in meters.</summary>
            public float Elevation { get; }
        }

        // The screen normally sits on the measured ad panel. Faces and insets are only used
        // when measuring fails: insets are then fractions of the mesh bounds (pole billboards
        // include the pole in their bounds, hence the large bottom inset).
        //
        // The vanilla Medium01/Large01/Huge01 and WallHuge01 models have floodlights aimed at the
        // ad face (light effects + glowing lamp heads in the mesh). A screen lights itself, so the
        // LED versions use the matching 02 models, which have no lights. The vanilla billboards
        // themselves are untouched. Screens placed with the old floodlit versions load as the new ones.
        public static readonly ScreenTemplate[] Templates =
        {
            new ScreenTemplate("BillboardWallSmall01", "LedScreenWallSmall01", "LED Wall Screen (Small)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f),
            new ScreenTemplate("BillboardWallMedium01", "LedScreenWallMedium01", "LED Wall Screen (Medium)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f),
            new ScreenTemplate("BillboardWallLarge01", "LedScreenWallLarge01", "LED Wall Screen (Large)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f),
            new ScreenTemplate("BillboardWallHuge02", "LedScreenWallHuge02", "LED Wall Screen (Huge)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                legacyNames: new[] { "LedScreenWallHuge01" }),
            new ScreenTemplate("BillboardWallMedium01", "LedScreenHighPoleMedium01", "LED High-Pole Screen (Medium)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                new PoleMount(columns: 1, rows: 1, poles: 1, poleDiameter: 0.35f, elevation: 6f)),
            new ScreenTemplate("BillboardWallLarge01", "LedScreenHighPoleLarge01", "LED High-Pole Screen (Large)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                new PoleMount(columns: 1, rows: 1, poles: 2, poleDiameter: 0.4f, elevation: 7f)),
            new ScreenTemplate("BillboardWallHuge02", "LedScreenHighPoleHuge01", "LED High-Pole Screen (Huge)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                new PoleMount(columns: 1, rows: 1, poles: 1, poleDiameter: 1.4f, elevation: 9f)),
            new ScreenTemplate("BillboardWallHuge02", "LedScreenHighPoleWide01", "LED High-Pole Screen (Extra Wide)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                new PoleMount(columns: 2, rows: 1, poles: 2, poleDiameter: 1.6f, elevation: 10f)),
            new ScreenTemplate("BillboardWallHuge02", "LedScreenHighPoleGiant01", "LED High-Pole Screen (Giant)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                new PoleMount(columns: 2, rows: 2, poles: 2, poleDiameter: 2.2f, elevation: 10f)),
            new ScreenTemplate("BillboardWallHuge02", "LedScreenHighPoleRibbon01", "LED High-Pole Screen (Ribbon)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                new PoleMount(columns: 3, rows: 1, poles: 3, poleDiameter: 1.6f, elevation: 10f)),
            new ScreenTemplate("BillboardSmall01", "LedScreenSmall01", "LED Billboard (Small)", ScreenFaces.Both, 0.03f, 0.03f, 0.5f),
            new ScreenTemplate("BillboardSmall02", "LedScreenSmall02", "LED Billboard (Small 2)", ScreenFaces.Both, 0.03f, 0.03f, 0.5f),
            new ScreenTemplate("BillboardMedium02", "LedScreenMedium02", "LED Billboard (Medium)", ScreenFaces.Both, 0.03f, 0.03f, 0.5f,
                legacyNames: new[] { "LedScreenMedium01" }),
            new ScreenTemplate("BillboardLarge02", "LedScreenLarge02", "LED Billboard (Large)", ScreenFaces.Both, 0.03f, 0.03f, 0.5f,
                legacyNames: new[] { "LedScreenLarge01" }),
            new ScreenTemplate("BillboardHuge02", "LedScreenHuge02", "LED Billboard (Huge)", ScreenFaces.Both, 0.03f, 0.03f, 0.5f,
                legacyNames: new[] { "LedScreenHuge01" }),
            new ScreenTemplate("BillboardRoundSmall01", "LedScreenRoundSmall01", "LED Round Billboard (Small)", ScreenFaces.Both, 0.03f, 0.03f, 0.5f),
            new ScreenTemplate("BillboardRoundMedium01", "LedScreenRoundMedium01", "LED Round Billboard (Medium)", ScreenFaces.Both, 0.03f, 0.03f, 0.5f),
            new ScreenTemplate("BillboardRoundLarge01", "LedScreenRoundLarge01", "LED Round Billboard (Large)", ScreenFaces.Both, 0.03f, 0.03f, 0.5f),
        };

        // Clones must not inherit these: they would make the game spawn our screens on
        // buildings in place of random billboards, hide them by theme, or clash on IDs.
        // PlaceholderObject turns every placed instance into an unselectable placeholder
        // (vanilla billboards are swapped for brand variants inside buildings).
        // EffectSource holds the floodlights aimed at the ad face; a screen lights itself.
        private static readonly Type[] kStrippedComponents =
        {
            typeof(PlaceholderObject),
            typeof(SpawnableBrandDummy),
            typeof(SpawnableObject),
            typeof(ObsoleteIdentifiers),
            typeof(ThemeObject),
            typeof(AssetPackItem),
            typeof(EffectSource),
        };

        private PrefabSystem m_PrefabSystem;
        private bool m_PrefabsCreated;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            // Work happens in OnGamePreload; nothing to do per frame.
            Enabled = false;
        }

        protected override void OnUpdate()
        {
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            if (m_PrefabsCreated)
                return;

            // Set first so a failure doesn't make us add duplicates on every load.
            m_PrefabsCreated = true;
            try
            {
                CreatePrefabs();
            }
            catch (Exception e)
            {
                Mod.log.Error(e, "Failed to create LED screen prefabs");
            }
        }

        private void CreatePrefabs()
        {
            var prefabsByName = CollectPrefabsByName();
            List<PoleCandidate> poles = null;

            UIAssetCategoryPrefab category = null;
            for (int i = 0; i < Templates.Length; i++)
            {
                var template = Templates[i];
                if (!prefabsByName.TryGetValue(template.Source, out var source))
                {
                    Mod.log.Warn($"Vanilla prefab '{template.Source}' not found, skipping '{template.Name}'");
                    continue;
                }

                if (category == null)
                    category = CreateCategory(prefabsByName, source);

                if (HasLights(source))
                    Mod.log.Warn($"'{template.Source}' has floodlights; '{template.Name}' will show them");

                var clone = source.Clone(template.Name);
                foreach (var type in kStrippedComponents)
                    clone.Remove(type);

                if (template.LegacyNames.Length > 0)
                {
                    // Added after stripping: the vanilla identifiers must not come along.
                    var obsolete = clone.AddComponent<ObsoleteIdentifiers>();
                    obsolete.m_PrefabIdentifiers = Array.ConvertAll(template.LegacyNames,
                        legacy => new PrefabIdentifierInfo { m_Name = legacy, m_Type = clone.GetType().Name });
                }

                var ui = clone.AddOrGetComponent<UIObject>();
                if (category != null)
                    ui.m_Group = category;
                ui.m_Priority = (i + 1) * 10;
                // Empty icon = the game renders a thumbnail of the mesh.

                clone.AddOrGetComponent<PlaceableObject>();

                var screen = clone.AddComponent<LedScreenPrefab>();
                screen.m_Faces = template.Faces;
                screen.m_InsetSide = template.InsetSide;
                screen.m_InsetTop = template.InsetTop;
                screen.m_InsetBottom = template.InsetBottom;

                Mod.log.Info(AdPanelMeasurer.Describe(source));
                bool measured = AdPanelMeasurer.TryMeasure(source, out var panel);
                if (template.Pole != null)
                {
                    poles ??= CollectPoleCandidates(prefabsByName);
                    if (!measured || !(clone is ObjectGeometryPrefab cloneGeometry) || !TryMountOnPoles(cloneGeometry, template, poles, ref panel))
                    {
                        Mod.log.Warn($"Skipping '{template.Name}': couldn't build the pole version");
                        continue;
                    }
                }
                if (measured)
                {
                    screen.m_HasPanel = true;
                    screen.m_PanelCenter = panel.m_Center;
                    screen.m_PanelSize = panel.m_Size;
                    screen.m_PanelNormal = panel.m_Normal;
                    screen.m_PanelHalfDepth = panel.m_HalfDepth;
                    screen.m_Faces = panel.m_Faces;
                    Mod.log.Info($"{template.Name}: ad panel center {panel.m_Center} size {panel.m_Size} normal {panel.m_Normal} halfDepth {panel.m_HalfDepth:0.000} faces {panel.m_Faces}");
                }
                else
                {
                    Mod.log.Warn($"{template.Name}: couldn't measure the ad panel, falling back to mesh bounds");
                }

                if (m_PrefabSystem.AddPrefab(clone))
                    Mod.log.Info($"Added prefab '{template.Name}' (from '{template.Source}', {source.GetType().Name})");
                else
                    Mod.log.Warn($"PrefabSystem rejected '{template.Name}'");
            }
        }

        public enum PoleKind
        {
            /// <summary>Slim grey steel (flag poles).</summary>
            Steel,

            /// <summary>Thick concrete column (industrial chimneys).</summary>
            Concrete,
        }

        /// <summary>A vanilla mesh that works as a pole, measured once.</summary>
        private sealed class PoleCandidate
        {
            public string Name;
            public PoleKind Kind;
            public ObjectMeshInfo Mesh;

            /// <summary>Thickness of the trunk itself (not arms, caps or platforms).</summary>
            public float Diameter;

            /// <summary>Centre of the trunk in the pole prefab's space (y = 0).</summary>
            public float3 TrunkCenter;

            public float Height;
            public bool HasLights;
        }

        // Plain vertical vanilla meshes: steel flag poles for small screens, concrete chimneys
        // as monopoles for big ones. Extra height is sunk into the ground. (Wooden power-line
        // poles are left out: crossarms and a brown look.)
        private static readonly (string Prefix, PoleKind Kind)[] kPoleSources =
        {
            ("FlagPole01", PoleKind.Steel),
            ("FlagPole02", PoleKind.Steel),
            ("IndustrialChimney", PoleKind.Concrete),
        };

        // Screens wanting a thicker pole than this get a concrete column in Auto style.
        private const float kConcreteFromDiameter = 1f;

        // In Steel style, wide screens get a pole about every this many meters.
        private const float kSteelPoleSpacing = 5f;

        private static List<PoleCandidate> CollectPoleCandidates(Dictionary<string, PrefabBase> prefabsByName)
        {
            var result = new List<PoleCandidate>();
            foreach (var pair in prefabsByName)
            {
                string name = pair.Key;
                int source = Array.FindIndex(kPoleSources, s => name.StartsWith(s.Prefix, StringComparison.Ordinal));
                if (source < 0 || name.IndexOf("Random", StringComparison.Ordinal) >= 0 || name.IndexOf("_Clt", StringComparison.Ordinal) >= 0 ||
                    !(pair.Value is ObjectGeometryPrefab geometry) || geometry.m_Meshes == null)
                {
                    continue;
                }

                // The flag cloth is a separate "_Clt" mesh; we only want the pole.
                foreach (var info in geometry.m_Meshes)
                {
                    if (!(info.m_Mesh is RenderPrefab render) || render.name.IndexOf("_Clt", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;

                    if (TryMeasureTrunk(info, render, out float diameter, out float3 center, out float height) &&
                        height > 4f * diameter && diameter > 0.05f)
                    {
                        var candidate = new PoleCandidate
                        {
                            Name = name,
                            Kind = kPoleSources[source].Kind,
                            Mesh = info,
                            Diameter = diameter,
                            TrunkCenter = center,
                            Height = height,
                            HasLights = render.Has<EmissiveProperties>(),
                        };
                        result.Add(candidate);
                        Mod.log.Info($"Pole candidate '{name}' ({candidate.Kind}): trunk {diameter:0.00} m thick, {height:0.0} m tall{(candidate.HasLights ? ", has lights" : "")}");
                    }
                    break;
                }
            }
            return result;
        }

        /// <summary>
        /// Measures the trunk from the mesh's vertices between 10% and 90% of its height, so
        /// arms, caps, platforms and foundations don't count. Falls back to the base ring.
        /// </summary>
        private static bool TryMeasureTrunk(ObjectMeshInfo info, RenderPrefab render, out float diameter, out float3 center, out float height)
        {
            diameter = 0f;
            center = float3.zero;
            height = 0f;
            if (!render.hasGeometryAsset)
                return false;

            Mesh[] meshes = render.ObtainMeshes();
            try
            {
                if (meshes == null || meshes.Length == 0 || !meshes[0].isReadable)
                    return false;

                var points = new List<float3>();
                foreach (var mesh in meshes)
                {
                    foreach (var v in mesh.vertices)
                        points.Add(info.m_Position + math.mul(info.m_Rotation, (float3)v));
                }
                if (points.Count == 0)
                    return false;

                float minY = points.Min(p => p.y);
                float maxY = points.Max(p => p.y);
                height = maxY;
                float span = maxY - minY;

                var band = points.Where(p => p.y > minY + span * 0.1f && p.y < minY + span * 0.9f).ToList();
                if (band.Count < 6)
                    band = points.Where(p => p.y < minY + span * 0.03f).ToList();
                if (band.Count < 3)
                    return false;

                float2 min = new float2(band.Min(p => p.x), band.Min(p => p.z));
                float2 max = new float2(band.Max(p => p.x), band.Max(p => p.z));
                diameter = math.cmax(max - min);
                center = new float3((min.x + max.x) * 0.5f, 0f, (min.y + max.y) * 0.5f);
                return true;
            }
            finally
            {
                render.ReleaseMeshes();
            }
        }

        private static PoleKind ChoosePoleKind(PoleMount mount)
        {
            switch (Mod.Settings?.PoleStyle ?? PoleStyle.Auto)
            {
                case PoleStyle.Steel: return PoleKind.Steel;
                case PoleStyle.Concrete: return PoleKind.Concrete;
                default: return mount.PoleDiameter >= kConcreteFromDiameter ? PoleKind.Concrete : PoleKind.Steel;
            }
        }

        /// <summary>
        /// Arranges the panel as a grid raised to the template's elevation, puts poles of a fitting
        /// thickness right against its back (spread across the width), and widens the screen to
        /// span the grid.
        /// </summary>
        private static bool TryMountOnPoles(ObjectGeometryPrefab clone, ScreenTemplate template, List<PoleCandidate> candidates, ref AdPanelMeasurer.Panel panel)
        {
            var mount = template.Pole;
            if (clone.m_Meshes == null || clone.m_Meshes.Length == 0)
                return false;

            Bounds3 panelBounds = MeshBounds(clone.m_Meshes[0]);
            for (int i = 1; i < clone.m_Meshes.Length; i++)
                panelBounds |= MeshBounds(clone.m_Meshes[i]);

            float3 up = new float3(0f, 1f, 0f);
            float3 normal = panel.m_Normal;
            float3 right = math.normalize(math.cross(up, normal));

            // Panel tile size: its extent across the screen, and its height.
            float3 extent = panelBounds.max - panelBounds.min;
            float tileWidth = math.abs(extent.x * right.x) + math.abs(extent.z * right.z);
            float tileHeight = extent.y;
            float gridWidth = tileWidth * mount.Columns;
            float gridHeight = tileHeight * mount.Rows;
            float lift = mount.Elevation - panelBounds.min.y;

            // Pole top hidden behind the grid, a bit above its middle.
            float poleTop = mount.Elevation + gridHeight * 0.6f;
            PoleKind kind = ChoosePoleKind(mount);
            PoleCandidate pole = PickPole(candidates, kind, mount.PoleDiameter, poleTop, tileWidth)
                ?? PickPole(candidates, kind == PoleKind.Steel ? PoleKind.Concrete : PoleKind.Steel, mount.PoleDiameter, poleTop, tileWidth);
            if (pole == null)
            {
                Mod.log.Warn($"No vanilla pole is tall enough ({poleTop:0.0} m) for '{template.Name}'");
                return false;
            }

            // Slim steel poles can't carry a wide screen alone; add more across the width.
            int poleCount = mount.Poles;
            if (pole.Kind == PoleKind.Steel)
                poleCount = Math.Max(poleCount, (int)math.ceil(gridWidth / kSteelPoleSpacing));

            // Back of the panel: the bounds corner furthest against the screen normal.
            float back = float.MaxValue;
            for (int c = 0; c < 4; c++)
            {
                var corner = new float3((c & 1) != 0 ? panelBounds.max.x : panelBounds.min.x, 0f, (c & 2) != 0 ? panelBounds.max.z : panelBounds.min.z);
                back = math.min(back, math.dot(corner, normal));
            }

            var meshes = new List<ObjectMeshInfo>();
            for (int row = 0; row < mount.Rows; row++)
            {
                for (int column = 0; column < mount.Columns; column++)
                {
                    float3 offset = right * ((column - (mount.Columns - 1) * 0.5f) * tileWidth) + up * (lift + row * tileHeight);
                    foreach (var info in clone.m_Meshes)
                    {
                        meshes.Add(new ObjectMeshInfo
                        {
                            m_Mesh = info.m_Mesh,
                            m_Position = info.m_Position + offset,
                            m_Rotation = info.m_Rotation,
                            m_RequireState = info.m_RequireState,
                        });
                    }
                }
            }

            // Trunk touching the back of the panel (slightly overlapping, so no gap shows).
            float trunkRadius = pole.Diameter * 0.5f;
            float behind = back - trunkRadius * 0.8f;
            float sink = pole.Height - poleTop; // extra height goes into the ground
            float across = math.dot(panel.m_Center, right);
            for (int k = 0; k < poleCount; k++)
            {
                float spread = poleCount == 1 ? 0f : (k / (poleCount - 1f) - 0.5f) * gridWidth * 0.7f;
                float3 target = right * (across + spread) + normal * behind;
                meshes.Add(new ObjectMeshInfo
                {
                    m_Mesh = pole.Mesh.m_Mesh,
                    m_Position = pole.Mesh.m_Position + new float3(target.x - pole.TrunkCenter.x, -sink, target.z - pole.TrunkCenter.z),
                    m_Rotation = pole.Mesh.m_Rotation,
                    m_RequireState = pole.Mesh.m_RequireState,
                });
            }

            clone.m_Meshes = meshes.ToArray();
            // Stand on the ground instead of snapping to walls.
            clone.Remove(typeof(AttachedObject));

            // One continuous screen over the whole grid (it also covers the frames between tiles).
            panel.m_Center += up * (lift + (mount.Rows - 1) * tileHeight * 0.5f);
            panel.m_Size += new float2((mount.Columns - 1) * tileWidth, (mount.Rows - 1) * tileHeight);

            Mod.log.Info($"{template.Name}: {mount.Columns}x{mount.Rows} panel(s) at {mount.Elevation:0.0} m on {poleCount} x '{pole.Name}' ({pole.Kind}, trunk {pole.Diameter:0.00} m, wanted {mount.PoleDiameter:0.00}, sunk {sink:0.0} m)");
            return true;
        }

        private static PoleCandidate PickPole(List<PoleCandidate> candidates, PoleKind kind, float wantedDiameter, float minHeight, float tileWidth)
        {
            PoleCandidate best = null;
            float bestScore = float.MaxValue;
            foreach (var candidate in candidates)
            {
                if (candidate.Kind != kind || candidate.Height < minHeight || candidate.Diameter > tileWidth * 0.4f)
                    continue;
                float score = math.abs(math.log(candidate.Diameter / wantedDiameter)) + (candidate.HasLights ? 1f : 0f);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }
            return best;
        }

        /// <summary>Floodlit billboards have light effects and glowing (emissive) lamp heads in their mesh.</summary>
        private static bool HasLights(PrefabBase prefab)
        {
            if (prefab.Has<EffectSource>())
                return true;
            if (prefab is ObjectGeometryPrefab geometry && geometry.m_Meshes != null)
            {
                foreach (var info in geometry.m_Meshes)
                {
                    if (info.m_Mesh is RenderPrefab render && render.Has<EmissiveProperties>())
                        return true;
                }
            }
            return false;
        }

        private static Bounds3 MeshBounds(ObjectMeshInfo info)
        {
            Bounds3 local = info.m_Mesh is RenderPrefab render ? render.bounds : default;
            // Rotate all 8 corners; vanilla meshes are rarely rotated but it's cheap to be exact.
            Bounds3 result = new Bounds3(new float3(float.MaxValue), new float3(float.MinValue));
            for (int c = 0; c < 8; c++)
            {
                var corner = new float3(
                    (c & 1) != 0 ? local.max.x : local.min.x,
                    (c & 2) != 0 ? local.max.y : local.min.y,
                    (c & 4) != 0 ? local.max.z : local.min.z);
                float3 p = info.m_Position + math.mul(info.m_Rotation, corner);
                result.min = math.min(result.min, p);
                result.max = math.max(result.max, p);
            }
            return result;
        }

        private Dictionary<string, PrefabBase> CollectPrefabsByName()
        {
            var result = new Dictionary<string, PrefabBase>();
            var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PrefabData>());
            using (var entities = query.ToEntityArray(Allocator.Temp))
            {
                foreach (var entity in entities)
                {
                    if (m_PrefabSystem.TryGetPrefab(entity, out PrefabBase prefab) && prefab != null && !result.ContainsKey(prefab.name))
                        result.Add(prefab.name, prefab);
                }
            }
            query.Dispose();
            return result;
        }

        /// <summary>Creates our own asset category inside the menu that holds the vanilla prop categories.</summary>
        private UIAssetCategoryPrefab CreateCategory(Dictionary<string, PrefabBase> prefabsByName, PrefabBase iconSource)
        {
            UIAssetMenuPrefab menu = null;

            // Prefer the menu of the source billboard's own category, then any "Props" category, then Landscaping.
            if (iconSource.TryGet<UIObject>(out var sourceUi) && sourceUi.m_Group is UIAssetCategoryPrefab sourceCategory)
                menu = sourceCategory.m_Menu;

            if (menu == null)
            {
                foreach (var prefab in prefabsByName.Values)
                {
                    if (prefab is UIAssetCategoryPrefab c && c.m_Menu != null && c.name.StartsWith("Props", StringComparison.Ordinal))
                    {
                        menu = c.m_Menu;
                        break;
                    }
                }
            }

            if (menu == null && prefabsByName.TryGetValue("Landscaping", out var landscaping))
                menu = landscaping as UIAssetMenuPrefab;

            if (menu == null)
            {
                Mod.log.Warn("No suitable toolbar menu found; LED screens will only be reachable via search");
                return null;
            }

            var category = ScriptableObject.CreateInstance<UIAssetCategoryPrefab>();
            category.name = kCategoryName;
            category.m_Menu = menu;
            var ui = category.AddComponent<UIObject>();
            ui.m_Icon = sourceUi != null && !string.IsNullOrEmpty(sourceUi.m_Icon)
                ? sourceUi.m_Icon
                : $"{iconSource.thumbnailUrl}?width=128&height=128";
            ui.m_Priority = 1000;

            if (!m_PrefabSystem.AddPrefab(category))
            {
                Mod.log.Warn("PrefabSystem rejected the LED Screens category");
                return null;
            }

            Mod.log.Info($"Added category '{kCategoryName}' to menu '{menu.name}'");
            return category;
        }
    }
}
