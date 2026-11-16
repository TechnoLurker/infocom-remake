using System.Diagnostics;

// Patched: logging compiled out unless ZMACHINE_LOG is defined (it built a string for every opcode).
using System.Text;

namespace ZMachineLib
{
	internal static class Log
	{
		private static readonly StringBuilder _output = new StringBuilder();

		[Conditional("ZMACHINE_LOG")]
		public static void Write(string s)
		{
			_output.Append(s);
		}

		[Conditional("ZMACHINE_LOG")]
		public static void WriteLine(string s)
		{
			_output.AppendLine(s);
		}

		[Conditional("ZMACHINE_LOG")]
		public static void Flush()
		{
			Print();
			_output.Clear();
		}

		[Conditional("ZMACHINE_LOG")]
		public static void Print()
		{
			Debug.WriteLine(_output.ToString());
		}
	}
}
