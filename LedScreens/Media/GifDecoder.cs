using System;
using System.Collections.Generic;
using UnityEngine;

namespace LedScreens.Media
{
    /// <summary>Fully composited, resized frames ready for texture upload (bottom-up rows, opaque).</summary>
    public sealed class DecodedClip
    {
        public int Width;
        public int Height;
        public readonly List<Color32[]> Frames = new List<Color32[]>();
        public readonly List<float> Delays = new List<float>();
    }

    /// <summary>
    /// Minimal GIF87a/89a decoder. Handles global/local palettes, transparency,
    /// disposal methods, interlacing and frame delays. No System.Drawing dependency.
    /// Thread-safe: only touches its own data, so it can run on a worker thread.
    /// </summary>
    public static class GifDecoder
    {
        // Browsers clamp tiny delays to 100 ms, so most GIFs are authored for that behaviour.
        private const float kMinDelay = 0.02f;
        private const float kDefaultDelay = 0.1f;

        public static DecodedClip Decode(byte[] data, int maxSize, int maxFrames)
        {
            var reader = new Reader(data);

            string signature = reader.ReadAscii(6);
            if (signature != "GIF87a" && signature != "GIF89a")
                throw new FormatException("Not a GIF file");

            int width = reader.ReadUInt16();
            int height = reader.ReadUInt16();
            int packed = reader.ReadByte();
            reader.ReadByte(); // background color index; browsers draw transparent instead
            reader.ReadByte(); // pixel aspect ratio

            if (width <= 0 || height <= 0)
                throw new FormatException("Invalid GIF dimensions");

            Color32[] globalPalette = null;
            if ((packed & 0x80) != 0)
                globalPalette = reader.ReadPalette(2 << (packed & 7));

            ComputeOutputSize(width, height, maxSize, out int outWidth, out int outHeight);
            var clip = new DecodedClip { Width = outWidth, Height = outHeight };

            var canvas = new Color32[width * height];
            Color32[] restoreCanvas = null;

            // Graphic Control Extension state, applies to the next image only.
            int disposal = 0;
            float delay = kDefaultDelay;
            int transparentIndex = -1;

            // Disposal of the previously drawn frame, applied before drawing the next one.
            int previousDisposal = 0;
            int prevLeft = 0, prevTop = 0, prevWidth = 0, prevHeight = 0;

            byte[] indices = null;

            try
            {
                while (clip.Frames.Count < maxFrames && !reader.EndOfData)
                {
                    int block = reader.ReadByte();
                    if (block == 0x3B) // trailer
                        break;

                    if (block == 0x21) // extension
                    {
                        int label = reader.ReadByte();
                        if (label == 0xF9)
                        {
                            int size = reader.ReadByte();
                            int gcePacked = reader.ReadByte();
                            int delayCs = reader.ReadUInt16();
                            int transIndex = reader.ReadByte();
                            reader.Skip(size - 4);
                            reader.SkipSubBlocks();

                            disposal = (gcePacked >> 2) & 7;
                            transparentIndex = (gcePacked & 1) != 0 ? transIndex : -1;
                            delay = delayCs < 2 ? kDefaultDelay : Math.Max(kMinDelay, delayCs / 100f);
                        }
                        else
                        {
                            reader.SkipSubBlocks();
                        }
                        continue;
                    }

                    if (block != 0x2C) // anything else means corrupt data
                        break;

                    int left = reader.ReadUInt16();
                    int top = reader.ReadUInt16();
                    int frameWidth = reader.ReadUInt16();
                    int frameHeight = reader.ReadUInt16();
                    int imagePacked = reader.ReadByte();

                    Color32[] palette = globalPalette;
                    if ((imagePacked & 0x80) != 0)
                        palette = reader.ReadPalette(2 << (imagePacked & 7));
                    bool interlaced = (imagePacked & 0x40) != 0;

                    int minCodeSize = reader.ReadByte();
                    byte[] lzwData = reader.ReadSubBlocks(out int lzwLength);

                    int pixelCount = frameWidth * frameHeight;
                    if (indices == null || indices.Length < pixelCount)
                        indices = new byte[pixelCount];
                    DecodeLzw(lzwData, lzwLength, minCodeSize, indices, pixelCount);

                    // Dispose the previous frame.
                    if (previousDisposal == 2)
                        ClearRect(canvas, width, height, prevLeft, prevTop, prevWidth, prevHeight);
                    else if (previousDisposal == 3 && restoreCanvas != null)
                        Array.Copy(restoreCanvas, canvas, canvas.Length);

                    if (disposal == 3)
                    {
                        if (restoreCanvas == null)
                            restoreCanvas = new Color32[canvas.Length];
                        Array.Copy(canvas, restoreCanvas, canvas.Length);
                    }

                    if (palette != null)
                        DrawFrame(canvas, width, height, indices, palette, transparentIndex, left, top, frameWidth, frameHeight, interlaced);

                    clip.Frames.Add(Resample(canvas, width, height, outWidth, outHeight));
                    clip.Delays.Add(delay);

                    previousDisposal = disposal;
                    prevLeft = left;
                    prevTop = top;
                    prevWidth = frameWidth;
                    prevHeight = frameHeight;

                    disposal = 0;
                    delay = kDefaultDelay;
                    transparentIndex = -1;
                }
            }
            catch (IndexOutOfRangeException)
            {
                // Truncated file: keep the frames decoded so far.
                if (clip.Frames.Count == 0)
                    throw new FormatException("Truncated GIF file");
            }

            if (clip.Frames.Count == 0)
                throw new FormatException("GIF contains no frames");
            return clip;
        }

