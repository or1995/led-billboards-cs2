using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace LedScreens.Media
{
    /// <summary>
    /// Reuses the large per-frame arrays of the media load pipeline (decoded frames, LED-effect
    /// frames, mip levels, encoded texture data). Playlists load media all the time, and with
    /// fresh arrays each load left hundreds of MB of garbage, so the game's stop-the-world
    /// garbage collector ran often and caused frame drops. Thread-safe. Arrays come back with
    /// old contents, so callers must overwrite every element.
    /// </summary>
    internal static class BufferPool<T>
    {
        // How much memory idle arrays may hold on to; anything beyond is left to the GC.
        private const long kMaxRetainedBytes = 192L * 1024 * 1024;

        private static readonly int kElementSize = Marshal.SizeOf(typeof(T));
        private static readonly ConcurrentDictionary<int, ConcurrentQueue<T[]>> s_Free = new ConcurrentDictionary<int, ConcurrentQueue<T[]>>();
        private static long s_Retained;

        /// <summary>An array of exactly <paramref name="length"/> elements, reused when possible.</summary>
        public static T[] Rent(int length)
        {
            if (s_Free.TryGetValue(length, out var queue) && queue.TryDequeue(out var array))
            {
                Interlocked.Add(ref s_Retained, -(long)length * kElementSize);
                return array;
            }
            return new T[length];
        }

        /// <summary>Hands an array back. The caller must not use it afterwards.</summary>
        public static void Return(T[] array)
        {
            if (array == null || array.Length == 0)
                return;
            long bytes = (long)array.Length * kElementSize;
            if (Interlocked.Add(ref s_Retained, bytes) > kMaxRetainedBytes)
            {
                Interlocked.Add(ref s_Retained, -bytes);
                return;
            }
            s_Free.GetOrAdd(array.Length, _ => new ConcurrentQueue<T[]>()).Enqueue(array);
        }
    }
}
