using UnityEngine;

namespace LedScreens.Media
{
    /// <summary>Procedural animated test card, so the mod works before any GIF is added.</summary>
    public static class TestPattern
    {
        private const int kWidth = 256;
        private const int kHeight = 128;
        private const int kFrames = 48;
        private const float kDelay = 1f / 24f;

        private static readonly Color32[] kBars =
        {
            new Color32(235, 235, 235, 255),
            new Color32(235, 235, 16, 255),
            new Color32(16, 235, 235, 255),
            new Color32(16, 235, 16, 255),
            new Color32(235, 16, 235, 255),
            new Color32(235, 16, 16, 255),
            new Color32(16, 16, 235, 255),
        };

        public static DecodedClip Generate()
        {
            var clip = new DecodedClip { Width = kWidth, Height = kHeight };
            for (int f = 0; f < kFrames; f++)
            {
                var pixels = new Color32[kWidth * kHeight];
                float phase = (float)f / kFrames;
                int sweepX = (int)(phase * kWidth);

                for (int y = 0; y < kHeight; y++)
                {
                    // Rows are bottom-up: y = 0 is the bottom of the screen.
                    for (int x = 0; x < kWidth; x++)
                    {
                        Color32 c;
                        if (y >= kHeight / 3)
                        {
                            c = kBars[x * kBars.Length / kWidth];
                        }
                        else
                        {
                            // Bottom third: scrolling diagonal stripes.
                            int stripe = ((x + y + f * 4) / 8) & 1;
                            byte v = (byte)(stripe == 0 ? 20 : 200);
                            c = new Color32(v, v, v, 255);
                        }

                        // Moving sweep line so animation is obvious.
                        if (Mathf.Abs(x - sweepX) < 3)
                            c = new Color32(255, 255, 255, 255);

                        pixels[y * kWidth + x] = c;
                    }
                }

                clip.Frames.Add(pixels);
                clip.Delays.Add(kDelay);
            }
            return clip;
        }
    }
}
