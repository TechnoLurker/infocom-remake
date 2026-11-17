using System;
using System.IO;

namespace InfocomWorkbench
{
	/// <summary>A running story as the Workbench sees it: something that draws into a window and takes keys and clicks.</summary>
	public interface IStorySession
	{
		string StoryPath { get; }
		string Title { get; }
		int Version { get; }
		bool Finished { get; }
		Exception Error { get; }
		InputMode Mode { get; }

		/// <summary>Starts the story for a text area of this many pixels.</summary>
		void Start(int width, int height);
		void Stop();
		void Draw(FrameBuffer fb, int x, int y, int w, int h, bool active);

		void TypeChar(char c);
		void Backspace();
		void Enter();
		void HistoryUp();
		void HistoryDown();
		void Scroll(int lines);
		/// <summary>A left click at a position relative to the text area's top-left corner.</summary>
		void Click(int x, int y);
		bool InjectCommand(string command);
		void Print(string s);
	}

	public static class StorySessions
	{
		/// <summary>Picks the right session: a TRS-80 program, or a story for its Z-machine version.</summary>
		public static IStorySession Create(string storyPath, string savesDir)
		{
			if (Trs80.TrsProgram.IsTrsFile(storyPath))
				return new Trs80.Trs80Session(storyPath, savesDir);
			int version = 0;
			try
			{
				using (var fs = File.OpenRead(storyPath))
					version = fs.ReadByte();
			}
			catch (Exception)
			{
			}
			if (version == 6)
				return new V6Session(storyPath, savesDir);
			return new GameSession(storyPath, savesDir);
		}
	}
}
