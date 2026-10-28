using System;
using UnityEngine;

namespace LedScreens.Media
{
    /// <summary>An animation or still image uploaded to the GPU: one texture per frame plus timing.</summary>
    public sealed class MediaClip : IMedia
    {
        private readonly Texture[] m_Frames;
        private readonly float[] m_FrameEnds;

        public MediaClip(string name, Texture[] frames, float[] delays)
        {
            Name = name;
            m_Frames = frames;
            m_FrameEnds = new float[delays.Length];
            float time = 0f;
            for (int i = 0; i < delays.Length; i++)
            {
                time += delays[i];
                m_FrameEnds[i] = time;
            }
            Duration = Math.Max(time, 0.01f);
            Width = frames[0].width;
            Height = frames[0].height;
        }

        public string Name { get; }

        public int Width { get; }

        public int Height { get; }

        public float Duration { get; }

        public int FrameCount => m_Frames.Length;

        public float LastUsedTime { get; set; }

        /// <summary>Estimated GPU memory (the profiler API returns 0 in release builds).</summary>
        public long MemoryBytes
        {
            get
            {
                float bytesPerPixel = 4f;
                bool mips = true;
                if (m_Frames[0] is Texture2D texture)
                {
                    if (texture.format == TextureFormat.DXT1)
                        bytesPerPixel = 0.5f;
                    else if (texture.format == TextureFormat.DXT5)
                        bytesPerPixel = 1f;
                    mips = texture.mipmapCount > 1;
                }
                float mipFactor = mips ? 4f / 3f : 1f;
                return (long)(Width * Height * bytesPerPixel * mipFactor * m_Frames.Length);
            }
        }

        public Texture GetFrame(double time)
        {
            if (m_Frames.Length == 1)
                return m_Frames[0];

            float t = (float)(time % Duration);
            if (t < 0f)
                t += Duration;

            // Binary search for the first frame that ends after t.
            int lo = 0, hi = m_FrameEnds.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (m_FrameEnds[mid] <= t)
                    lo = mid + 1;
                else
                    hi = mid;
            }
            return m_Frames[lo];
        }

        public void Dispose()
        {
            foreach (var frame in m_Frames)
            {
                if (frame != null)
                    UnityEngine.Object.Destroy(frame);
            }
        }
    }
}
