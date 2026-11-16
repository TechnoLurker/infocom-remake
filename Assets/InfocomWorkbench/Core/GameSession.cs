using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using ZMachineLib;

namespace InfocomWorkbench
{
	public enum InputMode { None, Line, Char }

	/// <summary>
	/// One running story. The Z-machine runs on its own thread and blocks in Read();
	/// the Workbench (main thread) feeds it keystrokes and draws its text.
	/// Everything shared between the two threads is guarded by _lock.
	/// </summary>
	public sealed class GameSession : IZMachineIO
	{
		const int MaxScrollback = 2000;

		readonly object _lock = new object();
		readonly AutoResetEvent _ready = new AutoResetEvent(false);

		public readonly string StoryPath;
		public readonly string Title;
		readonly string _savesDir;

		ZMachine _zm;
		Thread _thread;
		volatile bool _stopping;

		public int Version { get; private set; }
		public bool Finished { get; private set; }
		public Exception Error { get; private set; }

		// Lower window: finished paragraphs plus the one being written.
		readonly List<string> _paragraphs = new List<string>();
		readonly StringBuilder _current = new StringBuilder();
		int _trimmed; // paragraphs dropped off the top of the scrollback

		// Upper window (v4+ status areas, and v3 split windows).
		int _cols;
		int _upperRows;
		char[,] _grid = new char[0, 0];
		bool[,] _gridReverse = new bool[0, 0];
		int _curRow, _curCol, _window;
		bool _reverse;

		// v1-3 status line.
		string _statusLeft = string.Empty;
		string _statusRight = string.Empty;

		// Input.
		InputMode _mode;
		readonly StringBuilder _input = new StringBuilder();
		int _maxLength;
		string _submitted;
		char _submittedChar;
		readonly List<string> _history = new List<string>();
		int _historyIndex;

		// Paging: everything up to _ackParagraph has been seen; _morePaged counts lines paged past it.
		int _ackParagraph;
		int _morePaged;
		int _scrollBack;
		bool _moreShowing;

		string _lastSaveName;

		public GameSession(string storyPath, string savesDir)
		{
			StoryPath = storyPath;
			Title = Path.GetFileNameWithoutExtension(storyPath);
			_savesDir = savesDir;
			_lastSaveName = Title.ToLowerInvariant() + ".sav";
		}

		public InputMode Mode { get { lock (_lock) return _mode; } }

		// ---------------------------------------------------------------- lifecycle

