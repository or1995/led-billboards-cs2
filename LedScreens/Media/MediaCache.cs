using System;
using System.IO;
using System.Linq;
using Colossal.PSI.Environment;
using UnityEngine;

namespace LedScreens.Media
{
    /// <summary>
    /// Optional disk cache of converted GIFs: the encoded texture data per file, LED effect and
    /// quality settings, so a GIF that comes round again in a playlist loads from disk instead of
    /// being decoded and converted again. Least recently used files go first when it is full.
    /// Thread-safe; used from the load tasks.
    /// </summary>
    internal static class MediaCache
    {
        private const uint kMagic = 0x43444C4C; // "LLDC"
        private const int kVersion = 1;
        private const string kExtension = ".ledc";

        private static readonly object s_TrimLock = new object();

        public static string Folder { get; } = Path.Combine(EnvPath.kUserDataPath, "ModsData", nameof(LedScreens), "Cache");

        /// <summary>Cache file name for one way of converting one version of a file.</summary>
        public static string Key(string name, long fileStamp, LedEffectMode effect, int maxSize, int maxFrames, int columns, bool compress)
        {
            string text = $"{kVersion}|{name}|{fileStamp}|{(int)effect}|{maxSize}|{maxFrames}|{columns}|{compress}";
            // FNV-1a, 64 bit: stable across runs, unlike string.GetHashCode.
            ulong hash = 14695981039346656037UL;
            foreach (char c in text)
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }
            return hash.ToString("x16");
        }

        public static bool TryLoad(string key, out DecodedClip clip)
        {
            clip = null;
            string path = Path.Combine(Folder, key + kExtension);
            if (!File.Exists(path))
                return false;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                using (var reader = new BinaryReader(stream))
                {
                    if (reader.ReadUInt32() != kMagic || reader.ReadInt32() != kVersion)
                        throw new InvalidDataException("Not a cache file of this version");
                    var result = new DecodedClip
                    {
                        Width = reader.ReadInt32(),
                        Height = reader.ReadInt32(),
                        Format = (TextureFormat)reader.ReadInt32(),
                        MipCount = reader.ReadInt32(),
                        PointFilter = reader.ReadBoolean(),
                    };
                    int frames = reader.ReadInt32();
                    int frameLength = reader.ReadInt32();
                    if (frames <= 0 || frameLength <= 0)
                        throw new InvalidDataException("Empty cache file");
                    for (int i = 0; i < frames; i++)
                        result.Delays.Add(reader.ReadSingle());
                    for (int i = 0; i < frames; i++)
                    {
                        var data = BufferPool<byte>.Rent(frameLength);
                        ReadFully(stream, data);
                        result.Raw.Add(data);
                    }
                    clip = result;
                }
                // Mark as recently used, so trimming removes other files first.
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                return true;
            }
            catch (Exception e)
            {
                Mod.log.Warn($"Ignoring broken media cache file {key}: {e.Message}");
                TryDelete(path);
                return false;
            }
        }

        public static void Save(string key, DecodedClip clip, long maxBytes)
        {
            if (clip.Raw.Count == 0)
                return;
            string path = Path.Combine(Folder, key + kExtension);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Folder);
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(kMagic);
                    writer.Write(kVersion);
                    writer.Write(clip.Width);
                    writer.Write(clip.Height);
                    writer.Write((int)clip.Format);
                    writer.Write(clip.MipCount);
                    writer.Write(clip.PointFilter);
                    writer.Write(clip.Raw.Count);
                    writer.Write(clip.Raw[0].Length);
                    foreach (float delay in clip.Delays)
                        writer.Write(delay);
                    foreach (var data in clip.Raw)
                        writer.Write(data);
                }
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(temp, path);
            }
            catch (Exception e)
            {
                Mod.log.Warn($"Couldn't write media cache file {key}: {e.Message}");
                TryDelete(temp);
                return;
            }
            Trim(maxBytes);
        }

        /// <summary>Deletes the least recently used files until the cache fits.</summary>
        public static void Trim(long maxBytes)
        {
            lock (s_TrimLock)
            {
                try
                {
                    if (!Directory.Exists(Folder))
                        return;
                    var files = new DirectoryInfo(Folder).GetFiles("*" + kExtension).OrderBy(f => f.LastWriteTimeUtc).ToList();
                    long total = files.Sum(f => f.Length);
                    foreach (var file in files)
                    {
                        if (total <= maxBytes)
                            break;
                        total -= file.Length;
                        TryDelete(file.FullName);
                    }
                }
                catch (Exception e)
                {
                    Mod.log.Warn($"Couldn't trim the media cache: {e.Message}");
                }
            }
        }

        public static void Clear()
        {
            lock (s_TrimLock)
            {
                try
                {
                    if (!Directory.Exists(Folder))
                        return;
                    int count = 0;
                    foreach (var file in new DirectoryInfo(Folder).GetFiles())
                    {
                        TryDelete(file.FullName);
                        count++;
                    }
                    Mod.log.Info($"Cleared the media cache ({count} file(s))");
                }
                catch (Exception e)
                {
                    Mod.log.Warn($"Couldn't clear the media cache: {e.Message}");
                }
            }
        }

        private static void ReadFully(Stream stream, byte[] buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = stream.Read(buffer, offset, buffer.Length - offset);
                if (read <= 0)
                    throw new EndOfStreamException("Cache file is cut short");
                offset += read;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
