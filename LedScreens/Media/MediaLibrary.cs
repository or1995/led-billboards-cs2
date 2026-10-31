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
    /// unloaded. The LED effect is baked in while loading, so each file is loaded once per effect
    /// that screens use. All public members must be called from the main thread.
    /// </summary>
    public sealed class MediaLibrary : IDisposable
    {
        public const string kTestPattern = "builtin:testpattern";

        private const float kUploadBudgetMs = 4f;
        private const float kUnloadAfterSeconds = 60f;
        private const float kUnloadVideoAfterSeconds = 300f;
        private const float kUnloadCheckInterval = 5f;
        private const float kFolderCheckInterval = 3f;

        private static readonly string[] kGifExtensions = { ".gif" };
        private static readonly string[] kImageExtensions = { ".png", ".jpg", ".jpeg" };
        // What Unity's VideoPlayer can play on Windows (through Media Foundation).
        private static readonly string[] kVideoExtensions = { ".mp4", ".m4v", ".mov", ".webm", ".avi", ".wmv", ".ogv" };

        private readonly Dictionary<MediaKey, IMedia> m_Media = new Dictionary<MediaKey, IMedia>();
        private readonly Dictionary<MediaKey, Task<DecodedClip>> m_Decoding = new Dictionary<MediaKey, Task<DecodedClip>>();
        private readonly Dictionary<MediaKey, Task<byte[]>> m_ReadingImages = new Dictionary<MediaKey, Task<byte[]>>();
        private readonly HashSet<MediaKey> m_Failed = new HashSet<MediaKey>();
        private readonly Stopwatch m_Stopwatch = new Stopwatch();
        private List<string> m_Available = new List<string>();
        // File name -> size/date stamp from the last folder scan, to spot new, changed and deleted files.
        private Dictionary<string, long> m_Snapshot = new Dictionary<string, long>();
        private Task<Dictionary<string, long>> m_FolderScan;
        private float m_NextFolderCheck;
        private PendingUpload m_Upload;
        private float m_NextUnloadCheck;
        private int m_LoadSettingsHash;

        public static MediaLibrary Instance { get; } = new MediaLibrary();

        public string MediaFolder { get; private set; }

        public IReadOnlyList<string> Available => m_Available;

        /// <summary>Goes up whenever <see cref="Available"/> changes, so UI lists know to refresh.</summary>
        public int Version { get; private set; }

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
                SetSnapshot(ScanFolder(MediaFolder));
            }
            catch (Exception e)
            {
                Mod.log.Error(e, $"Failed to scan media folder {MediaFolder}");
                SetSnapshot(new Dictionary<string, long>());
            }

            UnloadAll();
            Mod.log.Info($"Media folder {MediaFolder}: {m_Available.Count} file(s)");
        }

        /// <summary>Returns the media with this LED effect if it is loaded, otherwise starts loading it and returns null.</summary>
        public IMedia GetClip(string name, LedEffectMode effect, float now)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            var key = new MediaKey(name, effect);
            if (m_Media.TryGetValue(key, out var media))
            {
                media.LastUsedTime = now;
                return media;
            }

            if (!m_Decoding.ContainsKey(key) && !m_ReadingImages.ContainsKey(key) && !m_Failed.Contains(key) &&
                (m_Upload == null || !m_Upload.Key.Equals(key)))
            {
                StartLoad(key, now);
            }
            return null;
        }

        /// <summary>Keeps the media loaded if it already is, without starting a load (for screens out of view).</summary>
        public void Touch(string name, LedEffectMode effect, float now)
        {
            if (!string.IsNullOrEmpty(name) && m_Media.TryGetValue(new MediaKey(name, effect), out var media))
                media.LastUsedTime = now;
        }

        /// <summary>Call once per frame from the main thread.</summary>
        public void Update(float now)
        {
            m_Stopwatch.Restart();

            // Media is baked with the quality settings, so reload it when they change. A new LED
            // effect needs no reload: screens simply ask for the other variant.
            int settingsHash = LoadSettingsHash();
            if (m_LoadSettingsHash != 0 && settingsHash != m_LoadSettingsHash)
            {
                Mod.log.Info("Media settings changed, reloading media");
                Refresh();
            }
            m_LoadSettingsHash = settingsHash;

            if (m_Upload == null)
                TakeFinishedDecode();

            while (m_Upload != null && m_Stopwatch.Elapsed.TotalMilliseconds < kUploadBudgetMs)
            {
                if (m_Upload.UploadNext())
                {
                    var clip = m_Upload.CreateClip();
                    Add(m_Upload.Key, clip, now);
                    Mod.log.Info($"Loaded '{m_Upload.Key}': {clip.Width}x{clip.Height}, {clip.FrameCount} frames, {clip.Duration:0.00}s, {clip.MemoryBytes / (1024f * 1024f):0.0} MB");
                    m_Upload = null;
                    TakeFinishedDecode();
                }
            }

            // Image decoding can't be split up, so do at most one per frame.
            if (m_Stopwatch.Elapsed.TotalMilliseconds < kUploadBudgetMs)
                TakeFinishedImage(now);

            foreach (var media in m_Media.Values)
            {
                if (media is VideoMedia video)
                    video.Update(now);
            }

            CheckFolder(now);

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

        /// <summary>
        /// Every few seconds, lists the media folder on a worker thread. New files show up in the
        /// lists; changed or deleted ones are dropped so they load again. Nothing else reloads.
        /// </summary>
        private void CheckFolder(float now)
        {
            if (m_FolderScan == null)
            {
                if (now < m_NextFolderCheck || MediaFolder == null)
                    return;
                string folder = MediaFolder;
                m_FolderScan = Task.Run(() => ScanFolder(folder));
                return;
            }
            if (!m_FolderScan.IsCompleted)
                return;

            var scan = m_FolderScan;
            m_FolderScan = null;
            m_NextFolderCheck = now + kFolderCheckInterval;
            if (scan.IsFaulted)
                return;

            var files = scan.Result;
            int added = 0, changed = 0, removed = 0;
            foreach (var pair in files)
            {
                if (!m_Snapshot.TryGetValue(pair.Key, out long stamp))
                    added++;
                else if (stamp == pair.Value)
                    continue;
                else
                    changed++;
                Forget(pair.Key); // also clears an earlier "not found"
            }
            foreach (var name in m_Snapshot.Keys)
            {
                if (!files.ContainsKey(name))
                {
                    removed++;
                    Forget(name);
                }
            }
            if (added + changed + removed == 0)
                return;

            SetSnapshot(files);
            Mod.log.Info($"Media folder changed: {added} new, {changed} changed, {removed} removed ({m_Available.Count} file(s))");
        }

        private void SetSnapshot(Dictionary<string, long> files)
        {
            m_Snapshot = files;
            m_Available = files.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
            Version++;
        }

        /// <summary>Media files in the folder (names relative to it, with '/') and a size/date stamp for each.</summary>
        private static Dictionary<string, long> ScanFolder(string folder)
        {
            var files = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (string path in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
            {
                if (GetKind(path) == Kind.Unsupported)
                    continue;
                try
                {
                    var info = new FileInfo(path);
                    string name = path.Substring(folder.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
                    files[name] = info.LastWriteTimeUtc.Ticks * 31 + info.Length;
                }
                catch (IOException)
                {
                    // Being copied or deleted right now; the next scan gets it.
                }
            }
            return files;
        }

        /// <summary>Drops everything loaded or loading for this file (all LED effects), so it loads fresh next time.</summary>
        private void Forget(string name)
        {
            foreach (var key in m_Media.Keys.Where(k => k.Name == name).ToList())
            {
                m_Media[key].Dispose();
                m_Media.Remove(key);
            }
            m_Failed.RemoveWhere(k => k.Name == name);
            foreach (var key in m_Decoding.Keys.Where(k => k.Name == name).ToList())
                m_Decoding.Remove(key);
            foreach (var key in m_ReadingImages.Keys.Where(k => k.Name == name).ToList())
                m_ReadingImages.Remove(key);
            if (m_Upload != null && m_Upload.Key.Name == name)
            {
                m_Upload.Abort();
                m_Upload = null;
            }
        }

        private static int LoadSettingsHash()
        {
            var s = Mod.Settings;
            if (s == null)
                return 0;
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + s.MaxTextureSize;
                hash = hash * 31 + s.MaxFrames;
                hash = hash * 31 + (s.CompressTextures ? 1 : 0);
                hash = hash * 31 + s.LedResolution;
                return hash == 0 ? 1 : hash;
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

        private void StartLoad(MediaKey key, float now)
        {
            var setting = Mod.Settings;
            int maxSize = setting?.MaxTextureSize ?? 512;
            int maxFrames = setting?.MaxFrames ?? 300;
            int columns = setting?.LedResolution ?? 128;
            bool compress = setting?.CompressTextures ?? true;
            bool useCache = setting?.CacheConvertedMedia ?? false;
            long cacheLimit = (long)(setting?.CacheSizeLimitGB ?? 4) * 1024 * 1024 * 1024;
            string name = key.Name;
            var effect = key.Effect;

            if (name == kTestPattern)
            {
                m_Decoding[key] = Task.Run(() => TextureEncoder.Encode(LedEffect.Apply(TestPattern.Generate(), effect, columns), compress));
                return;
            }

            string path = Path.GetFullPath(Path.Combine(MediaFolder, name));
            if (!path.StartsWith(Path.GetFullPath(MediaFolder), StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            {
                Mod.log.Warn($"Media '{name}' not found in {MediaFolder}");
                m_Failed.Add(key);
                return;
            }

            switch (GetKind(path))
            {
                case Kind.Gif:
                    m_Decoding[key] = Task.Run(() =>
                    {
                        string cacheKey = null;
                        if (useCache)
                        {
                            var info = new FileInfo(path);
                            cacheKey = MediaCache.Key(name, info.LastWriteTimeUtc.Ticks * 31 + info.Length, effect, maxSize, maxFrames, columns, compress);
                            if (MediaCache.TryLoad(cacheKey, out var cached))
                                return cached;
                        }
                        var clip = TextureEncoder.Encode(LedEffect.Apply(GifDecoder.Decode(File.ReadAllBytes(path), maxSize, maxFrames), effect, columns), compress);
                        if (cacheKey != null)
                            MediaCache.Save(cacheKey, clip, cacheLimit);
                        return clip;
                    });
                    break;
                case Kind.Image:
                    m_ReadingImages[key] = Task.Run(() => File.ReadAllBytes(path));
                    break;
                case Kind.Video:
                    Add(key, new VideoMedia(name, path, maxSize, effect, columns), now);
                    break;
                default:
                    Mod.log.Warn($"Media '{name}' has an unsupported file type");
                    m_Failed.Add(key);
                    break;
            }
        }

        private void Add(MediaKey key, IMedia media, float now)
        {
            media.LastUsedTime = now;
            m_Media[key] = media;
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

                m_Upload = new PendingUpload(pair.Key, pair.Value.Result, LedEffect.BrightnessCompensation(pair.Key.Effect));
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

                    var effect = pair.Key.Effect;
                    var frame = LoadImage(pair.Key.Name, pair.Value.Result, Mod.Settings?.MaxTextureSize ?? 512, effect, Mod.Settings?.LedResolution ?? 128);
                    var clip = new MediaClip(pair.Key.Name, new[] { frame }, new[] { 1f }, LedEffect.BrightnessCompensation(effect));
                    Add(pair.Key, clip, now);
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
        private static Texture LoadImage(string name, byte[] bytes, int maxSize, LedEffectMode effect, int columns)
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

            if (effect != LedEffectMode.Off)
            {
                // Bake the LED look on the CPU while the decoded pixels are still readable.
                var pixels = LedEffect.ApplyFrame(texture.GetPixels32(), texture.width, texture.height, effect, columns, out int width, out int height);
                UnityEngine.Object.Destroy(texture);
                var led = new Texture2D(width, height, TextureFormat.RGBA32, true)
                {
                    name = $"LedScreens_{name}",
                    filterMode = effect == LedEffectMode.Pixelated ? FilterMode.Point : FilterMode.Trilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    anisoLevel = 4,
                };
                led.SetPixels32(pixels);
                led.Apply(true, true);
                BufferPool<Color32>.Return(pixels);
                return led;
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
            List<MediaKey> expired = null;
            foreach (var pair in m_Media)
            {
                bool isVideo = pair.Value is VideoMedia;
                bool failedVideo = isVideo && ((VideoMedia)pair.Value).Failed;
                // Videos take a moment to start again, so keep them around longer (playlists come back to them).
                float unloadAfter = isVideo ? kUnloadVideoAfterSeconds : kUnloadAfterSeconds;
                if (failedVideo || now - pair.Value.LastUsedTime > unloadAfter)
                    (expired ??= new List<MediaKey>()).Add(pair.Key);
                if (failedVideo)
                    m_Failed.Add(pair.Key);
            }
            if (expired == null)
                return;

            foreach (var key in expired)
            {
                m_Media[key].Dispose();
                m_Media.Remove(key);
                Mod.log.Info($"Unloaded media '{key}'");
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

        /// <summary>One file loaded with one LED effect.</summary>
        private readonly struct MediaKey : IEquatable<MediaKey>
        {
            public readonly string Name;
            public readonly LedEffectMode Effect;

            public MediaKey(string name, LedEffectMode effect)
            {
                Name = name;
                Effect = effect;
            }

            public bool Equals(MediaKey other) => Effect == other.Effect && string.Equals(Name, other.Name, StringComparison.Ordinal);

            public override bool Equals(object obj) => obj is MediaKey other && Equals(other);

            public override int GetHashCode() => (Name?.GetHashCode() ?? 0) * 31 + (int)Effect;

            public override string ToString() => Effect == LedEffectMode.Off ? Name : $"{Name} ({Effect})";
        }

        /// <summary>
        /// Uploads one decoded clip frame by frame so a big GIF doesn't freeze the game. The
        /// frames arrive encoded (<see cref="TextureEncoder"/>), so each upload is just a copy.
        /// </summary>
        private sealed class PendingUpload
        {
            private readonly DecodedClip m_Decoded;
            private readonly float m_BrightnessScale;
            private readonly Texture[] m_Textures;
            private int m_Next;

            public PendingUpload(MediaKey key, DecodedClip decoded, float brightnessScale)
            {
                Key = key;
                m_Decoded = decoded;
                m_BrightnessScale = brightnessScale;
                m_Textures = new Texture[decoded.Raw.Count];
            }

            public MediaKey Key { get; }

            private string Name => Key.Name;

            /// <summary>Uploads one frame; returns true when all frames are done.</summary>
            public bool UploadNext()
            {
                var texture = new Texture2D(m_Decoded.Width, m_Decoded.Height, m_Decoded.Format, m_Decoded.MipCount, false)
                {
                    name = $"LedScreens_{Name}_{m_Next}",
                    filterMode = m_Decoded.PointFilter ? FilterMode.Point : FilterMode.Trilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    anisoLevel = 4,
                };
                texture.LoadRawTextureData(m_Decoded.Raw[m_Next]);
                texture.Apply(false, true);

                // The texture has its own copy now; let the next load reuse the buffer.
                BufferPool<byte>.Return(m_Decoded.Raw[m_Next]);
                m_Decoded.Raw[m_Next] = null;
                m_Textures[m_Next++] = texture;
                return m_Next >= m_Textures.Length;
            }

            public MediaClip CreateClip() => new MediaClip(Name, m_Textures, m_Decoded.Delays.ToArray(), m_BrightnessScale);

            public void Abort()
            {
                for (int i = 0; i < m_Next; i++)
                    UnityEngine.Object.Destroy(m_Textures[i]);
            }
        }
    }
}
