using System;
using System.Threading.Tasks;
using UnityEngine;

namespace LedScreens.Media
{
    /// <summary>
    /// Prepares decoded frames for the GPU on worker threads: builds the mip chain and, when
    /// compression is on, encodes it as DXT1. The main thread then only copies the bytes into a
    /// texture, which is fast, instead of generating mips and compressing there (Unity's
    /// Texture2D.Compress is main-thread only and took ~70 ms per big frame).
    /// </summary>
    public static class TextureEncoder
    {
        // Leave cores for the game's own worker threads.
        private static readonly ParallelOptions kParallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) };

        /// <summary>Encodes every frame of the clip in place: fills <see cref="DecodedClip.Raw"/> and drops the pixels.</summary>
        public static DecodedClip Encode(DecodedClip clip, bool compress)
        {
            int width = clip.Width, height = clip.Height;
            // D3D needs the top level of a block-compressed texture to be whole 4x4 blocks.
            bool dxt = compress && width % 4 == 0 && height % 4 == 0;
            clip.Format = dxt ? TextureFormat.DXT1 : TextureFormat.RGBA32;
            clip.MipCount = MipCount(width, height);

            var raw = new byte[clip.Frames.Count][];
            Parallel.For(0, raw.Length, kParallel, i =>
            {
                raw[i] = EncodeFrame(clip.Frames[i], width, height, clip.MipCount, dxt);
                BufferPool<Color32>.Return(clip.Frames[i]);
                clip.Frames[i] = null;
            });
            clip.Raw.Clear();
            clip.Raw.AddRange(raw);
            return clip;
        }

        private static int MipCount(int width, int height)
        {
            int count = 1;
            for (int size = Math.Max(width, height); size > 1; size >>= 1)
                count++;
            return count;
        }

        private static byte[] EncodeFrame(Color32[] pixels, int width, int height, int mipCount, bool dxt)
        {
            int total = 0;
            for (int level = 0, w = width, h = height; level < mipCount; level++, w = Math.Max(1, w >> 1), h = Math.Max(1, h >> 1))
                total += dxt ? ((w + 3) / 4) * ((h + 3) / 4) * 8 : w * h * 4;

            // Pooled: the upload hands it back once the texture has its copy.
            var data = BufferPool<byte>.Rent(total);
            int offset = 0, levelWidth = width, levelHeight = height;
            var level0 = pixels;
            for (int level = 0; level < mipCount; level++)
            {
                offset = dxt ? WriteDxt1(pixels, levelWidth, levelHeight, data, offset) : WriteRgba(pixels, data, offset);
                if (level + 1 < mipCount)
                {
                    var next = Downsample(pixels, levelWidth, levelHeight, out levelWidth, out levelHeight);
                    if (pixels != level0)
                        BufferPool<Color32>.Return(pixels);
                    pixels = next;
                }
            }
            if (pixels != level0)
                BufferPool<Color32>.Return(pixels);
            return data;
        }

        private static int WriteRgba(Color32[] pixels, byte[] data, int offset)
        {
            foreach (var c in pixels)
            {
                data[offset++] = c.r;
                data[offset++] = c.g;
                data[offset++] = c.b;
                data[offset++] = c.a;
            }
            return offset;
        }

        /// <summary>Halves the image with a 2x2 box filter (odd edges repeat the last row/column).</summary>
        private static Color32[] Downsample(Color32[] source, int width, int height, out int outWidth, out int outHeight)
        {
            outWidth = Math.Max(1, width >> 1);
            outHeight = Math.Max(1, height >> 1);
            var result = BufferPool<Color32>.Rent(outWidth * outHeight); // every pixel is written below
            for (int y = 0; y < outHeight; y++)
            {
                int y0 = Math.Min(y * 2, height - 1) * width;
                int y1 = Math.Min(y * 2 + 1, height - 1) * width;
                for (int x = 0; x < outWidth; x++)
                {
                    int x0 = Math.Min(x * 2, width - 1);
                    int x1 = Math.Min(x * 2 + 1, width - 1);
                    Color32 a = source[y0 + x0], b = source[y0 + x1], c = source[y1 + x0], d = source[y1 + x1];
                    result[y * outWidth + x] = new Color32(
                        (byte)((a.r + b.r + c.r + d.r + 2) >> 2),
                        (byte)((a.g + b.g + c.g + d.g + 2) >> 2),
                        (byte)((a.b + b.b + c.b + d.b + 2) >> 2),
                        255);
                }
            }
            return result;
        }

        private static int WriteDxt1(Color32[] pixels, int width, int height, byte[] data, int offset)
        {
            var block = new int[16 * 3];
            var palette = new int[4 * 3];
            for (int by = 0; by < height; by += 4)
            {
                for (int bx = 0; bx < width; bx += 4)
                {
                    for (int i = 0; i < 16; i++)
                    {
                        // Small mips are smaller than a block: repeat the edge pixels.
                        int x = Math.Min(bx + (i & 3), width - 1);
                        int y = Math.Min(by + (i >> 2), height - 1);
                        Color32 c = pixels[y * width + x];
                        block[i * 3] = c.r;
                        block[i * 3 + 1] = c.g;
                        block[i * 3 + 2] = c.b;
                    }
                    EncodeBlock(block, palette, data, offset);
                    offset += 8;
                }
            }
            return offset;
        }

        /// <summary>
        /// One 4x4 block: the end colours are the extremes along the block's main colour axis
        /// (found by power iteration), then each pixel takes the nearest of the four palette colours.
        /// </summary>
        private static void EncodeBlock(int[] block, int[] palette, byte[] data, int offset)
        {
            float mr = 0, mg = 0, mb = 0;
            for (int i = 0; i < 16; i++)
            {
                mr += block[i * 3];
                mg += block[i * 3 + 1];
                mb += block[i * 3 + 2];
            }
            mr /= 16f;
            mg /= 16f;
            mb /= 16f;

            float rr = 0, rg = 0, rb = 0, gg = 0, gb = 0, bb = 0;
            for (int i = 0; i < 16; i++)
            {
                float r = block[i * 3] - mr, g = block[i * 3 + 1] - mg, b = block[i * 3 + 2] - mb;
                rr += r * r;
                rg += r * g;
                rb += r * b;
                gg += g * g;
                gb += g * b;
                bb += b * b;
            }

            float ar = 1f, ag = 1f, ab = 1f;
            for (int k = 0; k < 4; k++)
            {
                float nr = rr * ar + rg * ag + rb * ab;
                float ng = rg * ar + gg * ag + gb * ab;
                float nb = rb * ar + gb * ag + bb * ab;
                float length = Math.Max(Math.Abs(nr), Math.Max(Math.Abs(ng), Math.Abs(nb)));
                if (length < 1e-6f)
                    break;
                ar = nr / length;
                ag = ng / length;
                ab = nb / length;
            }

            int minIndex = 0, maxIndex = 0;
            float minDot = float.MaxValue, maxDot = float.MinValue;
            for (int i = 0; i < 16; i++)
            {
                float dot = block[i * 3] * ar + block[i * 3 + 1] * ag + block[i * 3 + 2] * ab;
                if (dot < minDot)
                {
                    minDot = dot;
                    minIndex = i;
                }
                if (dot > maxDot)
                {
                    maxDot = dot;
                    maxIndex = i;
                }
            }

            int c0 = To565(block[maxIndex * 3], block[maxIndex * 3 + 1], block[maxIndex * 3 + 2]);
            int c1 = To565(block[minIndex * 3], block[minIndex * 3 + 1], block[minIndex * 3 + 2]);
            uint indices = 0;
            if (c0 < c1)
            {
                int swap = c0;
                c0 = c1;
                c1 = swap;
            }

            if (c0 != c1)
            {
                // c0 > c1 selects the four-colour mode: c0, c1, 2/3 c0 + 1/3 c1, 1/3 c0 + 2/3 c1.
                From565(c0, palette, 0);
                From565(c1, palette, 3);
                for (int ch = 0; ch < 3; ch++)
                {
                    palette[6 + ch] = (2 * palette[ch] + palette[3 + ch] + 1) / 3;
                    palette[9 + ch] = (palette[ch] + 2 * palette[3 + ch] + 1) / 3;
                }

                for (int i = 0; i < 16; i++)
                {
                    int best = 0, bestDistance = int.MaxValue;
                    for (int p = 0; p < 4; p++)
                    {
                        int dr = block[i * 3] - palette[p * 3];
                        int dg = block[i * 3 + 1] - palette[p * 3 + 1];
                        int db = block[i * 3 + 2] - palette[p * 3 + 2];
                        int distance = dr * dr + dg * dg + db * db;
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            best = p;
                        }
                    }
                    indices |= (uint)best << (i * 2);
                }
            }

            data[offset] = (byte)c0;
            data[offset + 1] = (byte)(c0 >> 8);
            data[offset + 2] = (byte)c1;
            data[offset + 3] = (byte)(c1 >> 8);
            data[offset + 4] = (byte)indices;
            data[offset + 5] = (byte)(indices >> 8);
            data[offset + 6] = (byte)(indices >> 16);
            data[offset + 7] = (byte)(indices >> 24);
        }

        private static int To565(int r, int g, int b) => ((r * 31 + 127) / 255 << 11) | ((g * 63 + 127) / 255 << 5) | ((b * 31 + 127) / 255);

        private static void From565(int c, int[] palette, int offset)
        {
            int r = (c >> 11) & 31, g = (c >> 5) & 63, b = c & 31;
            palette[offset] = (r << 3) | (r >> 2);
            palette[offset + 1] = (g << 2) | (g >> 4);
            palette[offset + 2] = (b << 3) | (b >> 2);
        }
    }
}
