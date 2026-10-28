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
        // A video that shows no new frame for this long is considered stuck.
        private const float kStallSeconds = 1.5f;

        private readonly GameObject m_Host;
        private readonly VideoPlayer m_Player;
        private readonly int m_MaxSize;
        private readonly LedEffectMode m_Effect;
        private readonly int m_Columns;
        private RenderTexture m_Target;

        // LED effect (GPU path): the frame reduced to the LED grid, and for LED dots the
        // enlarged grid with the dot mask laid over it.
        private RenderTexture m_Leds;
        private RenderTexture m_Dots;
        private Texture2D m_DotMask;
        private static Material s_MaskMaterial;
        private static bool s_MaskMaterialMissing;
        private long m_LastFrame = -1;
        private float m_LastProgressTime;
        private int m_Recoveries;
        private bool m_HasFrame;

        public VideoMedia(string name, string path, int maxSize, LedEffectMode effect, int columns)
        {
            Name = name;
            m_MaxSize = maxSize;
            m_Effect = effect;
            m_Columns = columns;

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

        public float BrightnessScale => m_Dots != null ? LedEffect.BrightnessCompensation(LedEffectMode.LedDots) : 1f;

        /// <summary>Keeps showing the last frame through short stalls and loop restarts.</summary>
        public Texture GetFrame(double time) => m_HasFrame ? (m_Dots ?? m_Leds ?? m_Target) : null;

        /// <summary>Call once per frame: refreshes mipmaps for new frames and unsticks stalled playback.</summary>
        public void Update(float now)
        {
            if (m_Target == null || Failed)
                return;

            long frame = m_Player.frame;
            if (frame != m_LastFrame)
            {
                m_LastFrame = frame;
                m_LastProgressTime = now;
                m_Recoveries = 0;
                if (frame >= 0)
                {
                    m_HasFrame = true;
                    // The video player writes only the full-size image. Distant screens sample the
                    // smaller mip levels, which would otherwise keep an old frame and look frozen.
                    m_Target.GenerateMips();
                    ApplyLedEffect();
                }
                return;
            }

            if (now - m_LastProgressTime < kStallSeconds)
                return;

            // Stuck: H.264 files with B-frames or odd timestamps can stall Unity's Windows
            // decoder, often at the loop point. Nudge forward first, restart if that doesn't help.
            m_LastProgressTime = now;
            m_Recoveries++;
            if (m_Recoveries == 1)
            {
                double target = m_Player.time + 0.5;
                if (m_Player.length > 0 && target >= m_Player.length - 0.25)
                    target = 0;
                m_Player.time = target;
                m_Player.Play();
                Mod.log.Info($"Video '{Name}' stalled at {m_Player.time:0.0}s, skipping ahead");
            }
            else
            {
                m_Player.Stop();
                m_Player.Play(); // prepares again; OnPrepared reuses the render texture
                Mod.log.Info($"Video '{Name}' still stalled, restarting it");
                if (m_Recoveries == 4)
                    Mod.log.Warn($"Video '{Name}' keeps stalling. Re-encoding it as H.264 baseline usually fixes this (see the how-to guide).");
            }
        }

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
            ReleaseLedEffect();
        }

        private void CreateLedEffect(int width, int height)
        {
            if (m_Effect == LedEffectMode.Off)
                return;

            LedEffect.GridSize(width, height, m_Columns, out int ledWidth, out int ledHeight);
            m_Leds = CreateTarget($"LedScreens_VideoLeds_{Name}", ledWidth, ledHeight, FilterMode.Point);
            if (m_Effect != LedEffectMode.LedDots)
                return;

            // The dot mask is drawn with the built-in sprite shader's alpha blending. If the build
            // doesn't have it, videos fall back to plain pixelated.
            if (s_MaskMaterial == null && !s_MaskMaterialMissing)
            {
                var shader = Shader.Find("Sprites/Default") ?? Shader.Find("UI/Default");
                if (shader != null)
                    s_MaskMaterial = new Material(shader) { name = "LedScreens_DotMask" };
                else
                {
                    s_MaskMaterialMissing = true;
                    Mod.log.Warn("No sprite shader for the LED dot mask; videos use the pixelated effect instead");
                }
            }
            if (s_MaskMaterial == null)
                return;

            m_Dots = CreateTarget($"LedScreens_VideoDots_{Name}", ledWidth * LedEffect.kCellSize, ledHeight * LedEffect.kCellSize, FilterMode.Trilinear);
            m_DotMask = LedEffect.CreateMaskOverlay(ledWidth, ledHeight);
        }

        private void ApplyLedEffect()
        {
            if (m_Leds == null)
                return;

            // Shrink the frame to the LED grid (the source's bilinear filter averages it).
            Graphics.Blit(m_Target, m_Leds);
            if (m_Dots == null)
            {
                m_Leds.GenerateMips();
                return;
            }

            // Enlarge with sharp pixels (m_Leds is point-filtered), then darken the gaps.
            Graphics.Blit(m_Leds, m_Dots);
            Graphics.Blit(m_DotMask, m_Dots, s_MaskMaterial);
            m_Dots.GenerateMips();
        }

        private void ReleaseLedEffect()
        {
            foreach (var target in new[] { m_Leds, m_Dots })
            {
                if (target != null)
                {
                    target.Release();
                    Object.Destroy(target);
                }
            }
            m_Leds = null;
            m_Dots = null;
            if (m_DotMask != null)
            {
                Object.Destroy(m_DotMask);
                m_DotMask = null;
            }
        }

        private static RenderTexture CreateTarget(string name, int width, int height, FilterMode filter)
        {
            var target = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                name = name,
                useMipMap = true,
                autoGenerateMips = false,
                filterMode = filter,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 4,
            };
            target.Create();
            return target;
        }

        private void OnPrepared(VideoPlayer player)
        {
            // Also called again after a restart: keep the existing texture.
            if (m_Target != null)
            {
                player.targetTexture = m_Target;
                player.Play();
                return;
            }

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
                autoGenerateMips = false, // regenerated in Update when a new frame arrives
                filterMode = FilterMode.Trilinear,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 4,
            };
            m_Target.Create();
            CreateLedEffect(width, height);
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
