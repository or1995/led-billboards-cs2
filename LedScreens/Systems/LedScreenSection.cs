using Colossal.Entities;
using Colossal.UI.Binding;
using Game.UI.InGame;
using LedScreens.Components;
using LedScreens.Media;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace LedScreens.Systems
{
    /// <summary>
    /// "LED Screen" section in the selected object panel. The UI module (LedScreens.mjs) renders
    /// it, keyed by this class's full name, and calls the triggers below to change the screen.
    /// </summary>
    public partial class LedScreenSection : InfoSectionBase
    {
        public const string kGroup = "LedScreens";

        private const float kMinSpeed = 0.1f;
        private const float kMaxSpeed = 4f;
        private const float kMinBrightness = 0f;
        private const float kMaxBrightness = 3f;

        private LedScreen m_Screen;
        private readonly System.Collections.Generic.List<string> m_PlaylistItems = new System.Collections.Generic.List<string>();
        private string m_NowPlaying;

        protected override string group => nameof(LedScreenSection);

        protected override void OnCreate()
        {
            base.OnCreate();
            m_InfoUISystem.AddMiddleSection(this);

            AddBinding(new TriggerBinding<string>(kGroup, "setMedia", SetMedia));
            AddBinding(new TriggerBinding<float>(kGroup, "setSpeed", value => Modify((ref LedScreen s) => s.m_Speed = Mathf.Clamp(value, kMinSpeed, kMaxSpeed))));
            AddBinding(new TriggerBinding<float>(kGroup, "setBrightness", value => Modify((ref LedScreen s) => s.m_Brightness = Mathf.Clamp(value, kMinBrightness, kMaxBrightness))));
            AddBinding(new TriggerBinding(kGroup, "syncStart", () => Modify((ref LedScreen s) => s.m_TimeOffset = 0f)));
            AddBinding(new TriggerBinding(kGroup, "randomStart", () => Modify((ref LedScreen s) => s.m_TimeOffset = UnityEngine.Random.Range(0f, 600f))));
            AddBinding(new TriggerBinding(kGroup, "useForNewScreens", UseForNewScreens));
            AddBinding(new TriggerBinding(kGroup, "reloadMedia", () =>
            {
                MediaLibrary.Instance.Refresh();
                m_InfoUISystem.RequestUpdate();
            }));
            AddBinding(new TriggerBinding(kGroup, "openMediaFolder", () => Application.OpenURL("file:///" + MediaLibrary.Instance.MediaFolder.Replace('\\', '/'))));

            AddBinding(new TriggerBinding<int>(kGroup, "setPlaylistMode", value => Modify((ref LedScreen s) =>
            {
                s.m_Playlist = (PlaylistMode)Mathf.Clamp(value, 0, 2);
                if (s.m_Interval <= 0f)
                    s.m_Interval = LedScreen.kDefaultInterval;
                if (s.m_Seed == 0)
                    s.m_Seed = (uint)UnityEngine.Random.Range(1, int.MaxValue);
            })));
            AddBinding(new TriggerBinding<float>(kGroup, "setInterval", value => Modify((ref LedScreen s) => s.m_Interval = Mathf.Clamp(value, Playlist.kMinInterval, Playlist.kMaxInterval))));
            AddBinding(new TriggerBinding<string>(kGroup, "togglePlaylistItem", TogglePlaylistItem));
            AddBinding(new TriggerBinding(kGroup, "playAllMedia", ClearPlaylist));
            AddBinding(new TriggerBinding(kGroup, "reshuffle", () => Modify((ref LedScreen s) => s.m_Seed = (uint)UnityEngine.Random.Range(1, int.MaxValue))));
        }

        protected override void Reset()
        {
            m_Screen = default;
            m_PlaylistItems.Clear();
            m_NowPlaying = null;
        }

        protected override void OnUpdate()
        {
            visible = EntityManager.HasComponent<LedScreen>(selectedEntity);
        }

        protected override void OnPreUpdate()
        {
            base.OnPreUpdate();
            // While a playlist runs, refresh the panel so "Now playing" follows the screen.
            if (visible && m_Screen.m_Playlist != PlaylistMode.Off && EntityManager.Exists(selectedEntity) &&
                EntityManager.TryGetComponent<LedScreen>(selectedEntity, out var screen))
            {
                string playing = ScreenMediaResolver.Instance.Resolve(EntityManager, selectedEntity, screen, ScreenMediaResolver.Clock, out _, out _);
                if (playing != m_NowPlaying)
                    m_InfoUISystem.RequestUpdate();
            }
        }

        protected override void OnProcess()
        {
            Entity entity = selectedEntity;
            EntityManager.TryGetComponent(entity, out m_Screen);
            if (EntityManager.TryGetBuffer(entity, isReadOnly: true, out DynamicBuffer<LedPlaylistItem> items))
            {
                foreach (var item in items)
                    m_PlaylistItems.Add(item.m_Media.ToString());
            }
            m_NowPlaying = ScreenMediaResolver.Instance.Resolve(EntityManager, entity, m_Screen, ScreenMediaResolver.Clock, out _, out _);
        }

        public override void OnWriteProperties(IJsonWriter writer)
        {
            string media = m_Screen.m_Media.ToString();
            var available = MediaLibrary.Instance.Available;

            writer.PropertyName("media");
            writer.Write(media);
            writer.PropertyName("nowPlaying");
            writer.Write(m_NowPlaying ?? media);
            writer.PropertyName("isVideo");
            writer.Write(MediaLibrary.IsVideo(m_NowPlaying ?? media));
            writer.PropertyName("speed");
            writer.Write(m_Screen.m_Speed);
            writer.PropertyName("brightness");
            writer.Write(m_Screen.m_Brightness);
            writer.PropertyName("synced");
            writer.Write(m_Screen.m_TimeOffset == 0f);
            writer.PropertyName("isDefault");
            writer.Write(Mod.Settings != null && Mod.Settings.DefaultMedia == media);

            writer.PropertyName("playlistMode");
            writer.Write((int)m_Screen.m_Playlist);
            writer.PropertyName("interval");
            writer.Write(m_Screen.m_Interval > 0f ? m_Screen.m_Interval : LedScreen.kDefaultInterval);
            writer.PropertyName("playlist");
            writer.ArrayBegin(m_PlaylistItems.Count);
            foreach (var item in m_PlaylistItems)
                writer.Write(item);
            writer.ArrayEnd();

            writer.PropertyName("available");
            writer.ArrayBegin(available.Count + 1);
            writer.Write(MediaLibrary.kTestPattern);
            foreach (var name in available)
                writer.Write(name);
            writer.ArrayEnd();
        }

        private delegate void ScreenEdit(ref LedScreen screen);

        private void Modify(ScreenEdit edit)
        {
            Entity entity = selectedEntity;
            if (entity == Entity.Null || !EntityManager.TryGetComponent<LedScreen>(entity, out var screen))
                return;

            edit(ref screen);
            screen.m_Flags |= LedScreenFlags.Initialized;
            EntityManager.SetComponentData(entity, screen);
            m_InfoUISystem.RequestUpdate();
        }

        private void SetMedia(string media)
        {
            Modify((ref LedScreen s) =>
            {
                s.m_Media = default;
                s.m_Media.CopyFromTruncated(media ?? string.Empty);
            });
        }

        private void TogglePlaylistItem(string media)
        {
            Entity entity = selectedEntity;
            if (entity == Entity.Null || string.IsNullOrEmpty(media) || !EntityManager.HasComponent<LedScreen>(entity))
                return;

            // Screens get the buffer the first time something is ticked.
            DynamicBuffer<LedPlaylistItem> items = EntityManager.HasBuffer<LedPlaylistItem>(entity)
                ? EntityManager.GetBuffer<LedPlaylistItem>(entity)
                : EntityManager.AddBuffer<LedPlaylistItem>(entity);

            var key = new LedPlaylistItem(media).m_Media;
            for (int i = 0; i < items.Length; i++)
            {
                if (items[i].m_Media == key)
                {
                    items.RemoveAt(i);
                    m_InfoUISystem.RequestUpdate();
                    return;
                }
            }
            items.Add(new LedPlaylistItem(media));
            m_InfoUISystem.RequestUpdate();
        }

        private void ClearPlaylist()
        {
            Entity entity = selectedEntity;
            if (entity != Entity.Null && EntityManager.HasBuffer<LedPlaylistItem>(entity))
                EntityManager.GetBuffer<LedPlaylistItem>(entity).Clear();
            m_InfoUISystem.RequestUpdate();
        }

        private void UseForNewScreens()
        {
            if (Mod.Settings == null || !EntityManager.TryGetComponent<LedScreen>(selectedEntity, out var screen))
                return;
            Mod.Settings.DefaultMedia = screen.m_Media.ToString();
            Mod.Settings.ApplyAndSave();
            m_InfoUISystem.RequestUpdate();
        }
    }
}
