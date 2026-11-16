using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using ZMachineLib;

namespace InfocomWorkbench
{
	/// <summary>
	/// A running Version 6 story (Zork Zero, Shogun, Journey, Arthur). The game lays out its own
	/// windows on a pixel screen the size of the Workbench window's text area; this draws that
	/// screen and feeds it keys and mouse clicks. Like GameSession, the Z-machine runs on its own
	/// thread and everything shared is guarded by _lock.
	/// </summary>
	public sealed class V6Session : IZMachineV6IO, IStorySession
	{
		readonly object _lock = new object();
		readonly AutoResetEvent _ready = new AutoResetEvent(false);
		readonly string _savesDir;

		ZMachine _zm;
		Thread _thread;
		volatile bool _stopping;
		V6Screen _screen;

		public string StoryPath { get; }
		public string Title { get; }
		public int Version => 6;
		public bool Finished { get; private set; }
		public Exception Error { get; private set; }

		// Input.
		InputMode _mode;
		readonly StringBuilder _input = new StringBuilder();
		int _maxLength;
		byte[] _terminators = new byte[0];
		int _terminator;
		int _key;
		bool _continueLine;
		readonly List<string> _history = new List<string>();
		int _historyIndex;
		bool _moreShowing;

		// Mouse, in screen units (1-based).
		int _mouseX = 1, _mouseY = 1, _buttons;

		string _lastSaveName;

		public V6Session(string storyPath, string savesDir)
		{
			StoryPath = storyPath;
			Title = Path.GetFileNameWithoutExtension(storyPath);
			_savesDir = savesDir;
			_lastSaveName = Title.ToLowerInvariant() + ".sav";
		}

		public InputMode Mode { get { lock (_lock) return _mode; } }

		// ---------------------------------------------------------------- lifecycle

		public void Start(int width, int height)
		{
			_screen = new V6Screen(width, height, 9, 6) { More = WaitForMore };
			byte[] story;
			try
			{
				story = File.ReadAllBytes(StoryPath);
			}
			catch (Exception e)
			{
				_screen.Print("Can't read " + StoryPath + ":\n" + e.Message + "\n");
				Finished = true;
				return;
			}

			_zm = new ZMachine(this);
			_thread = new Thread(() => RunStory(story)) { IsBackground = true, Name = "Z-machine " + Title };
			_thread.Start();
		}

		void RunStory(byte[] story)
		{
			try
			{
				_zm.LoadFile(new MemoryStream(story, false));
				_zm.Run();
			}
			catch (Exception e)
			{
				if (!_stopping)
					lock (_lock) Error = e;
			}
			finally
			{
				lock (_lock)
				{
					Finished = true;
					_mode = InputMode.None;
				}
			}
		}

		public void Stop()
		{
			_stopping = true;
			if (_zm != null)
				_zm.Stop();
			_ready.Set();
		}

		// ---------------------------------------------------------------- drawing

		public void Draw(FrameBuffer fb, int x, int y, int w, int h, bool active)
		{
			lock (_lock)
			{
				if (_screen == null)
					return;
				int sw = Math.Min(w, _screen.Width), sh = Math.Min(h, _screen.Height);
				byte[] src = _screen.Pixels;
				for (int yy = 0; yy < sh; yy++)
				{
					int row = yy * _screen.Width;
					for (int xx = 0; xx < sw; xx++)
						fb.Plot(x + xx, y + yy, Pen.ForZColour(src[row + xx]));
				}
				// Window bigger than the story's screen: the rest is the default background.
				if (w > sw)
					fb.Fill(x + sw, y, w - sw, h, Pen.ForZColour(_screen.DefaultBg));
				if (h > sh)
					fb.Fill(x, y + sh, sw, h - sh, Pen.ForZColour(_screen.DefaultBg));

				_screen.CursorPixel(out int cx, out int cy, out bool inside);
				if (_moreShowing)
				{
					var w0 = _screen.Windows[0];
					int my = w0.Y - 1 + Math.Max(0, w0.H - V6Screen.FontHeight);
					fb.Text(x + w0.X - 1 + w0.LeftMargin, y + my, "[MORE]", Pen.ForZColour(w0.Bg), Pen.ForZColour(w0.Fg));
				}
				else if (active && _screen.CursorShown && inside && (_mode == InputMode.Line || _mode == InputMode.Char))
					fb.Fill(x + cx, y + cy, V6Screen.FontWidth, V6Screen.FontHeight, Pen.Orange);

				if (Finished && Error == null)
				{
					string msg = " The story has ended. Close this window to return to Workbench. ";
					fb.Text(x, y + h - 8, msg, Pen.Blue, Pen.White);
				}
			}
		}

