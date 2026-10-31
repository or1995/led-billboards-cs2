using System;
using Colossal.Serialization.Entities;
using Unity.Collections;
using Unity.Entities;

namespace LedScreens.Components
{
    [Flags]
    public enum LedScreenFlags : int
    {
        None = 0,
        Initialized = 1,
    }

    public enum PlaylistMode : int
    {
        /// <summary>Always show m_Media.</summary>
        Off = 0,

        /// <summary>Go through the playlist in order.</summary>
        InOrder = 1,

        /// <summary>Go through the playlist in a random order (each item once per round).</summary>
        Shuffle = 2,
    }

    /// <summary>LED look of one screen: the global setting, or its own choice.</summary>
    public enum ScreenEffect : int
    {
        /// <summary>Use the "LED pixel effect" setting.</summary>
        Default = 0,
        Off = 1,
        Pixelated = 2,
        LedDots = 3,
    }

    /// <summary>Per placed screen: what it plays and how. Saved with the city.</summary>
    public struct LedScreen : IComponentData, IQueryTypeParameter, ISerializable
    {
        // 1: media, speed, brightness, offset, flags. 2: + playlist mode, interval, seed. 3: + effect.
        private const int kVersion = 3;

        public const float kDefaultInterval = 10f;

        /// <summary>File name relative to the media folder, or a builtin: id.</summary>
        public FixedString128Bytes m_Media;

        public float m_Speed;

        public float m_Brightness;

        /// <summary>Seconds added to the clock, so screens with the same GIF can play out of sync.</summary>
        public float m_TimeOffset;

        public LedScreenFlags m_Flags;

        public PlaylistMode m_Playlist;

        /// <summary>Seconds each playlist item is shown.</summary>
        public float m_Interval;

        /// <summary>Makes each screen shuffle in its own order.</summary>
        public uint m_Seed;

        public ScreenEffect m_Effect;

        public static LedScreen Create(string media)
        {
            var screen = new LedScreen
            {
                m_Speed = 1f,
                m_Brightness = 1f,
                m_Flags = LedScreenFlags.Initialized,
                m_Interval = kDefaultInterval,
                m_Seed = (uint)UnityEngine.Random.Range(1, int.MaxValue),
            };
            screen.m_Media.CopyFromTruncated(media ?? string.Empty);
            return screen;
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(m_Media.ToString());
            writer.Write(m_Speed);
            writer.Write(m_Brightness);
            writer.Write(m_TimeOffset);
            writer.Write((int)m_Flags);
            writer.Write((int)m_Playlist);
            writer.Write(m_Interval);
            writer.Write(m_Seed);
            writer.Write((int)m_Effect);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out int version);
            reader.Read(out string media);
            reader.Read(out m_Speed);
            reader.Read(out m_Brightness);
            reader.Read(out m_TimeOffset);
            reader.Read(out int flags);
            m_Media = default;
            m_Media.CopyFromTruncated(media ?? string.Empty);
            m_Flags = (LedScreenFlags)flags;

            m_Playlist = PlaylistMode.Off;
            m_Interval = kDefaultInterval;
            m_Seed = 1;
            if (version >= 2)
            {
                reader.Read(out int playlist);
                reader.Read(out m_Interval);
                reader.Read(out m_Seed);
                m_Playlist = (PlaylistMode)playlist;
            }

            m_Effect = ScreenEffect.Default;
            if (version >= 3)
            {
                reader.Read(out int effect);
                m_Effect = (ScreenEffect)effect;
            }
        }
    }
}
