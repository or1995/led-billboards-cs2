using UnityEngine;
using UnityEngine.Video;

namespace LedScreens.Media
{
    /// <summary>
    /// A looping, muted video played by Unity's VideoPlayer into a render texture. All screens
    /// showing the same file share one player, so they play in sync (per-screen speed and
    /// offset don't apply to videos).
    /// </summary>
    public sealed class VideoMedia : IMedia
    {
        private readonly GameObject m_Host;
        private readonly VideoPlayer m_Player;
        private readonly int m_MaxSize;
        private RenderTexture m_Target;

        public VideoMedia(string name, string path, int maxSize)
        {
            Name = name;
            m_MaxSize = maxSize;

            m_Host = new GameObject($"LedScreens_Video_{name}");
            Object.DontDestroyOnLoad(m_Host);
            m_Player = m_Host.AddComponent<VideoPlayer>();
            m_Player.playOnAwake = false;
            m_Player.source = VideoSource.Url;
            m_Player.url = "file://" + path.Replace('\\', '/');
            m_Player.isLooping = true;
            m_Player.audioOutputMode = VideoAudioOutputMode.None;
            m_Player.renderMode = VideoRenderMode.RenderTexture;
            m_Player.aspectRatio = VideoAspectRatio.Stretch;
            m_Player.skipOnDrop = true;
            m_Player.prepareCompleted += OnPrepared;
            m_Player.errorReceived += OnError;
            m_Player.Prepare();
        }

        public string Name { get; }

        public float LastUsedTime { get; set; }

        public bool Failed { get; private set; }

        public Texture GetFrame(double time) => m_Target != null && m_Player.isPlaying ? m_Target : null;

        public void Dispose()
        {
            if (m_Player != null)
            {
                m_Player.prepareCompleted -= OnPrepared;
                m_Player.errorReceived -= OnError;
                m_Player.Stop();
            }
            if (m_Host != null)
                Object.Destroy(m_Host);
            if (m_Target != null)
            {
                m_Target.Release();
                Object.Destroy(m_Target);
                m_Target = null;
            }
        }

        private void OnPrepared(VideoPlayer player)
        {
            int width = (int)player.width;
            int height = (int)player.height;
            if (width <= 0 || height <= 0)
            {
                OnError(player, "video has no picture");
                return;
            }

            float scale = Mathf.Min(1f, (float)m_MaxSize / Mathf.Max(width, height));
            width = Mathf.Max(4, Mathf.RoundToInt(width * scale));
            height = Mathf.Max(4, Mathf.RoundToInt(height * scale));

            m_Target = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                name = $"LedScreens_Video_{Name}",
                useMipMap = true,
                autoGenerateMips = true,
                filterMode = FilterMode.Trilinear,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 4,
            };
            m_Target.Create();
            player.targetTexture = m_Target;
            player.Play();
            Mod.log.Info($"Loaded video '{Name}': {player.width}x{player.height} shown at {width}x{height}, {player.length:0.0}s");
        }

        private void OnError(VideoPlayer player, string message)
        {
            Failed = true;
            Mod.log.Error($"Video '{Name}' failed: {message}");
        }
    }
}
