using Colossal.Entities;
using Colossal.Serialization.Entities;
using Game;
using Game.Common;
using Game.Objects;
using Game.Tools;
using Game.UI.InGame;
using LedScreens.Components;
using Unity.Collections;
using Unity.Entities;

namespace LedScreens.Systems
{
    /// <summary>
    /// Gives freshly created screens their settings: a placement preview of an existing screen
    /// (move / relocate) copies the original, a new screen gets the "media for new screens" setting.
    /// </summary>
    public partial class LedScreenInitSystem : GameSystemBase
    {
        // Road-side screens come in numbers; switching less often keeps media loading down.
        private const float kRoadSideInterval = 30f;

        private EntityQuery m_CreatedQuery;
        private EntityQuery m_PlaceholderQuery;
        private SelectedInfoUISystem m_SelectedInfoUISystem;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_SelectedInfoUISystem = World.GetOrCreateSystemManaged<SelectedInfoUISystem>();
            m_CreatedQuery = GetEntityQuery(
                ComponentType.ReadWrite<LedScreen>(),
                ComponentType.ReadOnly<Created>(),
                ComponentType.Exclude<Deleted>());
            m_PlaceholderQuery = GetEntityQuery(
                ComponentType.ReadOnly<LedScreen>(),
                ComponentType.ReadOnly<Placeholder>());
            RequireForUpdate(m_CreatedQuery);
        }

        protected override void OnGameLoadingComplete(Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            // Screens placed with mod version 0.1 inherited the vanilla billboard's Placeholder
            // tag, which makes them unselectable. Fix them up in saves.
            if (!m_PlaceholderQuery.IsEmptyIgnoreFilter)
            {
                int count = m_PlaceholderQuery.CalculateEntityCount();
                EntityManager.RemoveComponent<Placeholder>(m_PlaceholderQuery);
                Mod.log.Info($"Made {count} screen(s) selectable");
            }
        }

        protected override void OnUpdate()
        {
            using (var entities = m_CreatedQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (var entity in entities)
                {
                    var screen = EntityManager.GetComponentData<LedScreen>(entity);
                    if ((screen.m_Flags & LedScreenFlags.Initialized) != 0)
                        continue;

                    if (EntityManager.TryGetComponent<Temp>(entity, out var temp) &&
                        temp.m_Original != Entity.Null &&
                        EntityManager.TryGetComponent<LedScreen>(temp.m_Original, out var original) &&
                        (original.m_Flags & LedScreenFlags.Initialized) != 0)
                    {
                        screen = original;
                        CopyPlaylist(temp.m_Original, entity);
                    }
                    else
                    {
                        screen = LedScreen.Create(Mod.Settings?.DefaultMedia);
                        // Screens in a road's tree spots can't easily be picked one by one, so
                        // give each its own shuffle so a street doesn't show the same ad everywhere.
                        if (EntityManager.HasComponent<Secondary>(entity) && (Mod.Settings?.RoadSideShuffle ?? true))
                        {
                            screen.m_Playlist = PlaylistMode.Shuffle;
                            screen.m_Interval = kRoadSideInterval;
                            screen.m_TimeOffset = UnityEngine.Random.Range(0f, 600f);
                        }
                    }

                    EntityManager.SetComponentData(entity, screen);
                }
            }
        }

        /// <summary>Gives a move preview the same playlist as the screen being moved.</summary>
        private void CopyPlaylist(Entity from, Entity to)
        {
            if (!EntityManager.TryGetBuffer(from, isReadOnly: true, out DynamicBuffer<LedPlaylistItem> source) || source.Length == 0)
                return;

            // Read the items first: adding a buffer to 'to' is a structural change that invalidates 'source'.
            var items = source.ToNativeArray(Allocator.Temp);
            var target = EntityManager.HasBuffer<LedPlaylistItem>(to) ? EntityManager.GetBuffer<LedPlaylistItem>(to) : EntityManager.AddBuffer<LedPlaylistItem>(to);
            target.CopyFrom(items);
            items.Dispose();
        }

        /// <summary>Sets the media of the currently selected screen. Returns false if no screen is selected.</summary>
        public bool ApplyMediaToSelected(string media)
        {
            Entity selected = m_SelectedInfoUISystem.selectedEntity;
            if (selected == Entity.Null || !EntityManager.TryGetComponent<LedScreen>(selected, out var screen))
            {
                Mod.log.Info("Apply to selected screen: no LED screen is selected");
                return false;
            }

            screen.m_Media = default;
            screen.m_Media.CopyFromTruncated(media ?? string.Empty);
            screen.m_Flags |= LedScreenFlags.Initialized;
            EntityManager.SetComponentData(selected, screen);
            Mod.log.Info($"Selected screen now shows '{media}'");
            return true;
        }
    }
}
