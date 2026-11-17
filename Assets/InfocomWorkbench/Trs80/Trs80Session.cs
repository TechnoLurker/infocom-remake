using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace InfocomWorkbench.Trs80
{
	/// <summary>
	/// Runs a TRS-80 Model I/III machine-language program (such as Pyramid 2000) in a Workbench window.
	/// The program runs on a Z80 on its own thread. There is no Tandy ROM: the few ROM routines a program
	/// like this calls (print a character, scan the keyboard, cassette in and out) are done here in C#,
	/// so the 64 x 16 screen, the keyboard and the cassette become a window, your keys and the Saves folder.
	/// </summary>
	public sealed class Trs80Session : IStorySession
	{
		public const int Cols = 64, Rows = 16;

		// Level II / Model III ROM entry points this player stands in for.
		const int RomKeyScan = 0x002B;   // A = key pressed, or 0
		const int RomPrint = 0x0033;     // print the character in A
		const int RomWaitKey = 0x0049;   // wait for a key, A = key
		const int RomCls = 0x01C9;       // clear the screen
		const int RomCasOff = 0x01F8;    // cassette motor off
		const int RomCasOn = 0x0212;     // select cassette and start the motor
		const int RomCasReadByte = 0x0235;
		const int RomCasWriteByte = 0x0264;
		const int RomCasWriteLeader = 0x0287;
		const int RomCasReadLeader = 0x0296;

		readonly object _lock = new object();
		readonly AutoResetEvent _keyEvent = new AutoResetEvent(false);
		readonly Queue<int> _keys = new Queue<int>();

		public string StoryPath { get; }
		public string Title { get; }
		public int Version => 0;
		public bool Finished { get; private set; }
		public Exception Error { get; private set; }

		readonly string _savesDir;
		Z80 _cpu;
		TrsProgram _program;
		Thread _thread;
		volatile bool _stopping;
		volatile bool _restartRequested;

		// The TRS-80 screen.
		readonly byte[,] _screen = new byte[Rows, Cols];
		int _row, _col;
		bool _cursorOn;
		volatile bool _waitingForKey;

		// What has been typed on the current line, for Up/Down command recall.
		readonly StringBuilder _line = new StringBuilder();
		readonly List<string> _history = new List<string>();
		int _historyIndex;

		// The virtual cassette.
		List<byte> _tapeOut;
		string _tapeOutPath;
		byte[] _tapeIn;
		int _tapeInPos;
		string _lastSaveName;

		public Trs80Session(string path, string savesDir)
		{
			StoryPath = path;
			Title = Path.GetFileNameWithoutExtension(path);
			_savesDir = savesDir;
			_lastSaveName = Title.ToLowerInvariant() + ".sav";
			ClearScreen();
		}

		public InputMode Mode => _waitingForKey && !Finished ? InputMode.Line : InputMode.None;

		// ---------------------------------------------------------------- lifecycle

		public void Start(int width, int height)
		{
			try
			{
				_program = TrsProgram.Load(StoryPath);
			}
			catch (Exception e)
			{
				Print("Can't load " + Path.GetFileName(StoryPath) + ":\r" + e.Message + "\r");
				Finished = true;
				return;
			}

			_cpu = new Z80 { TrapBelow = 0x4000, Trap = Rom };
			PowerOn();
			_thread = new Thread(Run) { IsBackground = true, Name = "TRS-80 " + Title };
			_thread.Start();
		}

		/// <summary>Loads the program into fresh memory and points the Z80 at its start, as after loading the tape.</summary>
		void PowerOn()
		{
			Array.Clear(_cpu.Mem, 0, _cpu.Mem.Length);
			Array.Copy(_program.Memory, 0x4000, _cpu.Mem, 0x4000, 0xC000);
			for (int a = 0; a < 0x4000; a++)
				_cpu.Mem[a] = 0xC9; // RET, should anything read the ROM area
			_cpu.PC = _program.Entry;
			_cpu.SP = 0x4288;
			_cpu.Halted = false;
			_cpu.IFF1 = _cpu.IFF2 = false;
			lock (_lock)
			{
				ClearScreen();
				_cursorOn = false;
				_keys.Clear();
				_line.Length = 0;
			}
			_tapeIn = null;
			_tapeOut = null;
		}

		void Run()
		{
			try
			{
				while (!_stopping && !Finished)
				{
					try
					{
						for (int i = 0; i < 10000 && !Finished; i++)
							_cpu.Step();
					}
					catch (RestartException)
					{
						_restartRequested = false;
						PowerOn();
					}
					if (_cpu.Halted)
						throw new InvalidOperationException(string.Format("The program stopped (HALT at {0:X4}).", _cpu.PC));
				}
			}
			catch (StopException)
			{
			}
			catch (Exception e)
			{
				if (!_stopping)
					lock (_lock) Error = e;
			}
			finally
			{
				Finished = true;
				_waitingForKey = false;
			}
		}

		sealed class StopException : Exception
		{
		}

		sealed class RestartException : Exception
		{
		}

		public void Stop()
		{
			_stopping = true;
			_keyEvent.Set();
		}

		// ---------------------------------------------------------------- ROM routines

		bool Rom(Z80 cpu)
		{
			switch (cpu.PC)
			{
				case 0x0000:
					lock (_lock)
						PutText("\r[The program has ended. Close this window to return to Workbench.]");
					Finished = true;
					return true;
				case RomKeyScan:
					cpu.A = PollKey(15);
					break;
				case RomWaitKey:
					int k;
					while ((k = PollKey(100)) == 0)
					{
					}
					cpu.A = k;
					break;
				case RomPrint:
					lock (_lock) PutChar(cpu.A);
					break;
				case RomCls:
					lock (_lock) ClearScreen();
					break;
				case RomCasOn:
					break;
				case RomCasWriteLeader:
					_tapeOutPath = AskFileName();
					_tapeOut = new List<byte>();
					break;
				case RomCasWriteByte:
					_tapeOut?.Add((byte)cpu.A);
					break;
				case RomCasReadLeader:
					LoadTape(AskFileName());
					break;
				case RomCasReadByte:
					// Past the end of the tape (or with no tape) read a pattern that fails any checksum.
					cpu.A = _tapeIn != null && _tapeInPos < _tapeIn.Length ? _tapeIn[_tapeInPos++] : 0xA5;
					break;
				case RomCasOff:
					FinishTape();
					break;
				default:
					throw new InvalidOperationException(string.Format(
						"The program called a TRS-80 ROM routine this player doesn't have (at {0:X4}, from {1:X4}).",
						cpu.PC, (cpu.Read16(cpu.SP) - 3) & 0xFFFF));
			}
			cpu.Ret();
			return true;
		}

		/// <summary>Takes the next key, waiting up to the given time for one. 0 means none.</summary>
		int PollKey(int waitMs)
		{
			if (_stopping)
				throw new StopException();
			if (_restartRequested)
				throw new RestartException();
			lock (_lock)
			{
				if (_keys.Count > 0)
				{
					_waitingForKey = false;
					return _keys.Dequeue();
				}
				_waitingForKey = true;
			}
			_keyEvent.WaitOne(waitMs);
			if (_stopping)
				throw new StopException();
			if (_restartRequested)
				throw new RestartException();
			return 0;
		}

		// ---------------------------------------------------------------- cassette (the Saves folder)

		string ReadLine()
		{
			var s = new StringBuilder();
			lock (_lock) _cursorOn = true;
			while (true)
			{
				int k = PollKey(100);
				if (k == 0)
					continue;
				lock (_lock)
				{
					if (k == 13)
					{
						PutChar(13);
						return s.ToString();
					}
					if (k == 8 && s.Length > 0)
					{
						s.Length--;
						PutChar(8);
					}
					else if (k >= 32 && k < 127 && s.Length < 40)
					{
						s.Append((char)k);
						PutChar(k);
					}
				}
			}
		}

		string AskFileName()
		{
			lock (_lock)
			{
				// Clear what typing gets queued while the program is busy with the tape.
				_line.Length = 0;
				PutText("\rTAPE FILE NAME (ENTER FOR \"" + _lastSaveName.ToUpperInvariant() + "\"): ");
			}
			string name = ReadLine().Trim();
			if (name.Length == 0)
				name = _lastSaveName;
			foreach (char bad in Path.GetInvalidFileNameChars())
				name = name.Replace(bad, '_');
			name = name.ToLowerInvariant();
			_lastSaveName = name;
			return Path.Combine(_savesDir, name);
		}

		void LoadTape(string path)
		{
			_tapeInPos = 0;
			try
			{
				_tapeIn = File.ReadAllBytes(path);
			}
			catch (Exception)
			{
				_tapeIn = null;
				lock (_lock) PutText("NO FILE CALLED " + Path.GetFileName(path).ToUpperInvariant() + " IN SAVES.\r");
			}
		}

		void FinishTape()
		{
			if (_tapeOut != null && _tapeOutPath != null)
			{
				try
				{
					Directory.CreateDirectory(_savesDir);
					File.WriteAllBytes(_tapeOutPath, _tapeOut.ToArray());
				}
				catch (Exception e)
				{
					lock (_lock) PutText("COULDN'T SAVE: " + e.Message.ToUpperInvariant() + "\r");
				}
			}
			_tapeOut = null;
			_tapeOutPath = null;
			_tapeIn = null;
		}

		// ---------------------------------------------------------------- the screen (the ROM's video driver)

		void ClearScreen()
		{
			for (int r = 0; r < Rows; r++)
				for (int c = 0; c < Cols; c++)
					_screen[r, c] = 32;
			_row = _col = 0;
		}

		void PutText(string s)
		{
			foreach (char c in s)
				PutChar(c == '\n' ? 13 : c);
		}

		void ScrollUp()
		{
			for (int r = 1; r < Rows; r++)
				for (int c = 0; c < Cols; c++)
					_screen[r - 1, c] = _screen[r, c];
			for (int c = 0; c < Cols; c++)
				_screen[Rows - 1, c] = 32;
		}

		void NewLine()
		{
			_col = 0;
			if (++_row >= Rows)
			{
				_row = Rows - 1;
				ScrollUp();
			}
		}

		void PutChar(int ch)
		{
			ch &= 0xFF;
			if (ch >= 0xC0)
			{
				// Space compression: C0-FF print 0-63 spaces.
				for (int i = 0; i < ch - 0xC0; i++)
					PutChar(32);
				return;
			}
			if (ch >= 32)
			{
				_screen[_row, _col] = (byte)ch;
				if (++_col >= Cols)
					NewLine();
				return;
			}
			switch (ch)
			{
				case 0x08: // backspace and erase
					if (_col > 0) _col--;
					else if (_row > 0) { _row--; _col = Cols - 1; }
					_screen[_row, _col] = 32;
					break;
				case 0x0A:
				case 0x0D:
					for (int c = _col; c < Cols; c++)
						_screen[_row, c] = 32;
					NewLine();
					break;
				case 0x0E: _cursorOn = true; break;
				case 0x0F: _cursorOn = false; break;
				case 0x18: if (_col > 0) _col--; break;
				case 0x19: if (_col < Cols - 1) _col++; break;
				case 0x1A: if (_row < Rows - 1) _row++; break;
				case 0x1B: if (_row > 0) _row--; break;
				case 0x1C: _row = _col = 0; break;
				case 0x1D: _col = 0; break;
				case 0x1E:
					for (int c = _col; c < Cols; c++)
						_screen[_row, c] = 32;
					break;
				case 0x1F:
					for (int c = _col; c < Cols; c++)
						_screen[_row, c] = 32;
					for (int r = _row + 1; r < Rows; r++)
						for (int c = 0; c < Cols; c++)
							_screen[r, c] = 32;
					break;
			}
		}

		public void Draw(FrameBuffer fb, int x, int y, int w, int h, bool active)
		{
			// TRS-80 characters sit in a 12-line cell; use that height when the window has room.
			int pitch = h >= Rows * 12 ? 12 : h >= Rows * 10 ? 10 : 8;
			int ox = x + Math.Max(0, (w - Cols * 8) / 2);
			int oy = y + Math.Max(0, (h - Rows * pitch) / 2);
			int textTop = (pitch - 8) / 2;
			lock (_lock)
			{
				for (int r = 0; r < Rows; r++)
				{
					for (int c = 0; c < Cols; c++)
					{
						int ch = _screen[r, c];
						int cx = ox + c * 8, cy = oy + r * pitch;
						if (ch >= 0x80)
							DrawBlock(fb, cx, cy, pitch, ch);
						else if (ch != 32)
							fb.Char(cx, cy + textTop, (char)ch, Pen.White);
					}
				}
				if (active && _cursorOn && !Finished)
					fb.Fill(ox + _col * 8, oy + _row * pitch + textTop, 8, 8, Pen.Orange);
			}
		}

		/// <summary>TRS-80 graphics characters 80-BF: a 2 x 3 grid of blocks, bit 0 top left to bit 5 bottom right.</summary>
		static void DrawBlock(FrameBuffer fb, int x, int y, int pitch, int ch)
		{
			int[] rowsY = { 0, pitch / 3, pitch * 2 / 3, pitch };
			for (int i = 0; i < 6; i++)
			{
				if ((ch & (1 << i)) == 0)
					continue;
				int bx = x + (i & 1) * 4, by = y + rowsY[i >> 1];
				fb.Fill(bx, by, 4, rowsY[(i >> 1) + 1] - rowsY[i >> 1], Pen.White);
			}
		}

		// ---------------------------------------------------------------- keys (main thread)

		void QueueKey(int k)
		{
			lock (_lock) _keys.Enqueue(k);
			_keyEvent.Set();
		}

		public void TypeChar(char c)
		{
			if (c < 32 || c > 126)
				return;
			// The TRS-80 keyboard types capitals unless shifted, and these programs expect them.
			c = char.ToUpperInvariant(c);
			lock (_lock) _line.Append(c);
			QueueKey(c);
		}

		public void Backspace()
		{
			lock (_lock)
				if (_line.Length > 0)
					_line.Length--;
			QueueKey(8);
		}

		public void Enter()
		{
			lock (_lock)
			{
				string line = _line.ToString();
				if (line.Trim().Length > 0)
					_history.Add(line);
				_historyIndex = _history.Count;
				_line.Length = 0;
			}
			QueueKey(13);
		}

		void ReplaceLine(string text)
		{
			int erase;
			lock (_lock)
			{
				erase = _line.Length;
				_line.Length = 0;
				_line.Append(text);
			}
			for (int i = 0; i < erase; i++)
				QueueKey(8);
			foreach (char c in text)
				QueueKey(c);
		}

		public void HistoryUp()
		{
			string text;
			lock (_lock)
			{
				if (_history.Count == 0)
					return;
				_historyIndex = Math.Max(0, _historyIndex - 1);
				text = _history[_historyIndex];
			}
			ReplaceLine(text);
		}

		public void HistoryDown()
		{
			string text;
			lock (_lock)
			{
				if (_historyIndex >= _history.Count)
					return;
				_historyIndex++;
				text = _historyIndex < _history.Count ? _history[_historyIndex] : string.Empty;
			}
			ReplaceLine(text);
		}

		public void Scroll(int lines)
		{
		}

		public void Click(int x, int y)
		{
		}

		/// <summary>The Story menu. Restore is LOAD here, and Restart loads the program again from the start.</summary>
		public bool InjectCommand(string command)
		{
			if (Mode != InputMode.Line)
				return false;
			if (command == "restart")
			{
				_restartRequested = true;
				_keyEvent.Set();
				return true;
			}
			if (command == "restore")
				command = "load";
			ReplaceLine(command.ToUpperInvariant());
			Enter();
			return true;
		}

		public void Print(string s)
		{
			lock (_lock) PutText(s.ToUpperInvariant());
		}
	}
}
