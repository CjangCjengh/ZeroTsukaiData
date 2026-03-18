using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace Tm2Converter
{
    /// <summary>
    /// TIM2 (TM2) to PNG converter for PS2 texture images.
    ///
    /// TIM2 file layout:
    ///   [FileHeader 16 bytes] [ImageHeader + PixelData + CLUT] × N
    ///
    /// FileHeader (16 bytes):
    ///   [0-3]  Magic "TIM2" (54 49 4D 32)
    ///   [4-5]  Version (usually 0x04)
    ///   [6-7]  Number of images (usually 1)
    ///   [8-15] Reserved (padding, usually 0)
    ///
    /// ImageHeader (variable size, typically 0x30 = 48 bytes):
    ///   [0x00] uint32  TotalSize (header + pixel + clut, excludes file header)
    ///   [0x04] uint32  ClutSize
    ///   [0x08] uint32  ImageSize (pixel data)
    ///   [0x0C] uint16  HeaderSize (image header only)
    ///   [0x0E] uint16  ClutColors
    ///   [0x10] byte    PixelFormat (GS register related)
    ///   [0x11] byte    MipMapCount
    ///   [0x12] byte    ClutFormat (0=none, 1=16bit, 2=24bit, 3=32bit)
    ///   [0x13] byte    Depth (1=16bpp, 2=24bpp, 3=32bpp, 4=4bit idx, 5=8bit idx)
    ///   [0x14] uint16  Width
    ///   [0x16] uint16  Height
    ///   [0x18-...]     GS registers / extended data
    ///
    /// Pixel formats in Zero no Tsukaima:
    ///   depth=5: 8-bit indexed, 256-color CLUT (252 files, most common)
    ///   depth=4: 4-bit indexed, 16-color CLUT (78 files)
    ///   depth=2: 24bpp direct RGB (2 files)
    ///   depth=3: 32bpp direct RGBA (1 file)
    ///   depth=0: Invalid/empty (1 file, skipped)
    ///
    /// PS2 CLUT swizzle (8-bit indexed only):
    ///   For every group of 32 palette entries, indices 8-15 and 16-23 are swapped.
    ///   This is a hardware optimization for the PS2 GS (Graphics Synthesizer).
    ///
    /// Alpha mapping:
    ///   PS2 uses 0-128 alpha range (0x80 = fully opaque).
    ///   Converted to standard 0-255 range: alpha = min(raw_alpha * 2, 255).
    /// </summary>
    internal class Tm2Converter
    {
        #region Public API

        /// <summary>
        /// Convert a single TM2 file to PNG.
        /// Returns true on success, false on skip/error.
        /// </summary>
        public static bool ConvertFile(string tm2Path, string pngPath)
        {
            byte[] data = File.ReadAllBytes(tm2Path);

            // Validate magic
            if (data.Length < 0x30 ||
                data[0] != 0x54 || data[1] != 0x49 || data[2] != 0x4D || data[3] != 0x32)
            {
                Console.WriteLine("  Not a valid TIM2 file: " + Path.GetFileName(tm2Path));
                return false;
            }

            int nImages = BitConverter.ToUInt16(data, 6);
            if (nImages < 1)
            {
                Console.WriteLine("  No images in file: " + Path.GetFileName(tm2Path));
                return false;
            }

            // Parse image header (first image, at offset 0x10)
            int imgHeaderOffset = 0x10;
            uint clutSize = BitConverter.ToUInt32(data, imgHeaderOffset + 0x04);
            uint imgSize = BitConverter.ToUInt32(data, imgHeaderOffset + 0x08);
            ushort headerSize = BitConverter.ToUInt16(data, imgHeaderOffset + 0x0C);
            ushort clutColors = BitConverter.ToUInt16(data, imgHeaderOffset + 0x0E);
            byte clutFormat = data[imgHeaderOffset + 0x12];
            byte depth = data[imgHeaderOffset + 0x13];
            ushort width = BitConverter.ToUInt16(data, imgHeaderOffset + 0x14);
            ushort height = BitConverter.ToUInt16(data, imgHeaderOffset + 0x16);

            if (width == 0 || height == 0)
            {
                Console.WriteLine("  Empty image (0x0): " + Path.GetFileName(tm2Path));
                return false;
            }

            // Pixel data starts after image header
            int pixelOffset = imgHeaderOffset + headerSize;
            // CLUT data starts after pixel data
            int clutOffset = pixelOffset + (int)imgSize;

            Bitmap bmp;

            switch (depth)
            {
                case 5: // 8-bit indexed
                    bmp = Decode8BitIndexed(data, pixelOffset, clutOffset, clutFormat,
                        clutColors, width, height);
                    break;

                case 4: // 4-bit indexed
                    bmp = Decode4BitIndexed(data, pixelOffset, clutOffset, clutFormat,
                        clutColors, width, height);
                    break;

                case 2: // 24bpp direct
                    bmp = Decode24Bpp(data, pixelOffset, width, height);
                    break;

                case 3: // 32bpp direct
                    bmp = Decode32Bpp(data, pixelOffset, width, height);
                    break;

                default:
                    Console.WriteLine("  Unsupported depth=" + depth + ": " +
                        Path.GetFileName(tm2Path));
                    return false;
            }

            if (bmp == null) return false;

            // Ensure output directory exists
            string outDir = Path.GetDirectoryName(pngPath);
            if (!string.IsNullOrEmpty(outDir))
                Directory.CreateDirectory(outDir);

            bmp.Save(pngPath, ImageFormat.Png);
            bmp.Dispose();
            return true;
        }

        /// <summary>
        /// Batch convert all TM2 files in a directory to PNG.
        /// </summary>
        public static void ConvertDirectory(string inputDir, string outputDir)
        {
            string[] files = Directory.GetFiles(inputDir, "*.tm2",
                SearchOption.TopDirectoryOnly);

            if (files.Length == 0)
            {
                // Try uppercase extension
                files = Directory.GetFiles(inputDir, "*.TM2",
                    SearchOption.TopDirectoryOnly);
            }

            if (files.Length == 0)
            {
                Console.WriteLine("No TM2 files found in: " + inputDir);
                return;
            }

            Directory.CreateDirectory(outputDir);
            Console.WriteLine("Converting " + files.Length + " TM2 files...");
            Console.WriteLine("  Input:  " + inputDir);
            Console.WriteLine("  Output: " + outputDir);
            Console.WriteLine();

            int success = 0;
            int skipped = 0;

            Array.Sort(files);
            for (int i = 0; i < files.Length; i++)
            {
                string tm2Path = files[i];
                string baseName = Path.GetFileNameWithoutExtension(tm2Path);
                string pngPath = Path.Combine(outputDir, baseName + ".png");

                if (ConvertFile(tm2Path, pngPath))
                {
                    success++;
                }
                else
                {
                    skipped++;
                }

                if ((i + 1) % 50 == 0 || i == files.Length - 1)
                    Console.WriteLine(string.Format("  [{0}/{1}] processed",
                        i + 1, files.Length));
            }

            Console.WriteLine();
            Console.WriteLine("Done!");
            Console.WriteLine("  Converted: " + success);
            Console.WriteLine("  Skipped:   " + skipped);
            Console.WriteLine("  Output:    " + outputDir);
        }

        #endregion

        #region Pixel Decoders

        /// <summary>
        /// Decode 8-bit indexed image with CLUT.
        /// Applies PS2 CLUT unswizzle for 256-color palettes.
        /// </summary>
        static Bitmap Decode8BitIndexed(byte[] data, int pixelOffset, int clutOffset,
            byte clutFormat, int clutColors, int width, int height)
        {
            Color[] palette = ReadClut(data, clutOffset, clutFormat, clutColors);

            // PS2 8-bit CLUT unswizzle: swap indices 8-15 with 16-23 in each 32-entry block
            if (clutColors == 256)
                UnswizzleClut8Bit(palette);

            Bitmap bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            BitmapData bmpData = bmp.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

            byte[] pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int srcIdx = pixelOffset + y * width + x;
                    if (srcIdx >= data.Length) break;

                    int palIdx = data[srcIdx];
                    if (palIdx >= palette.Length) palIdx = 0;

                    Color c = palette[palIdx];
                    int dstIdx = (y * width + x) * 4;
                    pixels[dstIdx + 0] = c.B;     // BGRA format for GDI+
                    pixels[dstIdx + 1] = c.G;
                    pixels[dstIdx + 2] = c.R;
                    pixels[dstIdx + 3] = c.A;
                }
            }

            Marshal.Copy(pixels, 0, bmpData.Scan0, pixels.Length);
            bmp.UnlockBits(bmpData);
            return bmp;
        }

        /// <summary>
        /// Decode 4-bit indexed image with CLUT.
        /// Each byte stores 2 pixels: low nibble first, high nibble second.
        /// </summary>
        static Bitmap Decode4BitIndexed(byte[] data, int pixelOffset, int clutOffset,
            byte clutFormat, int clutColors, int width, int height)
        {
            Color[] palette = ReadClut(data, clutOffset, clutFormat, clutColors);

            Bitmap bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            BitmapData bmpData = bmp.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

            byte[] pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x += 2)
                {
                    int srcIdx = pixelOffset + (y * width + x) / 2;
                    if (srcIdx >= data.Length) break;

                    byte b = data[srcIdx];
                    int loIdx = b & 0x0F;
                    int hiIdx = (b >> 4) & 0x0F;

                    // Low nibble = first pixel
                    if (loIdx < palette.Length)
                    {
                        Color c = palette[loIdx];
                        int dstIdx = (y * width + x) * 4;
                        pixels[dstIdx + 0] = c.B;
                        pixels[dstIdx + 1] = c.G;
                        pixels[dstIdx + 2] = c.R;
                        pixels[dstIdx + 3] = c.A;
                    }

                    // High nibble = second pixel
                    if (x + 1 < width && hiIdx < palette.Length)
                    {
                        Color c = palette[hiIdx];
                        int dstIdx = (y * width + (x + 1)) * 4;
                        pixels[dstIdx + 0] = c.B;
                        pixels[dstIdx + 1] = c.G;
                        pixels[dstIdx + 2] = c.R;
                        pixels[dstIdx + 3] = c.A;
                    }
                }
            }

            Marshal.Copy(pixels, 0, bmpData.Scan0, pixels.Length);
            bmp.UnlockBits(bmpData);
            return bmp;
        }

        /// <summary>
        /// Decode 24bpp direct color image (RGB, no alpha).
        /// </summary>
        static Bitmap Decode24Bpp(byte[] data, int pixelOffset, int width, int height)
        {
            Bitmap bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            BitmapData bmpData = bmp.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

            byte[] pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int srcIdx = pixelOffset + (y * width + x) * 3;
                    if (srcIdx + 2 >= data.Length) break;

                    int dstIdx = (y * width + x) * 4;
                    pixels[dstIdx + 0] = data[srcIdx + 2]; // B
                    pixels[dstIdx + 1] = data[srcIdx + 1]; // G
                    pixels[dstIdx + 2] = data[srcIdx + 0]; // R
                    pixels[dstIdx + 3] = 255;               // A (fully opaque)
                }
            }

            Marshal.Copy(pixels, 0, bmpData.Scan0, pixels.Length);
            bmp.UnlockBits(bmpData);
            return bmp;
        }

        /// <summary>
        /// Decode 32bpp direct color image (RGBA).
        /// PS2 alpha range 0-128 is scaled to 0-255.
        /// </summary>
        static Bitmap Decode32Bpp(byte[] data, int pixelOffset, int width, int height)
        {
            Bitmap bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            BitmapData bmpData = bmp.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

            byte[] pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int srcIdx = pixelOffset + (y * width + x) * 4;
                    if (srcIdx + 3 >= data.Length) break;

                    byte r = data[srcIdx + 0];
                    byte g = data[srcIdx + 1];
                    byte b = data[srcIdx + 2];
                    byte a = data[srcIdx + 3];
                    // PS2 alpha: 0x80 = fully opaque, scale to 0-255
                    a = (byte)Math.Min(a * 2, 255);

                    int dstIdx = (y * width + x) * 4;
                    pixels[dstIdx + 0] = b;
                    pixels[dstIdx + 1] = g;
                    pixels[dstIdx + 2] = r;
                    pixels[dstIdx + 3] = a;
                }
            }

            Marshal.Copy(pixels, 0, bmpData.Scan0, pixels.Length);
            bmp.UnlockBits(bmpData);
            return bmp;
        }

        #endregion

        #region CLUT Helpers

        /// <summary>
        /// Read CLUT (Color Lookup Table) entries.
        /// Supports 32-bit RGBA and 24-bit RGB CLUT formats.
        /// </summary>
        static Color[] ReadClut(byte[] data, int clutOffset, byte clutFormat, int clutColors)
        {
            Color[] palette = new Color[clutColors];

            for (int i = 0; i < clutColors; i++)
            {
                if (clutFormat == 3) // 32-bit CLUT (RGBA)
                {
                    int off = clutOffset + i * 4;
                    if (off + 3 >= data.Length) break;

                    byte r = data[off + 0];
                    byte g = data[off + 1];
                    byte b = data[off + 2];
                    byte a = data[off + 3];
                    // PS2 alpha: 0-128 range
                    a = (byte)Math.Min(a * 2, 255);
                    palette[i] = Color.FromArgb(a, r, g, b);
                }
                else if (clutFormat == 2) // 24-bit CLUT (RGB, no alpha)
                {
                    int off = clutOffset + i * 3;
                    if (off + 2 >= data.Length) break;

                    byte r = data[off + 0];
                    byte g = data[off + 1];
                    byte b = data[off + 2];
                    palette[i] = Color.FromArgb(255, r, g, b);
                }
                else if (clutFormat == 1) // 16-bit CLUT (ABGR1555)
                {
                    int off = clutOffset + i * 2;
                    if (off + 1 >= data.Length) break;

                    ushort val = BitConverter.ToUInt16(data, off);
                    byte r = (byte)((val & 0x001F) << 3);
                    byte g = (byte)(((val >> 5) & 0x001F) << 3);
                    byte b = (byte)(((val >> 10) & 0x001F) << 3);
                    byte a = (byte)((val & 0x8000) != 0 ? 255 : 0);
                    palette[i] = Color.FromArgb(a, r, g, b);
                }
            }

            return palette;
        }

        /// <summary>
        /// PS2 8-bit CLUT unswizzle.
        /// For every group of 32 entries, swap indices 8-15 with 16-23.
        /// This reverses the GS hardware swizzle pattern.
        /// </summary>
        static void UnswizzleClut8Bit(Color[] palette)
        {
            for (int block = 0; block < 256 && block < palette.Length; block += 32)
            {
                for (int i = 0; i < 8; i++)
                {
                    int idxA = block + 8 + i;
                    int idxB = block + 16 + i;
                    if (idxA >= palette.Length || idxB >= palette.Length) break;

                    Color tmp = palette[idxA];
                    palette[idxA] = palette[idxB];
                    palette[idxB] = tmp;
                }
            }
        }

        #endregion
    }
}
