using System;
using System.Collections.Generic;

namespace InfocomWorkbench
{
	/// <summary>
	/// The Version 6 screen (Z-Machine Standard 1.1, section 8.8): eight windows on one
	/// pixel screen, each with its own position, size, cursor, margins, colours, style and
	/// attributes. Text is drawn here in Topaz as it is printed, like Infocom's own
	/// interpreters, so the screen is just pixels. Each pixel holds a Z-machine colour
	/// number (2-12); the session maps them to pens when it copies the screen out.
	/// Plain C#, not thread safe: the session locks around every call.
	/// </summary>
	public sealed class V6Screen
	{
		public const int FontWidth = TopazFont.Width;
		public const int FontHeight = TopazFont.Height;

		// Window attributes (window_style flags).
		public const int AttrWrap = 1, AttrScroll = 2, AttrTranscript = 4, AttrBuffer = 8;

		public sealed class Win
		{
			public int Y = 1, X = 1, H, W;
			public int CursorY = 1, CursorX = 1;
			public int LeftMargin, RightMargin;
			public int NewlineRoutine, NewlineCountdown;
			public int Style;
			public int Fg, Bg;
			public int Font = 1;
			public int Attributes;
			public int LineCount;
		}

		struct Cell
		{
			public char C;
			public int Style, Fg, Bg;
		}

		public readonly int Width, Height;
		public readonly byte[] Pixels;
		public readonly Win[] Windows = new Win[8];
		public int Current;
		public readonly byte DefaultFg, DefaultBg;
		public bool CursorShown = true;
		public int MouseWindow = 1;

		/// <summary>Called when a scrolling window is about to push unseen text off the top. Blocks until the player presses a key.</summary>
		public Action More;

		/// <summary>A newline-interrupt routine (packed address) that has come due, or 0.</summary>
		public int PendingInterrupt;

		readonly List<Cell> _word = new List<Cell>();
		int _wordWindow;

		public V6Screen(int width, int height, byte defaultFg, byte defaultBg)
		{
			Width = Math.Max(FontWidth * 20, width);
			Height = Math.Max(FontHeight * 5, height);
			Pixels = new byte[Width * Height];
			DefaultFg = defaultFg;
			DefaultBg = defaultBg;
			Reset();
		}

		public void Reset()
		{
			_word.Clear();
			for (int i = 0; i < 8; i++)
				Windows[i] = new Win
				{
					Fg = DefaultFg,
					Bg = DefaultBg,
					Attributes = i == 0 ? AttrWrap | AttrScroll | AttrTranscript | AttrBuffer : AttrBuffer,
				};
			Windows[0].W = Width;
			Windows[0].H = Height;
			Windows[1].W = Width;
			Current = 0;
			CursorShown = true;
			MouseWindow = 1;
			PendingInterrupt = 0;
			for (int i = 0; i < Pixels.Length; i++)
				Pixels[i] = DefaultBg;
		}

		public Win Cur => Windows[Current];

		static int Clamp(int w) => w < 0 || w > 7 ? 0 : w;

		// ---------------------------------------------------------------- text

		public void Print(string s)
		{
			for (int i = 0; i < s.Length; i++)
				PutChar(s[i]);
		}

		void PutChar(char c)
		{
			var w = Cur;
			if (c == '\r')
				return;
			if (c == '\n')
			{
				Flush();
				NewLine(w);
				return;
			}
			var cell = new Cell { C = c, Style = w.Style, Fg = w.Fg, Bg = w.Bg };
			bool wordWrap = (w.Attributes & AttrWrap) != 0 && (w.Attributes & AttrBuffer) != 0;
			if (!wordWrap)
			{
				DrawCell(w, cell, true);
				return;
			}
			if (c == ' ')
			{
				Flush();
				// A space that won't fit ends the line instead.
				if (w.CursorX + FontWidth - 1 > w.W - w.RightMargin)
					NewLine(w);
				else
					DrawCell(w, cell, false);
				return;
			}
			if (_word.Count > 0 && _wordWindow != Current)
				Flush();
			_wordWindow = Current;
			_word.Add(cell);
		}

		/// <summary>Draws any word still waiting for its end, so the cursor is where the game expects.</summary>
		public void Flush()
		{
			if (_word.Count == 0)
				return;
			var w = Windows[_wordWindow];
			int width = _word.Count * FontWidth;
			int lineStart = w.LeftMargin + 1;
			if (w.CursorX > lineStart && w.CursorX + width - 1 > w.W - w.RightMargin)
				NewLine(w);
			foreach (var cell in _word)
				DrawCell(w, cell, true);
			_word.Clear();
		}

