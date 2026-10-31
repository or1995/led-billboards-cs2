using System.Collections.Generic;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using Game.UI;
using Game.UI.Localization;
using Game.UI.Widgets;
using LedScreens.Media;
using LedScreens.Systems;
using Unity.Entities;
using UnityEngine;

namespace LedScreens
{
    public enum ScreenFacesOverride
    {
        Auto,
        Front,
        Back,
        Both,
    }

    public enum PoleStyle
    {
        Auto,
        Steel,
        Concrete,
    }

    [FileLocation(nameof(LedScreens))]
    [SettingsUIGroupOrder(kMediaGroup, kDisplayGroup, kQualityGroup, kDebugGroup)]
    [SettingsUIShowGroupName(kMediaGroup, kDisplayGroup, kQualityGroup, kDebugGroup)]
    public class Setting : ModSetting
    {
        public const string kSection = "Main";

        public const string kMediaGroup = "Media";
        public const string kDisplayGroup = "Display";
        public const string kQualityGroup = "Quality";
        public const string kDebugGroup = "Debug";

        public Setting(IMod mod) : base(mod)
        {
            SetDefaults();
        }

        [SettingsUISection(kSection, kMediaGroup)]
        [SettingsUIDropdown(typeof(Setting), nameof(GetMediaItems))]
        [SettingsUIValueVersion(typeof(Setting), nameof(GetMediaItemsVersion))]
        public string DefaultMedia { get; set; }

        [SettingsUISection(kSection, kMediaGroup)]
        public bool RoadSideShuffle { get; set; }

        [SettingsUISection(kSection, kMediaGroup)]
        [SettingsUISlider(min = 1f, max = LedRoadSpacingSystem.kMaxSpacing, step = 1f, unit = Unit.kInteger)]
        public int RoadSideSpacing { get; set; }

        [SettingsUISection(kSection, kMediaGroup)]
        [SettingsUIButton]
        public bool ApplyToSelected
        {
            set => World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<LedScreenInitSystem>()?.ApplyMediaToSelected(DefaultMedia);
        }

        [SettingsUISection(kSection, kMediaGroup)]
        [SettingsUIButton]
        public bool OpenMediaFolder
        {
            set => Application.OpenURL("file:///" + MediaLibrary.Instance.MediaFolder.Replace('\\', '/'));
        }

        [SettingsUISection(kSection, kMediaGroup)]
        [SettingsUIButton]
        public bool ReloadMedia
        {
            set => MediaLibrary.Instance.Refresh();
        }

        [SettingsUISection(kSection, kDisplayGroup)]
        [SettingsUISlider(min = 0.1f, max = 10f, step = 0.1f, unit = Unit.kFloatSingleFraction)]
        public float Brightness { get; set; }

        [SettingsUISection(kSection, kDisplayGroup)]
        [SettingsUISlider(min = 100f, max = 6000f, step = 100f, unit = Unit.kLength)]
        public float DrawDistance { get; set; }

        [SettingsUISection(kSection, kDisplayGroup)]
        public LedEffectMode LedEffect { get; set; }

        [SettingsUISection(kSection, kDisplayGroup)]
        [SettingsUISlider(min = 32f, max = 320f, step = 16f, unit = Unit.kInteger)]
        public int LedResolution { get; set; }

        [SettingsUISection(kSection, kDisplayGroup)]
        public PoleStyle PoleStyle { get; set; }

        [SettingsUISection(kSection, kQualityGroup)]
        [SettingsUISlider(min = 64f, max = 1024f, step = 64f, unit = Unit.kInteger)]
        public int MaxTextureSize { get; set; }

        [SettingsUISection(kSection, kQualityGroup)]
        [SettingsUISlider(min = 10f, max = 600f, step = 10f, unit = Unit.kInteger)]
        public int MaxFrames { get; set; }

        [SettingsUISection(kSection, kQualityGroup)]
        public bool CompressTextures { get; set; }

        [SettingsUISection(kSection, kQualityGroup)]
        public bool CacheConvertedMedia { get; set; }

        [SettingsUISection(kSection, kQualityGroup)]
        [SettingsUISlider(min = 1f, max = 32f, step = 1f, unit = Unit.kInteger)]
        [SettingsUIHideByCondition(typeof(Setting), nameof(IsCacheOff))]
        public int CacheSizeLimitGB { get; set; }

        [SettingsUISection(kSection, kQualityGroup)]
        [SettingsUIButton]
        [SettingsUIConfirmation]
        public bool ClearCache
        {
            set => System.Threading.Tasks.Task.Run(MediaCache.Clear);
        }

        [SettingsUISection(kSection, kDebugGroup)]
        public ScreenFacesOverride FacesOverride { get; set; }

        [SettingsUISection(kSection, kDebugGroup)]
        [SettingsUISlider(min = -0.5f, max = 0.5f, step = 0.01f, unit = Unit.kFloatTwoFractions)]
        public float SurfaceOffset { get; set; }

        [SettingsUISection(kSection, kDebugGroup)]
        [SettingsUISlider(min = -0.3f, max = 0.3f, step = 0.01f, unit = Unit.kFloatTwoFractions)]
        public float ExtraInset { get; set; }

        public override void SetDefaults()
        {
            DefaultMedia = MediaLibrary.kTestPattern;
            RoadSideShuffle = true;
            RoadSideSpacing = 1;
            Brightness = 1f;
            DrawDistance = 2500f;
            PoleStyle = PoleStyle.Auto;
            LedEffect = LedEffectMode.Off;
            LedResolution = 128;
            MaxTextureSize = 512;
            MaxFrames = 300;
            CompressTextures = true;
            CacheConvertedMedia = false;
            CacheSizeLimitGB = 4;
            FacesOverride = ScreenFacesOverride.Auto;
            SurfaceOffset = 0f;
            ExtraInset = 0f;
        }

        public bool IsCacheOff() => !CacheConvertedMedia;

        /// <summary>Changes when the media list does, so the dropdown above picks up new files.</summary>
        public int GetMediaItemsVersion() => MediaLibrary.Instance.Version;

        public DropdownItem<string>[] GetMediaItems()
        {
            var items = new List<DropdownItem<string>>
            {
                new DropdownItem<string> { value = MediaLibrary.kTestPattern, displayName = LocalizedString.Value("Test pattern (built-in)") },
            };
            foreach (var name in MediaLibrary.Instance.Available)
                items.Add(new DropdownItem<string> { value = name, displayName = LocalizedString.Value(name) });
            return items.ToArray();
        }
    }
}
