using System.Collections.Generic;

namespace ZMachineLib
{
	internal class ZStackFrame
	{
		public uint PC { get; set; }
		public Stack<ushort> RoutineStack { get; set; }
		public ushort[] Variables { get; set; }
		public bool StoreResult { get; set; }
		public int ArgumentCount { get; set; }

		// Patched: an interrupt routine (timed input, newline countdown) called by the interpreter
		// itself. Its return value goes back to the interpreter, not into a variable.
		public bool Interrupt { get; set; }

		public ZStackFrame()
		{
			Variables = new ushort[0x10];
			RoutineStack = new Stack<ushort>();
			StoreResult = true;
		}

		public ZStackFrame Clone()
		{
			var values = RoutineStack.ToArray();
			System.Array.Reverse(values);
			return new ZStackFrame
			{
				PC = PC,
				RoutineStack = new Stack<ushort>(values),
				Variables = (ushort[])Variables.Clone(),
				StoreResult = StoreResult,
				ArgumentCount = ArgumentCount,
				Interrupt = Interrupt,
			};
		}
	}
}
