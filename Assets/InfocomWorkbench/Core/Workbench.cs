using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace InfocomWorkbench
{
	public enum WbKey { Enter, Backspace, Up, Down, PageUp, PageDown, Escape }

	/// <summary>
	/// The whole Workbench 1.3 style desktop: screen title bar, disk icons (one per story file),
	/// windows with Intuition-style gadgets, right-mouse menus and the Guru Meditation alert.
	/// It knows nothing about Unity: a host feeds it mouse and keys and copies Frame to the display.
	/// </summary>
	public sealed class Workbench
	{
		public const int ScreenWidth = 640;
		public const int TitleBarHeight = 10;
		const int GadgetWidth = 18;
		const double DoubleClickSeconds = 0.5;

		public int Height { get; private set; }
		public bool Pal { get; private set; }
		public bool TallPixels { get; set; } = true;
		public FrameBuffer Frame { get; private set; }

		/// <summary>Raised when PAL/NTSC or tall pixels change, so the host can resize its texture and save settings.</summary>
		public event Action DisplayChanged;
		/// <summary>Raised when the player picks Quit from the Workbench menu.</summary>
		public event Action QuitRequested;

		readonly string _gamesDir;
		readonly string _savesDir;
		readonly List<DiskIcon> _icons = new List<DiskIcon>();
		readonly List<Window> _windows = new List<Window>(); // back to front
		Window _active;

		int _mouseX, _mouseY;
		double _time;

		enum DragKind { None, Move, Size, Icon }
		DragKind _drag;
		Window _dragWindow;
		DiskIcon _dragIcon;
		int _dragDX, _dragDY;
		bool _dragMoved;
		double _lastClickTime = -10;
		DiskIcon _lastClickIcon;
		double _busyUntil;

		bool _menuOpen;
		List<Menu> _menus = new List<Menu>();
		int _menuHover = -1, _itemHover = -1;

		Window _guruWindow;

		public Workbench(string gamesDir, string savesDir, bool pal)
		{
			_gamesDir = gamesDir;
			_savesDir = savesDir;
			SetMode(pal);
			Rescan();
		}

		// ================================================================ setup

		public void SetMode(bool pal)
		{
			Pal = pal;
			Height = pal ? 256 : 200;
			Frame = new FrameBuffer(ScreenWidth, Height);
			foreach (var w in _windows)
				ClampWindow(w);
			LayoutIcons();
			DisplayChanged?.Invoke();
		}

		public void Rescan()
		{
			_icons.Clear();
			try
			{
				Directory.CreateDirectory(_gamesDir);
				foreach (string path in Directory.GetFiles(_gamesDir).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
					if (LooksLikeStory(path))
						_icons.Add(new DiskIcon { Path = path, Label = System.IO.Path.GetFileNameWithoutExtension(path) });
			}
			catch (Exception)
			{
				// An unreadable folder just means no icons; the info window explains where files go.
			}
			LayoutIcons();
			if (_icons.Count == 0)
				OpenInfo("Games", NoGamesText());
		}

		static bool LooksLikeStory(string path)
		{
			string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
			bool known = ext.Length == 3 && ext[1] == 'z' && ext[2] >= '1' && ext[2] <= '8'
				|| ext == ".dat" || ext == ".data" || ext == ".zcode";
			if (!known)
				return false;
			try
			{
				using (var fs = File.OpenRead(path))
				{
					if (fs.Length < 512)
						return false;
					int version = fs.ReadByte();
					return version >= 1 && version <= 8;
				}
			}
			catch (Exception)
			{
				return false;
			}
		}

		void LayoutIcons()
		{
			// Like disks on the Workbench: a column down the right-hand side, then further columns leftwards.
			const int cellW = 84, cellH = 30;
			int x = ScreenWidth - cellW, y = TitleBarHeight + 6;
			foreach (var icon in _icons)
			{
				if (y + cellH > Height)
				{
					y = TitleBarHeight + 6;
					x -= cellW;
				}
				icon.X = x + (cellW - DiskIcon.ArtWidth) / 2;
				icon.Y = y;
				y += cellH;
			}
		}

		string[] NoGamesText()
		{
			var lines = new List<string>
			{
				"No story files found yet.",
				"",
				"Copy your Infocom story files",
				"(.z3, .z4, .z5, .z6 or .dat) into:",
				"",
			};
			lines.AddRange(Chop(_gamesDir, 44));
			lines.Add("");
			lines.Add("Then hold the right mouse button");
			lines.Add("and choose Workbench > Rescan.");
			return lines.ToArray();
		}

		static IEnumerable<string> Chop(string s, int width)
		{
			for (int i = 0; i < s.Length; i += width)
				yield return s.Substring(i, Math.Min(width, s.Length - i));
		}

		public void Shutdown()
		{
			foreach (var w in _windows)
				w.Session?.Stop();
		}

		// ================================================================ windows

		Window OpenInfo(string title, string[] lines)
		{
			var existing = _windows.FirstOrDefault(w => w.Title == title && w.Session == null);
			if (existing != null)
				_windows.Remove(existing);
			int width = Math.Min(ScreenWidth - 40, Math.Max(200, lines.Max(l => l.Length) * 8 + 24));
			int height = Math.Min(Height - 30, lines.Length * 8 + TitleBarHeight + 14);
			var win = new Window
			{
				Title = title,
				InfoLines = lines,
				W = width,
				H = height,
				X = (ScreenWidth - width) / 2 - 40,
				Y = 24,
			};
			ClampWindow(win);
			_windows.Add(win);
			_active = win;
			return win;
		}

		void OpenStory(DiskIcon icon)
		{
			var running = _windows.FirstOrDefault(w => w.Session != null && w.Session.StoryPath == icon.Path);
			if (running != null)
			{
				BringToFront(running);
				_active = running;
				return;
			}

			var win = new Window
			{
				Title = icon.Label,
				X = 0,
				Y = TitleBarHeight + 1,
				W = ScreenWidth,
				H = Height - TitleBarHeight - 1,
			};
			win.Session = StorySessions.Create(icon.Path, _savesDir);
			win.Session.Start(win.TextW, win.TextH);
			_windows.Add(win);
			_active = win;
			_busyUntil = _time + 0.6;
		}

		void CloseWindow(Window w)
		{
			w.Session?.Stop();
			_windows.Remove(w);
			if (_active == w)
				_active = _windows.Count > 0 ? _windows[_windows.Count - 1] : null;
			if (_guruWindow == w)
				_guruWindow = null;
		}

		void BringToFront(Window w)
		{
			_windows.Remove(w);
			_windows.Add(w);
		}

		void SendToBack(Window w)
		{
			_windows.Remove(w);
			_windows.Insert(0, w);
		}

		void ClampWindow(Window w)
		{
			w.W = Math.Min(w.W, ScreenWidth);
			w.H = Math.Min(w.H, Height);
			w.X = Math.Max(0, Math.Min(w.X, ScreenWidth - w.W));
			w.Y = Math.Max(0, Math.Min(w.Y, Height - w.H));
		}

		Window WindowAt(int x, int y)
		{
			for (int i = _windows.Count - 1; i >= 0; i--)
				if (_windows[i].Contains(x, y))
					return _windows[i];
			return null;
		}

		DiskIcon IconAt(int x, int y)
		{
			for (int i = _icons.Count - 1; i >= 0; i--)
				if (_icons[i].Contains(x, y))
					return _icons[i];
			return null;
		}

		IStorySession ActiveSession => _active?.Session;

		// ================================================================ input

		public void Update(double timeSeconds)
		{
			_time = timeSeconds;

			// A story that crashed gets the classic alert, once.
			if (_guruWindow == null)
				foreach (var w in _windows)
					if (w.Session?.Error != null && !w.GuruShown)
					{
						w.GuruShown = true;
						_guruWindow = w;
						w.Session.Print("\n*** Interpreter error: " + w.Session.Error.Message + "\n");
						break;
					}
		}

		public void MouseMove(int x, int y)
		{
			_mouseX = x;
			_mouseY = y;

			if (_menuOpen)
			{
				UpdateMenuHover();
				return;
			}

			switch (_drag)
			{
				case DragKind.Move:
					_dragWindow.X = x - _dragDX;
					_dragWindow.Y = y - _dragDY;
					ClampWindow(_dragWindow);
					break;
				case DragKind.Size:
					_dragWindow.W = Math.Max(160, Math.Min(ScreenWidth - _dragWindow.X, x - _dragWindow.X + _dragDX));
					_dragWindow.H = Math.Max(60, Math.Min(Height - _dragWindow.Y, y - _dragWindow.Y + _dragDY));
					break;
				case DragKind.Icon:
					if (Math.Abs(x - _dragIcon.X - _dragDX) + Math.Abs(y - _dragIcon.Y - _dragDY) > 2)
						_dragMoved = true;
					if (_dragMoved)
					{
						_dragIcon.X = Math.Max(0, Math.Min(ScreenWidth - DiskIcon.ArtWidth, x - _dragDX));
						_dragIcon.Y = Math.Max(TitleBarHeight, Math.Min(Height - 24, y - _dragDY));
					}
					break;
			}
		}

		/// <summary>button: 0 = left (select), 1 = right (menu).</summary>
		public void MouseDown(int button)
		{
			int x = _mouseX, y = _mouseY;

			if (button == 1)
			{
				OpenMenus();
				return;
			}
			if (button != 0)
				return;

			if (_guruWindow != null)
			{
				_guruWindow = null;
				return;
			}

			var win = WindowAt(x, y);
			if (win != null)
			{
				_active = win;
				int lx = x - win.X, ly = y - win.Y;
				if (ly < TitleBarHeight)
				{
					if (lx < GadgetWidth)
					{
						CloseWindow(win);
						return;
					}
					if (lx >= win.W - GadgetWidth)
					{
						SendToBack(win);
						return;
					}
					if (lx >= win.W - 2 * GadgetWidth)
					{
						BringToFront(win);
						return;
					}
					_drag = DragKind.Move;
					_dragWindow = win;
					_dragDX = lx;
					_dragDY = ly;
					return;
				}
				if (lx >= win.W - GadgetWidth && ly >= win.H - 10)
				{
					_drag = DragKind.Size;
					_dragWindow = win;
					_dragDX = win.W - lx;
					_dragDY = win.H - ly;
					return;
				}
				// Clicks in the text area go to the story (version 6 games use the mouse).
				if (win.Session != null && x >= win.TextX && y >= win.TextY && x < win.TextX + win.TextW && y < win.TextY + win.TextH)
					win.Session.Click(x - win.TextX, y - win.TextY);
				return;
			}

			if (y < TitleBarHeight)
			{
				// Screen depth gadgets: there's only one screen, so they do nothing, like the real thing with one screen open.
				return;
			}

			var icon = IconAt(x, y);
			foreach (var i in _icons)
				i.Selected = i == icon;
			_active = null;
			if (icon != null)
			{
				if (icon == _lastClickIcon && _time - _lastClickTime < DoubleClickSeconds)
				{
					_lastClickIcon = null;
					OpenStory(icon);
					return;
				}
				_lastClickIcon = icon;
				_lastClickTime = _time;
				_drag = DragKind.Icon;
				_dragIcon = icon;
				_dragDX = x - icon.X;
				_dragDY = y - icon.Y;
				_dragMoved = false;
			}
		}

		public void MouseUp(int button)
		{
			if (button == 1 && _menuOpen)
			{
				_menuOpen = false;
				if (_menuHover >= 0 && _itemHover >= 0)
				{
					var item = _menus[_menuHover].Items[_itemHover];
					if (item.Enabled)
						item.Action();
				}
				_menuHover = _itemHover = -1;
				return;
			}
			if (button == 0)
			{
				_drag = DragKind.None;
				_dragWindow = null;
				_dragIcon = null;
			}
		}

		public void Wheel(int notches)
		{
			ActiveSession?.Scroll(notches * 3);
		}

		public void KeyChar(char c)
		{
			ActiveSession?.TypeChar(c);
		}

		public void Key(WbKey key)
		{
			var s = ActiveSession;
			if (s == null)
				return;
			int page = Math.Max(1, _active.TextH / TopazFont.Height - 2);
			switch (key)
			{
				case WbKey.Enter: s.Enter(); break;
				case WbKey.Backspace: s.Backspace(); break;
				case WbKey.Up: s.HistoryUp(); break;
				case WbKey.Down: s.HistoryDown(); break;
				case WbKey.PageUp: s.Scroll(page); break;
				case WbKey.PageDown: s.Scroll(-page); break;
				case WbKey.Escape: s.Scroll(-100000); break;
			}
		}

		// ================================================================ menus

		void OpenMenus()
		{
			_menus = new List<Menu>();

			var selected = _icons.FirstOrDefault(i => i.Selected);
			_menus.Add(new Menu("Workbench",
				new MenuItem("Open", () => OpenStory(selected), selected != null),
				new MenuItem("Rescan games", Rescan),
				new MenuItem("About", () => OpenInfo("About", AboutText())),
				new MenuItem("Quit", () => QuitRequested?.Invoke())));

			var session = ActiveSession;
			if (session != null)
			{
				bool waiting = session.Mode == InputMode.Line;
				_menus.Add(new Menu("Story",
					new MenuItem("Save", () => session.InjectCommand("save"), waiting),
					new MenuItem("Restore", () => session.InjectCommand("restore"), waiting),
					new MenuItem("Restart", () => session.InjectCommand("restart"), waiting),
					new MenuItem("Quit story", () => session.InjectCommand("quit"), waiting),
					new MenuItem("Close window", () => CloseWindow(_active))));
			}

			_menus.Add(new Menu("Display",
				new MenuItem("PAL  640x256", () => SetMode(true)) { Checked = Pal },
				new MenuItem("NTSC 640x200", () => SetMode(false)) { Checked = !Pal },
				new MenuItem("Tall pixels", () => { TallPixels = !TallPixels; DisplayChanged?.Invoke(); }) { Checked = TallPixels }));

			int mx = 4;
			foreach (var m in _menus)
			{
				m.X = mx;
				mx += (m.Title.Length + 2) * TopazFont.Width;
				int widest = m.Items.Max(i => i.Label.Length);
				m.PanelW = (widest + 3) * TopazFont.Width + 8;
				m.PanelH = m.Items.Count * 10 + 4;
				m.PanelX = Math.Min(m.X, ScreenWidth - m.PanelW);
			}
			_menuOpen = true;
			UpdateMenuHover();
		}

		void UpdateMenuHover()
		{
			int x = _mouseX, y = _mouseY;
			if (y < TitleBarHeight)
			{
				_itemHover = -1;
				_menuHover = -1;
				for (int i = 0; i < _menus.Count; i++)
				{
					var m = _menus[i];
					if (x >= m.X && x < m.X + (m.Title.Length + 2) * TopazFont.Width)
						_menuHover = i;
				}
				return;
			}
			_itemHover = -1;
			if (_menuHover >= 0)
			{
				var m = _menus[_menuHover];
				if (x >= m.PanelX && x < m.PanelX + m.PanelW && y >= TitleBarHeight + 2 && y < TitleBarHeight + m.PanelH - 2)
				{
					int i = (y - TitleBarHeight - 2) / 10;
					if (i >= 0 && i < m.Items.Count)
						_itemHover = i;
				}
			}
		}

		string[] AboutText()
		{
			var lines = new List<string>
			{
				"Infocom stories, Workbench 1.3 style",
				"",
				"Z-machine:  ZMachineLib by Brian Peek (MIT),",
				"            reworked for v1-6 in this player",
				"Font:       Topaz recreation by dMG/TrueSchool",
				"",
				"Story files:",
			};
			lines.AddRange(Chop(_gamesDir, 44));
			lines.Add("Saved games:");
			lines.AddRange(Chop(_savesDir, 44));
			return lines.ToArray();
		}

		// ================================================================ drawing

		public void Render()
		{
			var fb = Frame;
			fb.ResetClip();
			fb.Clear(Pen.Blue);

			foreach (var icon in _icons)
				DrawIcon(fb, icon);

			DrawScreenTitle(fb);

			foreach (var w in _windows)
				DrawWindow(fb, w);

			fb.ResetClip();
			if (_menuOpen)
				DrawMenus(fb);
			if (_guruWindow != null)
				DrawGuru(fb);

			bool busy = _time < _busyUntil;
			fb.Sprite(_mouseX, _mouseY, busy ? Art.Busy : Art.Pointer, Art.PenFor, 2);
		}

		void DrawScreenTitle(FrameBuffer fb)
		{
			fb.Fill(0, 0, ScreenWidth, TitleBarHeight, Pen.White);
			long used = _windows.Where(w => w.Session != null).Sum(w => StorySize(w.Session.StoryPath));
			fb.Text(4, 1, "Workbench release 1.3   " + Math.Max(0, 412544 - used) + " free memory", Pen.Blue);
			DrawDepthGadgets(fb, ScreenWidth - 2 * GadgetWidth, 0, true);
		}

		static long StorySize(string path)
		{
			try { return new FileInfo(path).Length; }
			catch (Exception) { return 0; }
		}

		void DrawIcon(FrameBuffer fb, DiskIcon icon)
		{
			fb.Sprite(icon.X, icon.Y, Art.Disk, Art.PenFor, 1, icon.Selected);
			int labelW = icon.Label.Length * TopazFont.Width;
			int lx = icon.X + DiskIcon.ArtWidth / 2 - labelW / 2;
			fb.Text(lx, icon.Y + Art.Disk.Length + 2, icon.Label, Pen.White, icon.Selected ? Pen.Black : -1);
		}

		void DrawDepthGadgets(FrameBuffer fb, int x, int y, bool onWhite)
		{
			byte bg = onWhite ? Pen.White : Pen.Blue;
			byte fg = onWhite ? Pen.Black : Pen.White;
			fb.Fill(x, y, 2 * GadgetWidth, TitleBarHeight, bg);
			fb.Fill(x, y, 1, TitleBarHeight, fg);
			fb.Fill(x + GadgetWidth, y, 1, TitleBarHeight, fg);
			// To front: outline behind, filled box in front.
			fb.Box(x + 4, y + 2, 8, 5, fg);
			fb.Fill(x + 8, y + 4, 8, 5, fg);
			// To back: filled box behind, outline in front.
			int bx = x + GadgetWidth;
			fb.Fill(bx + 4, y + 2, 8, 5, fg);
			fb.Fill(bx + 8, y + 4, 8, 5, bg);
			fb.Box(bx + 8, y + 4, 8, 5, fg);
		}

		void DrawWindow(FrameBuffer fb, Window w)
		{
			bool active = w == _active;
			fb.SetClip(w.X, w.Y, w.W, w.H);
			fb.Fill(w.X, w.Y, w.W, w.H, Pen.Blue);

			// Borders.
			fb.Fill(w.X, w.Y, 2, w.H, Pen.White);
			fb.Fill(w.X + w.W - 2, w.Y, 2, w.H, Pen.White);
			fb.Fill(w.X, w.Y + w.H - 2, w.W, 2, Pen.White);

			// Title bar: striped when active, plain when not.
			for (int r = 0; r < TitleBarHeight; r++)
			{
				bool stripe = active ? r % 2 == 0 : r == 0 || r == TitleBarHeight - 1;
				fb.Fill(w.X, w.Y + r, w.W, 1, stripe ? Pen.White : Pen.Blue);
			}

			// Close gadget.
			fb.Fill(w.X, w.Y, GadgetWidth, TitleBarHeight, Pen.Blue);
			fb.Box(w.X, w.Y, GadgetWidth + 1, TitleBarHeight, Pen.White);
			fb.Box(w.X + 5, w.Y + 2, 8, 6, Pen.White);
			fb.Fill(w.X + 8, w.Y + 4, 2, 2, Pen.White);

			if (!string.IsNullOrEmpty(w.Title))
			{
				string t = " " + w.Title + " ";
				int maxChars = Math.Max(0, (w.W - 3 * GadgetWidth - 12) / 8);
				if (t.Length > maxChars)
					t = t.Substring(0, maxChars);
				fb.Text(w.X + GadgetWidth + 6, w.Y + 1, t, active ? Pen.Blue : Pen.White, active ? Pen.White : Pen.Blue);
			}

			DrawDepthGadgets(fb, w.X + w.W - 2 * GadgetWidth, w.Y, false);
			fb.Box(w.X + w.W - 2 * GadgetWidth, w.Y, 2 * GadgetWidth, TitleBarHeight, Pen.White);

			// Contents.
			fb.SetClip(w.TextX, w.TextY, w.TextW, w.TextH);
			if (w.Session != null)
				w.Session.Draw(fb, w.TextX, w.TextY, w.TextW, w.TextH, active);
			else if (w.InfoLines != null)
				for (int i = 0; i < w.InfoLines.Length; i++)
					fb.Text(w.TextX + 4, w.TextY + 3 + i * 8, w.InfoLines[i], Pen.White);

			// Sizing gadget, bottom right.
			fb.SetClip(w.X, w.Y, w.W, w.H);
			int sx = w.X + w.W - GadgetWidth, sy = w.Y + w.H - 10;
			fb.Fill(sx, sy, GadgetWidth, 10, Pen.Blue);
			fb.Box(sx, sy, GadgetWidth, 10, Pen.White);
			fb.Box(sx + 4, sy + 2, 6, 4, Pen.White);
			fb.Box(sx + 7, sy + 4, 6, 4, Pen.White);
			fb.ResetClip();
		}

		void DrawMenus(FrameBuffer fb)
		{
			fb.Fill(0, 0, ScreenWidth, TitleBarHeight, Pen.White);
			for (int i = 0; i < _menus.Count; i++)
			{
				var m = _menus[i];
				fb.Text(m.X + TopazFont.Width, 1, m.Title, Pen.Blue);
				if (i == _menuHover)
					fb.Complement(m.X, 0, (m.Title.Length + 2) * TopazFont.Width, TitleBarHeight);
			}
			if (_menuHover < 0)
				return;

			var menu = _menus[_menuHover];
			int px = menu.PanelX, py = TitleBarHeight;
			fb.Fill(px, py, menu.PanelW, menu.PanelH, Pen.White);
			fb.Box(px, py, menu.PanelW, menu.PanelH, Pen.Black);
			for (int i = 0; i < menu.Items.Count; i++)
			{
				var item = menu.Items[i];
				int iy = py + 2 + i * 10;
				if (item.Checked)
					fb.Sprite(px + 4, iy + 2, Art.CheckMark, Art.PenFor);
				fb.Text(px + 18, iy + 1, item.Label, Pen.Blue);
				if (!item.Enabled)
				{
					// Ghosted, like Intuition: a dot pattern over the text.
					for (int gy = 0; gy < 10; gy += 2)
						for (int gx = (gy / 2) % 2; gx < menu.PanelW - 4; gx += 2)
							fb.Plot(px + 2 + gx, iy + gy, Pen.White);
				}
				else if (i == _itemHover)
					fb.Complement(px + 2, iy, menu.PanelW - 4, 10);
			}
		}

		void DrawGuru(FrameBuffer fb)
		{
			const int h = 40;
			fb.Fill(0, 0, ScreenWidth, h, Pen.GuruBlack);
			if (((int)(_time / 0.8)) % 2 == 0)
			{
				fb.Fill(0, 0, ScreenWidth, 4, Pen.GuruRed);
				fb.Fill(0, h - 4, ScreenWidth, 4, Pen.GuruRed);
				fb.Fill(0, 0, 6, h, Pen.GuruRed);
				fb.Fill(ScreenWidth - 6, 0, 6, h, Pen.GuruRed);
			}
			string line1 = "Software Failure.   Press left mouse button to continue.";
			string line2 = "Guru Meditation #00000004.0000AAC0";
			fb.Text((ScreenWidth - line1.Length * 8) / 2, 10, line1, Pen.GuruRed);
			fb.Text((ScreenWidth - line2.Length * 8) / 2, 22, line2, Pen.GuruRed);
		}

		// ================================================================ types

		sealed class Window
		{
			public int X, Y, W, H;
			public string Title;
			public IStorySession Session;
			public string[] InfoLines;
			public bool GuruShown;

			public int TextX => X + 4;
			public int TextY => Y + TitleBarHeight + 1;
			public int TextW => W - 8;
			public int TextH => H - TitleBarHeight - 4;

			public bool Contains(int x, int y) => x >= X && y >= Y && x < X + W && y < Y + H;
		}

		sealed class DiskIcon
		{
			public const int ArtWidth = 30;
			public string Path;
			public string Label;
			public int X, Y;
			public bool Selected;

			public bool Contains(int x, int y)
			{
				if (x >= X && x < X + ArtWidth && y >= Y && y < Y + 16)
					return true;
				int labelW = Label.Length * TopazFont.Width;
				int lx = X + ArtWidth / 2 - labelW / 2;
				return x >= lx && x < lx + labelW && y >= Y + 16 && y < Y + 26;
			}
		}

		sealed class Menu
		{
			public readonly string Title;
			public readonly List<MenuItem> Items;
			public int X, PanelX, PanelW, PanelH;

			public Menu(string title, params MenuItem[] items)
			{
				Title = title;
				Items = items.ToList();
			}
		}

		sealed class MenuItem
		{
			public readonly string Label;
			public readonly Action Action;
			public readonly bool Enabled;
			public bool Checked;

			public MenuItem(string label, Action action, bool enabled = true)
			{
				Label = label;
				Action = action;
				Enabled = enabled;
			}
		}
	}
}