        /// <summary>Fits the image into maxSize and rounds both sides to a multiple of 4 (needed for DXT compression).</summary>
        private static void ComputeOutputSize(int width, int height, int maxSize, out int outWidth, out int outHeight)
        {
            float scale = Math.Min(1f, (float)maxSize / Math.Max(width, height));
            outWidth = Math.Max(4, (int)Math.Round(width * scale / 4f) * 4);
            outHeight = Math.Max(4, (int)Math.Round(height * scale / 4f) * 4);
        }

        private static void ClearRect(Color32[] canvas, int width, int height, int left, int top, int rectWidth, int rectHeight)
        {
            int x1 = Math.Min(width, left + rectWidth);
            int y1 = Math.Min(height, top + rectHeight);
            for (int y = top; y < y1; y++)
            {
                for (int x = left; x < x1; x++)
                    canvas[y * width + x] = default;
            }
        }

        private static void DrawFrame(Color32[] canvas, int width, int height, byte[] indices, Color32[] palette, int transparentIndex,
            int left, int top, int frameWidth, int frameHeight, bool interlaced)
        {
            int pass = 0, step = interlaced ? 8 : 1, row = 0;
            for (int i = 0; i < frameHeight; i++)
            {
                int y = top + row;
                if (y < height)
                {
                    int src = i * frameWidth;
                    int dst = y * width;
                    for (int x = 0; x < frameWidth; x++)
                    {
                        int canvasX = left + x;
                        if (canvasX >= width)
                            break;
                        int index = indices[src + x];
                        if (index == transparentIndex || index >= palette.Length)
                            continue;
                        canvas[dst + canvasX] = palette[index];
                    }
                }

                row += step;
                if (interlaced && row >= frameHeight)
                {
                    // Interlace passes: rows 0,8,16.. then 4,12.. then 2,6.. then 1,3..
                    do
                    {
                        pass++;
                        switch (pass)
                        {
                            case 1: row = 4; step = 8; break;
                            case 2: row = 2; step = 4; break;
                            case 3: row = 1; step = 2; break;
                            default: row = frameHeight; break;
                        }
                    } while (row >= frameHeight && pass < 4);
                }
            }
        }

        /// <summary>Box-filter resample to the output size; flips rows (Unity textures are bottom-up) and drops alpha (transparent = black).</summary>
        private static Color32[] Resample(Color32[] canvas, int width, int height, int outWidth, int outHeight)
        {
            var result = new Color32[outWidth * outHeight];
            for (int oy = 0; oy < outHeight; oy++)
            {
                int y0 = oy * height / outHeight;
                int y1 = Math.Max(y0 + 1, (oy + 1) * height / outHeight);
                int dstRow = (outHeight - 1 - oy) * outWidth;
                for (int ox = 0; ox < outWidth; ox++)
                {
                    int x0 = ox * width / outWidth;
                    int x1 = Math.Max(x0 + 1, (ox + 1) * width / outWidth);
                    int r = 0, g = 0, b = 0, count = 0;
                    for (int y = y0; y < y1; y++)
                    {
                        int srcRow = y * width;
                        for (int x = x0; x < x1; x++)
                        {
                            Color32 c = canvas[srcRow + x];
                            // Premultiply so transparent pixels count as black.
                            r += c.r * c.a;
                            g += c.g * c.a;
                            b += c.b * c.a;
                            count++;
                        }
                    }
                    int div = count * 255;
                    result[dstRow + ox] = new Color32((byte)(r / div), (byte)(g / div), (byte)(b / div), 255);
                }
            }
            return result;
        }