		// ---------------------------------------------------------------- main-thread input

		bool ConsumeMore()
		{
			if (!_moreShowing)
				return false;
			_moreShowing = false;
			_ready.Set();
			return true;
		}

		public void TypeChar(char c)
		{
			lock (_lock)
			{
				if (ConsumeMore())
					return;
				if (_mode == InputMode.Char)
					SubmitKey(c);
				else if (_mode == InputMode.Line && _input.Length < _maxLength && c >= 32 && c != 127 && _screen.InputFits(_screen.Cur))
				{
					_input.Append(c);
					_screen.EchoChar(c);
				}
			}
		}

		public void Backspace()
		{
			lock (_lock)
			{
				if (ConsumeMore())
					return;
				if (_mode == InputMode.Line && _input.Length > 0)
				{
					_input.Length--;
					_screen.EchoBackspace();
				}
				else if (_mode == InputMode.Char)
					SubmitKey(8);
			}
		}

		public void Enter()
		{
			lock (_lock)
			{
				if (ConsumeMore())
					return;
				if (_mode == InputMode.Line)
					SubmitLine(13);
				else if (_mode == InputMode.Char)
					SubmitKey(13);
			}
		}

		void ReplaceInput(string text)
		{
			while (_input.Length > 0)
			{
				_input.Length--;
				_screen.EchoBackspace();
			}
			foreach (char c in text)
			{
				if (_input.Length >= _maxLength || !_screen.InputFits(_screen.Cur))
					break;
				_input.Append(c);
				_screen.EchoChar(c);
			}
		}

		public void HistoryUp()
		{
			lock (_lock)
			{
				if (ConsumeMore())
					return;
				if (_mode == InputMode.Char) { SubmitKey(129); return; }
				if (_mode != InputMode.Line || _history.Count == 0)
					return;
				_historyIndex = Math.Max(0, _historyIndex - 1);
				ReplaceInput(_history[_historyIndex]);
			}
		}

		public void HistoryDown()
		{
			lock (_lock)
			{
				if (ConsumeMore())
					return;
				if (_mode == InputMode.Char) { SubmitKey(130); return; }
				if (_mode != InputMode.Line)
					return;
				_historyIndex = Math.Min(_history.Count, _historyIndex + 1);
				ReplaceInput(_historyIndex < _history.Count ? _history[_historyIndex] : string.Empty);
			}
		}

		public void Scroll(int lines)
		{
			// Version 6 stories draw their own screen, so there's no scrollback to page through.
		}

		public void Click(int x, int y)
		{
			lock (_lock)
			{
				if (ConsumeMore() || _screen == null)
					return;
				int ux = x + 1, uy = y + 1;
				if (!_screen.InMouseWindow(ux, uy))
					return;
				_mouseX = ux;
				_mouseY = uy;
				_buttons = 1;
				if (_mode == InputMode.Char)
					SubmitKey(254);
				else if (_mode == InputMode.Line && Array.IndexOf(_terminators, (byte)254) >= 0)
					SubmitLine(254);
			}
		}

		public bool InjectCommand(string command)
		{
			lock (_lock)
			{
				ConsumeMore();
				if (_mode != InputMode.Line)
					return false;
				ReplaceInput(command);
				SubmitLine(13);
				return true;
			}
		}

		void SubmitLine(int terminator)
		{
			string line = _input.ToString();
			if (terminator == 13 && line.Trim().Length > 0)
				_history.Add(line);
			_historyIndex = _history.Count;
			_terminator = terminator;
			_mode = InputMode.None;
			_ready.Set();
		}

		void SubmitKey(int key)
		{
			_key = key;
			_mode = InputMode.None;
			_ready.Set();
		}

