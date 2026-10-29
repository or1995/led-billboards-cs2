using System;
using Unity.Entities;
using Unity.Mathematics;

namespace LedScreens.Components
{
    [Flags]
    public enum ScreenFaces : int
    {
        Front = 1,
        Back = 2,
        Both = Front | Back,
    }

    /// <summary>
    /// On the prefab entity: where the screen sits on the prop. Normally this is the ad panel
    /// measured from the vanilla mesh; if that failed, the insets place it inside the mesh bounds.
    /// </summary>
    public struct LedScreenData : IComponentData, IQueryTypeParameter
    {
        public ScreenFaces m_Faces;

        /// <summary>True when the m_Panel* fields hold a measured ad panel.</summary>
        public bool m_HasPanel;

        /// <summary>Middle of the panel in prefab space (between its front and back faces).</summary>
        public float3 m_PanelCenter;

        public float2 m_PanelSize;

        /// <summary>Horizontal direction the front face looks towards.</summary>
        public float3 m_PanelNormal;

        public float m_PanelHalfDepth;

        public float m_InsetSide;

        public float m_InsetTop;

        public float m_InsetBottom;

        /// <summary>Meters in front of the panel face, to avoid z-fighting.</summary>
        public float m_SurfaceOffset;

        /// <summary>Round ad face: the image is drawn as a disc, cropped to fill it.</summary>
        public bool m_Round;
    }
}
