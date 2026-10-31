# Development

## Requirements

- Cities: Skylines II with the **Modding Toolchain** installed (*Options → Modding*). It sets the `CSII_*` user environment variables the project relies on.
- .NET SDK 8 or later. The mod targets .NET Framework 4.8; the SDK fetches the reference assemblies itself.
- No Node.js is needed: the UI is a plain JavaScript module.

## Build

```powershell
dotnet build LedScreens.sln -c Release
```

The build deploys `LedScreens.dll` and `LedScreens.mjs` to
`%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Mods\LedScreens`. Restart the game to load a new build.

If the Unity part of the toolchain is missing (no `UnityModsProject` folder), the project still builds, but it prints a warning and skips the mod post processor. The mod doesn't use Burst or `SystemAPI` code generation, so it works either way.

## Architecture

### Why screens are drawn by the mod

CS2 draws props with a batch renderer that shares one material per prefab, so it can't give every instance its own texture. Instead:

- **The prop** is a clone of a vanilla billboard. It handles placement, selection, collision, saving and moving.
- **The screen surface** is a quad that `LedScreenRenderSystem` draws every frame with `Graphics.RenderMesh`, using the screen's current frame as `_UnlitColorMap` of an `HDRP/Unlit` material. Each screen can therefore show a different texture.

Only shader variants the game itself uses ship in the build. The emissive-map and GPU-instancing variants of `HDRP/Unlit` are stripped and render flat white, so the mod uses neither.

### Startup flow

1. **`Mod.OnLoad`:** registers the settings, English texts and systems, and scans the media folder.
2. **`LedScreenPrefabSystem.OnGamePreload`:** runs once, after the vanilla prefabs have loaded (mods load before them). It:
   - clones the vanilla billboards and strips components that would break them (placeholder, spawnable, brand, theme, light effects);
   - measures each ad panel (`AdPanelMeasurer` reads the `*_Ads` submesh) so the screen fits exactly;
   - for templates with a `Layout`, builds a grid of panels: wall grids stay wall-mounted; pole layouts add a vanilla pole mesh (`FlagPole*` or `IndustrialChimney*`), picking the pole by its measured trunk diameter and sinking extra height into the ground. Double-sided layouts add a copy of the grid turned 180° around the pole;
   - sets each prop's toolbar icon to `coui://ui-mods/ledscreens-icons/<PrefabName>.png`. The icons are made by `tools/make_icons.py` (Pillow) into `LedScreens/UI/ledscreens-icons/`, and the build copies them next to the mod. Add a line there when you add a template;
   - adds the LED Screens category to the Landscaping menu.

   Saves store placed screens by prefab name. Never rename or remove a template, and don't change the source or layout of one that has shipped: add a new template instead.
3. **`LedScreenPrefab`** (a `ComponentBase`) adds `LedScreenData` to the prefab entity and `LedScreen` to every placed instance.
4. **`LedScreenInitSystem`:** gives new screens their default media and copies settings to move previews. It also removes the `Placeholder` tag from screens placed with old versions.

### Per frame

- **`LedScreenRenderSystem`:**
  1. Queries all screens and skips those beyond draw distance, off-camera or facing away.
  2. Resolves the media through `ScreenMediaResolver`, which handles playlists via `Playlist`.
  3. Groups screens by texture and draws them.
- **`MediaLibrary`:**
  - decodes GIFs on worker threads (`GifDecoder`, a custom decoder) and uploads frames within a 4 ms per-frame budget;
  - loads PNG/JPG with `ImageConversion`;
  - streams videos with `VideoPlayer` into render textures;
  - bakes the LED effect into the textures while loading, so media is cached per file *and* effect (screens can override the global effect);
  - only starts loads for screens that are on screen; out-of-view screens just keep already-loaded media alive;
  - reuses the big per-frame arrays through `BufferPool` (the game's garbage collector stops the world);
  - can cache converted GIFs on disk (`MediaCache`, optional);
  - unloads media nobody has looked at for 60 seconds.

### Info panel

- **`LedScreenSection`** (`InfoSectionBase`) writes the selected screen's state as JSON and exposes triggers (`setMedia`, `setPlaylistMode`, …) under the `LedScreens` binding group.
- **`UI/LedScreens.mjs`** registers a React component for `LedScreens.Systems.LedScreenSection` in `selectedInfoSectionComponents`.
  - It uses the game's globals: `window.React`, `cs2/api`, `cs2/ui`, `cs2/modding`.
  - **Header rule:** the header comment must stay as is, *including* the empty `Dependencies:` line. Without it the game throws a NullReferenceException at startup.

### Saved data

| Data | Type | Contents |
|---|---|---|
| `LedScreen` | `IComponentData`, `ISerializable`, versioned | media, speed, brightness, offset, playlist mode, interval, shuffle seed, LED effect (v3) |
| `LedPlaylistItem` | buffer | the ticked playlist files |

Renamed prefabs keep old saves working through `ObsoleteIdentifiers` (see `LegacyNames` in the templates).

## Debugging

- **Mod log:** `Logs\LedScreens.Mod.log` contains:
  - every created prefab with its measured ad panel;
  - pole candidates and the chosen pole;
  - media loads and failures;
  - the first screen draw.
- **UI log:** `Logs\UI.log` shows UI/JavaScript errors.
- **Game internals:** decompile `Cities2_Data\Managed\Game.dll` to look up game APIs. `ilspycmd` 9.x works with the .NET 8 SDK.

## Adding a screen type

Add a `ScreenTemplate` to `LedScreenPrefabSystem.Templates`:

```csharp
new ScreenTemplate("BillboardWallLarge01", "LedScreenMyScreen01", "LED My Screen", ScreenFaces.Front, 0.03f, 0.03f, 0.03f,
    new PoleMount(columns: 2, rows: 1, poles: 2, poleDiameter: 1.2f, elevation: 8f)),
```

- The source must be a vanilla prefab with an `*_Ads` material; the screen is fitted to it automatically.
- The display name is used for the in-game label.
- **Never rename a prefab that people have placed.** If you must, add the old name to `legacyNames`.