		void DrawCell(Win w, Cell cell, bool wrapChars)
		{
			int right = w.W - w.RightMargin;
			if (w.CursorX + FontWidth - 1 > right)
			{
				if ((w.Attributes & AttrWrap) == 0 || !wrapChars)
					return; // clipped at the right margin
				NewLine(w);
			}
			DrawGlyph(w, w.CursorX, w.CursorY, cell.C, cell.Style, cell.Fg, cell.Bg);
			w.CursorX += FontWidth;
		}

		void NewLine(Win w)
		{
			w.CursorX = w.LeftMargin + 1;
			bool scrolls = (w.Attributes & AttrScroll) != 0;
			if (w.LineCount != -999)
				w.LineCount++;
			if (w.CursorY + 2 * FontHeight - 1 > w.H && scrolls)
			{
				int rows = Math.Max(1, w.H / FontHeight);
				if (w.LineCount != -999 && w.LineCount >= rows - 1 && More != null)
				{
					More();
					w.LineCount = 1;
				}
				Scroll(w, FontHeight);
				w.CursorY = Math.Max(1, w.H - FontHeight + 1);
			}
			else
				w.CursorY += FontHeight;

			if (w.NewlineCountdown != 0)
			{
				w.NewlineCountdown--;
				if (w.NewlineCountdown == 0 && w.NewlineRoutine != 0)
					PendingInterrupt = w.NewlineRoutine;
			}
		}

		/// <summary>Draws one character with its style at a window-relative position, clipped to the window.</summary>
		void DrawGlyph(Win w, int cx, int cy, char c, int style, int fg, int bg)
		{
			if ((style & 1) != 0)
			{
				int t = fg; fg = bg; bg = t;
			}
			bool bold = (style & 2) != 0, italic = (style & 4) != 0;
			int code = c < 256 ? c : '?';
			int x0 = w.X - 1 + cx - 1, y0 = w.Y - 1 + cy - 1;
			int clipX0 = Math.Max(0, w.X - 1), clipY0 = Math.Max(0, w.Y - 1);
			int clipX1 = Math.Min(Width, w.X - 1 + w.W), clipY1 = Math.Min(Height, w.Y - 1 + w.H);
			for (int row = 0; row < FontHeight; row++)
			{
				int py = y0 + row;
				if (py < clipY0 || py >= clipY1)
					continue;
				int bits = TopazFont.Glyphs[code * FontHeight + row];
				if (bold)
					bits |= bits >> 1;
				// Amiga-style algorithmic italic: rows above the baseline lean right.
				int shift = italic ? Math.Max(0, (6 - row) / 2) : 0;
				for (int col = 0; col < FontWidth; col++)
				{
					int px = x0 + col;
					if (px < clipX0 || px >= clipX1)
						continue;
					int src = col - shift;
					bool on = src >= 0 && src < FontWidth && (bits & (0x80 >> src)) != 0;
					Pixels[py * Width + px] = (byte)(on ? fg : bg);
				}
			}
		}

		// ---------------------------------------------------------------- input echo

		public bool InputFits(Win w) => w.CursorX + FontWidth - 1 <= w.W - w.RightMargin;

		public void EchoChar(char c)
		{
			var w = Cur;
			if (!InputFits(w))
				return;
			DrawGlyph(w, w.CursorX, w.CursorY, c, w.Style, w.Fg, w.Bg);
			w.CursorX += FontWidth;
		}

		public void EchoBackspace()
		{
			var w = Cur;
			if (w.CursorX - FontWidth < w.LeftMargin + 1)
				return;
			w.CursorX -= FontWidth;
			FillWindowRect(w, w.CursorX, w.CursorY, FontWidth, FontHeight, (w.Style & 1) != 0 ? w.Fg : w.Bg);
		}

		public void ResetLineCounts()
		{
			foreach (var w in Windows)
				if (w.LineCount != -999)
					w.LineCount = 0;
		}

		// ---------------------------------------------------------------- windows

		public void Select(int window)
		{
			Flush();
			Current = Clamp(window);
		}

		public void Split(int pixels)
		{
			Flush();
			pixels = Math.Max(0, Math.Min(Height, pixels));
			var w0 = Windows[0];
			var w1 = Windows[1];
			int abs0Y = w0.Y + w0.CursorY - 1;
			int abs1Y = w1.Y + w1.CursorY - 1;
			w1.Y = 1; w1.X = 1; w1.W = Width; w1.H = pixels;
			w0.Y = pixels + 1; w0.X = 1; w0.W = Width; w0.H = Height - pixels;
			w0.CursorY = abs0Y - w0.Y + 1;
			if (w0.CursorY < 1 || w0.CursorY > Math.Max(1, w0.H))
			{
				w0.CursorY = 1;
				w0.CursorX = w0.LeftMargin + 1;
			}
			w1.CursorY = abs1Y - w1.Y + 1;
			if (w1.CursorY < 1 || w1.CursorY > Math.Max(1, w1.H))
			{
				w1.CursorY = 1;
				w1.CursorX = w1.LeftMargin + 1;
			}
		}