		public void Start(int columns, int rows)
		{
			_cols = Math.Max(20, Math.Min(255, columns));
			byte[] story;
			try
			{
				story = File.ReadAllBytes(StoryPath);
			}
			catch (Exception e)
			{
				AppendLower("Can't read " + StoryPath + ":\n" + e.Message + "\n");
				Finished = true;
				return;
			}

			Version = story.Length > 0 ? story[0] : 0;
			if (Version < 1 || Version > 5)
			{
				AppendLower(Version == 6
					? "This is a version 6 (graphical) story. This player can't run those yet.\n"
					: "This doesn't look like a story file this player can run (Z-machine version " + Version + ").\n");
				Finished = true;
				return;
			}

			_zm = new ZMachine(this)
			{
				ScreenColumns = (byte)_cols,
				ScreenRows = (byte)Math.Max(10, Math.Min(255, rows)),
			};

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

		// ---------------------------------------------------------------- main-thread input

		/// <summary>Returns true if the key was consumed by a [MORE] prompt.</summary>
		bool ConsumeMore()
		{
			_scrollBack = 0;
			if (_moreShowing)
			{
				_morePaged += Math.Max(1, _lastLowerRows - 1);
				_moreShowing = false;
				return true;
			}
			return false;
		}

		public void TypeChar(char c)
		{
			lock (_lock)
			{
				if (ConsumeMore())
					return;
				if (_mode == InputMode.Char)
				{
					_submittedChar = c;
					_mode = InputMode.None;
					_ready.Set();
				}
				else if (_mode == InputMode.Line && _input.Length < _maxLength && c >= 32 && c < 127)
				{
					_input.Append(c);
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
					_input.Length--;
				else if (_mode == InputMode.Char)
					SubmitChar((char)8);
			}
		}

		public void Enter()
		{
			lock (_lock)
			{
				if (ConsumeMore())
					return;
				if (_mode == InputMode.Line)
					SubmitLine(_input.ToString(), true);
				else if (_mode == InputMode.Char)
					SubmitChar((char)13);
			}
		}

		public void HistoryUp()
		{
			lock (_lock)
			{
				if (_mode == InputMode.Char) { SubmitChar((char)129); return; }
				if (_mode != InputMode.Line || _history.Count == 0)
					return;
				_historyIndex = Math.Max(0, _historyIndex - 1);
				_input.Length = 0;
				_input.Append(_history[_historyIndex]);
			}
		}

		public void HistoryDown()
		{
			lock (_lock)
			{
				if (_mode == InputMode.Char) { SubmitChar((char)130); return; }
				if (_mode != InputMode.Line)
					return;
				_historyIndex = Math.Min(_history.Count, _historyIndex + 1);
				_input.Length = 0;
				if (_historyIndex < _history.Count)
					_input.Append(_history[_historyIndex]);
			}
		}

		public void Scroll(int lines)
		{
			lock (_lock) _scrollBack = Math.Max(0, _scrollBack + lines);
		}

		/// <summary>Types a whole command for the player, if the story is waiting for one (used by the menus).</summary>
		public bool InjectCommand(string command)
		{
			lock (_lock)
			{
				_moreShowing = false;
				_scrollBack = 0;
				if (_mode != InputMode.Line)
					return false;
				SubmitLine(command, true);
				return true;
			}
		}

		void SubmitLine(string line, bool remember)
		{
			// Echo what was typed after the prompt, then let the story continue.
			_current.Append(line);
			EndParagraph();
			_ackParagraph = _paragraphs.Count + _trimmed;
			_morePaged = 0;
			_scrollBack = 0;
			if (remember && line.Trim().Length > 0)
				_history.Add(line);
			_historyIndex = _history.Count;
			_input.Length = 0;
			_submitted = line;
			_mode = InputMode.None;
			_ready.Set();
		}

		void SubmitChar(char c)
		{
			_ackParagraph = _paragraphs.Count + _trimmed;
			_morePaged = 0;
			_submittedChar = c;
			_mode = InputMode.None;
			_ready.Set();
		}

		// ---------------------------------------------------------------- lower window text

		void AppendLower(string s)
		{
			for (int i = 0; i < s.Length; i++)
			{
				char c = s[i];
				if (c == '\r')
					continue;
				if (c == '\n')
					EndParagraph();
				else
					_current.Append(c);
			}
		}

		void EndParagraph()
		{
			_paragraphs.Add(_current.ToString());
			_current.Length = 0;
			if (_paragraphs.Count > MaxScrollback)
			{
				_paragraphs.RemoveAt(0);
				_trimmed++;
			}
		}

		// ---------------------------------------------------------------- drawing

		int _lastLowerRows = 20;

		public const int StatusHeight = 10;

		/// <summary>Draws the story into the text area of its window.</summary>
		public void Draw(FrameBuffer fb, int x, int y, int w, int h, bool active)
		{
			lock (_lock)
			{
				int cols = Math.Max(1, w / TopazFont.Width);
				int top = y;

				if (Version >= 1 && Version <= 3)
				{
					DrawStatusLine(fb, x, top, cols);
					top += StatusHeight;
				}

				for (int r = 0; r < _upperRows; r++)
				{
					for (int c = 0; c < Math.Min(cols, _cols); c++)
					{
						bool rev = _gridReverse[r, c];
						fb.Char(x + c * 8, top, _grid[r, c], rev ? Pen.Blue : Pen.White, rev ? Pen.White : Pen.Blue);
					}
					top += TopazFont.Height;
				}

				int rows = Math.Max(1, (y + h - top) / TopazFont.Height);
				_lastLowerRows = rows;
				DrawLower(fb, x, top, cols, rows, y + h, active);
			}
		}

		void DrawStatusLine(FrameBuffer fb, int x, int y, int cols)
		{
			// Like Infocom's Amiga interpreter: the room name and the score each sit in a white box.
			string left = " " + _statusLeft + " ";
			string right = " " + _statusRight + " ";
			if (_statusLeft.Length > 0)
				fb.Text(x, y + 1, left, Pen.Blue, Pen.White);
			if (_statusRight.Length > 0)
			{
				int rx = x + Math.Max(left.Length + 1, cols - right.Length - 2) * TopazFont.Width;
				fb.Text(rx, y + 1, right, Pen.Blue, Pen.White);
			}
		}

		readonly List<string> _wrapped = new List<string>();
		readonly List<string> _tail = new List<string>();

		void DrawLower(FrameBuffer fb, int x, int top, int cols, int rows, int bottom, bool active)
		{
			// Wrap all finished paragraphs (cheap enough at this size) and note where the unseen part starts.
			_wrapped.Clear();
			int ackLine = 0;
			int ackIndex = _ackParagraph - _trimmed;
			for (int p = 0; p < _paragraphs.Count; p++)
			{
				if (p == ackIndex)
					ackLine = _wrapped.Count;
				Wrap(_paragraphs[p], cols, _wrapped);
			}
			if (ackIndex >= _paragraphs.Count)
				ackLine = _wrapped.Count;

			string tailText = _current.ToString();
			bool showInput = _mode == InputMode.Line;
			if (showInput)
				tailText += _input.ToString();
			_tail.Clear();
			Wrap(tailText, cols, _tail);
			// Cursor goes after the last character; if that fills the line, onto a new one.
			bool cursorOnNewLine = showInput && _tail.Count > 0 && _tail[_tail.Count - 1].Length >= cols;
			if (cursorOnNewLine)
				_tail.Add(string.Empty);

			int total = _wrapped.Count + _tail.Count;
			int start = ackLine + _morePaged;
			int first;
			_moreShowing = total - start > rows && _scrollBack == 0 && !Finished;
			if (_moreShowing)
				first = start;
			else
				first = Math.Max(0, total - rows - _scrollBack);

			int visible = Math.Min(rows, total - first);
			int y0 = top + (rows - visible) * TopazFont.Height;
			if (_moreShowing)
				y0 = top;
			for (int i = 0; i < visible; i++)
			{
				int line = first + i;
				if (_moreShowing && i == rows - 1)
				{
					fb.Text(x, y0 + i * 8, "[MORE]", Pen.Blue, Pen.White);
					break;
				}
				string text = line < _wrapped.Count ? _wrapped[line] : _tail[line - _wrapped.Count];
				fb.Text(x, y0 + i * 8, text, Pen.White);
			}

			// Orange block cursor at the end of the input line, when it is on screen.
			if (active && !_moreShowing && _scrollBack == 0 && (showInput || _mode == InputMode.Char) && _tail.Count > 0)
			{
				int cx = cursorOnNewLine ? 0 : _tail[_tail.Count - 1].Length;
				int cy = y0 + (visible - 1) * 8;
				if (cx < cols)
					fb.Fill(x + cx * 8, cy, 8, 8, Pen.Orange);
			}
		}

		static void Wrap(string text, int cols, List<string> output)
		{
			if (text.Length == 0)
			{
				output.Add(string.Empty);
				return;
			}
			int pos = 0;
			while (pos < text.Length)
			{
				int remaining = text.Length - pos;
				if (remaining <= cols)
				{
					output.Add(text.Substring(pos));
					return;
				}
				int brk = text.LastIndexOf(' ', pos + cols, cols + 1);
				if (brk <= pos)
				{
					output.Add(text.Substring(pos, cols));
					pos += cols;
				}
				else
				{
					output.Add(text.Substring(pos, brk - pos));
					pos = brk + 1;
				}
			}
		}

		// ---------------------------------------------------------------- IZMachineIO (Z-machine thread)

		public void Print(string s)
		{
			lock (_lock)
			{
				if (_window == 1)
					PrintUpper(s);
				else
					AppendLower(s);
			}
		}

		void PrintUpper(string s)
		{
			foreach (char c in s)
			{
				if (c == '\r')
					continue;
				if (c == '\n')
				{
					_curRow++;
					_curCol = 0;
					continue;
				}
				if (_curRow >= 0 && _curRow < _upperRows && _curCol >= 0 && _curCol < _cols)
				{
					_grid[_curRow, _curCol] = c;
					_gridReverse[_curRow, _curCol] = _reverse;
				}
				_curCol++;
			}
		}

		string WaitForLine(int maxLength)
		{
			lock (_lock)
			{
				_mode = InputMode.Line;
				_maxLength = Math.Max(1, maxLength);
				_input.Length = 0;
				_historyIndex = _history.Count;
			}
			while (!_ready.WaitOne(100))
				if (_stopping)
					return null;
			return _stopping ? null : _submitted;
		}

		public string Read(int max)
		{
			return WaitForLine(Version >= 5 ? max : max - 1);
		}

		public char ReadChar()
		{
			lock (_lock) _mode = InputMode.Char;
			while (!_ready.WaitOne(100))
				if (_stopping)
					return (char)13;
			return _submittedChar;
		}

		public void SetCursor(ushort line, ushort column, ushort window)
		{
			lock (_lock)
			{
				_curRow = line - 1;
				_curCol = column - 1;
			}
		}

		public void SetWindow(ushort window)
		{
			lock (_lock)
			{
				_window = window;
				if (window == 1)
				{
					_curRow = 0;
					_curCol = 0;
				}
			}
		}

		public void EraseWindow(ushort window)
		{
			lock (_lock)
			{
				short w = (short)window;
				if (w == -1)
				{
					ResizeUpper(0);
					ClearLower();
				}
				else if (w == -2)
				{
					ClearUpper();
					ClearLower();
				}
				else if (w == 0)
					ClearLower();
				else if (w == 1)
					ClearUpper();
			}
		}

		void ClearLower()
		{
			// Keep the scrollback, but start the visible page fresh.
			if (_current.Length > 0)
				EndParagraph();
			_ackParagraph = _paragraphs.Count + _trimmed;
			_morePaged = 0;
			for (int i = 0; i < _lastLowerRows; i++)
				_paragraphs.Add(string.Empty);
			_ackParagraph = _paragraphs.Count + _trimmed;
		}

		void ClearUpper()
		{
			for (int r = 0; r < _upperRows; r++)
				for (int c = 0; c < _cols; c++)
				{
					_grid[r, c] = ' ';
					_gridReverse[r, c] = false;
				}
		}

		void ResizeUpper(int rows)
		{
			var grid = new char[rows, _cols];
			var rev = new bool[rows, _cols];
			for (int r = 0; r < rows; r++)
				for (int c = 0; c < _cols; c++)
				{
					bool keep = r < _upperRows;
					grid[r, c] = keep ? _grid[r, c] : ' ';
					rev[r, c] = keep && _gridReverse[r, c];
				}
			_grid = grid;
			_gridReverse = rev;
			_upperRows = rows;
		}

		public void BufferMode(bool buffer)
		{
		}

		public void SplitWindow(ushort lines)
		{
			lock (_lock)
			{
				ResizeUpper(lines);
				if (Version <= 3)
					ClearUpper();
			}
		}

		public void ShowStatus()
		{
		}

		public void UpdateStatusLine(string location, short scoreOrHours, short movesOrMinutes, bool isTimeGame)
		{
			string right;
			if (isTimeGame)
			{
				int hours = scoreOrHours;
				string ampm = hours >= 12 ? "pm" : "am";
				int h12 = hours % 12 == 0 ? 12 : hours % 12;
				right = "Time: " + h12 + ":" + movesOrMinutes.ToString("00") + " " + ampm;
			}
			else
				right = "Score: " + scoreOrHours + "/" + movesOrMinutes;
			lock (_lock)
			{
				_statusLeft = location ?? string.Empty;
				_statusRight = right;
			}
		}

		public void SetTextStyle(TextStyle textStyle)
		{
			lock (_lock) _reverse = (textStyle & TextStyle.Reverse) != 0;
		}

		public void SetColor(ZColor foreground, ZColor background)
		{
		}

		public void SoundEffect(ushort number)
		{
		}

		public void Quit()
		{
			lock (_lock)
			{
				AppendLower("\n[The story has ended. Close this window to return to Workbench.]");
			}
		}

		// ---------------------------------------------------------------- save and restore

		string AskFileName()
		{
			lock (_lock)
			{
				AppendLower("Enter a file name.\nDefault is \"" + _lastSaveName + "\": ");
			}
			string name = WaitForLine(60);
			if (name == null)
				return null;
			name = name.Trim();
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
	}
}
