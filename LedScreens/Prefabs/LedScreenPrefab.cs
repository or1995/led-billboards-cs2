using System.Collections.Generic;
using Game.Prefabs;
using LedScreens.Components;
using Unity.Entities;
using Unity.Mathematics;

namespace LedScreens.Prefabs
{
    /// <summary>Added to the cloned billboard prefabs to turn them into LED screens.</summary>
    public class LedScreenPrefab : ComponentBase
    {
        public ScreenFaces m_Faces = ScreenFaces.Front;

        public bool m_HasPanel;

        public float3 m_PanelCenter;

        public float2 m_PanelSize;

        public float3 m_PanelNormal = new float3(0f, 0f, 1f);

        public float m_PanelHalfDepth;

        public float m_InsetSide = 0.03f;

        public float m_InsetTop = 0.03f;

        public float m_InsetBottom = 0.03f;

        public float m_SurfaceOffset = 0.02f;

        public bool m_Round;

        public override void GetPrefabComponents(HashSet<ComponentType> components)
        {
            components.Add(ComponentType.ReadWrite<LedScreenData>());
        }

        public override void GetArchetypeComponents(HashSet<ComponentType> components)
        {
            // Every placed instance gets its own LedScreen, so each one can play a different GIF.
            components.Add(ComponentType.ReadWrite<LedScreen>());
        }

        public override void Initialize(EntityManager entityManager, Entity entity)
        {
            base.Initialize(entityManager, entity);
            entityManager.SetComponentData(entity, new LedScreenData
            {
                m_Faces = m_Faces,
                m_HasPanel = m_HasPanel,
                m_PanelCenter = m_PanelCenter,
                m_PanelSize = m_PanelSize,
                m_PanelNormal = m_PanelNormal,
                m_PanelHalfDepth = m_PanelHalfDepth,
                m_InsetSide = m_InsetSide,
                m_InsetTop = m_InsetTop,
                m_InsetBottom = m_InsetBottom,
                m_SurfaceOffset = m_SurfaceOffset,
                m_Round = m_Round,
            });
        }
    }
}