		public void Erase(int window)
		{
			Flush();
			if (window == -1)
			{
				FillScreen((byte)Windows[0].Bg);
				Split(0);
				Current = 0;
				var w0 = Windows[0];
				w0.CursorY = 1;
				w0.CursorX = w0.LeftMargin + 1;
				return;
			}
			if (window == -2)
			{
				FillScreen((byte)Cur.Bg);
				return;
			}
			var w = Windows[Clamp(window)];
			FillWindowRect(w, 1, 1, w.W, w.H, w.Bg);
			w.CursorY = 1;
			w.CursorX = w.LeftMargin + 1;
			w.LineCount = 0;
		}

		public void EraseLine(int pixels)
		{
			Flush();
			var w = Cur;
			int right = w.W - w.RightMargin;
			int width = pixels == 1 ? right - w.CursorX + 1 : Math.Min(pixels, right - w.CursorX + 1);
			if (width > 0)
				FillWindowRect(w, w.CursorX, w.CursorY, width, FontHeight, w.Bg);
		}

		public void MoveCursor(int y, int x, int window)
		{
			var w = Windows[Clamp(window)];
			if (Clamp(window) == _wordWindow)
				Flush();
			w.CursorY = y < 1 ? 1 : y;
			w.CursorX = x < 1 ? 1 : x;
			if (w.CursorX < w.LeftMargin + 1)
				w.CursorX = w.LeftMargin + 1;
		}

		public void GetCursor(out int y, out int x)
		{
			Flush();
			y = Cur.CursorY;
			x = Cur.CursorX;
		}

		public void SetStyle(int style)
		{
			var w = Cur;
			w.Style = style == 0 ? 0 : w.Style | style;
		}

		int ResolveColour(Win w, int colour, bool foreground)
		{
			switch (colour)
			{
				case 0: return foreground ? w.Fg : w.Bg;
				case 1: return foreground ? DefaultFg : DefaultBg;
				case -1:
					int px = w.X - 1 + w.CursorX - 1, py = w.Y - 1 + w.CursorY - 1;
					if (px >= 0 && py >= 0 && px < Width && py < Height)
						return Pixels[py * Width + px];
					return foreground ? w.Fg : w.Bg;
				default:
					return colour >= 2 && colour <= 12 ? colour : (foreground ? w.Fg : w.Bg);
			}
		}

		public void SetColours(int fg, int bg, int window)
		{
			var w = Windows[Clamp(window)];
			int newFg = ResolveColour(w, fg, true);
			int newBg = ResolveColour(w, bg, false);
			w.Fg = newFg;
			w.Bg = newBg;
		}

		public int SetFont(int font, int window)
		{
			var w = Windows[Clamp(window)];
			if (font == 0)
				return w.Font;
			if (font != 1 && font != 4)
				return 0;
			int old = w.Font;
			w.Font = font;
			return old;
		}

		public void SetMargins(int left, int right, int window)
		{
			var w = Windows[Clamp(window)];
			if (Clamp(window) == _wordWindow)
				Flush();
			w.LeftMargin = Math.Max(0, left);
			w.RightMargin = Math.Max(0, right);
			if (w.CursorX < w.LeftMargin + 1)
				w.CursorX = w.LeftMargin + 1;
		}

		public void MoveWindow(int window, int y, int x)
		{
			var w = Windows[Clamp(window)];
			if (Clamp(window) == _wordWindow)
				Flush();
			w.Y = y;
			w.X = x;
		}

		public void WindowSize(int window, int y, int x)
		{
			var w = Windows[Clamp(window)];
			if (Clamp(window) == _wordWindow)
				Flush();
			w.H = Math.Max(0, y);
			w.W = Math.Max(0, x);
			if (w.CursorY > w.H || w.CursorX > w.W)
			{
				w.CursorY = 1;
				w.CursorX = w.LeftMargin + 1;
			}
		}

		public void WindowStyle(int window, int flags, int operation)
		{
			var w = Windows[Clamp(window)];
			if (Clamp(window) == _wordWindow)
				Flush();
			switch (operation)
			{
				case 0: w.Attributes = flags; break;
				case 1: w.Attributes |= flags; break;
				case 2: w.Attributes &= ~flags; break;
				case 3: w.Attributes ^= flags; break;
			}
		}

		public int GetProperty(int window, int property)
		{
			var w = Windows[Clamp(window)];
			if (Clamp(window) == _wordWindow)
				Flush();
			switch (property)
			{
				case 0: return w.Y;
				case 1: return w.X;
				case 2: return w.H;
				case 3: return w.W;
				case 4: return w.CursorY;
				case 5: return w.CursorX;
				case 6: return w.LeftMargin;
				case 7: return w.RightMargin;
				case 8: return w.NewlineRoutine;
				case 9: return w.NewlineCountdown;
				case 10: return w.Style;
				case 11: return w.Fg | (w.Bg << 8);
				case 12: return w.Font;
				case 13: return (FontHeight << 8) | FontWidth;
				case 14: return w.Attributes;
				case 15: return w.LineCount;
				case 16: return TrueColour(w.Fg);
				case 17: return TrueColour(w.Bg);
				default: return 0;
			}
		}

