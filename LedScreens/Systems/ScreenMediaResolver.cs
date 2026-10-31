using System.Collections.Generic;
using Colossal.Entities;
using LedScreens.Components;
using LedScreens.Media;
using Unity.Collections;
using Unity.Entities;

namespace LedScreens.Systems
{
    /// <summary>
    /// Answers "what is this screen showing right now?", taking its playlist into account.
    /// Shared by the renderer and the info panel so both agree. Main thread only.
    /// </summary>
    internal sealed class ScreenMediaResolver
    {
        public static readonly ScreenMediaResolver Instance = new ScreenMediaResolver();

        /// <summary>The clock all screens animate and switch by.</summary>
        public static double Clock => UnityEngine.Time.realtimeSinceStartupAsDouble;

        // FixedString -> string, so the renderer doesn't allocate a string per screen per frame.
        private readonly Dictionary<FixedString128Bytes, string> m_Names = new Dictionary<FixedString128Bytes, string>();

        public string Name(in FixedString128Bytes media)
        {
            if (!m_Names.TryGetValue(media, out var name))
            {
                name = media.ToString();
                m_Names[media] = name;
            }
            return name;
        }

        /// <summary>The LED effect this screen uses: its own choice, or the global setting.</summary>
        public static LedEffectMode Effect(in LedScreen screen)
        {
            switch (screen.m_Effect)
            {
                case ScreenEffect.Off: return LedEffectMode.Off;
                case ScreenEffect.Pixelated: return LedEffectMode.Pixelated;
                case ScreenEffect.LedDots: return LedEffectMode.LedDots;
                default: return Mod.Settings?.LedEffect ?? LedEffectMode.Off;
            }
        }

        /// <summary>
        /// Returns the media showing at <paramref name="time"/>. When a playlist is on, also gives
        /// the next item and how long until it starts (so it can be loaded in advance).
        /// </summary>
        public string Resolve(EntityManager entityManager, Entity entity, in LedScreen screen, double time, out string next, out float timeToNext)
        {
            next = null;
            timeToNext = float.MaxValue;
            if (screen.m_Playlist == PlaylistMode.Off)
                return Name(screen.m_Media);

            bool hasItems = entityManager.TryGetBuffer(entity, isReadOnly: true, out DynamicBuffer<LedPlaylistItem> items) && items.Length > 0;
            IReadOnlyList<string> all = MediaLibrary.Instance.Available;
            int count = hasItems ? items.Length : all.Count;
            if (count == 0)
                return Name(screen.m_Media);

            var position = Playlist.Locate(screen, count, time);
            next = hasItems ? Name(items[position.m_Next].m_Media) : all[position.m_Next];
            timeToNext = position.m_TimeToNext;
            return hasItems ? Name(items[position.m_Current].m_Media) : all[position.m_Current];
        }
    }
}
