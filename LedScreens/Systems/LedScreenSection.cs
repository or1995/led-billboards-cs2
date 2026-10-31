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
    /// Screens in a road's tree spots can't be selected (clicks go to the road, which has no
    /// panel), so the street panel (opened from the street name) gets the section instead: it
    /// shows the first of the street's LED screens and every change applies to all of them.
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
        private int m_MediaVersion;

        // The screens the panel edits: the selected screen, or the LED screens along the selected street.
        private readonly System.Collections.Generic.List<Entity> m_Targets = new System.Collections.Generic.List<Entity>();
        private readonly System.Collections.Generic.HashSet<Entity> m_Seen = new System.Collections.Generic.HashSet<Entity>();
        private bool m_RoadSelected;
        private int m_WrittenCount;
        private LedRoadSpacingSystem m_SpacingSystem;

        private Entity Primary => m_Targets.Count > 0 ? m_Targets[0] : Entity.Null;

        protected override string group => nameof(LedScreenSection);

        protected override void OnCreate()
        {
            base.OnCreate();
            m_InfoUISystem.AddMiddleSection(this);
            m_SpacingSystem = World.GetOrCreateSystemManaged<LedRoadSpacingSystem>();

            AddBinding(new TriggerBinding<string>(kGroup, "setMedia", SetMedia));
            AddBinding(new TriggerBinding<float>(kGroup, "setSpeed", value => Modify((ref LedScreen s) => s.m_Speed = Mathf.Clamp(value, kMinSpeed, kMaxSpeed))));
            AddBinding(new TriggerBinding<float>(kGroup, "setBrightness", value => Modify((ref LedScreen s) => s.m_Brightness = Mathf.Clamp(value, kMinBrightness, kMaxBrightness))));
            AddBinding(new TriggerBinding<int>(kGroup, "setEffect", value => Modify((ref LedScreen s) => s.m_Effect = (ScreenEffect)Mathf.Clamp(value, 0, 3))));
            AddBinding(new TriggerBinding<int>(kGroup, "setSpacing", SetSpacing));
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
            CollectTargets();
            visible = m_Targets.Count > 0;
        }

        private void CollectTargets()
        {
            m_Targets.Clear();
            m_RoadSelected = false;
            Entity selected = selectedEntity;
            if (selected == Entity.Null || !EntityManager.Exists(selected))
                return;
            if (EntityManager.HasComponent<LedScreen>(selected))
            {
                m_Targets.Add(selected);
                return;
            }
            // A street (selected through its name) is made of road segments; a segment can also be
            // selected directly in some cases. Screens can sit on segments and their end nodes.
            m_Seen.Clear();
            if (EntityManager.TryGetBuffer(selected, isReadOnly: true, out DynamicBuffer<Game.Net.AggregateElement> segments))
            {
                for (int i = 0; i < segments.Length; i++)
                    AddRoadScreens(segments[i].m_Edge);
            }
            else
            {
                AddRoadScreens(selected);
            }
            m_RoadSelected = m_Targets.Count > 0;
        }

        private void AddRoadScreens(Entity segment)
        {
            AddScreensOwnedBy(segment);
            if (EntityManager.TryGetComponent<Game.Net.Edge>(segment, out var edge))
            {
                AddScreensOwnedBy(edge.m_Start);
                AddScreensOwnedBy(edge.m_End);
            }
        }

        private void AddScreensOwnedBy(Entity owner)
        {
            if (!EntityManager.TryGetBuffer(owner, isReadOnly: true, out DynamicBuffer<Game.Objects.SubObject> subObjects))
                return;
            for (int i = 0; i < subObjects.Length; i++)
            {
                Entity entity = subObjects[i].m_SubObject;
                if (EntityManager.HasComponent<LedScreen>(entity) && !EntityManager.HasComponent<Game.Common.Deleted>(entity) && m_Seen.Add(entity))
                    m_Targets.Add(entity);
            }
        }

        protected override void OnPreUpdate()
        {
            base.OnPreUpdate();
            // Files added to or removed from the media folder: refresh the lists.
            if (visible && m_MediaVersion != MediaLibrary.Instance.Version)
            {
                m_MediaVersion = MediaLibrary.Instance.Version;
                m_InfoUISystem.RequestUpdate();
            }
            // Street screens added or removed (spacing changes take a frame): refresh the count.
            if (visible && m_RoadSelected && m_Targets.Count != m_WrittenCount)
                m_InfoUISystem.RequestUpdate();
            // While a playlist runs, refresh the panel so "Now playing" follows the screen.
            Entity primary = Primary;
            if (visible && m_Screen.m_Playlist != PlaylistMode.Off && primary != Entity.Null && EntityManager.Exists(primary) &&
                EntityManager.TryGetComponent<LedScreen>(primary, out var screen))
            {
                string playing = ScreenMediaResolver.Instance.Resolve(EntityManager, primary, screen, ScreenMediaResolver.Clock, out _, out _);
                if (playing != m_NowPlaying)
                    m_InfoUISystem.RequestUpdate();
            }
        }

        protected override void OnProcess()
        {
            Entity entity = Primary;
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

            m_WrittenCount = m_Targets.Count;
            writer.PropertyName("roadScreens");
            writer.Write(m_RoadSelected ? m_Targets.Count : 0);
            writer.PropertyName("spacing");
            writer.Write(Mathf.Max(1, m_Screen.m_Spacing));
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
            writer.PropertyName("effect");
            writer.Write((int)m_Screen.m_Effect);
            writer.PropertyName("defaultEffect");
            writer.Write((int)(Mod.Settings?.LedEffect ?? LedEffectMode.Off));
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
            foreach (Entity entity in m_Targets)
            {
                if (!EntityManager.TryGetComponent<LedScreen>(entity, out var screen))
                    continue;
                edit(ref screen);
                screen.m_Flags |= LedScreenFlags.Initialized;
                EntityManager.SetComponentData(entity, screen);
            }
            m_InfoUISystem.RequestUpdate();
        }

        private void SetSpacing(int value)
        {
            value = Mathf.Clamp(value, 1, LedRoadSpacingSystem.kMaxSpacing);
            foreach (Entity entity in m_Targets)
            {
                if (!EntityManager.TryGetComponent<LedScreen>(entity, out var screen) ||
                    !EntityManager.TryGetComponent<Game.Common.Owner>(entity, out var owner))
                {
                    continue;
                }
                // Closer together: have the game put the removed screens back, then thin them again.
                if (value < Mathf.Max(1, screen.m_Spacing))
                    m_SpacingSystem.Regenerate(owner.m_Owner);
                else
                    m_SpacingSystem.Thin(owner.m_Owner);
            }
            Modify((ref LedScreen s) => s.m_Spacing = value);
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
            if (string.IsNullOrEmpty(media) || m_Targets.Count == 0)
                return;

            // Tick or untick on all targets, following the first one.
            var key = new LedPlaylistItem(media).m_Media;
            bool remove = IndexOf(Primary, key) >= 0;
            foreach (Entity entity in m_Targets)
            {
                if (!EntityManager.HasComponent<LedScreen>(entity))
                    continue;
                int index = IndexOf(entity, key);
                if (remove && index >= 0)
                {
                    EntityManager.GetBuffer<LedPlaylistItem>(entity).RemoveAt(index);
                }
                else if (!remove && index < 0)
                {
                    // Screens get the buffer the first time something is ticked.
                    DynamicBuffer<LedPlaylistItem> items = EntityManager.HasBuffer<LedPlaylistItem>(entity)
                        ? EntityManager.GetBuffer<LedPlaylistItem>(entity)
                        : EntityManager.AddBuffer<LedPlaylistItem>(entity);
                    items.Add(new LedPlaylistItem(media));
                }
            }
            m_InfoUISystem.RequestUpdate();
        }

        private int IndexOf(Entity entity, FixedString128Bytes media)
        {
            if (!EntityManager.TryGetBuffer(entity, isReadOnly: true, out DynamicBuffer<LedPlaylistItem> items))
                return -1;
            for (int i = 0; i < items.Length; i++)
            {
                if (items[i].m_Media == media)
                    return i;
            }
            return -1;
        }

        private void ClearPlaylist()
        {
            foreach (Entity entity in m_Targets)
            {
                if (EntityManager.HasBuffer<LedPlaylistItem>(entity))
                    EntityManager.GetBuffer<LedPlaylistItem>(entity).Clear();
            }
            m_InfoUISystem.RequestUpdate();
        }

        private void UseForNewScreens()
        {
            if (Mod.Settings == null || !EntityManager.TryGetComponent<LedScreen>(Primary, out var screen))
                return;
            Mod.Settings.DefaultMedia = screen.m_Media.ToString();
            Mod.Settings.ApplyAndSave();
            m_InfoUISystem.RequestUpdate();
        }
    }
}
