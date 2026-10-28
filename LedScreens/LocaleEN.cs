using System.Collections.Generic;
using Colossal;
using LedScreens.Media;
using LedScreens.Systems;

namespace LedScreens
{
    public class LocaleEN : IDictionarySource
    {
        private readonly Setting m_Setting;

        public LocaleEN(Setting setting)
        {
            m_Setting = setting;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            var entries = new Dictionary<string, string>
            {
                { m_Setting.GetSettingsLocaleID(), "LED Screens" },
                { m_Setting.GetOptionTabLocaleID(Setting.kSection), "Main" },

                { m_Setting.GetOptionGroupLocaleID(Setting.kMediaGroup), "Media" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kDisplayGroup), "Display" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kQualityGroup), "Quality" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kDebugGroup), "Screen alignment (debug)" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.DefaultMedia)), "Media" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.DefaultMedia)), "What new screens play. Also used by 'Apply to selected screen'." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ApplyToSelected)), "Apply to selected screen" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ApplyToSelected)), "Click a screen in the city first, then open this menu, pick the media above and press this button." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.OpenMediaFolder)), "Open media folder" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.OpenMediaFolder)), "Opens the folder for your media: GIF, PNG, JPG, or video (MP4, WebM, MOV, M4V, AVI, WMV, OGV)." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ReloadMedia)), "Reload media" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ReloadMedia)), "Rescans the media folder and reloads everything (also applies quality changes)." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.Brightness)), "Brightness" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.Brightness)), "Global brightness multiplier for all screens. Screens keep the same brightness day and night." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.DrawDistance)), "Draw distance" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.DrawDistance)), "Screens farther than this from the camera are not drawn." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.LedEffect)), "LED pixel effect" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.LedEffect)), "Off: smooth image. Pixelated: sharp square pixels at the LED resolution. LED dots: every pixel is a round glowing dot with dark gaps, like a real LED screen up close; it blends into a smooth image from a distance. Media reloads automatically." },
                { m_Setting.GetEnumValueLocaleID(LedEffectMode.Off), "Off" },
                { m_Setting.GetEnumValueLocaleID(LedEffectMode.Pixelated), "Pixelated" },
                { m_Setting.GetEnumValueLocaleID(LedEffectMode.LedDots), "LED dots" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.LedResolution)), "LED resolution" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.LedResolution)), "How many LEDs across the width of the media. Lower = bigger, chunkier pixels." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.PoleStyle)), "Pole style" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.PoleStyle)), "Poles of the high-pole screens. Auto: slim steel poles for medium and large screens, a concrete column for huge and bigger ones. Steel: always steel (wide screens get more poles). Concrete: always a concrete column. Restart the game to apply." },
                { m_Setting.GetEnumValueLocaleID(PoleStyle.Auto), "Auto" },
                { m_Setting.GetEnumValueLocaleID(PoleStyle.Steel), "Steel" },
                { m_Setting.GetEnumValueLocaleID(PoleStyle.Concrete), "Concrete" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.MaxTextureSize)), "Max GIF resolution" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.MaxTextureSize)), "GIFs larger than this are scaled down. Press Reload media to apply." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.MaxFrames)), "Max frames per GIF" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.MaxFrames)), "Frames beyond this are dropped. Press Reload media to apply." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.CompressTextures)), "Compress textures" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.CompressTextures)), "Uses about 4x less video memory at a small quality cost. Press Reload media to apply." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.FacesOverride)), "Screen sides" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.FacesOverride)), "Overrides which side of the prop the screen is drawn on, for testing alignment." },
                { m_Setting.GetEnumValueLocaleID(ScreenFacesOverride.Auto), "Auto" },
                { m_Setting.GetEnumValueLocaleID(ScreenFacesOverride.Front), "Front" },
                { m_Setting.GetEnumValueLocaleID(ScreenFacesOverride.Back), "Back" },
                { m_Setting.GetEnumValueLocaleID(ScreenFacesOverride.Both), "Both" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.SurfaceOffset)), "Surface offset" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.SurfaceOffset)), "Moves the screen surface away from (+) or into (-) the prop, in meters." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ExtraInset)), "Extra border" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ExtraInset)), "Shrinks (+) or grows (-) the screen inside the prop's frame." },
            };

            foreach (var template in LedScreenPrefabSystem.Templates)
            {
                entries[$"Assets.NAME[{template.Name}]"] = template.DisplayName;
                entries[$"Assets.DESCRIPTION[{template.Name}]"] = "An LED screen that plays an animated GIF. Put GIFs in the media folder (see mod settings).";
            }
            entries[$"SubServices.NAME[{LedScreenPrefabSystem.kCategoryName}]"] = "LED Screens";
            entries[$"Assets.SUB_SERVICE_DESCRIPTION[{LedScreenPrefabSystem.kCategoryName}]"] = "Animated LED screens and ad boards.";

            return entries;
        }

        public void Unload()
        {
        }
    }
}
