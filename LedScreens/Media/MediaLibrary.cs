using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Colossal.PSI.Environment;
using UnityEngine;

namespace LedScreens.Media
{
    /// <summary>
    /// Owns all loaded media. GIFs are decoded on worker threads and uploaded to the GPU on the
    /// main thread in small time slices; images are read on a worker and decoded by Unity on the
    /// main thread; videos stream through a VideoPlayer. Media nobody looked at for a while is
    /// unloaded. All public members must be called from the main thread.
    /// </summary>
    public sealed class MediaLibrary : IDisposable
    {
        public const string kTestPattern = "builtin:testpattern";

        private const float kUploadBudgetMs = 4f;
        private const float kUnloadAfterSeconds = 60f;
        private const float kUnloadCheckInterval = 5f;

        private static readonly string[] kGifExtensions = { ".gif" };
        private static readonly string[] kImageExtensions = { ".png", ".jpg", ".jpeg" };
        // What Unity's VideoPlayer can play on Windows (through Media Foundation).
        private static readonly string[] kVideoExtensions = { ".mp4", ".m4v", ".mov", ".webm", ".avi", ".wmv", ".ogv" };

        private readonly Dictionary<string, IMedia> m_Media = new Dictionary<string, IMedia>();
        private readonly Dictionary<string, Task<DecodedClip>> m_Decoding = new Dictionary<string, Task<DecodedClip>>();
        private readonly Dictionary<string, Task<byte[]>> m_ReadingImages = new Dictionary<string, Task<byte[]>>();
        private readonly HashSet<string> m_Failed = new HashSet<string>();
        private readonly Stopwatch m_Stopwatch = new Stopwatch();
        private List<string> m_Available = new List<string>();
        private PendingUpload m_Upload;
        private float m_NextUnloadCheck;

        public static MediaLibrary Instance { get; } = new MediaLibrary();

        public string MediaFolder { get; private set; }

        public IReadOnlyList<string> Available => m_Available;

        /// <summary>Shown while media is loading or when its file is missing or broken.</summary>
        public Texture2D NoSignal { get; private set; }

        public void Initialize()
        {
            MediaFolder = Path.Combine(EnvPath.kUserDataPath, "ModsData", nameof(LedScreens), "Media");
            Directory.CreateDirectory(MediaFolder);
            if (NoSignal == null)
                NoSignal = CreateNoSignalTexture();
            Refresh();
        }

