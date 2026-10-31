using System;
using System.Collections.Generic;
using System.IO;
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

        // Unique folder name: the UI files of all mods share the "ui-mods" host.
        private const string kIconFolder = "ledscreens-icons";

        public sealed class ScreenTemplate
        {
            public ScreenTemplate(string source, string name, string displayName, ScreenFaces faces, float insetSide, float insetTop, float insetBottom,
                Layout layout = null, string[] legacyNames = null, bool roadSide = false, bool requireMeasured = false, bool freestanding = false)
            {
                Source = source;
                Name = name;
                DisplayName = displayName;
                Faces = faces;
                InsetSide = insetSide;
                InsetTop = insetTop;
                InsetBottom = insetBottom;
                Layout = layout;
                LegacyNames = legacyNames ?? Array.Empty<string>();
                RoadSide = roadSide;
                RequireMeasured = requireMeasured;
                Freestanding = freestanding;
            }

            public string Source { get; }
            public string Name { get; }
            public string DisplayName { get; }
            public ScreenFaces Faces { get; }
            public float InsetSide { get; }
            public float InsetTop { get; }
            public float InsetBottom { get; }

            /// <summary>When set, the panel is repeated as a grid and/or raised onto poles.</summary>
            public Layout Layout { get; }

            /// <summary>Round ad face (the round billboards).</summary>
            public bool Round => Source.StartsWith("BillboardRound", StringComparison.Ordinal) || Source.StartsWith("SignRound", StringComparison.Ordinal);

            /// <summary>Earlier names of this prefab; saves that use them load as this prefab.</summary>
            public string[] LegacyNames { get; }

            /// <summary>Can be drawn along roads like trees (Line/Curve mode with road snapping fills the road's tree spots).</summary>
            public bool RoadSide { get; }

            /// <summary>Skip this prefab when the source's ad face can't be measured, instead of guessing from the mesh bounds.</summary>
            public bool RequireMeasured { get; }

            /// <summary>Stands on the ground on its own instead of snapping to walls (for a source that attaches to walls).</summary>
            public bool Freestanding { get; }
        }

        /// <summary>
        /// A grid of copies of the source panel with one continuous image across all of them.
        /// Either it stays on the wall like the source, or it stands on poles whose thickness
        /// suits the size of the screen, optionally with a second grid back to back, or four
        /// grids forming a cube.
        /// </summary>
        public sealed class Layout
        {
            private Layout(int columns, int rows, int poles, float poleDiameter, float elevation, int sides, bool freestanding)
            {
                Columns = columns;
                Rows = rows;
                Poles = poles;
                PoleDiameter = poleDiameter;
                Elevation = elevation;
                Sides = sides;
                Freestanding = freestanding;
            }

            /// <summary>A grid that mounts on walls like the source panel.</summary>
            public static Layout Wall(int columns, int rows) => new Layout(columns, rows, 0, 0f, 0f, 1, false);

            /// <summary>A grid standing on poles, its bottom at the given height.</summary>
            public static Layout OnPoles(int columns, int rows, int poles, float poleDiameter, float elevation, bool doubleSided = false) =>
                new Layout(columns, rows, poles, poleDiameter, elevation, doubleSided ? 2 : 1, true);

            /// <summary>
            /// Four grids forming an open-topped cube, its bottom at the given height: on a pole
            /// inside the cube, or with no poles, hanging in the air (to put under an arch or bridge).
            /// </summary>
            public static Layout Cube(int columns, int rows, float elevation, int poles = 0, float poleDiameter = 0f) =>
                new Layout(columns, rows, poles, poleDiameter, elevation, 4, true);

            /// <summary>A grid in the air with nothing holding it, its bottom at the given height (e.g. along a bridge).</summary>
            public static Layout Floating(int columns, int rows, float elevation, bool doubleSided = false) =>
                new Layout(columns, rows, 0, 0f, elevation, doubleSided ? 2 : 1, true);

            public int Columns { get; }
            public int Rows { get; }

            /// <summary>Number of poles; 0 for a wall-mounted grid.</summary>
            public int Poles { get; }

            public bool HasPoles => Poles > 0;

            /// <summary>Wanted pole thickness in meters; the closest vanilla pole is used.</summary>
            public float PoleDiameter { get; }

            /// <summary>Height of the bottom of the panels above the ground, in meters.</summary>
            public float Elevation { get; }

            /// <summary>Copies of the grid turned around the middle: 1, 2 (back to back) or 4 (a cube).</summary>
            public int Sides { get; }

            public bool DoubleSided => Sides == 2;

            /// <summary>Stands on the ground at the elevation instead of mounting on walls.</summary>
            public bool Freestanding { get; }
        }

        // The screen normally sits on the measured ad panel. Faces and insets are only used
        // when measuring fails: insets are then fractions of the mesh bounds (pole billboards
        // include the pole in their bounds, hence the large bottom inset).
        //
        // The vanilla Medium01/Large01/Huge01 and WallHuge01 models have floodlights aimed at the
        // ad face (light effects + glowing lamp heads in the mesh). A screen lights itself, so the
        // LED versions use the matching 02 models, which have no lights. The vanilla billboards
        // themselves are untouched. Screens placed with the old floodlit versions load as the new ones.
        //
        // Placed screens are saved by prefab name: never rename or remove a template without
        // listing the old name in legacyNames. Changing a source or layout is save-safe but
        // changes how placed screens look.
        //
        // Grids with more than one row are built from Large panels: the Huge panel has a walkway
        // along its bottom edge (part of the same mesh), which would cross the screen when stacked.
        // A Large panel is exactly 2/3 of a Huge one.
        // The order here is the order in the toolbar.
        public static readonly ScreenTemplate[] Templates =
        {
            // Wall screens
            new ScreenTemplate("BillboardWallSmall01", "LedScreenWallSmall01", "LED Wall Screen (Small)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f),
            new ScreenTemplate("BillboardWallMedium01", "LedScreenWallMedium01", "LED Wall Screen (Medium)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f),
            new ScreenTemplate("BillboardWallLarge01", "LedScreenWallLarge01", "LED Wall Screen (Large)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f),
            new ScreenTemplate("BillboardWallHuge02", "LedScreenWallHuge02", "LED Wall Screen (Huge)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                legacyNames: new[] { "LedScreenWallHuge01" }),
            new ScreenTemplate("BillboardWallSmall01", "LedPosterWallSmall2x2", "LED Poster Screen (Wall)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.Wall(2, 2)),
            new ScreenTemplate("BillboardWallMedium01", "LedPortraitWallMedium1x3", "LED Portrait Wall Screen", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.Wall(1, 3)),
            new ScreenTemplate("BillboardWallMedium01", "LedTickerWallMedium3x1", "LED Ticker Strip (Short)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.Wall(3, 1)),
            new ScreenTemplate("BillboardWallMedium01", "LedTickerWallMedium6x1", "LED Ticker Strip (Long)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.Wall(6, 1)),

            // Video walls and building facades
            new ScreenTemplate("BillboardWallLarge01", "LedVideoWallLarge2x1", "LED Video Wall (2×1)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.Wall(2, 1)),
            new ScreenTemplate("BillboardWallLarge01", "LedVideoWallLarge2x2", "LED Video Wall (2×2)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.Wall(2, 2)),
            new ScreenTemplate("BillboardWallLarge01", "LedVideoWallLarge3x2", "LED Video Wall (3×2)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.Wall(3, 2)),
            new ScreenTemplate("BillboardWallLarge01", "LedFacadeLarge3x3", "LED Facade Screen (3×3)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.Wall(3, 3), legacyNames: new[] { "LedFacadeHuge2x2" }),
            new ScreenTemplate("BillboardWallLarge01", "LedFacadeLarge5x3", "LED Facade Screen (5×3)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.Wall(5, 3), legacyNames: new[] { "LedFacadeHuge3x2" }),
            new ScreenTemplate("BillboardWallLarge01", "LedFacadeLarge6x5", "LED Facade Screen (6×5)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.Wall(6, 5), legacyNames: new[] { "LedFacadeHuge4x3" }),
            new ScreenTemplate("BillboardWallLarge01", "LedFacadeLarge2x6", "LED Facade Tower (Portrait)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.Wall(2, 6), legacyNames: new[] { "LedFacadeHuge1x3" }),

            // Skyscraper screens: huge wall grids for the side of a tower, centred where you click.
            new ScreenTemplate("BillboardWallLarge01", "LedSkyscraperTall4x12", "LED Skyscraper Screen (Tall, 34×47 m)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.Wall(4, 12)),
            new ScreenTemplate("BillboardWallLarge01", "LedSkyscraperStrip2x16", "LED Skyscraper Strip (17×63 m)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.Wall(2, 16)),
            new ScreenTemplate("BillboardWallLarge01", "LedSkyscraperWide8x6", "LED Skyscraper Screen (Wide, 67×24 m)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.Wall(8, 6)),
            new ScreenTemplate("BillboardWallLarge01", "LedSkyscraperMega8x12", "LED Skyscraper Screen (Mega, 67×47 m)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.Wall(8, 12)),
            new ScreenTemplate("BillboardWallLarge01", "LedSkyscraperBand12x2", "LED Skyscraper Crown Band (100×8 m)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.Wall(12, 2)),

            // Free-standing billboards
            new ScreenTemplate("BillboardSmall01", "LedScreenSmall01", "LED Billboard (Small)", ScreenFaces.Both, 0.03f, 0.03f, 0.5f, roadSide: true),
            new ScreenTemplate("BillboardSmall02", "LedScreenSmall02", "LED Billboard (Small 2)", ScreenFaces.Both, 0.03f, 0.03f, 0.5f, roadSide: true),
            new ScreenTemplate("BillboardMedium02", "LedScreenMedium02", "LED Billboard (Medium)", ScreenFaces.Both, 0.03f, 0.03f, 0.5f,
                legacyNames: new[] { "LedScreenMedium01" }),
            new ScreenTemplate("BillboardLarge02", "LedScreenLarge02", "LED Billboard (Large)", ScreenFaces.Both, 0.03f, 0.03f, 0.5f,
                legacyNames: new[] { "LedScreenLarge01" }),
            new ScreenTemplate("BillboardHuge02", "LedScreenHuge02", "LED Billboard (Huge)", ScreenFaces.Both, 0.03f, 0.03f, 0.5f,
                legacyNames: new[] { "LedScreenHuge01" }),
            new ScreenTemplate("BillboardWallSmall01", "LedTotemSmall2x3", "LED Street Totem", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.OnPoles(columns: 2, rows: 3, poles: 1, poleDiameter: 0.2f, elevation: 0.4f), roadSide: true),
            new ScreenTemplate("BillboardWallSmall01", "LedTotemSmall2x3Double", "LED Street Totem (Double-Sided)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.OnPoles(columns: 2, rows: 3, poles: 1, poleDiameter: 0.2f, elevation: 0.4f, doubleSided: true), roadSide: true),
            new ScreenTemplate("BillboardWallLarge01", "LedRoadsideLarge01", "LED Roadside Screen (Large)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.OnPoles(columns: 1, rows: 1, poles: 2, poleDiameter: 0.3f, elevation: 2.5f), roadSide: true),
            new ScreenTemplate("BillboardWallHuge02", "LedRoadsideHuge01", "LED Roadside Screen (Huge)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.OnPoles(columns: 1, rows: 1, poles: 3, poleDiameter: 0.4f, elevation: 3f)),
            new ScreenTemplate("BillboardWallLarge01", "LedRoadsideLargeDouble", "LED Roadside Screen (Large, Double-Sided)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.OnPoles(columns: 1, rows: 1, poles: 2, poleDiameter: 0.3f, elevation: 2.5f, doubleSided: true), roadSide: true),
            new ScreenTemplate("BillboardWallSmall01", "LedPoleBannerDouble", "LED Lamp-Post Banner (Double-Sided)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.OnPoles(columns: 1, rows: 2, poles: 1, poleDiameter: 0.2f, elevation: 3f, doubleSided: true), roadSide: true),
            new ScreenTemplate("BillboardWallSmall01", "LedCityLightDouble", "LED City Light Poster (Double-Sided)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.OnPoles(columns: 2, rows: 2, poles: 2, poleDiameter: 0.15f, elevation: 0.3f, doubleSided: true), roadSide: true),
            new ScreenTemplate("BillboardWallMedium01", "LedSidewalkSignDouble", "LED Sidewalk Sign (Double-Sided)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.OnPoles(columns: 1, rows: 1, poles: 2, poleDiameter: 0.2f, elevation: 1.2f, doubleSided: true), roadSide: true),
            new ScreenTemplate("BillboardWallMedium01", "LedStreetTickerDouble", "LED Street Ticker (Double-Sided)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.OnPoles(columns: 3, rows: 1, poles: 2, poleDiameter: 0.2f, elevation: 2.4f, doubleSided: true), roadSide: true),

            // High-pole screens
            new ScreenTemplate("BillboardWallMedium01", "LedScreenHighPoleMedium01", "LED High-Pole Screen (Medium)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.OnPoles(columns: 1, rows: 1, poles: 1, poleDiameter: 0.35f, elevation: 6f), roadSide: true),
            new ScreenTemplate("BillboardWallMedium01", "LedHighPoleMediumDouble", "LED High-Pole Screen (Medium, Double-Sided)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.OnPoles(columns: 1, rows: 1, poles: 1, poleDiameter: 0.35f, elevation: 6f, doubleSided: true), roadSide: true),
            new ScreenTemplate("BillboardWallLarge01", "LedScreenHighPoleLarge01", "LED High-Pole Screen (Large)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.OnPoles(columns: 1, rows: 1, poles: 2, poleDiameter: 0.4f, elevation: 7f)),
            new ScreenTemplate("BillboardWallLarge01", "LedHighPoleLargeDouble", "LED High-Pole Screen (Large, Double-Sided)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.OnPoles(columns: 1, rows: 1, poles: 2, poleDiameter: 0.4f, elevation: 7f, doubleSided: true), roadSide: true),
            new ScreenTemplate("BillboardWallHuge02", "LedScreenHighPoleHuge01", "LED High-Pole Screen (Huge)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.OnPoles(columns: 1, rows: 1, poles: 1, poleDiameter: 1.4f, elevation: 9f)),
            new ScreenTemplate("BillboardWallHuge02", "LedHighPoleHugeDouble", "LED High-Pole Screen (Huge, Double-Sided)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.OnPoles(columns: 1, rows: 1, poles: 1, poleDiameter: 1.4f, elevation: 9f, doubleSided: true)),
            new ScreenTemplate("BillboardWallLarge01", "LedHighPolePortrait1x3", "LED High-Pole Screen (Portrait)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.OnPoles(columns: 1, rows: 3, poles: 1, poleDiameter: 1.8f, elevation: 8f), legacyNames: new[] { "LedHighPolePortrait1x2" }),
            new ScreenTemplate("BillboardWallHuge02", "LedScreenHighPoleWide01", "LED High-Pole Screen (Extra Wide)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.OnPoles(columns: 2, rows: 1, poles: 2, poleDiameter: 1.6f, elevation: 10f)),
            new ScreenTemplate("BillboardWallHuge02", "LedHighPoleWideDouble", "LED High-Pole Screen (Extra Wide, Double-Sided)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.OnPoles(columns: 2, rows: 1, poles: 2, poleDiameter: 1.6f, elevation: 10f, doubleSided: true)),
            new ScreenTemplate("BillboardWallLarge01", "LedScreenHighPoleGiant01", "LED High-Pole Screen (Giant)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.OnPoles(columns: 3, rows: 3, poles: 2, poleDiameter: 2.2f, elevation: 10f)),
            new ScreenTemplate("BillboardWallLarge01", "LedHighPoleMega5x3", "LED High-Pole Screen (Mega)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.OnPoles(columns: 5, rows: 3, poles: 3, poleDiameter: 2.4f, elevation: 12f), legacyNames: new[] { "LedHighPoleMega3x2" }),
            new ScreenTemplate("BillboardWallHuge02", "LedScreenHighPoleRibbon01", "LED High-Pole Screen (Ribbon)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.OnPoles(columns: 3, rows: 1, poles: 3, poleDiameter: 1.6f, elevation: 10f)),

            // Round signs
            new ScreenTemplate("BillboardRoundSmall01", "LedScreenRoundSmall01", "LED Round Billboard (Small)", ScreenFaces.Both, 0.03f, 0.03f, 0.5f),
            new ScreenTemplate("BillboardRoundMedium01", "LedScreenRoundMedium01", "LED Round Billboard (Medium)", ScreenFaces.Both, 0.03f, 0.03f, 0.5f),
            new ScreenTemplate("BillboardRoundLarge01", "LedScreenRoundLarge01", "LED Round Billboard (Large)", ScreenFaces.Both, 0.03f, 0.03f, 0.5f),
            new ScreenTemplate("BillboardRoundSmall01", "LedRoundPoleSmall01", "LED Round Sign on Pole (Small)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.OnPoles(columns: 1, rows: 1, poles: 1, poleDiameter: 0.15f, elevation: 2.2f, doubleSided: true), roadSide: true),
            new ScreenTemplate("BillboardRoundMedium01", "LedRoundPoleMedium01", "LED Round Sign on Pole (Medium)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.OnPoles(columns: 1, rows: 1, poles: 1, poleDiameter: 0.2f, elevation: 2.6f, doubleSided: true), roadSide: true),
            new ScreenTemplate("BillboardRoundLarge01", "LedRoundPoleLarge01", "LED Round Sign on Pole (Large)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.OnPoles(columns: 1, rows: 1, poles: 1, poleDiameter: 0.25f, elevation: 3.2f, doubleSided: true), roadSide: true),

            // Bridge side screens: double-sided strips hanging in the air, to line up along the side
            // of a pedestrian bridge. The bottom sits 1.2 m below a deck at the default 10 m elevation step.
            new ScreenTemplate("BillboardWallMedium01", "LedBridgeSide4x1", "LED Bridge Side Screen (16 m)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.Floating(columns: 4, rows: 1, elevation: 8.8f, doubleSided: true)),
            new ScreenTemplate("BillboardWallMedium01", "LedBridgeSide6x1", "LED Bridge Side Screen (24 m)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.Floating(columns: 6, rows: 1, elevation: 8.8f, doubleSided: true)),
            new ScreenTemplate("BillboardWallMedium01", "LedBridgeSide8x1", "LED Bridge Side Screen (32 m)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.Floating(columns: 8, rows: 1, elevation: 8.8f, doubleSided: true)),

            // Screen cubes: four screens around an open-topped box
            new ScreenTemplate("BillboardWallLarge01", "LedCubeHangingLarge", "LED Screen Cube (Hanging, Large)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.Cube(columns: 1, rows: 1, elevation: 6.5f)),
            new ScreenTemplate("BillboardWallMedium01", "LedCubeHangingMedium", "LED Screen Cube (Hanging, Medium)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.Cube(columns: 1, rows: 2, elevation: 5.5f)),
            new ScreenTemplate("BillboardWallLarge01", "LedCubePoleLarge", "LED Screen Cube on Pole (Large)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.Cube(columns: 1, rows: 1, elevation: 9f, poles: 1, poleDiameter: 1.4f)),
            new ScreenTemplate("BillboardWallMedium01", "LedCubePoleMedium", "LED Screen Cube on Pole (Medium)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                Layout.Cube(columns: 1, rows: 2, elevation: 3f, poles: 1, poleDiameter: 0.35f), roadSide: true),

            // The small rounded standing signs (vanilla "Screen" props): double-sided, also along roads.
            // If their ad face can't be measured, the insets (fractions of the mesh bounds) leave out
            // the stem below and the clip on top.
            new ScreenTemplate("Screen01", "LedSignScreen01", "LED Standing Sign (Double-Sided)", ScreenFaces.Both, 0.06f, 0.1f, 0.25f,
                freestanding: true, roadSide: true),
            new ScreenTemplate("Screen02", "LedSignScreen02", "LED Standing Sign 2 (Double-Sided)", ScreenFaces.Both, 0.06f, 0.1f, 0.25f,
                freestanding: true, roadSide: true),


            // LED versions of more vanilla billboards, posters and shop signs. Each is only created
            // when its ad face can be measured (some vanilla props have no separate ad surface).
            new ScreenTemplate("SignRoundLarge01", "LedRoundShopSign01", "LED Round Shop Sign", ScreenFaces.Both, 0.03f, 0.03f, 0.03f,
                requireMeasured: true),
            new ScreenTemplate("BillboardWallSmall02", "LedWallSmall02", "LED Wall Screen (Small, Style 2)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                requireMeasured: true),
            new ScreenTemplate("BillboardWallSmall03", "LedWallSmall03", "LED Wall Screen (Small, Style 3)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                requireMeasured: true),
            new ScreenTemplate("BillboardWallSmall04", "LedWallSmall04", "LED Wall Screen (Small, Style 4)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                requireMeasured: true),
            new ScreenTemplate("BillboardWallMedium02", "LedWallMedium02", "LED Wall Screen (Medium, Style 2)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                requireMeasured: true),
            new ScreenTemplate("BillboardWallLarge02", "LedWallLarge02", "LED Wall Screen (Large, Style 2)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                requireMeasured: true),
            new ScreenTemplate("BillboardWallLarge03", "LedWallLarge03", "LED Wall Screen (Large, Style 3)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                requireMeasured: true),
            new ScreenTemplate("BillboardWallLarge04", "LedWallLarge04", "LED Wall Screen (Large, Style 4)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                requireMeasured: true),
            new ScreenTemplate("PosterSmall01", "LedPosterSmall01", "LED Poster (Small 1)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                requireMeasured: true),
            new ScreenTemplate("PosterSmall02", "LedPosterSmall02", "LED Poster (Small 2)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                requireMeasured: true),
            new ScreenTemplate("PosterMedium01", "LedPosterMedium01", "LED Poster (Medium 1)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                requireMeasured: true),
            new ScreenTemplate("PosterMedium02", "LedPosterMedium02", "LED Poster (Medium 2)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                requireMeasured: true),
            new ScreenTemplate("PosterLarge01", "LedPosterLarge01", "LED Poster (Large 1)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                requireMeasured: true),
            new ScreenTemplate("PosterLarge02", "LedPosterLarge02", "LED Poster (Large 2)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                requireMeasured: true),
            new ScreenTemplate("PosterHuge01", "LedPosterHuge01", "LED Poster (Huge 1)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                requireMeasured: true),
            new ScreenTemplate("PosterHuge02", "LedPosterHuge02", "LED Poster (Huge 2)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                requireMeasured: true),
            new ScreenTemplate("SignFrontwaySmall01", "LedShopSignSmall01", "LED Shop Sign (Small 1)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                requireMeasured: true),
            new ScreenTemplate("SignFrontwaySmall02", "LedShopSignSmall02", "LED Shop Sign (Small 2)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                requireMeasured: true),
            new ScreenTemplate("SignFrontwayMedium01", "LedShopSignMedium01", "LED Shop Sign (Medium 1)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                requireMeasured: true),
            new ScreenTemplate("SignFrontwayMedium02", "LedShopSignMedium02", "LED Shop Sign (Medium 2)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                requireMeasured: true),
            new ScreenTemplate("SignFrontwayLarge01", "LedShopSignLarge01", "LED Shop Sign (Large 1)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                requireMeasured: true),
            new ScreenTemplate("SignFrontwayLarge02", "LedShopSignLarge02", "LED Shop Sign (Large 2)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                requireMeasured: true),
            new ScreenTemplate("SignSidewaySmall01", "LedHangingSignSmall01", "LED Hanging Shop Sign (Small 1)", ScreenFaces.Both, 0.03f, 0.03f, 0.03f,
                requireMeasured: true, legacyNames: new[] { "LedSidewaySignSmall01" }),
            new ScreenTemplate("SignSidewaySmall02", "LedHangingSignSmall02", "LED Hanging Shop Sign (Small 2)", ScreenFaces.Both, 0.03f, 0.03f, 0.03f,
                requireMeasured: true, legacyNames: new[] { "LedSidewaySignSmall02" }),
            new ScreenTemplate("SignSidewayMedium01", "LedHangingSignMedium01", "LED Hanging Shop Sign (Medium 1)", ScreenFaces.Both, 0.03f, 0.03f, 0.03f,
                requireMeasured: true, legacyNames: new[] { "LedSidewaySignMedium01" }),
            new ScreenTemplate("SignSidewayMedium02", "LedHangingSignMedium02", "LED Hanging Shop Sign (Medium 2)", ScreenFaces.Both, 0.03f, 0.03f, 0.03f,
                requireMeasured: true, legacyNames: new[] { "LedSidewaySignMedium02" }),
            new ScreenTemplate("SignSidewayLarge01", "LedHangingSignLarge01", "LED Hanging Shop Sign (Large 1)", ScreenFaces.Both, 0.03f, 0.03f, 0.03f,
                requireMeasured: true, legacyNames: new[] { "LedSidewaySignLarge01" }),
            new ScreenTemplate("SignSidewayLarge02", "LedHangingSignLarge02", "LED Hanging Shop Sign (Large 2)", ScreenFaces.Both, 0.03f, 0.03f, 0.03f,
                requireMeasured: true, legacyNames: new[] { "LedSidewaySignLarge02" }),
            new ScreenTemplate("SignNeonSmall01", "LedNeonSignSmall01", "LED Neon-Style Sign (Small 1)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                requireMeasured: true),
            new ScreenTemplate("SignNeonSmall02", "LedNeonSignSmall02", "LED Neon-Style Sign (Small 2)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                requireMeasured: true),
            new ScreenTemplate("SignNeonMedium01", "LedNeonSignMedium01", "LED Neon-Style Sign (Medium 1)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                requireMeasured: true),
            new ScreenTemplate("SignNeonMedium02", "LedNeonSignMedium02", "LED Neon-Style Sign (Medium 2)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                requireMeasured: true),
            new ScreenTemplate("SignNeonLarge01", "LedNeonSignLarge01", "LED Neon-Style Sign (Large 1)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                requireMeasured: true),
            new ScreenTemplate("SignNeonLarge02", "LedNeonSignLarge02", "LED Neon-Style Sign (Large 2)", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
                requireMeasured: true),
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
                // Without an icon the toolbar shows a generic placeholder (clones have no baked thumbnail).
                string icon = IconUrl(template.Name);
                if (icon != null)
                    ui.m_Icon = icon;

                clone.AddOrGetComponent<PlaceableObject>();

                var screen = clone.AddComponent<LedScreenPrefab>();
                screen.m_Faces = template.Faces;
                screen.m_InsetSide = template.InsetSide;
                screen.m_InsetTop = template.InsetTop;
                screen.m_InsetBottom = template.InsetBottom;
                screen.m_Round = template.Round;
                screen.m_RoadSide = template.RoadSide;

                Mod.log.Info(AdPanelMeasurer.Describe(source));
                bool measured = AdPanelMeasurer.TryMeasure(source, out var panel);
                if (!measured && template.RequireMeasured)
                {
                    Mod.log.Info($"Skipping '{template.Name}': '{template.Source}' has no measurable ad face");
                    continue;
                }
                if (template.Freestanding && template.Layout == null)
                    clone.Remove(typeof(AttachedObject));
                // Free-standing billboards have the ad on the front only; put a screen on their plain back too.
                if (measured && template.Layout == null && template.Faces == ScreenFaces.Both && !template.Round &&
                    panel.m_Faces == ScreenFaces.Front && !AdPanelMeasurer.TryMeasureBack(source, ref panel))
                {
                    Mod.log.Warn($"{template.Name}: no flat back found, showing the front only");
                }
                if (template.Layout != null)
                {
                    if (template.Layout.HasPoles)
                        poles ??= CollectPoleCandidates(prefabsByName);
                    if (!measured || !(clone is ObjectGeometryPrefab cloneGeometry) || !TryArrange(cloneGeometry, template, poles, ref panel))
                    {
                        Mod.log.Warn($"Skipping '{template.Name}': couldn't build its layout");
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
                    screen.m_PanelBackDistance = panel.m_BackDistance;
                    // Grids are built from flat panels.
                    screen.m_PanelCurve = template.Layout == null ? panel.m_Curve : 0f;
                    screen.m_Faces = panel.m_Faces;
                    Mod.log.Info($"{template.Name}: ad panel center {panel.m_Center} size {panel.m_Size} normal {panel.m_Normal} halfDepth {panel.m_HalfDepth:0.000} back {panel.m_BackDistance:0.000} faces {panel.m_Faces}");
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

        private static PoleKind ChoosePoleKind(Layout mount)
        {
            switch (Mod.Settings?.PoleStyle ?? PoleStyle.Auto)
            {
                case PoleStyle.Steel: return PoleKind.Steel;
                case PoleStyle.Concrete: return PoleKind.Concrete;
                default: return mount.PoleDiameter >= kConcreteFromDiameter ? PoleKind.Concrete : PoleKind.Steel;
            }
        }

        /// <summary>
        /// Arranges the panel as a grid. Wall grids stay centred on the source panel; pole grids are
        /// raised to the layout's elevation, with poles of a fitting thickness right against their
        /// back (spread across the width), or between the two grids of a double-sided screen.
        /// The screen is widened to span the grid.
        /// </summary>
        private static bool TryArrange(ObjectGeometryPrefab clone, ScreenTemplate template, List<PoleCandidate> candidates, ref AdPanelMeasurer.Panel panel)
        {
            var layout = template.Layout;
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
            float gridWidth = tileWidth * layout.Columns;
            float gridHeight = tileHeight * layout.Rows;

            // Wall grids grow evenly around the source panel; free-standing ones start at the elevation.
            float lift = layout.Freestanding
                ? layout.Elevation - panelBounds.min.y
                : -(layout.Rows - 1) * tileHeight * 0.5f;

            PoleCandidate pole = null;
            int poleCount = 0;
            float poleTop = 0f;
            if (layout.HasPoles)
            {
                // Pole top hidden behind the grid, a bit above its middle.
                poleTop = layout.Elevation + gridHeight * 0.6f;
                PoleKind kind = ChoosePoleKind(layout);
                pole = PickPole(candidates, kind, layout.PoleDiameter, poleTop, tileWidth)
                    ?? PickPole(candidates, kind == PoleKind.Steel ? PoleKind.Concrete : PoleKind.Steel, layout.PoleDiameter, poleTop, tileWidth);
                if (pole == null)
                {
                    Mod.log.Warn($"No vanilla pole is tall enough ({poleTop:0.0} m) for '{template.Name}'");
                    return false;
                }

                // Slim steel poles can't carry a wide screen alone; add more across the width.
                // (A cube's pole stands hidden inside it.)
                poleCount = layout.Poles;
                if (pole.Kind == PoleKind.Steel && layout.Sides < 4)
                    poleCount = Math.Max(poleCount, (int)math.ceil(gridWidth / kSteelPoleSpacing));
            }

            // Back and front of the panel: the bounds corners furthest against and along the normal.
            float back = float.MaxValue, frontMost = float.MinValue;
            for (int c = 0; c < 4; c++)
            {
                var corner = new float3((c & 1) != 0 ? panelBounds.max.x : panelBounds.min.x, 0f, (c & 2) != 0 ? panelBounds.max.z : panelBounds.min.z);
                back = math.min(back, math.dot(corner, normal));
                frontMost = math.max(frontMost, math.dot(corner, normal));
            }

            // Trunk touching the back of the panel (slightly overlapping, so no gap shows). A
            // double-sided screen is the grid plus a copy turned around the trunk's middle. A cube
            // turns copies a quarter at a time around its centre, half the grid width behind the
            // front, so the outer faces meet at the corners.
            float trunkRadius = pole != null ? pole.Diameter * 0.5f : 0f;
            float behind = layout.Sides >= 4 ? frontMost - gridWidth * 0.5f : back - trunkRadius * 0.8f;
            float across = math.dot(panel.m_Center, right);
            float3 pivot = right * across + normal * behind;

            var meshes = new List<ObjectMeshInfo>();
            for (int side = 0; side < layout.Sides; side++)
            {
                quaternion turn = quaternion.RotateY(side * 2f * math.PI / layout.Sides);
                for (int row = 0; row < layout.Rows; row++)
                {
                    for (int column = 0; column < layout.Columns; column++)
                    {
                        float3 offset = right * ((column - (layout.Columns - 1) * 0.5f) * tileWidth) + up * (lift + row * tileHeight);
                        foreach (var info in clone.m_Meshes)
                        {
                            float3 position = info.m_Position + offset;
                            quaternion rotation = info.m_Rotation;
                            if (side > 0)
                            {
                                position = pivot + math.mul(turn, position - pivot);
                                rotation = math.mul(turn, rotation);
                            }
                            meshes.Add(new ObjectMeshInfo
                            {
                                m_Mesh = info.m_Mesh,
                                m_Position = position,
                                m_Rotation = rotation,
                                m_RequireState = info.m_RequireState,
                            });
                        }
                    }
                }
            }

            if (pole != null)
            {
                float sink = pole.Height - poleTop; // extra height goes into the ground
                for (int k = 0; k < poleCount; k++)
                {
                    float spread = poleCount == 1 ? 0f : (k / (poleCount - 1f) - 0.5f) * gridWidth * 0.7f;
                    float3 target = pivot + right * spread;
                    meshes.Add(new ObjectMeshInfo
                    {
                        m_Mesh = pole.Mesh.m_Mesh,
                        m_Position = pole.Mesh.m_Position + new float3(target.x - pole.TrunkCenter.x, -sink, target.z - pole.TrunkCenter.z),
                        m_Rotation = pole.Mesh.m_Rotation,
                        m_RequireState = pole.Mesh.m_RequireState,
                    });
                }
                Mod.log.Info($"{template.Name}: {layout.Columns}x{layout.Rows} panel(s) x{layout.Sides} at {layout.Elevation:0.0} m on {poleCount} x '{pole.Name}' ({pole.Kind}, trunk {pole.Diameter:0.00} m, wanted {layout.PoleDiameter:0.00}, sunk {sink:0.0} m)");
            }
            else
            {
                string where = layout.Freestanding ? $"at {layout.Elevation:0.0} m, no poles" : "on walls";
                Mod.log.Info($"{template.Name}: {layout.Columns}x{layout.Rows} panel(s) x{layout.Sides} {where}");
            }
            // Stand on the ground instead of snapping to walls.
            if (layout.Freestanding)
                clone.Remove(typeof(AttachedObject));
            clone.m_Meshes = meshes.ToArray();

            // One continuous screen over the whole grid (it also covers the frames between tiles).
            panel.m_Center += up * (lift + (layout.Rows - 1) * tileHeight * 0.5f);
            panel.m_Size += new float2((layout.Columns - 1) * tileWidth, (layout.Rows - 1) * tileHeight);
            if (layout.Sides > 1)
            {
                // Centre on the turning axis; the faces then all sit the half depth away from it.
                float front = math.dot(panel.m_Center, normal) + panel.m_HalfDepth;
                panel.m_Center += normal * (behind - math.dot(panel.m_Center, normal));
                panel.m_HalfDepth = front - behind;
                panel.m_Faces = layout.Sides >= 4 ? ScreenFaces.All : ScreenFaces.Both;
            }
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

        /// <summary>
        /// Icons ship next to the mod's UI module, which the game serves from "coui://ui-mods/".
        /// Returns null if there's no icon with this name.
        /// </summary>
        private static string IconUrl(string name)
        {
            if (string.IsNullOrEmpty(Mod.Directory) || !File.Exists(Path.Combine(Mod.Directory, kIconFolder, name + ".png")))
                return null;
            return $"coui://ui-mods/{kIconFolder}/{name}.png";
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
            ui.m_Icon = IconUrl(kCategoryName)
                ?? (sourceUi != null && !string.IsNullOrEmpty(sourceUi.m_Icon)
                    ? sourceUi.m_Icon
                    : $"{iconSource.thumbnailUrl}?width=128&height=128");
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
