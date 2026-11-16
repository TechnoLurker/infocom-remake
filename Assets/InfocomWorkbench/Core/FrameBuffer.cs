using System;

namespace InfocomWorkbench
{
	/// <summary>Colour indices. 0-3 are the Workbench 1.3 screen colours, 4-6 the mouse pointer.</summary>
	public static class Pen
	{
		public const byte Blue = 0;
		public const byte White = 1;
		public const byte Black = 2;
		public const byte Orange = 3;
		public const byte PointerRed = 4;
		public const byte PointerDark = 5;
		public const byte PointerLight = 6;
		public const byte GuruRed = 7;
		public const byte GuruBlack = 8;

		/// <summary>RGB for each pen, 0xRRGGBB. Workbench 1.3 defaults: 05A, FFF, 002, F80.</summary>
		public static readonly uint[] Rgb =
		{
			0x0055AA, 0xFFFFFF, 0x000022, 0xFF8800,
			0xEE4444, 0x000000, 0xEECCAA,
			0xFF0000, 0x000000,
		};
	}

	/// <summary>An indexed-colour bitmap the size of the Amiga screen. Everything is drawn here.</summary>
	public sealed class FrameBuffer
	{
		public readonly int Width;
		public readonly int Height;
		public readonly byte[] Pixels;

		int _clipX0, _clipY0, _clipX1, _clipY1;

		public FrameBuffer(int width, int height)
		{
			Width = width;
			Height = height;
			Pixels = new byte[width * height];
			ResetClip();
		}

		public void ResetClip()
		{
			_clipX0 = 0; _clipY0 = 0; _clipX1 = Width; _clipY1 = Height;
		}

		public void SetClip(int x, int y, int w, int h)
		{
			_clipX0 = Math.Max(0, x);
			_clipY0 = Math.Max(0, y);
			_clipX1 = Math.Min(Width, x + w);
			_clipY1 = Math.Min(Height, y + h);
		}

		public void Clear(byte pen)
		{
			for (int i = 0; i < Pixels.Length; i++)
				Pixels[i] = pen;
		}

		public void Plot(int x, int y, byte pen)
		{
			if (x < _clipX0 || y < _clipY0 || x >= _clipX1 || y >= _clipY1)
				return;
			Pixels[y * Width + x] = pen;
		}

		public void Fill(int x, int y, int w, int h, byte pen)
		{
			int x0 = Math.Max(x, _clipX0), y0 = Math.Max(y, _clipY0);
			int x1 = Math.Min(x + w, _clipX1), y1 = Math.Min(y + h, _clipY1);
			for (int yy = y0; yy < y1; yy++)
			{
				int row = yy * Width;
				for (int xx = x0; xx < x1; xx++)
					Pixels[row + xx] = pen;
			}
		}

		/// <summary>Swaps every pixel in the rectangle with its complement (pen XOR 3), as Intuition does for highlights.</summary>
		public void Complement(int x, int y, int w, int h)
		{
			int x0 = Math.Max(x, _clipX0), y0 = Math.Max(y, _clipY0);
			int x1 = Math.Min(x + w, _clipX1), y1 = Math.Min(y + h, _clipY1);
			for (int yy = y0; yy < y1; yy++)
			{
				int row = yy * Width;
				for (int xx = x0; xx < x1; xx++)
					Pixels[row + xx] = (byte)(Pixels[row + xx] ^ 3);
			}
		}

		public void Box(int x, int y, int w, int h, byte pen)
		{
			Fill(x, y, w, 1, pen);
			Fill(x, y + h - 1, w, 1, pen);
			Fill(x, y, 1, h, pen);
			Fill(x + w - 1, y, 1, h, pen);
		}

		/// <summary>Draws one Topaz character. bg &lt; 0 leaves the background untouched.</summary>
		public void Char(int x, int y, char c, byte fg, int bg = -1)
		{
			int code = c < 256 ? c : '?';
			int g = code * TopazFont.Height;
			for (int row = 0; row < TopazFont.Height; row++)
			{
				byte bits = TopazFont.Glyphs[g + row];
				for (int col = 0; col < TopazFont.Width; col++)
				{
					bool on = (bits & (0x80 >> col)) != 0;
					if (on)
						Plot(x + col, y + row, fg);
					else if (bg >= 0)
						Plot(x + col, y + row, (byte)bg);
				}
			}
		}

		public void Text(int x, int y, string s, byte fg, int bg = -1)
		{
			if (s == null)
				return;
			for (int i = 0; i < s.Length; i++)
				Char(x + i * TopazFont.Width, y, s[i], fg, bg);
		}

		/// <summary>Draws pixel art given as rows of characters, using a map from character to pen. Unmapped characters are transparent.</summary>
		public void Sprite(int x, int y, string[] rows, Func<char, int> penFor, int xScale = 1, bool complement = false)
		{
			for (int r = 0; r < rows.Length; r++)
			{
				string line = rows[r];
				for (int c = 0; c < line.Length; c++)
				{
					int pen = penFor(line[c]);
					if (pen < 0)
						continue;
					if (complement)
						pen ^= 3;
					for (int s = 0; s < xScale; s++)
						Plot(x + c * xScale + s, y + r, (byte)pen);
				}
			}
		}
	}
}