        /// <summary>Rescans the media folder and drops everything loaded so changed files and quality settings take effect.</summary>
        public void Refresh()
        {
            try
            {
                m_Available = Directory.EnumerateFiles(MediaFolder, "*.*", SearchOption.AllDirectories)
                    .Where(path => GetKind(path) != Kind.Unsupported)
                    .Select(path => path.Substring(MediaFolder.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/'))
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception e)
            {
                Mod.log.Error(e, $"Failed to scan media folder {MediaFolder}");
                m_Available = new List<string>();
            }

            UnloadAll();
            Mod.log.Info($"Media folder {MediaFolder}: {m_Available.Count} file(s)");
        }

        /// <summary>Returns the media if it is loaded, otherwise starts loading it and returns null.</summary>
        public IMedia GetClip(string name, float now)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            if (m_Media.TryGetValue(name, out var media))
            {
                media.LastUsedTime = now;
                return media;
            }

            if (!m_Decoding.ContainsKey(name) && !m_ReadingImages.ContainsKey(name) && !m_Failed.Contains(name) &&
                (m_Upload == null || m_Upload.Name != name))
            {
                StartLoad(name, now);
            }
            return null;
        }

        /// <summary>Call once per frame from the main thread.</summary>
        public void Update(float now)
        {
            m_Stopwatch.Restart();

            if (m_Upload == null)
                TakeFinishedDecode();

            while (m_Upload != null && m_Stopwatch.Elapsed.TotalMilliseconds < kUploadBudgetMs)
            {
                if (m_Upload.UploadNext())
                {
                    var clip = m_Upload.CreateClip();
                    Add(clip, now);
                    Mod.log.Info($"Loaded '{clip.Name}': {clip.Width}x{clip.Height}, {clip.FrameCount} frames, {clip.Duration:0.00}s, {clip.MemoryBytes / (1024f * 1024f):0.0} MB");
                    m_Upload = null;
                    TakeFinishedDecode();
                }
            }

            // Image decoding can't be split up, so do at most one per frame.
            if (m_Stopwatch.Elapsed.TotalMilliseconds < kUploadBudgetMs)
                TakeFinishedImage(now);

            if (now >= m_NextUnloadCheck)
            {
                m_NextUnloadCheck = now + kUnloadCheckInterval;
                UnloadUnused(now);
            }
        }

        public void Dispose()
        {
            UnloadAll();
            if (NoSignal != null)
            {
                UnityEngine.Object.Destroy(NoSignal);
                NoSignal = null;
            }
        }

        public static bool IsVideo(string name) => !string.IsNullOrEmpty(name) && GetKind(name) == Kind.Video;

        private enum Kind
        {
            Unsupported,
            Gif,
            Image,
            Video,
        }

        private static Kind GetKind(string path)
        {
            string extension = Path.GetExtension(path);
            if (kGifExtensions.Any(e => e.Equals(extension, StringComparison.OrdinalIgnoreCase)))
                return Kind.Gif;
            if (kImageExtensions.Any(e => e.Equals(extension, StringComparison.OrdinalIgnoreCase)))
                return Kind.Image;
            if (kVideoExtensions.Any(e => e.Equals(extension, StringComparison.OrdinalIgnoreCase)))
                return Kind.Video;
            return Kind.Unsupported;
        }

        private void StartLoad(string name, float now)
        {
            var setting = Mod.Settings;
            int maxSize = setting?.MaxTextureSize ?? 512;
            int maxFrames = setting?.MaxFrames ?? 300;

            if (name == kTestPattern)
            {
                m_Decoding[name] = Task.Run(() => TestPattern.Generate());
                return;
            }

            string path = Path.GetFullPath(Path.Combine(MediaFolder, name));
            if (!path.StartsWith(Path.GetFullPath(MediaFolder), StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            {
                Mod.log.Warn($"Media '{name}' not found in {MediaFolder}");
                m_Failed.Add(name);
                return;
            }

            switch (GetKind(path))
            {
                case Kind.Gif:
                    m_Decoding[name] = Task.Run(() => GifDecoder.Decode(File.ReadAllBytes(path), maxSize, maxFrames));
                    break;
                case Kind.Image:
                    m_ReadingImages[name] = Task.Run(() => File.ReadAllBytes(path));
                    break;
                case Kind.Video:
                    Add(new VideoMedia(name, path, maxSize), now);
                    break;
                default:
                    Mod.log.Warn($"Media '{name}' has an unsupported file type");
                    m_Failed.Add(name);
                    break;
            }
        }

        private void Add(IMedia media, float now)
        {
            media.LastUsedTime = now;
            m_Media[media.Name] = media;
        }

        private void TakeFinishedDecode()
        {
            foreach (var pair in m_Decoding)
            {
                if (!pair.Value.IsCompleted)
                    continue;

                m_Decoding.Remove(pair.Key);
                if (pair.Value.IsFaulted)
                {
                    Mod.log.Error(pair.Value.Exception?.GetBaseException(), $"Failed to decode '{pair.Key}'");
                    m_Failed.Add(pair.Key);
                    return;
                }

                bool compress = Mod.Settings?.CompressTextures ?? true;
                m_Upload = new PendingUpload(pair.Key, pair.Value.Result, compress);
                return;
            }
        }

        private void TakeFinishedImage(float now)
        {
            foreach (var pair in m_ReadingImages)
            {
                if (!pair.Value.IsCompleted)
                    continue;

                m_ReadingImages.Remove(pair.Key);
                try
                {
                    if (pair.Value.IsFaulted)
                        throw pair.Value.Exception.GetBaseException();

                    var frame = LoadImage(pair.Key, pair.Value.Result, Mod.Settings?.MaxTextureSize ?? 512);
                    var clip = new MediaClip(pair.Key, new[] { frame }, new[] { 1f });
                    Add(clip, now);
                    Mod.log.Info($"Loaded image '{clip.Name}': {clip.Width}x{clip.Height}");
                }
                catch (Exception e)
                {
                    Mod.log.Error(e, $"Failed to load image '{pair.Key}'");
                    m_Failed.Add(pair.Key);
                }
                return;
            }
        }

        /// <summary>Decodes a PNG/JPG; images over the size limit are scaled down on the GPU.</summary>
        private static Texture LoadImage(string name, byte[] bytes, int maxSize)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true)
            {
                name = $"LedScreens_{name}",
                filterMode = FilterMode.Trilinear,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 4,
            };
            if (!texture.LoadImage(bytes, false))
            {
                UnityEngine.Object.Destroy(texture);
                throw new FormatException("Not a valid PNG or JPG file");
            }

            int largest = Math.Max(texture.width, texture.height);
            if (largest <= maxSize)
            {
                texture.Apply(true, true);
                return texture;
            }

            float scale = (float)maxSize / largest;
            var scaled = new RenderTexture(Math.Max(4, (int)(texture.width * scale)), Math.Max(4, (int)(texture.height * scale)), 0,
                RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                name = texture.name,
                useMipMap = true,
                autoGenerateMips = true,
                filterMode = FilterMode.Trilinear,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 4,
            };
            scaled.Create();
            Graphics.Blit(texture, scaled);
            UnityEngine.Object.Destroy(texture);
            return scaled;
        }

        private void UnloadUnused(float now)
        {
            List<string> expired = null;
            foreach (var pair in m_Media)
            {
                bool failedVideo = pair.Value is VideoMedia video && video.Failed;
                if (failedVideo || now - pair.Value.LastUsedTime > kUnloadAfterSeconds)
                    (expired ??= new List<string>()).Add(pair.Key);
                if (failedVideo)
                    m_Failed.Add(pair.Key);
            }
            if (expired == null)
                return;

            foreach (var name in expired)
            {
                m_Media[name].Dispose();
                m_Media.Remove(name);
                Mod.log.Info($"Unloaded media '{name}'");
            }
        }

        private void UnloadAll()
        {
            foreach (var media in m_Media.Values)
                media.Dispose();
            m_Media.Clear();
            m_Failed.Clear();
            m_Upload?.Abort();
            m_Upload = null;
            // Running loads can't be cancelled; forgetting them drops their results.
            m_Decoding.Clear();
            m_ReadingImages.Clear();
        }

        private static Texture2D CreateNoSignalTexture()
        {
            const int width = 64, height = 32;
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                name = "LedScreens_NoSignal",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            var random = new System.Random(1234);
            var pixels = new Color32[width * height];
            for (int i = 0; i < pixels.Length; i++)
            {
                byte v = (byte)random.Next(10, 70);
                pixels[i] = new Color32(v, v, (byte)(v + 20), 255);
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        /// <summary>Uploads one decoded clip frame by frame so a big GIF doesn't freeze the game.</summary>
        private sealed class PendingUpload
        {
            private readonly DecodedClip m_Decoded;
            private readonly bool m_Compress;
            private readonly Texture[] m_Textures;
            private int m_Next;

            public PendingUpload(string name, DecodedClip decoded, bool compress)
            {
                Name = name;
                m_Decoded = decoded;
                m_Compress = compress;
                m_Textures = new Texture[decoded.Frames.Count];
            }

            public string Name { get; }

            /// <summary>Uploads one frame; returns true when all frames are done.</summary>
            public bool UploadNext()
            {
                var texture = new Texture2D(m_Decoded.Width, m_Decoded.Height, TextureFormat.RGBA32, true)
                {
                    name = $"LedScreens_{Name}_{m_Next}",
                    filterMode = FilterMode.Trilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    anisoLevel = 4,
                };
                texture.SetPixels32(m_Decoded.Frames[m_Next]);
                texture.Apply(true, false);
                if (m_Compress)
                    texture.Compress(false);
                texture.Apply(false, true);

                m_Decoded.Frames[m_Next] = null; // let the GC reclaim the CPU copy early
                m_Textures[m_Next++] = texture;
                return m_Next >= m_Textures.Length;
            }

            public MediaClip CreateClip() => new MediaClip(Name, m_Textures, m_Decoded.Delays.ToArray());

            public void Abort()
            {
                for (int i = 0; i < m_Next; i++)
                    UnityEngine.Object.Destroy(m_Textures[i]);
            }
        }
    }
}
