namespace ZMachineLib
{
	/// <summary>Result of a line read in a Version 6 story.</summary>
	public struct LineInput
	{
		/// <summary>The text typed (not lowercased). On a timeout, what has been typed so far.</summary>
		public string Text;
		/// <summary>The ZSCII key that ended input: 13 for Enter, 254/253 for a mouse click, 0 on a timeout.</summary>
		public int Terminator;
		public bool TimedOut;
	}

	/// <summary>
	/// The Version 6 screen model (Z-Machine Standard 1.1, section 8.8): eight pixel windows,
	/// pictures, and the mouse. Added for the Workbench player. The host keeps the window
	/// state, because it is the one that measures and draws text. Units are pixels.
	/// Window numbers passed in are already resolved: -3 (current window) never reaches the host.
	/// </summary>
	public interface IZMachineV6IO : IZMachineIO
	{
		int ScreenWidth { get; }
		int ScreenHeight { get; }
		int FontWidth { get; }
		int FontHeight { get; }
		byte DefaultForeground { get; }
		byte DefaultBackground { get; }
		bool ColoursAvailable { get; }
		bool PicturesAvailable { get; }
		int CurrentWindow { get; }

		void Reset();
		void SelectWindow(int window);
		void Split(int pixels);
		void Erase(int window);
		void EraseLine(int pixels);
		void MoveCursor(int y, int x, int window);
		void CursorVisible(bool visible);
		void GetCursorPosition(out int y, out int x);
		void SetStyle(int style);
		void SetColours(int foreground, int background, int window);
		/// <summary>Returns the previous font, or 0 if the font isn't available.</summary>
		int SetFont(int font, int window);
		void SetMargins(int left, int right, int window);
		void MoveWindow(int window, int y, int x);
		void WindowSize(int window, int y, int x);
		void WindowStyle(int window, int flags, int operation);
		int GetWindowProperty(int window, int property);
		void PutWindowProperty(int window, int property, int value);
		void ScrollWindow(int window, int pixels);
		int BufferScreen(int mode);
		/// <summary>Width in pixels of the text, as it would print in the current window's font.</summary>
		int TextWidth(string s);

		/// <summary>Picture 0: height = number of pictures, width = release. Returns false if the picture doesn't exist.</summary>
		bool PictureData(int picture, out int height, out int width);
		void DrawPicture(int picture, int y, int x);
		void ErasePicture(int picture, int y, int x);

		/// <summary>Reads a line. timeoutTenths 0 = wait forever. After a timeout the next call carries on with the same partial line.</summary>
		LineInput ReadLine(int maxChars, string initial, int timeoutTenths, byte[] terminators);
		/// <summary>Reads one key as ZSCII, or returns 0 on a timeout.</summary>
		int ReadKey(int timeoutTenths);
		void GetMouse(out int y, out int x, out int buttons, out int menu);
		void MouseWindow(int window);

		/// <summary>The packed address of a newline-interrupt routine that is now due (property 8), or 0.</summary>
		int TakeNewlineInterrupt();
	}
}
