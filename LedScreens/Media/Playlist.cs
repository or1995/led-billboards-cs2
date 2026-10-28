using System;
using LedScreens.Components;

namespace LedScreens.Media
{
    /// <summary>
    /// Works out which playlist item a screen shows at a given time. It is a pure function of
    /// the clock, so no playback state has to be saved and every frame agrees with the next.
    /// </summary>
    public static class Playlist
    {
        public const float kMinInterval = 2f;
        public const float kMaxInterval = 300f;

        public struct Position
        {
            /// <summary>Index of the item showing now.</summary>
            public int m_Current;

            /// <summary>Index of the item that comes next.</summary>
            public int m_Next;

            /// <summary>Seconds until the switch to m_Next.</summary>
            public float m_TimeToNext;
        }

        public static Position Locate(in LedScreen screen, int count, double time)
        {
            if (count <= 1)
                return new Position { m_Current = 0, m_Next = 0, m_TimeToNext = float.MaxValue };

            float interval = Math.Max(kMinInterval, screen.m_Interval);
            double t = time + screen.m_TimeOffset;
            long slot = (long)Math.Floor(t / interval);
            float timeToNext = (float)((slot + 1) * interval - t);

            return new Position
            {
                m_Current = ItemAt(screen, count, slot),
                m_Next = ItemAt(screen, count, slot + 1),
                m_TimeToNext = timeToNext,
            };
        }

        private static int ItemAt(in LedScreen screen, int count, long slot)
        {
            long round = FloorDiv(slot, count);
            int indexInRound = (int)(slot - round * count);
            if (screen.m_Playlist != PlaylistMode.Shuffle)
                return indexInRound;
            // Two items can only alternate without repeating; the seed picks which goes first.
            if (count == 2)
                return (int)((slot + (screen.m_Seed & 1)) & 1);

            // Shuffle: a fresh permutation each round, so every item plays once per round.
            int item = Shuffled(screen.m_Seed, round, count, indexInRound);
            // Don't repeat the last item of the previous round right away.
            if (indexInRound == 0 && item == Shuffled(screen.m_Seed, round - 1, count, count - 1))
                item = Shuffled(screen.m_Seed, round, count, 1);
            else if (indexInRound == 1 && Shuffled(screen.m_Seed, round, count, 0) == Shuffled(screen.m_Seed, round - 1, count, count - 1))
                item = Shuffled(screen.m_Seed, round, count, 0);
            return item;
        }

        // Main thread only (render system and UI), so one scratch buffer is enough.
        private static int[] s_Order = new int[64];

        /// <summary>Element <paramref name="index"/> of a seeded Fisher-Yates shuffle of 0..count-1.</summary>
        private static int Shuffled(uint seed, long round, int count, int index)
        {
            // Playlists are short, so rebuilding the permutation is cheaper than caching it.
            if (s_Order.Length < count)
                s_Order = new int[Math.Max(count, s_Order.Length * 2)];
            int[] order = s_Order;
            for (int i = 0; i < count; i++)
                order[i] = i;

            uint state = Hash(seed ^ (uint)round ^ (uint)(round >> 32) * 0x9E3779B9u);
            for (int i = count - 1; i > 0; i--)
            {
                state = Hash(state + (uint)i);
                int j = (int)(state % (uint)(i + 1));
                (order[i], order[j]) = (order[j], order[i]);
            }
            return order[index];
        }

        private static uint Hash(uint x)
        {
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            x *= 0x846CA68Bu;
            x ^= x >> 16;
            return x;
        }

        private static long FloorDiv(long a, long b)
        {
            long q = a / b;
            return (a % b != 0 && (a < 0) != (b < 0)) ? q - 1 : q;
        }
    }
}