		/// <summary>Called from inside Print (Z-machine thread, lock held) when the main window needs a [MORE].</summary>
		void WaitForMore()
		{
			if (_stopping)
				return;
			_moreShowing = true;
			_ready.Reset();
			Monitor.Exit(_lock);
			try
			{
				while (!_ready.WaitOne(100))
					if (_stopping)
						break;
			}
			finally
			{
				Monitor.Enter(_lock);
				_moreShowing = false;
			}
		}

		// ---------------------------------------------------------------- IZMachineV6IO (Z-machine thread)

		public int ScreenWidth => _screen.Width;
		public int ScreenHeight => _screen.Height;
		public int FontWidth => V6Screen.FontWidth;
		public int FontHeight => V6Screen.FontHeight;
		public byte DefaultForeground => _screen.DefaultFg;
		public byte DefaultBackground => _screen.DefaultBg;
		public bool ColoursAvailable => true;
		public bool PicturesAvailable => false;
		public int CurrentWindow { get { lock (_lock) return _screen.Current; } }

		public void Reset() { lock (_lock) _screen.Reset(); }

		/// <summary>Everything printed, with the window it went to (for transcripts and tests).</summary>
		public event Action<int, string> Printed;

		public void Print(string s)
		{
			int window;
			lock (_lock)
			{
				if (_screen == null)
					return;
				window = _screen.Current;
				_screen.Print(s);
			}
			Printed?.Invoke(window, s);
		}

		public bool MoreShowing { get { lock (_lock) return _moreShowing; } }

		public void SelectWindow(int window) { lock (_lock) _screen.Select(window); }
		public void Split(int pixels) { lock (_lock) _screen.Split(pixels); }
		public void Erase(int window) { lock (_lock) _screen.Erase(window); }
		public void EraseLine(int pixels) { lock (_lock) _screen.EraseLine(pixels); }
		public void MoveCursor(int y, int x, int window) { lock (_lock) _screen.MoveCursor(y, x, window); }
		public void CursorVisible(bool visible) { lock (_lock) _screen.CursorShown = visible; }
		public void GetCursorPosition(out int y, out int x) { lock (_lock) _screen.GetCursor(out y, out x); }
		public void SetStyle(int style) { lock (_lock) _screen.SetStyle(style); }
		public void SetColours(int foreground, int background, int window) { lock (_lock) _screen.SetColours(foreground, background, window); }
		public int SetFont(int font, int window) { lock (_lock) return _screen.SetFont(font, window); }
		public void SetMargins(int left, int right, int window) { lock (_lock) _screen.SetMargins(left, right, window); }
		public void MoveWindow(int window, int y, int x) { lock (_lock) _screen.MoveWindow(window, y, x); }
		public void WindowSize(int window, int y, int x) { lock (_lock) _screen.WindowSize(window, y, x); }
		public void WindowStyle(int window, int flags, int operation) { lock (_lock) _screen.WindowStyle(window, flags, operation); }
		public int GetWindowProperty(int window, int property) { lock (_lock) return _screen.GetProperty(window, property); }
		public void PutWindowProperty(int window, int property, int value) { lock (_lock) _screen.PutProperty(window, property, value); }
		public void ScrollWindow(int window, int pixels) { lock (_lock) _screen.ScrollWindow(window, pixels); }
		public int BufferScreen(int mode) => 0;
		public int TextWidth(string s) => s.Length * V6Screen.FontWidth;

		public bool PictureData(int picture, out int height, out int width)
		{
			// Pictures come later, from the Amiga picture files.
			height = 0;
			width = 0;
			return false;
		}

		public void DrawPicture(int picture, int y, int x) { }
		public void ErasePicture(int picture, int y, int x) { }

		public void GetMouse(out int y, out int x, out int buttons, out int menu)
		{
			lock (_lock)
			{
				y = _mouseY;
				x = _mouseX;
				buttons = _buttons;
				menu = 0;
				_buttons = 0;
			}
		}

		public void MouseWindow(int window) { lock (_lock) _screen.MouseWindow = window; }

		public int TakeNewlineInterrupt()
		{
			lock (_lock)
			{
				int r = _screen.PendingInterrupt;
				_screen.PendingInterrupt = 0;
				return r;
			}
		}

