/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/. 
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework.Graphics;
using System.IO;
using Microsoft.Xna.Framework;
using System.Runtime.InteropServices;
using System.Drawing.Imaging;

namespace FSO.Files
{
    public class ImageLoader
    {
        public static bool UseSoftLoad = true;
        public static int PremultiplyPNG = 0;
		public Color[] ColorData;
  		public readonly byte[] ByteData;


        public static HashSet<uint> MASK_COLORS = new HashSet<uint>{
            new Microsoft.Xna.Framework.Color(0xFF, 0x00, 0xFF, 0xFF).PackedValue,
            new Microsoft.Xna.Framework.Color(0xFE, 0x02, 0xFE, 0xFF).PackedValue,
            new Microsoft.Xna.Framework.Color(0xFF, 0x01, 0xFF, 0xFF).PackedValue
        };

        public Texture2D GetTexture(GraphicsDevice gd, int width, int height)
	{
    	if (ColorData == null && ByteData == null)
    {
        return null;
    }

    var tex = new Texture2D(gd, width, height);
    if (ColorData != null)
    {
        tex.SetData(ColorData);
    }
    else
    {
        tex.SetData(ByteData);
    }

    return tex;
}

        // Pure-managed BMP reader. Handles 1/4/8/24/32 bpp BI_RGB and 32bpp BI_BITFIELDS.
        // Returns bytes in RGBA order (matches MonoGame Texture2D.SetData<byte[]> default).
        // TSO UI BMPs are mostly 8bpp paletted with magenta as the transparency-mask color —
        // masking happens in ManualTextureMaskSingleThreaded after the decode.
        public static Tuple<byte[], int, int> BitmapReader(Stream str)
        {
            using (var br = new BinaryReader(str, Encoding.Default, leaveOpen: true))
            {
                // --- BITMAPFILEHEADER (14 bytes) ---
                ushort sig = br.ReadUInt16();
                if (sig != 0x4D42) throw new InvalidDataException("not a BMP");
                br.ReadUInt32(); // fileSize
                br.ReadUInt32(); // reserved
                uint pixelOffset = br.ReadUInt32();

                // --- BITMAPINFOHEADER (at least 40 bytes; V4/V5 larger) ---
                uint dibSize = br.ReadUInt32();
                int width = br.ReadInt32();
                int height = br.ReadInt32();
                ushort planes = br.ReadUInt16();
                ushort bpp = br.ReadUInt16();
                uint compression = br.ReadUInt32(); // 0=BI_RGB, 3=BI_BITFIELDS
                br.ReadUInt32(); // imageSize
                br.ReadInt32();  // xppm
                br.ReadInt32();  // yppm
                uint paletteCount = br.ReadUInt32();
                br.ReadUInt32(); // importantColors

                uint rMask = 0x00FF0000, gMask = 0x0000FF00, bMask = 0x000000FF, aMask = 0xFF000000;
                if (compression == 3)
                {
                    rMask = br.ReadUInt32();
                    gMask = br.ReadUInt32();
                    bMask = br.ReadUInt32();
                    if (dibSize >= 56) aMask = br.ReadUInt32();
                }

                // Skip to palette (rest of DIB header after what we've read).
                long headerRead = 14 + (compression == 3 ? Math.Min(dibSize, (uint)(40 + (dibSize >= 56 ? 16 : 12))) : 40);
                long paletteOffset = 14 + dibSize;
                if (str.CanSeek) str.Seek(paletteOffset, SeekOrigin.Begin);
                else while (headerRead++ < paletteOffset) br.ReadByte();

                // --- Palette (for <=8bpp) ---
                byte[] palette = null;
                if (bpp <= 8)
                {
                    uint count = paletteCount == 0 ? (1u << bpp) : paletteCount;
                    palette = br.ReadBytes((int)count * 4); // BGRA per entry
                }

                // Seek to pixel data.
                if (str.CanSeek) str.Seek(pixelOffset, SeekOrigin.Begin);

                bool topDown = height < 0;
                int absHeight = Math.Abs(height);
                int rowBytes = ((width * bpp + 31) / 32) * 4;

                byte[] raw = br.ReadBytes(rowBytes * absHeight);
                byte[] rgba = new byte[width * absHeight * 4];

                int Shift(uint mask)
                {
                    if (mask == 0) return 0;
                    int s = 0;
                    while ((mask & 1) == 0) { mask >>= 1; s++; }
                    return s;
                }
                int Bits(uint mask)
                {
                    int b = 0;
                    while (mask != 0) { if ((mask & 1) != 0) b++; mask >>= 1; }
                    return b;
                }
                int rShift = Shift(rMask), gShift = Shift(gMask), bShift = Shift(bMask), aShift = Shift(aMask);
                int rBits = Bits(rMask), gBits = Bits(gMask), bBits = Bits(bMask), aBits = Bits(aMask);

                for (int y = 0; y < absHeight; y++)
                {
                    int srcRow = topDown ? y : (absHeight - 1 - y);
                    int srcBase = srcRow * rowBytes;
                    int dstBase = y * width * 4;

                    for (int x = 0; x < width; x++)
                    {
                        byte r, g, b, a = 0xFF;
                        switch (bpp)
                        {
                            case 1:
                            {
                                int bit = 7 - (x & 7);
                                int idx = (raw[srcBase + (x >> 3)] >> bit) & 1;
                                b = palette[idx * 4 + 0]; g = palette[idx * 4 + 1]; r = palette[idx * 4 + 2];
                                break;
                            }
                            case 4:
                            {
                                int byteIdx = srcBase + (x >> 1);
                                int idx = (x & 1) == 0 ? (raw[byteIdx] >> 4) : (raw[byteIdx] & 0x0F);
                                b = palette[idx * 4 + 0]; g = palette[idx * 4 + 1]; r = palette[idx * 4 + 2];
                                break;
                            }
                            case 8:
                            {
                                int idx = raw[srcBase + x];
                                b = palette[idx * 4 + 0]; g = palette[idx * 4 + 1]; r = palette[idx * 4 + 2];
                                break;
                            }
                            case 24:
                            {
                                int o = srcBase + x * 3;
                                b = raw[o]; g = raw[o + 1]; r = raw[o + 2];
                                break;
                            }
                            case 32:
                            {
                                int o = srcBase + x * 4;
                                if (compression == 3)
                                {
                                    uint px = (uint)(raw[o] | (raw[o+1]<<8) | (raw[o+2]<<16) | (raw[o+3]<<24));
                                    r = (byte)(((px & rMask) >> rShift) * 255 / ((1 << rBits) - 1));
                                    g = (byte)(((px & gMask) >> gShift) * 255 / ((1 << gBits) - 1));
                                    b = (byte)(((px & bMask) >> bShift) * 255 / ((1 << bBits) - 1));
                                    a = aMask != 0 ? (byte)(((px & aMask) >> aShift) * 255 / ((1 << aBits) - 1)) : (byte)0xFF;
                                }
                                else
                                {
                                    b = raw[o]; g = raw[o+1]; r = raw[o+2]; a = raw[o+3];
                                    if (a == 0 && bpp == 32) a = 0xFF; // BMPs often leave alpha=0
                                }
                                break;
                            }
                            default:
                                throw new NotSupportedException($"{bpp}bpp BMP not supported");
                        }
                        rgba[dstBase + x * 4 + 0] = r;
                        rgba[dstBase + x * 4 + 1] = g;
                        rgba[dstBase + x * 4 + 2] = b;
                        rgba[dstBase + x * 4 + 3] = a;
                    }
                }

                return new Tuple<byte[], int, int>(rgba, width, absHeight);
            }
        }

