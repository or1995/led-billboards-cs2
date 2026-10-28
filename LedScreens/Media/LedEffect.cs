using System;
using UnityEngine;

namespace LedScreens.Media
{
    public enum LedEffectMode
    {
        /// <summary>Smooth image.</summary>
        Off,

        /// <summary>Reduced to the LED resolution and shown as sharp square pixels.</summary>
        Pixelated,

        /// <summary>Every LED is a round dot with dark gaps, like a real LED panel up close.</summary>
        LedDots,
    }

    /// <summary>
    /// Bakes the LED look into media textures. The game build only has the plain unlit shader,
    /// so instead of a shader effect the pixels themselves are rewritten: reduced to an LED grid
    /// and, for LED dots, each LED expanded into a cell with a round dot. Mipmaps blend the
    /// dots back into a smooth image from a distance.
    /// </summary>
    public static class LedEffect
    {
        /// <summary>Texels per LED in LED-dots mode (even, so texture sizes stay multiples of 4).</summary>
        public const int kCellSize = 6;

        private const float kDotRadius = 0.42f;
        private const float kDotSoftness = 0.12f;
        private const float kGapLevel = 0.04f;

        private static float[] s_Mask;
        private static float s_Fill;

        /// <summary>Brightness of each texel inside an LED cell (0..1), row-major.</summary>
        public static float[] Mask
        {
            get
            {
                if (s_Mask == null)
                    BuildMask();
                return s_Mask;
            }
        }

        /// <summary>Multiplier that keeps the average brightness the same with the dot gaps.</summary>
        public static float BrightnessCompensation(LedEffectMode mode)
        {
            if (mode != LedEffectMode.LedDots)
                return 1f;
            if (s_Mask == null)
                BuildMask();
            return 1f / s_Fill;
        }

        /// <summary>LED grid size for an image: <paramref name="columns"/> LEDs across (never more than the image has pixels), even numbers.</summary>
        public static void GridSize(int width, int height, int columns, out int ledWidth, out int ledHeight)
        {
            ledWidth = Math.Max(4, Math.Min(columns, width) & ~1);
            ledHeight = Math.Max(4, (int)Math.Round((double)height * ledWidth / width) & ~1);
        }

        /// <summary>Applies the effect to every frame of a decoded clip (worker-thread safe).</summary>
        public static DecodedClip Apply(DecodedClip clip, LedEffectMode mode, int columns)
        {
            if (mode == LedEffectMode.Off)
                return clip;

            var result = new DecodedClip { PointFilter = mode == LedEffectMode.Pixelated };
            foreach (var frame in clip.Frames)
            {
                result.Frames.Add(ApplyFrame(frame, clip.Width, clip.Height, mode, columns, out int width, out int height));
                result.Width = width;
                result.Height = height;
            }
            result.Delays.AddRange(clip.Delays);
            return result;
        }

        /// <summary>Applies the effect to one bottom-up, opaque frame.</summary>
        public static Color32[] ApplyFrame(Color32[] pixels, int width, int height, LedEffectMode mode, int columns, out int outWidth, out int outHeight)
        {
            GridSize(width, height, columns, out int ledWidth, out int ledHeight);
            var leds = BoxResample(pixels, width, height, ledWidth, ledHeight);
            if (mode == LedEffectMode.Pixelated)
            {
                outWidth = ledWidth;
                outHeight = ledHeight;
                return leds;
            }

            var mask = Mask;
            outWidth = ledWidth * kCellSize;
            outHeight = ledHeight * kCellSize;
            var result = new Color32[outWidth * outHeight];
            for (int ly = 0; ly < ledHeight; ly++)
            {
                for (int lx = 0; lx < ledWidth; lx++)
                {
                    Color32 led = leds[ly * ledWidth + lx];
                    for (int cy = 0; cy < kCellSize; cy++)
                    {
                        int row = (ly * kCellSize + cy) * outWidth + lx * kCellSize;
                        for (int cx = 0; cx < kCellSize; cx++)
                        {
                            float m = mask[cy * kCellSize + cx];
                            result[row + cx] = new Color32((byte)(led.r * m), (byte)(led.g * m), (byte)(led.b * m), 255);
                        }
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Overlay texture for the GPU (video) path: black, with alpha = how much each texel is
        /// darkened. Drawn with alpha blending over the enlarged LED image.
        /// </summary>
        public static Texture2D CreateMaskOverlay(int ledWidth, int ledHeight)
        {
            var mask = Mask;
            int width = ledWidth * kCellSize, height = ledHeight * kCellSize;
            var pixels = new Color32[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float m = mask[(y % kCellSize) * kCellSize + (x % kCellSize)];
                    pixels[y * width + x] = new Color32(0, 0, 0, (byte)((1f - m) * 255f));
                }
            }

            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                name = "LedScreens_DotMask",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private static void BuildMask()
        {
            var mask = new float[kCellSize * kCellSize];
            float sum = 0f;
            for (int y = 0; y < kCellSize; y++)
            {
                for (int x = 0; x < kCellSize; x++)
                {
                    // Distance from the cell centre, in cell units.
                    float dx = (x + 0.5f) / kCellSize - 0.5f;
                    float dy = (y + 0.5f) / kCellSize - 0.5f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float dot = Mathf.Clamp01((kDotRadius + kDotSoftness * 0.5f - d) / kDotSoftness);
                    float m = kGapLevel + (1f - kGapLevel) * dot;
                    mask[y * kCellSize + x] = m;
                    sum += m;
                }
            }
            s_Fill = sum / mask.Length;
            s_Mask = mask;
        }

        private static Color32[] BoxResample(Color32[] source, int width, int height, int outWidth, int outHeight)
        {
            var result = new Color32[outWidth * outHeight];
            for (int oy = 0; oy < outHeight; oy++)
            {
                int y0 = oy * height / outHeight;
                int y1 = Math.Max(y0 + 1, (oy + 1) * height / outHeight);
                for (int ox = 0; ox < outWidth; ox++)
                {
                    int x0 = ox * width / outWidth;
                    int x1 = Math.Max(x0 + 1, (ox + 1) * width / outWidth);
                    int r = 0, g = 0, b = 0, count = 0;
                    for (int y = y0; y < y1; y++)
                    {
                        int row = y * width;
                        for (int x = x0; x < x1; x++)
                        {
                            Color32 c = source[row + x];
                            r += c.r;
                            g += c.g;
                            b += c.b;
                            count++;
                        }
                    }
                    result[oy * outWidth + ox] = new Color32((byte)(r / count), (byte)(g / count), (byte)(b / count), 255);
                }
            }
            return result;
        }
    }
}