        private static void DecodeLzw(byte[] data, int dataLength, int minCodeSize, byte[] output, int pixelCount)
        {
            const int kMaxCodes = 4096;
            if (minCodeSize < 2 || minCodeSize > 11)
                throw new FormatException("Invalid LZW code size");

            int clearCode = 1 << minCodeSize;
            int endCode = clearCode + 1;
            int codeSize = minCodeSize + 1;
            int codeMask = (1 << codeSize) - 1;
            int available = clearCode + 2;
            int oldCode = -1;
            byte first = 0;

            var prefix = new short[kMaxCodes];
            var suffix = new byte[kMaxCodes];
            var stack = new byte[kMaxCodes + 1];
            for (int c = 0; c < clearCode; c++)
                suffix[c] = (byte)c;

            int datum = 0, bits = 0, pos = 0, outPos = 0, top = 0;
            while (outPos < pixelCount)
            {
                if (top == 0)
                {
                    while (bits < codeSize)
                    {
                        if (pos >= dataLength)
                            goto Done;
                        datum |= data[pos++] << bits;
                        bits += 8;
                    }
                    int code = datum & codeMask;
                    datum >>= codeSize;
                    bits -= codeSize;

                    if (code == clearCode)
                    {
                        codeSize = minCodeSize + 1;
                        codeMask = (1 << codeSize) - 1;
                        available = clearCode + 2;
                        oldCode = -1;
                        continue;
                    }
                    if (code == endCode)
                        break;
                    if (oldCode == -1)
                    {
                        if (code >= clearCode)
                            break; // corrupt
                        output[outPos++] = suffix[code];
                        oldCode = code;
                        first = (byte)code;
                        continue;
                    }
                    if (code > available)
                        break; // corrupt

                    int inCode = code;
                    if (code == available)
                    {
                        stack[top++] = first;
                        code = oldCode;
                    }
                    while (code >= clearCode)
                    {
                        stack[top++] = suffix[code];
                        code = prefix[code];
                    }
                    first = suffix[code];
                    stack[top++] = first;

                    if (available < kMaxCodes)
                    {
                        prefix[available] = (short)oldCode;
                        suffix[available] = first;
                        available++;
                        if ((available & codeMask) == 0 && available < kMaxCodes)
                        {
                            codeSize++;
                            codeMask = (1 << codeSize) - 1;
                        }
                    }
                    oldCode = inCode;
                }

                output[outPos++] = stack[--top];
            }

        Done:
            if (outPos < pixelCount)
                Array.Clear(output, outPos, pixelCount - outPos);
        }

        private sealed class Reader
        {
            private readonly byte[] m_Data;
            private int m_Position;

            public Reader(byte[] data)
            {
                m_Data = data;
            }

            public bool EndOfData => m_Position >= m_Data.Length;

            public int ReadByte() => m_Data[m_Position++];

            public int ReadUInt16()
            {
                int value = m_Data[m_Position] | (m_Data[m_Position + 1] << 8);
                m_Position += 2;
                return value;
            }

            public string ReadAscii(int length)
            {
                if (m_Position + length > m_Data.Length)
                    throw new FormatException("Unexpected end of file");
                var chars = new char[length];
                for (int i = 0; i < length; i++)
                    chars[i] = (char)m_Data[m_Position++];
                return new string(chars);
            }

            public void Skip(int count) => m_Position += Math.Max(0, count);

            public Color32[] ReadPalette(int count)
            {
                var palette = new Color32[count];
                for (int i = 0; i < count; i++)
                    palette[i] = new Color32(m_Data[m_Position++], m_Data[m_Position++], m_Data[m_Position++], 255);
                return palette;
            }

            public void SkipSubBlocks()
            {
                int size;
                while ((size = ReadByte()) != 0)
                    m_Position += size;
            }

            public byte[] ReadSubBlocks(out int length)
            {
                // First pass to size the buffer, second to copy.
                int start = m_Position;
                int total = 0, size;
                while ((size = ReadByte()) != 0)
                {
                    total += size;
                    m_Position += size;
                }

                var result = new byte[total];
                m_Position = start;
                int offset = 0;
                while ((size = ReadByte()) != 0)
                {
                    int copy = Math.Min(size, m_Data.Length - m_Position);
                    Buffer.BlockCopy(m_Data, m_Position, result, offset, copy);
                    offset += size;
                    m_Position += size;
                }
                length = total;
                return result;
            }
        }
    }
}