        public static Texture2D FromStream(GraphicsDevice gd, Stream str)
        {
            //if (!UseSoftLoad)
            //{
            //attempt monogame load of image

            int premult = 0;

            var magic = (str.ReadByte() | (str.ReadByte() << 8));
            str.Seek(0, SeekOrigin.Begin);
            magic += 0;
            if (magic == 0x4D42)
            {
                try
                {
                    //it's a bitmap — TSO uses paletted 8bpp BMPs; use pure-managed reader.
                    var bmp = BitmapReader(str);
                    if (bmp == null) return null;
                    var tex = new Texture2D(gd, bmp.Item2, bmp.Item3);
                    tex.SetData(bmp.Item1);
                    ManualTextureMaskSingleThreaded(ref tex, MASK_COLORS.ToArray());
                    return tex;
                }
                catch (Exception)
                {
                    return null; //bad bitmap :(
                }
            }
            else
            {
                //test for targa
                str.Seek(-18, SeekOrigin.End);
                byte[] sig = new byte[16];
                str.Read(sig, 0, 16);
                str.Seek(0, SeekOrigin.Begin);
                if (ASCIIEncoding.Default.GetString(sig) == "TRUEVISION-XFILE")
                {
                    try
                    {
                        var tga = new TargaImagePCL.TargaImage(str);
                        var tex = new Texture2D(gd, tga.Image.Width, tga.Image.Height);
                        tex.SetData(tga.Image.ToBGRA(true));
                        return tex;
                    }
                    catch (Exception)
                    {
                        return null; //bad tga
                    }
                }
                else
                {
                    //anything else (PNG/JPG) — MonoGame's native loader via STBImageSharp.
                    try
                    {
                        Color[] buffer = null;
                        Texture2D tex = Texture2D.FromStream(gd, str);
                        if (tex == null) return null;

                        premult += PremultiplyPNG;
                        if (premult == 1)
                        {
                            if (buffer == null)
                            {
                                buffer = new Color[tex.Width * tex.Height];
                                tex.GetData<Color>(buffer);
                            }

                            for (int i = 0; i < buffer.Length; i++)
                            {
                                var a = buffer[i].A;
                                buffer[i] = new Color((byte)((buffer[i].R * a) / 255), (byte)((buffer[i].G * a) / 255), (byte)((buffer[i].B * a) / 255), a);
                            }
                            tex.SetData(buffer);
                        }
                        else if (premult == -1) //divide out a premultiply... currently needed for dx since it premultiplies pngs without reason
                        {
                            if (buffer == null)
                            {
                                buffer = new Color[tex.Width * tex.Height];
                                tex.GetData<Color>(buffer);
                            }

                            for (int i = 0; i < buffer.Length; i++)
                            {
                                var a = buffer[i].A / 255f;
                                buffer[i] = new Color((byte)(buffer[i].R / a), (byte)(buffer[i].G / a), (byte)(buffer[i].B / a), buffer[i].A);
                            }
                            tex.SetData(buffer);
                        }
                        return tex;
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("error: " + e.ToString());
                        return new Texture2D(gd, 1, 1);
                    }
                }

            }
        }

		public static void ManualTextureMaskSingleThreaded(ref Texture2D Texture, uint[] ColorsFrom)
		{
			var ColorTo = Microsoft.Xna.Framework.Color.Transparent.PackedValue;

			var size = Texture.Width * Texture.Height;
			uint[] buffer = new uint[size];

			Texture.GetData<uint>(buffer);

			var didChange = false;

			for (int i = 0; i < size; i++)
			{

				if (ColorsFrom.Contains(buffer[i]))
				{
					didChange = true;
					buffer[i] = ColorTo;
				}
			}

			if (didChange)
			{
				Texture.SetData(buffer, 0, size);
			}
			else return;
		}

	}
}