		public void PutProperty(int window, int property, int value)
		{
			var w = Windows[Clamp(window)];
			if (Clamp(window) == _wordWindow)
				Flush();
			switch (property)
			{
				case 0: w.Y = value; break;
				case 1: w.X = value; break;
				case 2: w.H = value; break;
				case 3: w.W = value; break;
				case 4: w.CursorY = value; break;
				case 5: w.CursorX = value; break;
				case 6: w.LeftMargin = value; break;
				case 7: w.RightMargin = value; break;
				case 8: w.NewlineRoutine = value & 0xffff; break;
				case 9: w.NewlineCountdown = value; break;
				case 10: w.Style = value; break;
				case 11: w.Fg = value & 0xff; w.Bg = (value >> 8) & 0xff; break;
				case 12: w.Font = value; break;
				case 14: w.Attributes = value; break;
				case 15: w.LineCount = value; break;
			}
		}

		/// <summary>Standard 1.1 15-bit colour for a Z colour number, from the pen RGB.</summary>
		static int TrueColour(int z)
		{
			uint rgb = Pen.Rgb[Pen.ForZColour(z)];
			int r = (int)((rgb >> 16) & 0xff) >> 3, g = (int)((rgb >> 8) & 0xff) >> 3, b = (int)(rgb & 0xff) >> 3;
			return r | (g << 5) | (b << 10);
		}

		public void ScrollWindow(int window, int pixels)
		{
			var w = Windows[Clamp(window)];
			if (Clamp(window) == _wordWindow)
				Flush();
			Scroll(w, pixels);
		}

		void Scroll(Win w, int pixels)
		{
			int x0 = Math.Max(0, w.X - 1), y0 = Math.Max(0, w.Y - 1);
			int x1 = Math.Min(Width, w.X - 1 + w.W), y1 = Math.Min(Height, w.Y - 1 + w.H);
			if (x1 <= x0 || y1 <= y0 || pixels == 0)
				return;
			int h = y1 - y0;
			byte bg = (byte)w.Bg;
			if (pixels > 0)
			{
				for (int y = y0; y < y1; y++)
				{
					int src = y + pixels;
					for (int x = x0; x < x1; x++)
						Pixels[y * Width + x] = src < y1 ? Pixels[src * Width + x] : bg;
				}
			}
			else
			{
				pixels = -pixels;
				for (int y = y1 - 1; y >= y0; y--)
				{
					int src = y - pixels;
					for (int x = x0; x < x1; x++)
						Pixels[y * Width + x] = src >= y0 ? Pixels[src * Width + x] : bg;
				}
			}
		}

		void FillWindowRect(Win w, int cx, int cy, int width, int height, int colour)
		{
			int clipX0 = Math.Max(0, w.X - 1), clipY0 = Math.Max(0, w.Y - 1);
			int clipX1 = Math.Min(Width, w.X - 1 + w.W), clipY1 = Math.Min(Height, w.Y - 1 + w.H);
			int x0 = Math.Max(clipX0, w.X - 1 + cx - 1), y0 = Math.Max(clipY0, w.Y - 1 + cy - 1);
			int x1 = Math.Min(clipX1, w.X - 1 + cx - 1 + width), y1 = Math.Min(clipY1, w.Y - 1 + cy - 1 + height);
			for (int y = y0; y < y1; y++)
				for (int x = x0; x < x1; x++)
					Pixels[y * Width + x] = (byte)colour;
		}

		void FillScreen(byte colour)
		{
			for (int i = 0; i < Pixels.Length; i++)
				Pixels[i] = colour;
		}

		/// <summary>Screen position (0-based pixels) of the current window's cursor.</summary>
		public void CursorPixel(out int x, out int y, out bool inside)
		{
			var w = Cur;
			x = w.X - 1 + w.CursorX - 1;
			y = w.Y - 1 + w.CursorY - 1;
			inside = w.CursorX + FontWidth - 1 <= w.W && w.CursorY + FontHeight - 1 <= w.H && x >= 0 && y >= 0
				&& x + FontWidth <= Width && y + FontHeight <= Height;
		}

		public bool InMouseWindow(int x1, int y1)
		{
			if (MouseWindow < 0 || MouseWindow > 7)
				return true;
			var w = Windows[MouseWindow];
			return x1 >= w.X && y1 >= w.Y && x1 < w.X + w.W && y1 < w.Y + w.H;
		}
	}
}
