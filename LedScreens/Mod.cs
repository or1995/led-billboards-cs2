using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;
using LedScreens.Media;
using LedScreens.Systems;

namespace LedScreens
{
    public class Mod : IMod
    {
        public static ILog log = LogManager.GetLogger($"{nameof(LedScreens)}.{nameof(Mod)}").SetShowsErrorsInUI(false);

        public static Setting Settings { get; private set; }

        /// <summary>Folder the mod was loaded from (null if unknown).</summary>
        public static string Directory { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info(nameof(OnLoad));

            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
            {
                log.Info($"Current mod asset at {asset.path}");
                Directory = System.IO.Path.GetDirectoryName(asset.path);
            }

            MediaLibrary.Instance.Initialize();

            Settings = new Setting(this);
            Settings.RegisterInOptionsUI();
            GameManager.instance.localizationManager.AddSource("en-US", new LocaleEN(Settings));
            AssetDatabase.global.LoadSettings(nameof(LedScreens), Settings, new Setting(this));
            // A testing override left on hides one side of every double-sided screen; don't keep it across restarts.
            if (Settings.FacesOverride != ScreenFacesOverride.Auto)
            {
                log.Info($"Resetting the 'Screen sides' debug option ({Settings.FacesOverride}) to Auto");
                Settings.FacesOverride = ScreenFacesOverride.Auto;
            }

            // Vanilla prefabs are loaded after mods, so the screen prefabs are created on game preload.
            updateSystem.UpdateAt<LedScreenPrefabSystem>(SystemUpdatePhase.PrefabUpdate);
            updateSystem.UpdateAt<LedRoadSpacingSystem>(SystemUpdatePhase.Modification1);
            updateSystem.UpdateAt<LedScreenInitSystem>(SystemUpdatePhase.Modification5);
            updateSystem.UpdateAt<LedScreenRenderSystem>(SystemUpdatePhase.Rendering);
            updateSystem.UpdateAt<LedScreenSection>(SystemUpdatePhase.UIUpdate);
        }

        public void OnDispose()
        {
            log.Info(nameof(OnDispose));
            if (Settings != null)
            {
                Settings.UnregisterInOptionsUI();
                Settings = null;
            }
            MediaLibrary.Instance.Dispose();
        }
    }
}
