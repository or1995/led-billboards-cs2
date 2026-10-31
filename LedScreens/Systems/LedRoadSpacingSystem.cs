using System.Collections.Generic;
using Colossal.Entities;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Objects;
using Game.Tools;
using LedScreens.Components;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace LedScreens.Systems
{
    /// <summary>
    /// Spacing of road-side screens. The game puts an object in every one of a road's tree spots,
    /// and the spots' spacing comes from the road itself. To space screens further apart, this
    /// keeps one in every LedScreen.m_Spacing along each side of a road segment and deletes the rest.
    /// The game brings deleted ones back whenever it rebuilds the segment's objects; they come
    /// back as new screens, the init system sends the segment here again, and they go again.
    /// Runs early in the modification phase, so deletions go through the game's usual cleanup.
    /// </summary>
    public partial class LedRoadSpacingSystem : GameSystemBase
    {
        public const int kMaxSpacing = 6;

        private readonly HashSet<Entity> m_Thin = new HashSet<Entity>();
        private readonly HashSet<Entity> m_Regenerate = new HashSet<Entity>();
        private readonly List<Entity> m_Retry = new List<Entity>();
        private readonly List<(Entity entity, bool left, float t)> m_Screens = new List<(Entity, bool, float)>();

        /// <summary>Removes surplus screens on this road segment next frame.</summary>
        public void Thin(Entity road) => m_Thin.Add(road);

        /// <summary>Has the game rebuild the segment's objects, bringing removed screens back (for a smaller spacing).</summary>
        public void Regenerate(Entity road) => m_Regenerate.Add(road);

        protected override void OnUpdate()
        {
            if (m_Regenerate.Count == 0 && m_Thin.Count == 0)
                return;

            foreach (Entity road in m_Regenerate)
            {
                if (EntityManager.Exists(road) && EntityManager.HasComponent<Game.Net.Edge>(road) &&
                    !EntityManager.HasComponent<Deleted>(road) && !EntityManager.HasComponent<Updated>(road))
                {
                    EntityManager.AddComponent<Updated>(road);
                }
            }

            m_Retry.Clear();
            foreach (Entity road in m_Thin)
            {
                // A segment being rebuilt this frame would take back the screens deleted here, and
                // one being rebuilt for a new spacing gets thinned when its screens come back.
                if (m_Regenerate.Contains(road))
                    continue;
                if (EntityManager.Exists(road) && EntityManager.HasComponent<Updated>(road))
                    m_Retry.Add(road);
                else
                    ThinRoad(road);
            }

            m_Regenerate.Clear();
            m_Thin.Clear();
            foreach (Entity road in m_Retry)
                m_Thin.Add(road);
        }

        private void ThinRoad(Entity road)
        {
            if (!EntityManager.Exists(road) || EntityManager.HasComponent<Deleted>(road) || EntityManager.HasComponent<Temp>(road) ||
                !EntityManager.TryGetComponent<Game.Net.Curve>(road, out var curve) ||
                !EntityManager.TryGetBuffer(road, isReadOnly: true, out DynamicBuffer<SubObject> subObjects))
            {
                return;
            }

            m_Screens.Clear();
            int spacing = 1;
            for (int i = 0; i < subObjects.Length; i++)
            {
                Entity entity = subObjects[i].m_SubObject;
                if (!EntityManager.TryGetComponent<LedScreen>(entity, out var screen) ||
                    !EntityManager.HasComponent<Secondary>(entity) ||
                    EntityManager.HasComponent<Deleted>(entity) || EntityManager.HasComponent<Temp>(entity) ||
                    !EntityManager.TryGetComponent<Owner>(entity, out var owner) || owner.m_Owner != road ||
                    !EntityManager.TryGetComponent<Transform>(entity, out var transform))
                {
                    continue;
                }

                spacing = math.max(spacing, screen.m_Spacing);
                float2 position = transform.m_Position.xz;
                MathUtils.Distance(curve.m_Bezier.xz, position, out float t);
                float2 tangent = MathUtils.Tangent(curve.m_Bezier, t).xz;
                float2 offset = position - MathUtils.Position(curve.m_Bezier, t).xz;
                bool left = tangent.x * offset.y - tangent.y * offset.x > 0f;
                m_Screens.Add((entity, left, t));
            }

            spacing = math.min(spacing, kMaxSpacing);
            if (spacing <= 1 || m_Screens.Count < 2)
                return;

            // Along each side of the road, keep the first screen and then every spacing-th one.
            m_Screens.Sort((a, b) => a.left != b.left ? a.left.CompareTo(b.left) : a.t.CompareTo(b.t));
            var remove = new NativeList<Entity>(m_Screens.Count, Allocator.Temp);
            int rank = 0;
            for (int i = 0; i < m_Screens.Count; i++)
            {
                if (i > 0 && m_Screens[i].left != m_Screens[i - 1].left)
                    rank = 0;
                if (rank % spacing != 0)
                    remove.Add(m_Screens[i].entity);
                rank++;
            }
            if (remove.Length > 0)
                EntityManager.AddComponent<Deleted>(remove.AsArray());
            remove.Dispose();
        }
    }
}