		public LineInput ReadLine(int maxChars, string initial, int timeoutTenths, byte[] terminators)
		{
			lock (_lock)
			{
				_screen.Flush();
				_screen.ResetLineCounts();
				if (!_continueLine)
					_input.Length = 0;
				_continueLine = false;
				_maxLength = Math.Max(0, maxChars);
				_terminators = terminators ?? new byte[0];
				_historyIndex = _history.Count;
				_ready.Reset();
				_mode = InputMode.Line;
			}
			bool done = Wait(timeoutTenths);
			lock (_lock)
			{
				if (!done)
				{
					_mode = InputMode.None;
					_continueLine = !_stopping;
					return new LineInput { Text = _input.ToString(), Terminator = 0, TimedOut = !_stopping };
				}
				string text = _input.ToString();
				_input.Length = 0;
				if (_terminator == 13)
					_screen.Print("\n");
				return new LineInput { Text = text, Terminator = _terminator };
			}
		}

		public int ReadKey(int timeoutTenths)
		{
			lock (_lock)
			{
				_screen.Flush();
				_screen.ResetLineCounts();
				_ready.Reset();
				_mode = InputMode.Char;
			}
			bool done = Wait(timeoutTenths);
			lock (_lock)
			{
				if (!done)
				{
					_mode = InputMode.None;
					return _stopping ? 13 : 0;
				}
				return _key;
			}
		}

		/// <summary>Waits for input. Returns false on a timeout or when stopping.</summary>
		bool Wait(int timeoutTenths)
		{
			DateTime until = timeoutTenths > 0 ? DateTime.UtcNow.AddMilliseconds(timeoutTenths * 100) : DateTime.MaxValue;
			while (true)
			{
				if (_stopping)
					return false;
				int slice = 100;
				if (timeoutTenths > 0)
				{
					double left = (until - DateTime.UtcNow).TotalMilliseconds;
					if (left <= 0)
						return false;
					slice = (int)Math.Min(100, Math.Max(1, left));
				}
				if (_ready.WaitOne(slice))
				{
					lock (_lock)
						if (_mode == InputMode.None)
							return true;
				}
			}
		}

		// ---------------------------------------------------------------- IZMachineIO (v1-5 calls; unused by v6 except below)

		public string Read(int max) => ReadLine(max, string.Empty, 0, null).Text;
		public char ReadChar() => (char)ReadKey(0);
		public void SetCursor(ushort line, ushort column, ushort window) { }
		public void SetWindow(ushort window) { }
		public void EraseWindow(ushort window) { }
		public void BufferMode(bool buffer) { }
		public void SplitWindow(ushort lines) { }
		public void ShowStatus() { }
		public void SetTextStyle(TextStyle textStyle) { }
		public void SetColor(ZColor foreground, ZColor background) { }
		public void SoundEffect(ushort number) { }
		public void UpdateStatusLine(string location, short scoreOrHours, short movesOrMinutes, bool isTimeGame) { }
		public void GetCursor(out ushort line, out ushort column) { line = 1; column = 1; }
		public void EraseLine() { }

		public void Quit()
		{
		}

		// ---------------------------------------------------------------- save and restore

		string AskFileName()
		{
			Print("Enter a file name.\nDefault is \"" + _lastSaveName + "\": ");
			var r = ReadLine(60, string.Empty, 0, null);
			if (_stopping || r.TimedOut)
				return null;
			string name = (r.Text ?? string.Empty).Trim();
			if (name.Length == 0)
				name = _lastSaveName;
			foreach (char bad in Path.GetInvalidFileNameChars())
				name = name.Replace(bad, '_');
			_lastSaveName = name;
			return Path.Combine(_savesDir, name);
		}

		public bool Save(Stream stream)
		{
			string path = AskFileName();
			if (path == null)
				return false;
			try
			{
				Directory.CreateDirectory(_savesDir);
				using (var fs = File.Create(path))
					stream.CopyTo(fs);
				return true;
			}
			catch (Exception e)
			{
				Print("Couldn't save: " + e.Message + "\n");
				return false;
			}
		}

		public Stream Restore()
		{
			string path = AskFileName();
			if (path == null || !File.Exists(path))
				return null;
			try
			{
				return new MemoryStream(File.ReadAllBytes(path));
			}
			catch (Exception e)
			{
				Print("Couldn't restore: " + e.Message + "\n");
				return null;
			}
		}

		/// <summary>For tests and tools: the screen as Z colour numbers.</summary>
		public V6Screen Screen => _screen;
	}
}
