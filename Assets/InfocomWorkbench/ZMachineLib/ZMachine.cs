// Based on ZMachineLib (c) 2016 Brian Peek, MIT licence (LICENSE.txt).
// Reworked for the Workbench player against the Z-Machine Standard 1.1:
// versions 1-6 (6 = Zork Zero, Shogun, Journey, Arthur), output stream 3, catch/throw,
// undo, timed input, custom alphabets, a dictionary lookup by encoded text, and fixes to
// remove_obj, put_prop, clear_attr, load and copy_table. Saves use a small binary format.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ZMachineLib
{
	public class ZMachine
	{
		private const int HeaderVersion = 0x00;
		private const int HeaderInitialPC = 0x06;
		private const int HeaderDictionary = 0x08;
		private const int HeaderObjectTable = 0x0a;
		private const int HeaderGlobals = 0x0c;
		private const int HeaderStaticMemory = 0x0e;
		private const int HeaderFlags2 = 0x10;
		private const int HeaderAbbreviations = 0x18;
		private const int HeaderFileLength = 0x1a;
		private const int HeaderChecksum = 0x1c;
		private const int HeaderRoutinesOffset = 0x28;
		private const int HeaderStringsOffset = 0x2a;
		private const int HeaderTerminators = 0x2e;
		private const int HeaderStream3Width = 0x30;
		private const int HeaderAlphabet = 0x34;
		private const int HeaderExtension = 0x36;

		private readonly IZMachineIO _io;
		private IZMachineV6IO _io6;
		private byte[] _memory;
		private byte[] _story;
		private Stream _file;
		private volatile bool _running;
		private Random _random = new Random();

		private byte _version;
		private ushort _globals;
		private ushort _objectTable;
		private ushort _abbreviationsTable;
		private ushort _dictionary;
		private ushort _dynamicMemory;
		private uint _routinesOffset;
		private uint _stringsOffset;

		private int _parentOffset, _siblingOffset, _childOffset, _propertyOffset, _objectSize, _defaultsSize;

		private Stack<ZStackFrame> _stack = new Stack<ZStackFrame>();
		private ushort _interruptResult;
		private bool _inNewlineInterrupt;

		private readonly Opcode[] _0Opcodes = new Opcode[0x10];
		private readonly Opcode[] _1Opcodes = new Opcode[0x10];
		private readonly Opcode[] _2Opcodes = new Opcode[0x20];
		private readonly Opcode[] _varOpcodes = new Opcode[0x20];
		private readonly Opcode[] _extOpcodes = new Opcode[0x20];

		// Output streams.
		private bool _screenOutput = true;
		private readonly Stack<KeyValuePair<ushort, StringBuilder>> _stream3 = new Stack<KeyValuePair<ushort, StringBuilder>>();

		// Text.
		private string[] _alphabets;
		private const string DefaultA0 = "abcdefghijklmnopqrstuvwxyz";
		private const string DefaultA1 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
		private const string DefaultA2 = " \n0123456789.,!?_#'\"/\\-:()";
		// ZSCII 155-223 (Standard 1.1, 3.8.7).
		private const string ExtraChars = "\u00e4\u00f6\u00fc\u00c4\u00d6\u00dc\u00df\u00bb\u00ab\u00eb\u00ef\u00ff\u00cb\u00cf\u00e1\u00e9\u00ed\u00f3\u00fa\u00fd\u00c1\u00c9\u00cd\u00d3\u00da\u00dd\u00e0\u00e8\u00ec\u00f2\u00f9\u00c0\u00c8\u00cc\u00d2\u00d9\u00e2\u00ea\u00ee\u00f4\u00fb\u00c2\u00ca\u00ce\u00d4\u00db\u00e5\u00c5\u00f8\u00d8\u00e3\u00f1\u00f5\u00c3\u00d1\u00d5\u00e6\u00c6\u00e7\u00c7\u00fe\u00f0\u00de\u00d0\u00a3\u0153\u0152\u00a1\u00bf";

		// Undo.
		private byte[] _undoMemory;
		private ZStackFrame[] _undoStack;

		private int _font = 1;

		public ZMachine(IZMachineIO io)
		{
			_io = io;
		}

		// Patched: screen size reported to the game (v1-5; v6 asks the host).
		public byte ScreenRows { get; set; } = 25;
		public byte ScreenColumns { get; set; } = 80;
		public byte StoryVersion => _version;

		/// <summary>Lets the host stop the interpreter thread.</summary>
		public void Stop()
		{
			_running = false;
		}

		private static void Fill(Opcode[] opcodes)
		{
			for(int i = 0; i < opcodes.Length; i++)
				opcodes[i] = new Opcode { Name = "UNKNOWN", Handler = null };
		}

		private void Op(Opcode[] table, int number, string name, OpcodeHandler handler)
		{
			table[number] = new Opcode { Name = name, Handler = handler };
		}

		private void SetupOpcodes()
		{
			Fill(_0Opcodes); Fill(_1Opcodes); Fill(_2Opcodes); Fill(_varOpcodes); Fill(_extOpcodes);

			Op(_0Opcodes, 0x00, "RTRUE", a => ReturnValue(1));
			Op(_0Opcodes, 0x01, "RFALSE", a => ReturnValue(0));
			Op(_0Opcodes, 0x02, "PRINT", Print);
			Op(_0Opcodes, 0x03, "PRINT_RET", PrintRet);
			Op(_0Opcodes, 0x04, "NOP", a => { });
			if(_version <= 4)
			{
				Op(_0Opcodes, 0x05, "SAVE", Save);
				Op(_0Opcodes, 0x06, "RESTORE", Restore);
			}
			Op(_0Opcodes, 0x07, "RESTART", a => Restart());
			Op(_0Opcodes, 0x08, "RET_POPPED", a => ReturnValue(PopStack()));
			if(_version <= 4)
				Op(_0Opcodes, 0x09, "POP", a => PopStack());
			else
				Op(_0Opcodes, 0x09, "CATCH", a => Store((ushort)_stack.Count));
			Op(_0Opcodes, 0x0a, "QUIT", Quit);
			Op(_0Opcodes, 0x0b, "NEW_LINE", a => Output("\n"));
			Op(_0Opcodes, 0x0c, "SHOW_STATUS", ShowStatus);
			Op(_0Opcodes, 0x0d, "VERIFY", Verify);
			Op(_0Opcodes, 0x0f, "PIRACY", a => Branch(true));

			Op(_1Opcodes, 0x00, "JZ", a => Branch(a[0] == 0));
			Op(_1Opcodes, 0x01, "GET_SIBLING", GetSibling);
			Op(_1Opcodes, 0x02, "GET_CHILD", GetChild);
			Op(_1Opcodes, 0x03, "GET_PARENT", a => Store(GetObjectLink(a[0], _parentOffset)));
			Op(_1Opcodes, 0x04, "GET_PROP_LEN", a => Store((ushort)GetPropLen(a[0])));
			Op(_1Opcodes, 0x05, "INC", a => SetVariableInPlace((byte)a[0], (ushort)(GetVariableInPlace((byte)a[0]) + 1)));
			Op(_1Opcodes, 0x06, "DEC", a => SetVariableInPlace((byte)a[0], (ushort)(GetVariableInPlace((byte)a[0]) - 1)));
			Op(_1Opcodes, 0x07, "PRINT_ADDR", a => Output(DecodeString(a[0])));
			Op(_1Opcodes, 0x08, "CALL_1S", a => Call(a, true));
			Op(_1Opcodes, 0x09, "REMOVE_OBJ", a => Unlink(a[0]));
			Op(_1Opcodes, 0x0a, "PRINT_OBJ", a => Output(GetObjectName(a[0])));
			Op(_1Opcodes, 0x0b, "RET", a => ReturnValue(a[0]));
			Op(_1Opcodes, 0x0c, "JUMP", Jump);
			Op(_1Opcodes, 0x0d, "PRINT_PADDR", a => Output(DecodeString(GetPackedAddress(a[0], false))));
			Op(_1Opcodes, 0x0e, "LOAD", a => Store(GetVariableInPlace((byte)a[0])));
			if(_version <= 4)
				Op(_1Opcodes, 0x0f, "NOT", a => Store((ushort)~a[0]));
			else
				Op(_1Opcodes, 0x0f, "CALL_1N", a => Call(a, false));

			Op(_2Opcodes, 0x01, "JE", Je);
			Op(_2Opcodes, 0x02, "JL", a => Branch((short)a[0] < (short)a[1]));
			Op(_2Opcodes, 0x03, "JG", a => Branch((short)a[0] > (short)a[1]));
			Op(_2Opcodes, 0x04, "DEC_CHK", a =>
			{
				short v = (short)(GetVariableInPlace((byte)a[0]) - 1);
				SetVariableInPlace((byte)a[0], (ushort)v);
				Branch(v < (short)a[1]);
			});
			Op(_2Opcodes, 0x05, "INC_CHK", a =>
			{
				short v = (short)(GetVariableInPlace((byte)a[0]) + 1);
				SetVariableInPlace((byte)a[0], (ushort)v);
				Branch(v > (short)a[1]);
			});
			Op(_2Opcodes, 0x06, "JIN", a => Branch(GetObjectLink(a[0], _parentOffset) == a[1]));
			Op(_2Opcodes, 0x07, "TEST", a => Branch((a[0] & a[1]) == a[1]));
			Op(_2Opcodes, 0x08, "OR", a => Store((ushort)(a[0] | a[1])));
			Op(_2Opcodes, 0x09, "AND", a => Store((ushort)(a[0] & a[1])));
			Op(_2Opcodes, 0x0a, "TEST_ATTR", a => Branch(TestAttribute(a[0], a[1])));
			Op(_2Opcodes, 0x0b, "SET_ATTR", a => SetAttribute(a[0], a[1], true));
			Op(_2Opcodes, 0x0c, "CLEAR_ATTR", a => SetAttribute(a[0], a[1], false));
			Op(_2Opcodes, 0x0d, "STORE", a => SetVariableInPlace((byte)a[0], a[1]));
			Op(_2Opcodes, 0x0e, "INSERT_OBJ", a => InsertObject(a[0], a[1]));
			Op(_2Opcodes, 0x0f, "LOADW", a => Store(GetWord((ushort)(a[0] + 2 * a[1]))));
			Op(_2Opcodes, 0x10, "LOADB", a => Store(_memory[(ushort)(a[0] + a[1])]));
			Op(_2Opcodes, 0x11, "GET_PROP", GetProp);
			Op(_2Opcodes, 0x12, "GET_PROP_ADDR", a => Store((ushort)FindProperty(a[0], a[1], out _)));
			Op(_2Opcodes, 0x13, "GET_NEXT_PROP", a => Store((ushort)GetNextProp(a[0], a[1])));
			Op(_2Opcodes, 0x14, "ADD", a => Store((ushort)(a[0] + a[1])));
			Op(_2Opcodes, 0x15, "SUB", a => Store((ushort)(a[0] - a[1])));
			Op(_2Opcodes, 0x16, "MUL", a => Store((ushort)((short)a[0] * (short)a[1])));
			Op(_2Opcodes, 0x17, "DIV", a => Store(a[1] == 0 ? (ushort)0 : (ushort)((short)a[0] / (short)a[1])));
			Op(_2Opcodes, 0x18, "MOD", a => Store(a[1] == 0 ? (ushort)0 : (ushort)((short)a[0] % (short)a[1])));
			Op(_2Opcodes, 0x19, "CALL_2S", a => Call(a, true));
			Op(_2Opcodes, 0x1a, "CALL_2N", a => Call(a, false));
			Op(_2Opcodes, 0x1b, "SET_COLOUR", SetColour);
			Op(_2Opcodes, 0x1c, "THROW", Throw);

			Op(_varOpcodes, 0x00, "CALL_VS", a => Call(a, true));
			Op(_varOpcodes, 0x01, "STOREW", a => StoreWord((ushort)(a[0] + 2 * a[1]), a[2]));
			Op(_varOpcodes, 0x02, "STOREB", a => _memory[(ushort)(a[0] + a[1])] = (byte)a[2]);
			Op(_varOpcodes, 0x03, "PUT_PROP", PutProp);
			Op(_varOpcodes, 0x04, "READ", Read);
			Op(_varOpcodes, 0x05, "PRINT_CHAR", a => Output(ZsciiToString(a[0])));
			Op(_varOpcodes, 0x06, "PRINT_NUM", a => Output(((short)a[0]).ToString()));
			Op(_varOpcodes, 0x07, "RANDOM", RandomOp);
			Op(_varOpcodes, 0x08, "PUSH", a => _stack.Peek().RoutineStack.Push(a[0]));
			Op(_varOpcodes, 0x09, "PULL", Pull);
			Op(_varOpcodes, 0x0a, "SPLIT_WINDOW", SplitWindow);
			Op(_varOpcodes, 0x0b, "SET_WINDOW", SetWindow);
			Op(_varOpcodes, 0x0c, "CALL_VS2", a => Call(a, true));
			Op(_varOpcodes, 0x0d, "ERASE_WINDOW", EraseWindow);
			Op(_varOpcodes, 0x0e, "ERASE_LINE", EraseLine);
			Op(_varOpcodes, 0x0f, "SET_CURSOR", SetCursor);
			Op(_varOpcodes, 0x10, "GET_CURSOR", GetCursor);
			Op(_varOpcodes, 0x11, "SET_TEXT_STYLE", SetTextStyle);
			Op(_varOpcodes, 0x12, "BUFFER_MODE", a => { if(_io6 == null) _io.BufferMode(a[0] == 1); });
			Op(_varOpcodes, 0x13, "OUTPUT_STREAM", OutputStream);
			Op(_varOpcodes, 0x14, "INPUT_STREAM", a => { });
			Op(_varOpcodes, 0x15, "SOUND_EFFECT", a => { if(a.Count > 0) _io.SoundEffect(a[0]); });
			Op(_varOpcodes, 0x16, "READ_CHAR", ReadChar);
			Op(_varOpcodes, 0x17, "SCAN_TABLE", ScanTable);
			Op(_varOpcodes, 0x18, "NOT", a => Store((ushort)~a[0]));
			Op(_varOpcodes, 0x19, "CALL_VN", a => Call(a, false));
			Op(_varOpcodes, 0x1a, "CALL_VN2", a => Call(a, false));
			Op(_varOpcodes, 0x1b, "TOKENISE", a => Tokenise(a[0], a[1], a.Count > 2 ? a[2] : (ushort)0, a.Count > 3 && a[3] != 0));
			Op(_varOpcodes, 0x1c, "ENCODE_TEXT", EncodeTextOp);
			Op(_varOpcodes, 0x1d, "COPY_TABLE", CopyTable);
			Op(_varOpcodes, 0x1e, "PRINT_TABLE", PrintTable);
			Op(_varOpcodes, 0x1f, "CHECK_ARG_COUNT", a => Branch(a[0] <= _stack.Peek().ArgumentCount));

			Op(_extOpcodes, 0x00, "SAVE", Save);
			Op(_extOpcodes, 0x01, "RESTORE", Restore);
			Op(_extOpcodes, 0x02, "LOG_SHIFT", a =>
			{
				short places = (short)a[1];
				Store(places >= 0 ? (ushort)(a[0] << places) : (ushort)(a[0] >> -places));
			});
			Op(_extOpcodes, 0x03, "ART_SHIFT", a =>
			{
				short places = (short)a[1];
				Store(places >= 0 ? (ushort)((short)a[0] << places) : (ushort)((short)a[0] >> -places));
			});
			Op(_extOpcodes, 0x04, "SET_FONT", SetFont);
			Op(_extOpcodes, 0x09, "SAVE_UNDO", a => SaveUndo());
			Op(_extOpcodes, 0x0a, "RESTORE_UNDO", a => RestoreUndo());
			Op(_extOpcodes, 0x0b, "PRINT_UNICODE", a => Output(((char)a[0]).ToString()));
			Op(_extOpcodes, 0x0c, "CHECK_UNICODE", a => Store((ushort)(a[0] < 256 ? 3 : 0)));
			Op(_extOpcodes, 0x0d, "SET_TRUE_COLOUR", a => { });

			if(_version == 6)
			{
				Op(_extOpcodes, 0x05, "DRAW_PICTURE", a => { _io6.DrawPicture(a[0], Arg(a, 1), Arg(a, 2)); });
				Op(_extOpcodes, 0x06, "PICTURE_DATA", PictureData);
				Op(_extOpcodes, 0x07, "ERASE_PICTURE", a => { _io6.ErasePicture(a[0], Arg(a, 1), Arg(a, 2)); });
				Op(_extOpcodes, 0x08, "SET_MARGINS", a => _io6.SetMargins(a[0], Arg(a, 1), a.Count > 2 ? Window(a[2]) : _io6.CurrentWindow));
				Op(_extOpcodes, 0x10, "MOVE_WINDOW", a => _io6.MoveWindow(Window(a[0]), a[1], a[2]));
				Op(_extOpcodes, 0x11, "WINDOW_SIZE", a => _io6.WindowSize(Window(a[0]), a[1], a[2]));
				Op(_extOpcodes, 0x12, "WINDOW_STYLE", a => _io6.WindowStyle(Window(a[0]), a[1], Arg(a, 2)));
				Op(_extOpcodes, 0x13, "GET_WIND_PROP", a => Store((ushort)_io6.GetWindowProperty(Window(a[0]), a[1])));
				Op(_extOpcodes, 0x14, "SCROLL_WINDOW", a => _io6.ScrollWindow(Window(a[0]), (short)a[1]));
				Op(_extOpcodes, 0x15, "POP_STACK", PopUserStack);
				Op(_extOpcodes, 0x16, "READ_MOUSE", ReadMouse);
				Op(_extOpcodes, 0x17, "MOUSE_WINDOW", a => _io6.MouseWindow((short)a[0] == -1 ? -1 : Window(a[0])));
				Op(_extOpcodes, 0x18, "PUSH_STACK", PushUserStack);
				Op(_extOpcodes, 0x19, "PUT_WIND_PROP", a => _io6.PutWindowProperty(Window(a[0]), a[1], (short)a[2]));
				Op(_extOpcodes, 0x1a, "PRINT_FORM", PrintForm);
				Op(_extOpcodes, 0x1b, "MAKE_MENU", a => Branch(false));
				Op(_extOpcodes, 0x1c, "PICTURE_TABLE", a => { });
				Op(_extOpcodes, 0x1d, "BUFFER_SCREEN", a => Store((ushort)_io6.BufferScreen((short)a[0])));
			}
		}

		private static int Arg(List<ushort> a, int i) => i < a.Count ? a[i] : 0;

		private int Window(ushort w)
		{
			short s = (short)w;
			return s == -3 ? _io6.CurrentWindow : s;
		}

		// ================================================================ loading

		public void LoadFile(Stream stream)
		{
			_file = stream;
			_story = new byte[stream.Length];
			stream.Seek(0, SeekOrigin.Begin);
			int read = 0;
			while(read < _story.Length)
			{
				int n = stream.Read(_story, read, _story.Length - read);
				if(n <= 0)
					break;
				read += n;
			}
			Start();
		}

		private void Start()
		{
			_memory = (byte[])_story.Clone();
			_stack.Clear();
			_stream3.Clear();
			_screenOutput = true;
			_undoMemory = null;
			_undoStack = null;
			_font = 1;

			_version = _memory[HeaderVersion];
			if(_version < 1 || _version > 8 || _version == 7 || _version == 8)
				throw new NotSupportedException("Z-machine version " + _version + " isn't supported.");
			_io6 = _version == 6 ? _io as IZMachineV6IO : null;
			if(_version == 6 && _io6 == null)
				throw new NotSupportedException("This host can't show version 6 stories.");

			_dictionary = GetWord(HeaderDictionary);
			_objectTable = GetWord(HeaderObjectTable);
			_globals = GetWord(HeaderGlobals);
			_abbreviationsTable = GetWord(HeaderAbbreviations);
			_dynamicMemory = GetWord(HeaderStaticMemory);
			_routinesOffset = (uint)GetWord(HeaderRoutinesOffset) * 8;
			_stringsOffset = (uint)GetWord(HeaderStringsOffset) * 8;

			if(_version <= 3)
			{
				_parentOffset = 4; _siblingOffset = 5; _childOffset = 6; _propertyOffset = 7;
				_objectSize = 9; _defaultsSize = 62;
			}
			else
			{
				_parentOffset = 6; _siblingOffset = 8; _childOffset = 10; _propertyOffset = 12;
				_objectSize = 14; _defaultsSize = 126;
			}

			LoadAlphabets();
			WriteInterpreterHeader();
			SetupOpcodes();

			if(_io6 != null)
				_io6.Reset();

			if(_version == 6)
			{
				// Version 6 starts by calling a "main" routine (packed address at $06).
				EnterRoutine(GetPackedAddress(GetWord(HeaderInitialPC), true), null, false);
			}
			else
				_stack.Push(new ZStackFrame { PC = GetWord(HeaderInitialPC) });
		}

		/// <summary>The fields an interpreter fills in. Re-applied after restart, restore and undo.</summary>
		private void WriteInterpreterHeader()
		{
			if(_version <= 3)
				_memory[0x01] = (byte)((_memory[0x01] & 0x02) | 0x20); // keep "time game", split screen available
			else if(_version <= 5)
				_memory[0x01] = 0x10; // fixed-pitch font
			else
			{
				byte f = 0x04 | 0x08 | 0x10 | 0x80; // bold, italic, fixed pitch, timed input
				if(_io6.ColoursAvailable) f |= 0x01;
				if(_io6.PicturesAvailable) f |= 0x02;
				_memory[0x01] = f;
			}

			// Flags 2: clear what we can't do. Keep transcript and fixed-pitch bits.
			ushort flags2 = GetWord(HeaderFlags2);
			if(_version >= 5)
			{
				flags2 &= unchecked((ushort)~(0x08 | 0x80 | 0x100)); // sound, menus
				if(_version != 6 || !_io6.PicturesAvailable) flags2 &= unchecked((ushort)~0x08);
				if(_version != 6) flags2 &= unchecked((ushort)~0x20); // mouse only in v6 here
				if(_version != 6 || !_io6.ColoursAvailable) flags2 &= unchecked((ushort)~0x40);
			}
			StoreWord(HeaderFlags2, flags2);

			if(_version >= 4)
			{
				_memory[0x1e] = 4; // interpreter number: Amiga, as Infocom's was
				_memory[0x1f] = _version == 6 ? (byte)3 : (byte)'C';
			}

			if(_version == 6)
			{
				int fw = _io6.FontWidth, fh = _io6.FontHeight;
				_memory[0x20] = (byte)Math.Min(255, _io6.ScreenHeight / fh);
				_memory[0x21] = (byte)Math.Min(255, _io6.ScreenWidth / fw);
				StoreWord(0x22, (ushort)_io6.ScreenWidth);
				StoreWord(0x24, (ushort)_io6.ScreenHeight);
				_memory[0x26] = (byte)fh; // v6: height then width
				_memory[0x27] = (byte)fw;
				_memory[0x2c] = _io6.DefaultBackground;
				_memory[0x2d] = _io6.DefaultForeground;
			}
			else if(_version >= 4)
			{
				_memory[0x20] = ScreenRows;
				_memory[0x21] = ScreenColumns;
				if(_version >= 5)
				{
					StoreWord(0x22, ScreenColumns);
					StoreWord(0x24, ScreenRows);
					_memory[0x26] = 1; _memory[0x27] = 1;
					_memory[0x2c] = 2; _memory[0x2d] = 9;
				}
			}
			else
			{
				_memory[0x20] = ScreenRows;
				_memory[0x21] = ScreenColumns;
			}

			// Standard 1.0.
			_memory[0x32] = 1;
			_memory[0x33] = 0;
		}

		private void LoadAlphabets()
		{
			ushort table = _version >= 5 ? GetWord(HeaderAlphabet) : (ushort)0;
			if(table == 0)
			{
				_alphabets = new[] { DefaultA0, DefaultA1, DefaultA2 };
				return;
			}
			_alphabets = new string[3];
			for(int a = 0; a < 3; a++)
			{
				var sb = new StringBuilder();
				for(int i = 0; i < 26; i++)
					sb.Append(ZsciiToString(_memory[table + a * 26 + i]));
				_alphabets[a] = sb.ToString();
			}
			// A2 positions 0 and 1 are the escape and newline codes, whatever the table says.
			_alphabets[2] = " \n" + _alphabets[2].Substring(2);
		}

		// ================================================================ running

		public void Run(bool terminateOnInput = false)
		{
			_running = true;
			while(_running)
				Step();
		}

		private void Step()
		{
			ZStackFrame frame = _stack.Peek();
			byte o = _memory[frame.PC++];
			Opcode opcode;
			var args = new List<ushort>(4);

			if(o == 0xbe && _version >= 5)
			{
				byte number = _memory[frame.PC++];
				opcode = _extOpcodes[number & 0x1f];
				if(number > 0x1f)
					opcode = new Opcode { Name = "EXT_" + number };
				ReadVariableOperands(_memory[frame.PC++], args);
			}
			else if(o < 0x80)
			{
				// Long form 2OP.
				args.Add(ReadOperand((o & 0x40) != 0 ? OperandType.Variable : OperandType.SmallConstant));
				args.Add(ReadOperand((o & 0x20) != 0 ? OperandType.Variable : OperandType.SmallConstant));
				opcode = _2Opcodes[o & 0x1f];
			}
			else if(o < 0xb0)
			{
				args.Add(ReadOperand((OperandType)((o >> 4) & 0x03)));
				opcode = _1Opcodes[o & 0x0f];
			}
			else if(o < 0xc0)
				opcode = _0Opcodes[o & 0x0f];
			else
			{
				byte types = _memory[_stack.Peek().PC++];
				bool doubleTypes = o == 0xec || o == 0xfa;
				byte types2 = doubleTypes ? _memory[_stack.Peek().PC++] : (byte)0xff;
				ReadVariableOperands(types, args);
				if(doubleTypes && args.Count == 4)
					ReadVariableOperands(types2, args);
				opcode = o < 0xe0 ? _2Opcodes[o & 0x1f] : _varOpcodes[o & 0x1f];
			}

			if(opcode.Handler == null)
				throw new Exception($"Unknown opcode {o:X2} ({opcode.Name}) at {frame.PC:X5}");
			opcode.Handler(args);
		}

		/// <summary>Runs an interrupt routine (timed input, newline countdown) to completion and returns its result.</summary>
		private ushort CallInterrupt(ushort packed)
		{
			if(packed == 0)
				return 0;
			int depth = _stack.Count;
			EnterRoutine(GetPackedAddress(packed, true), null, false);
			_stack.Peek().Interrupt = true;
			_interruptResult = 0;
			while(_running && _stack.Count > depth)
				Step();
			return _interruptResult;
		}

		private void ReadVariableOperands(byte types, List<ushort> args)
		{
			for(int i = 6; i >= 0; i -= 2)
			{
				int type = (types >> i) & 0x03;
				if(type == 0x03)
					break;
				args.Add(ReadOperand((OperandType)type));
			}
		}

		private ushort ReadOperand(OperandType type)
		{
			ZStackFrame f = _stack.Peek();
			switch(type)
			{
				case OperandType.LargeConstant:
					ushort w = GetWord(f.PC);
					f.PC += 2;
					return w;
				case OperandType.SmallConstant:
					return _memory[f.PC++];
				default:
					return GetVariable(_memory[f.PC++]);
			}
		}

		// ================================================================ routines

		private void EnterRoutine(uint address, List<ushort> args, bool storeResult)
		{
			var f = new ZStackFrame { PC = address, StoreResult = storeResult };
			int count = _memory[f.PC++];
			if(count > 15)
				throw new Exception($"Bad routine at {address:X5}");
			if(_version <= 4)
			{
				for(int i = 0; i < count; i++)
				{
					f.Variables[i] = GetWord(f.PC);
					f.PC += 2;
				}
			}
			int supplied = args == null ? 0 : args.Count - 1;
			for(int i = 0; i < supplied && i < count; i++)
				f.Variables[i] = args[i + 1];
			f.ArgumentCount = supplied;
			_stack.Push(f);
		}

		private void Call(List<ushort> args, bool storeResult)
		{
			if(args[0] == 0)
			{
				if(storeResult)
					Store(0);
				return;
			}
			EnterRoutine(GetPackedAddress(args[0], true), args, storeResult);
		}

		private void ReturnValue(ushort value)
		{
			ZStackFrame f = _stack.Pop();
			if(f.Interrupt)
			{
				_interruptResult = value;
				return;
			}
			if(_stack.Count == 0)
			{
				// Version 6 main routine returned: the story is over.
				_running = false;
				_io.Quit();
				return;
			}
			if(f.StoreResult)
				Store(value);
		}

		private void Throw(List<ushort> args)
		{
			int frame = args[1];
			while(_stack.Count > frame && _stack.Count > 1)
				_stack.Pop();
			ReturnValue(args[0]);
		}

		private void Jump(List<ushort> args)
		{
			_stack.Peek().PC = (uint)(_stack.Peek().PC + (short)args[0] - 2);
		}

		private void Je(List<ushort> args)
		{
			bool equal = false;
			for(int i = 1; i < args.Count; i++)
				if(args[0] == args[i])
					equal = true;
			Branch(equal);
		}

		private void Branch(bool condition)
		{
			ZStackFrame f = _stack.Peek();
			byte b = _memory[f.PC++];
			bool onTrue = (b & 0x80) != 0;
			int offset;
			if((b & 0x40) != 0)
				offset = b & 0x3f;
			else
			{
				offset = ((b & 0x3f) << 8) | _memory[f.PC++];
				if((offset & 0x2000) != 0)
					offset -= 0x4000;
			}
			if(condition != onTrue)
				return;
			if(offset == 0)
				ReturnValue(0);
			else if(offset == 1)
				ReturnValue(1);
			else
				f.PC = (uint)(f.PC + offset - 2);
		}

		private void Store(ushort value)
		{
			byte dest = _memory[_stack.Peek().PC++];
			SetVariable(dest, value);
		}

		// ================================================================ variables

		private ushort PopStack()
		{
			var s = _stack.Peek().RoutineStack;
			if(s.Count == 0)
				throw new Exception("Stack underflow");
			return s.Pop();
		}

		private ushort GetVariable(byte variable)
		{
			if(variable == 0)
				return PopStack();
			if(variable < 0x10)
				return _stack.Peek().Variables[variable - 1];
			return GetWord((ushort)(_globals + 2 * (variable - 0x10)));
		}

		private void SetVariable(byte variable, ushort value)
		{
			if(variable == 0)
				_stack.Peek().RoutineStack.Push(value);
			else if(variable < 0x10)
				_stack.Peek().Variables[variable - 1] = value;
			else
				StoreWord((ushort)(_globals + 2 * (variable - 0x10)), value);
		}

		/// <summary>For opcodes that name a variable (inc, store, load...): the stack is read or written in place.</summary>
		private ushort GetVariableInPlace(byte variable)
		{
			if(variable == 0)
			{
				var s = _stack.Peek().RoutineStack;
				if(s.Count == 0)
					throw new Exception("Stack underflow");
				return s.Peek();
			}
			return GetVariable(variable);
		}

		private void SetVariableInPlace(byte variable, ushort value)
		{
			if(variable == 0)
			{
				var s = _stack.Peek().RoutineStack;
				if(s.Count > 0)
					s.Pop();
				s.Push(value);
			}
			else
				SetVariable(variable, value);
		}

		private void Pull(List<ushort> args)
		{
			if(_version == 6)
			{
				ushort value;
				if(args.Count > 0 && args[0] != 0)
				{
					ushort stack = args[0];
					ushort free = (ushort)(GetWord(stack) + 1);
					StoreWord(stack, free);
					value = GetWord((ushort)(stack + 2 * free));
				}
				else
					value = PopStack();
				Store(value);
			}
			else
			{
				ushort value = PopStack();
				SetVariableInPlace((byte)args[0], value);
			}
		}

		private void PushUserStack(List<ushort> args)
		{
			ushort stack = args[1];
			ushort free = GetWord(stack);
			if(free == 0)
			{
				Branch(false);
				return;
			}
			StoreWord((ushort)(stack + 2 * free), args[0]);
			StoreWord(stack, (ushort)(free - 1));
			Branch(true);
		}

		private void PopUserStack(List<ushort> args)
		{
			if(args.Count > 1 && args[1] != 0)
				StoreWord(args[1], (ushort)(GetWord(args[1]) + args[0]));
			else
				for(int i = 0; i < args[0]; i++)
					PopStack();
		}

		// ================================================================ text

		private uint GetPackedAddress(ushort address, bool routine)
		{
			if(_version <= 3)
				return (uint)address * 2;
			if(_version <= 5)
				return (uint)address * 4;
			if(_version <= 7)
				return (uint)address * 4 + (routine ? _routinesOffset : _stringsOffset);
			return (uint)address * 8;
		}

		private List<byte> ReadZChars(uint address, out uint end)
		{
			var chars = new List<byte>();
			while(true)
			{
				ushort word = GetWord(address);
				address += 2;
				chars.Add((byte)((word >> 10) & 0x1f));
				chars.Add((byte)((word >> 5) & 0x1f));
				chars.Add((byte)(word & 0x1f));
				if((word & 0x8000) != 0 || address >= _memory.Length)
					break;
			}
			end = address;
			return chars;
		}

		private string DecodeString(uint address)
		{
			return DecodeZChars(ReadZChars(address, out _), true);
		}

		private string DecodeZChars(List<byte> chars, bool allowAbbreviations)
		{
			var sb = new StringBuilder();
			int alphabet = 0;
			for(int i = 0; i < chars.Count; i++)
			{
				byte c = chars[i];
				if(c == 0)
				{
					sb.Append(' ');
					alphabet = 0;
				}
				else if(c <= 3)
				{
					if(i + 1 >= chars.Count)
						break;
					int index = 32 * (c - 1) + chars[++i];
					if(allowAbbreviations && (_version >= 3 || c == 1))
					{
						ushort wordAddr = GetWord((uint)(_abbreviationsTable + index * 2));
						sb.Append(DecodeZChars(ReadZChars((uint)wordAddr * 2, out _), false));
					}
					alphabet = 0;
				}
				else if(c == 4)
					alphabet = 1;
				else if(c == 5)
					alphabet = 2;
				else
				{
					if(alphabet == 2 && c == 6)
					{
						if(i + 2 >= chars.Count)
							break;
						int code = chars[i + 1] << 5 | chars[i + 2];
						i += 2;
						sb.Append(ZsciiToString(code));
					}
					else
						sb.Append(_alphabets[alphabet][c - 6]);
					alphabet = 0;
				}
			}
			return sb.ToString();
		}

		private static string ZsciiToString(int code)
		{
			if(code == 13 || code == 10)
				return "\n";
			if(code >= 32 && code <= 126)
				return ((char)code).ToString();
			if(code >= 155 && code < 155 + ExtraChars.Length)
				return ExtraChars[code - 155].ToString();
			if(code == 9)
				return " ";
			if(code == 0)
				return string.Empty;
			return "?";
		}

		private static byte CharToZscii(char c)
		{
			if(c == '\n' || c == '\r')
				return 13;
			if(c >= 32 && c <= 126)
				return (byte)c;
			int i = ExtraChars.IndexOf(c);
			if(i >= 0)
				return (byte)(155 + i);
			if(c >= 129 && c <= 154 || c >= 252)
				return (byte)c; // cursor keys, function keys, mouse clicks pass through
			return (byte)'?';
		}

		/// <summary>Encodes a word to dictionary form: 4 bytes (v1-3) or 6 bytes (v4+).</summary>
		private byte[] EncodeWord(IList<byte> zscii)
		{
			int resolution = _version <= 3 ? 6 : 9;
			var z = new List<byte>(resolution + 4);
			foreach(byte ch in zscii)
			{
				if(z.Count >= resolution)
					break;
				string s = ZsciiToString(ch);
				char c = s.Length > 0 ? s[0] : '?';
				int i;
				if((i = _alphabets[0].IndexOf(c)) >= 0)
					z.Add((byte)(i + 6));
				else if((i = _alphabets[1].IndexOf(c)) >= 0)
				{
					z.Add(4);
					z.Add((byte)(i + 6));
				}
				else if((i = _alphabets[2].IndexOf(c, 2)) >= 0)
				{
					z.Add(5);
					z.Add((byte)(i + 6));
				}
				else
				{
					z.Add(5);
					z.Add(6);
					z.Add((byte)(ch >> 5));
					z.Add((byte)(ch & 0x1f));
				}
			}
			while(z.Count < resolution)
				z.Add(5);
			var bytes = new byte[resolution / 3 * 2];
			for(int w = 0; w < resolution / 3; w++)
			{
				int word = z[w * 3] << 10 | z[w * 3 + 1] << 5 | z[w * 3 + 2];
				if(w == resolution / 3 - 1)
					word |= 0x8000;
				bytes[w * 2] = (byte)(word >> 8);
				bytes[w * 2 + 1] = (byte)word;
			}
			return bytes;
		}

		// ================================================================ output

		private void Output(string s)
		{
			if(s.Length == 0)
				return;
			if(_stream3.Count > 0)
			{
				_stream3.Peek().Value.Append(s);
				return;
			}
			if(!_screenOutput)
				return;
			_io.Print(s);
			if(_io6 != null && !_inNewlineInterrupt)
			{
				int routine = _io6.TakeNewlineInterrupt();
				if(routine != 0)
				{
					_inNewlineInterrupt = true;
					try { CallInterrupt((ushort)routine); }
					finally { _inNewlineInterrupt = false; }
				}
			}
		}

		private void Print(List<ushort> args)
		{
			ZStackFrame f = _stack.Peek();
			var chars = ReadZChars(f.PC, out uint end);
			f.PC = end;
			Output(DecodeZChars(chars, true));
		}

		private void PrintRet(List<ushort> args)
		{
			Print(args);
			Output("\n");
			ReturnValue(1);
		}

		private void OutputStream(List<ushort> args)
		{
			short n = (short)args[0];
			switch(n)
			{
				case 1: _screenOutput = true; break;
				case -1: _screenOutput = false; break;
				case 2: StoreWord(HeaderFlags2, (ushort)(GetWord(HeaderFlags2) | 1)); break;
				case -2: StoreWord(HeaderFlags2, (ushort)(GetWord(HeaderFlags2) & ~1)); break;
				case 3:
					if(_stream3.Count >= 16)
						throw new Exception("Output stream 3 nested too deeply");
					_stream3.Push(new KeyValuePair<ushort, StringBuilder>(args[1], new StringBuilder()));
					break;
				case -3:
					if(_stream3.Count == 0)
						break;
					var top = _stream3.Pop();
					string text = top.Value.ToString();
					ushort table = top.Key;
					int count = 0;
					foreach(char c in text)
						_memory[table + 2 + count++] = CharToZscii(c);
					StoreWord(table, (ushort)count);
					if(_io6 != null)
						StoreWord(HeaderStream3Width, (ushort)_io6.TextWidth(text));
					break;
			}
		}

		private void PrintTable(List<ushort> args)
		{
			ushort table = args[0];
			int width = args[1];
			int height = args.Count > 2 ? args[2] : 1;
			int skip = args.Count > 3 ? args[3] : 0;
			int y = 0, x = 0;
			if(_io6 != null)
				_io6.GetCursorPosition(out y, out x);
			ushort line = 0, column = 0;
			if(_io6 == null)
				_io.GetCursor(out line, out column);

			for(int r = 0; r < height; r++)
			{
				if(r > 0)
				{
					if(_io6 != null)
						_io6.MoveCursor(y + r * _io6.FontHeight, x, _io6.CurrentWindow);
					else
						_io.SetCursor((ushort)(line + r), column, 1);
				}
				var sb = new StringBuilder();
				for(int c = 0; c < width; c++)
					sb.Append(ZsciiToString(_memory[table + r * (width + skip) + c]));
				Output(sb.ToString());
			}
		}

		private void PrintForm(List<ushort> args)
		{
			ushort addr = args[0];
			_io6.GetCursorPosition(out int y, out int x);
			int row = 0;
			while(true)
			{
				ushort len = GetWord(addr);
				if(len == 0)
					break;
				if(row > 0)
					_io6.MoveCursor(y + row * _io6.FontHeight, x, _io6.CurrentWindow);
				var sb = new StringBuilder();
				for(int i = 0; i < len; i++)
					sb.Append(ZsciiToString(_memory[addr + 2 + i]));
				Output(sb.ToString());
				addr = (ushort)(addr + 2 + len);
				row++;
			}
		}

		// ================================================================ screen

		// Patched: computes the v3 status line from globals 0-2.
		private void UpdateStatus()
		{
			ushort location = GetWord(_globals);
			string name = location == 0 ? string.Empty : GetObjectName(location);
			short a = (short)GetWord((uint)(_globals + 2));
			short b = (short)GetWord((uint)(_globals + 4));
			bool timeGame = _version == 3 && (_memory[0x01] & 0x02) != 0;
			_io.UpdateStatusLine(name, a, b, timeGame);
		}

		private void ShowStatus(List<ushort> args)
		{
			if(_version <= 3)
			{
				UpdateStatus();
				_io.ShowStatus();
			}
		}

		private void SplitWindow(List<ushort> args)
		{
			if(_io6 != null)
				_io6.Split(args[0]);
			else
				_io.SplitWindow(args[0]);
		}

		private void SetWindow(List<ushort> args)
		{
			if(_io6 != null)
				_io6.SelectWindow(Window(args[0]));
			else
				_io.SetWindow(args[0]);
		}

		private void EraseWindow(List<ushort> args)
		{
			if(_io6 != null)
			{
				short w = (short)args[0];
				_io6.Erase(w == -3 ? _io6.CurrentWindow : w);
			}
			else
				_io.EraseWindow(args[0]);
		}

		private void EraseLine(List<ushort> args)
		{
			if(_io6 != null)
				_io6.EraseLine(args[0]);
			else if(args[0] == 1)
				_io.EraseLine();
		}

		private void SetCursor(List<ushort> args)
		{
			if(_io6 != null)
			{
				short line = (short)args[0];
				if(line == -1)
					_io6.CursorVisible(false);
				else if(line == -2)
					_io6.CursorVisible(true);
				else
					_io6.MoveCursor(line, Arg(args, 1), args.Count > 2 ? Window(args[2]) : _io6.CurrentWindow);
			}
			else
				_io.SetCursor(args[0], args[1], (ushort)(args.Count == 3 ? args[2] : 0));
		}

		private void GetCursor(List<ushort> args)
		{
			int y, x;
			if(_io6 != null)
				_io6.GetCursorPosition(out y, out x);
			else
			{
				_io.GetCursor(out ushort l, out ushort c);
				y = l; x = c;
			}
			StoreWord(args[0], (ushort)y);
			StoreWord((ushort)(args[0] + 2), (ushort)x);
		}

		private void SetTextStyle(List<ushort> args)
		{
			if(_io6 != null)
				_io6.SetStyle(args[0]);
			else
				_io.SetTextStyle((TextStyle)args[0]);
		}

		private void SetColour(List<ushort> args)
		{
			if(_io6 != null)
				_io6.SetColours((short)args[0], (short)args[1], args.Count > 2 ? Window(args[2]) : _io6.CurrentWindow);
			else
				_io.SetColor((ZColor)args[0], (ZColor)args[1]);
		}

		private void SetFont(List<ushort> args)
		{
			if(_io6 != null)
			{
				Store((ushort)_io6.SetFont(args[0], args.Count > 1 ? Window(args[1]) : _io6.CurrentWindow));
				return;
			}
			int font = args[0];
			if(font == 0)
				Store((ushort)_font);
			else if(font == 1 || font == 4)
			{
				Store((ushort)_font);
				_font = font;
			}
			else
				Store(0);
		}

		private void PictureData(List<ushort> args)
		{
			ushort array = args[1];
			bool ok = _io6.PictureData(args[0], out int height, out int width);
			// For a missing picture the spec says to leave the array alone, but Zork Zero then lays
			// out its screen from whatever was there. A size of zero is the safer answer.
			{
				StoreWord(array, (ushort)height);
				StoreWord((ushort)(array + 2), (ushort)width);
			}
			Branch(ok && (args[0] != 0 || height > 0));
		}

		private void ReadMouse(List<ushort> args)
		{
			_io6.GetMouse(out int y, out int x, out int buttons, out int menu);
			StoreWord(args[0], (ushort)y);
			StoreWord((ushort)(args[0] + 2), (ushort)x);
			StoreWord((ushort)(args[0] + 4), (ushort)buttons);
			StoreWord((ushort)(args[0] + 6), (ushort)menu);
		}

		private void NoteMouseClick()
		{
			// After a click, the header extension table holds where it happened.
			_io6.GetMouse(out int y, out int x, out _, out _);
			ushort ext = GetWord(HeaderExtension);
			if(ext == 0)
				return;
			ushort count = GetWord(ext);
			if(count >= 1) StoreWord((ushort)(ext + 2), (ushort)x);
			if(count >= 2) StoreWord((ushort)(ext + 4), (ushort)y);
		}

		// ================================================================ input

		private byte[] Terminators()
		{
			if(_version < 5)
				return new byte[0];
			ushort table = GetWord(HeaderTerminators);
			var list = new List<byte>();
			if(table != 0)
				for(int i = 0; _memory[table + i] != 0 && i < 64; i++)
					list.Add(_memory[table + i]);
			return list.ToArray();
		}

		private void Read(List<ushort> args)
		{
			ushort text = args[0];
			ushort parse = args.Count > 1 ? args[1] : (ushort)0;
			int time = args.Count > 2 ? args[2] : 0;
			ushort routine = args.Count > 3 ? args[3] : (ushort)0;

			if(_version <= 3)
				UpdateStatus();

			int capacity = _memory[text];
			string initial = string.Empty;
			if(_version >= 5 && _memory[text + 1] > 0)
			{
				var sb = new StringBuilder();
				for(int i = 0; i < _memory[text + 1] && i < capacity; i++)
					sb.Append(ZsciiToString(_memory[text + 2 + i]));
				initial = sb.ToString();
			}

			string typed;
			int terminator = 13;
			if(_io6 != null)
			{
				int max = (_version <= 4 ? capacity - 1 : capacity) - initial.Length;
				byte[] terms = Terminators();
				while(true)
				{
					LineInput r = _io6.ReadLine(Math.Max(0, max), initial, time > 0 && routine != 0 ? time : 0, terms);
					if(!_running)
						return;
					if(r.TimedOut)
					{
						if(CallInterrupt(routine) != 0)
						{
							typed = string.Empty;
							initial = string.Empty;
							terminator = 0;
							break;
						}
						if(!_running)
							return;
						continue;
					}
					typed = r.Text ?? string.Empty;
					terminator = r.Terminator;
					if(terminator == 253 || terminator == 254)
						NoteMouseClick();
					break;
				}
			}
			else
			{
				typed = _io.Read(capacity);
				if(!_running)
					return;
			}

			StoreInput(text, initial + (typed ?? string.Empty));
			if(parse != 0)
				Tokenise(text, parse, 0, false);
			if(_version >= 5)
				Store((ushort)terminator);
		}

		/// <summary>Writes typed text into a text buffer, lowercased (Standard 1.1, read).</summary>
		private void StoreInput(ushort text, string input)
		{
			int capacity = _memory[text];
			if(_version <= 4)
				capacity = Math.Max(0, capacity - 1);
			input = input.ToLowerInvariant();
			if(input.Length > capacity)
				input = input.Substring(0, capacity);

			int start = _version >= 5 ? 2 : 1;
			for(int i = 0; i < input.Length; i++)
				_memory[text + start + i] = CharToZscii(input[i]);
			if(_version >= 5)
				_memory[text + 1] = (byte)input.Length;
			else
				_memory[text + start + input.Length] = 0;
		}

		// Kept for hosts that drive the machine one line at a time.
		public void FinishRead(string input)
		{
		}

		private void ReadChar(List<ushort> args)
		{
			int time = args.Count > 1 ? args[1] : 0;
			ushort routine = args.Count > 2 ? args[2] : (ushort)0;
			int key;
			if(_io6 != null)
			{
				while(true)
				{
					key = _io6.ReadKey(time > 0 && routine != 0 ? time : 0);
					if(!_running)
						return;
					if(key != 0)
						break;
					if(CallInterrupt(routine) != 0)
						break;
					if(!_running)
						return;
				}
				if(key == 253 || key == 254)
					NoteMouseClick();
			}
			else
			{
				key = _io.ReadChar();
				if(!_running)
					return;
				key = CharToZscii((char)key);
				if(key == 10)
					key = 13;
			}
			Store((ushort)key);
		}

		private void Tokenise(ushort text, ushort parse, ushort dictionary, bool skipUnknown)
		{
			if(dictionary == 0)
				dictionary = _dictionary;

			// The characters as typed, with their positions in the buffer.
			var chars = new List<byte>();
			int start;
			if(_version >= 5)
			{
				start = 2;
				int n = _memory[text + 1];
				for(int i = 0; i < n; i++)
					chars.Add(_memory[text + 2 + i]);
			}
			else
			{
				start = 1;
				for(int i = 0; _memory[text + 1 + i] != 0 && i < 255; i++)
					chars.Add(_memory[text + 1 + i]);
			}

			int sepCount = _memory[dictionary];
			var separators = new HashSet<byte>();
			for(int i = 0; i < sepCount; i++)
				separators.Add(_memory[dictionary + 1 + i]);

			var words = new List<KeyValuePair<int, int>>(); // start index, length
			int wordStart = -1;
			for(int i = 0; i <= chars.Count; i++)
			{
				byte c = i < chars.Count ? chars[i] : (byte)' ';
				bool sep = i < chars.Count && separators.Contains(c);
				if(c == ' ' || sep)
				{
					if(wordStart >= 0)
						words.Add(new KeyValuePair<int, int>(wordStart, i - wordStart));
					wordStart = -1;
					if(sep)
						words.Add(new KeyValuePair<int, int>(i, 1));
				}
				else if(wordStart < 0)
					wordStart = i;
			}

			int max = _memory[parse];
			int count = Math.Min(words.Count, max);
			_memory[parse + 1] = (byte)count;
			for(int i = 0; i < count; i++)
			{
				var w = words[i];
				ushort found = LookupWord(dictionary, EncodeWord(chars.GetRange(w.Key, w.Value)));
				if(found == 0 && skipUnknown)
					continue;
				int entry = parse + 2 + i * 4;
				StoreWord((ushort)entry, found);
				_memory[entry + 2] = (byte)w.Value;
				_memory[entry + 3] = (byte)(w.Key + start);
			}
		}

		private ushort LookupWord(ushort dictionary, byte[] encoded)
		{
			int addr = dictionary + 1 + _memory[dictionary];
			int entryLength = _memory[addr];
			int entries = (short)GetWord((uint)(addr + 1));
			addr += 3;
			int n = Math.Abs(entries);
			for(int i = 0; i < n; i++)
			{
				int e = addr + i * entryLength;
				bool match = true;
				for(int b = 0; b < encoded.Length; b++)
					if(_memory[e + b] != encoded[b])
					{
						match = false;
						break;
					}
				if(match)
					return (ushort)e;
			}
			return 0;
		}

		private void EncodeTextOp(List<ushort> args)
		{
			var zscii = new List<byte>();
			for(int i = 0; i < args[1]; i++)
				zscii.Add(_memory[args[0] + args[2] + i]);
			byte[] encoded = EncodeWord(zscii);
			for(int i = 0; i < encoded.Length; i++)
				_memory[args[3] + i] = encoded[i];
		}

		// ================================================================ tables, maths

		private void ScanTable(List<ushort> args)
		{
			int form = args.Count > 3 ? args[3] : 0x82;
			int len = form & 0x7f;
			bool words = (form & 0x80) != 0;
			for(int i = 0; i < args[2]; i++)
			{
				ushort addr = (ushort)(args[1] + i * len);
				ushort val = words ? GetWord(addr) : _memory[addr];
				if(val == args[0])
				{
					Store(addr);
					Branch(true);
					return;
				}
			}
			Store(0);
			Branch(false);
		}

		private void CopyTable(List<ushort> args)
		{
			ushort first = args[0], second = args[1];
			short size = (short)args[2];
			if(second == 0)
			{
				for(int i = 0; i < Math.Abs((int)size); i++)
					_memory[first + i] = 0;
			}
			else if(size < 0 || first > second)
			{
				for(int i = 0; i < Math.Abs((int)size); i++)
					_memory[second + i] = _memory[first + i];
			}
			else
			{
				for(int i = size - 1; i >= 0; i--)
					_memory[second + i] = _memory[first + i];
			}
		}

		private void RandomOp(List<ushort> args)
		{
			short range = (short)args[0];
			if(range > 0)
				Store((ushort)(_random.Next(0, range) + 1));
			else
			{
				_random = range == 0 ? new Random() : new Random(-range);
				Store(0);
			}
		}

		private void Verify(List<ushort> args)
		{
			uint length = GetWord(HeaderFileLength);
			length *= _version <= 3 ? 2u : _version <= 5 ? 4u : 8u;
			ushort sum = 0;
			for(uint i = 0x40; i < length && i < _story.Length; i++)
				sum += _story[i];
			Branch(sum == GetWord(HeaderChecksum));
		}

		// ================================================================ objects

		private ushort GetObjectAddress(ushort obj)
		{
			return (ushort)(_objectTable + _defaultsSize + (obj - 1) * _objectSize);
		}

		private ushort GetObjectLink(ushort obj, int offset)
		{
			if(obj == 0)
				return 0;
			ushort addr = (ushort)(GetObjectAddress(obj) + offset);
			return _version <= 3 ? _memory[addr] : GetWord(addr);
		}

		private void SetObjectLink(ushort obj, int offset, ushort value)
		{
			ushort addr = (ushort)(GetObjectAddress(obj) + offset);
			if(_version <= 3)
				_memory[addr] = (byte)value;
			else
				StoreWord(addr, value);
		}

		private void GetSibling(List<ushort> args)
		{
			ushort s = GetObjectLink(args[0], _siblingOffset);
			Store(s);
			Branch(s != 0);
		}

		private void GetChild(List<ushort> args)
		{
			ushort c = GetObjectLink(args[0], _childOffset);
			Store(c);
			Branch(c != 0);
		}

		private void Unlink(ushort obj)
		{
			if(obj == 0)
				return;
			ushort parent = GetObjectLink(obj, _parentOffset);
			if(parent == 0)
				return;
			ushort sibling = GetObjectLink(obj, _siblingOffset);
			ushort first = GetObjectLink(parent, _childOffset);
			if(first == obj)
				SetObjectLink(parent, _childOffset, sibling);
			else
			{
				ushort cur = first;
				int guard = 0;
				while(cur != 0 && guard++ < 65535)
				{
					ushort next = GetObjectLink(cur, _siblingOffset);
					if(next == obj)
					{
						SetObjectLink(cur, _siblingOffset, sibling);
						break;
					}
					cur = next;
				}
			}
			SetObjectLink(obj, _parentOffset, 0);
			SetObjectLink(obj, _siblingOffset, 0);
		}

		private void InsertObject(ushort obj, ushort destination)
		{
			if(obj == 0 || destination == 0)
				return;
			Unlink(obj);
			SetObjectLink(obj, _siblingOffset, GetObjectLink(destination, _childOffset));
			SetObjectLink(destination, _childOffset, obj);
			SetObjectLink(obj, _parentOffset, destination);
		}

		private bool TestAttribute(ushort obj, ushort attribute)
		{
			if(obj == 0)
				return false;
			ushort addr = (ushort)(GetObjectAddress(obj) + attribute / 8);
			return (_memory[addr] & (0x80 >> (attribute % 8))) != 0;
		}

		private void SetAttribute(ushort obj, ushort attribute, bool on)
		{
			if(obj == 0)
				return;
			ushort addr = (ushort)(GetObjectAddress(obj) + attribute / 8);
			byte bit = (byte)(0x80 >> (attribute % 8));
			if(on)
				_memory[addr] |= bit;
			else
				_memory[addr] &= (byte)~bit;
		}

		private ushort PropertyTable(ushort obj)
		{
			return GetWord((uint)(GetObjectAddress(obj) + _propertyOffset));
		}

		/// <summary>Reads the property header at addr. Returns false at the end of the list.</summary>
		private bool PropertyInfo(int addr, out int number, out int length, out int data)
		{
			byte b = _memory[addr];
			if(b == 0)
			{
				number = length = data = 0;
				return false;
			}
			if(_version <= 3)
			{
				number = b & 0x1f;
				length = (b >> 5) + 1;
				data = addr + 1;
			}
			else
			{
				number = b & 0x3f;
				if((b & 0x80) != 0)
				{
					length = _memory[addr + 1] & 0x3f;
					if(length == 0)
						length = 64;
					data = addr + 2;
				}
				else
				{
					length = (b & 0x40) != 0 ? 2 : 1;
					data = addr + 1;
				}
			}
			return true;
		}

		private int FirstProperty(ushort obj)
		{
			ushort table = PropertyTable(obj);
			return table + 1 + 2 * _memory[table];
		}

		/// <summary>Returns the data address of the property, or 0.</summary>
		private int FindProperty(ushort obj, ushort prop, out int length)
		{
			length = 0;
			if(obj == 0)
				return 0;
			int addr = FirstProperty(obj);
			while(PropertyInfo(addr, out int number, out int len, out int data))
			{
				if(number == prop)
				{
					length = len;
					return data;
				}
				addr = data + len;
			}
			return 0;
		}

		private int GetPropLen(ushort dataAddr)
		{
			if(dataAddr == 0)
				return 0;
			byte b = _memory[dataAddr - 1];
			if(_version <= 3)
				return (b >> 5) + 1;
			if((b & 0x80) != 0)
			{
				int len = b & 0x3f;
				return len == 0 ? 64 : len;
			}
			return (b & 0x40) != 0 ? 2 : 1;
		}

		private int GetNextProp(ushort obj, ushort prop)
		{
			if(obj == 0)
				return 0;
			int addr = FirstProperty(obj);
			if(prop != 0)
			{
				int data = FindProperty(obj, prop, out int len);
				if(data == 0)
					return 0;
				addr = data + len;
			}
			return PropertyInfo(addr, out int number, out _, out _) ? number : 0;
		}

		private void GetProp(List<ushort> args)
		{
			int data = FindProperty(args[0], args[1], out int len);
			ushort value;
			if(data == 0)
				value = GetWord((uint)(_objectTable + (args[1] - 1) * 2));
			else if(len == 1)
				value = _memory[data];
			else
				value = GetWord((uint)data);
			Store(value);
		}

		private void PutProp(List<ushort> args)
		{
			int data = FindProperty(args[0], args[1], out int len);
			if(data == 0)
				return;
			if(len == 1)
				_memory[data] = (byte)args[2];
			else
				StoreWord((ushort)data, args[2]);
		}

		private string GetObjectName(ushort obj)
		{
			if(obj == 0)
				return string.Empty;
			ushort table = PropertyTable(obj);
			if(_memory[table] == 0)
				return string.Empty;
			return DecodeString((uint)table + 1);
		}

		// ================================================================ save, restore, undo

		private const uint SaveMagic = 0x49574232; // "IWB2"

		public Stream SaveState()
		{
			var ms = new MemoryStream();
			var bw = new BinaryWriter(ms);
			bw.Write(SaveMagic);
			bw.Write(_story, 0x02, 2);  // release
			bw.Write(_story, 0x12, 6);  // serial
			bw.Write(_story, 0x1c, 2);  // checksum
			bw.Write((int)_dynamicMemory);
			bw.Write(_memory, 0, _dynamicMemory);
			WriteStack(bw, _stack.ToArray());
			bw.Flush();
			ms.Position = 0;
			return ms;
		}

		private static void WriteStack(BinaryWriter bw, ZStackFrame[] topFirst)
		{
			bw.Write(topFirst.Length);
			for(int i = topFirst.Length - 1; i >= 0; i--)
			{
				var f = topFirst[i];
				bw.Write(f.PC);
				bw.Write(f.StoreResult);
				bw.Write(f.Interrupt);
				bw.Write(f.ArgumentCount);
				foreach(ushort v in f.Variables)
					bw.Write(v);
				var values = f.RoutineStack.ToArray();
				bw.Write(values.Length);
				for(int j = values.Length - 1; j >= 0; j--)
					bw.Write(values[j]);
			}
		}

		/// <summary>Restores a saved game. Returns false (and changes nothing) if it isn't a save of this story.</summary>
		public bool RestoreState(Stream stream)
		{
			try
			{
				var br = new BinaryReader(stream);
				stream.Position = 0;
				if(br.ReadUInt32() != SaveMagic)
					return false;
				byte[] id = br.ReadBytes(10);
				for(int i = 0; i < 2; i++) if(id[i] != _story[0x02 + i]) return false;
				for(int i = 0; i < 6; i++) if(id[2 + i] != _story[0x12 + i]) return false;
				for(int i = 0; i < 2; i++) if(id[8 + i] != _story[0x1c + i]) return false;
				int dyn = br.ReadInt32();
				if(dyn != _dynamicMemory)
					return false;
				byte[] mem = br.ReadBytes(dyn);
				int frames = br.ReadInt32();
				var list = new List<ZStackFrame>();
				for(int i = 0; i < frames; i++)
				{
					var f = new ZStackFrame
					{
						PC = br.ReadUInt32(),
						StoreResult = br.ReadBoolean(),
						Interrupt = br.ReadBoolean(),
						ArgumentCount = br.ReadInt32(),
					};
					for(int v = 0; v < f.Variables.Length; v++)
						f.Variables[v] = br.ReadUInt16();
					int n = br.ReadInt32();
					for(int j = 0; j < n; j++)
						f.RoutineStack.Push(br.ReadUInt16());
					list.Add(f);
				}
				Array.Copy(mem, _memory, dyn);
				_stack.Clear();
				foreach(var f in list)
					_stack.Push(f);
				WriteInterpreterHeader();
				return true;
			}
			catch(Exception)
			{
				return false;
			}
			finally
			{
				stream.Dispose();
			}
		}

		private void Save(List<ushort> args)
		{
			bool ok = false;
			if(_version < 5 || args.Count == 0)
				ok = _io.Save(SaveState());
			if(_version <= 3)
				Branch(ok);
			else
				Store((ushort)(ok ? 1 : 0));
		}

		private void Restore(List<ushort> args)
		{
			bool ok = false;
			if(_version < 5 || args.Count == 0)
			{
				Stream stream = _io.Restore();
				ok = stream != null && RestoreState(stream);
				if(stream != null && !ok)
					_io.Print("[That isn't a saved game of this story.]\n");
			}
			// After a good restore the PC is back at the save instruction's branch or store byte.
			if(_version <= 3)
				Branch(ok);
			else
				Store((ushort)(ok ? 2 : 0));
		}

		private void SaveUndo()
		{
			_undoMemory = new byte[_dynamicMemory];
			Array.Copy(_memory, _undoMemory, _dynamicMemory);
			var frames = _stack.ToArray();
			_undoStack = new ZStackFrame[frames.Length];
			for(int i = 0; i < frames.Length; i++)
				_undoStack[i] = frames[i].Clone();
			Store(1);
		}

		private void RestoreUndo()
		{
			if(_undoMemory == null)
			{
				Store(0);
				return;
			}
			Array.Copy(_undoMemory, _memory, _dynamicMemory);
			_stack.Clear();
			for(int i = _undoStack.Length - 1; i >= 0; i--)
				_stack.Push(_undoStack[i].Clone());
			WriteInterpreterHeader();
			Store(2);
		}

		private void Restart()
		{
			ushort keep = (ushort)(GetWord(HeaderFlags2) & 0x03);
			Start();
			StoreWord(HeaderFlags2, (ushort)(GetWord(HeaderFlags2) | keep));
		}

		private void Quit(List<ushort> args)
		{
			_running = false;
			_io.Quit();
		}

		// ================================================================ memory

		private ushort GetWord(uint address)
		{
			return (ushort)(_memory[address] << 8 | _memory[address + 1]);
		}

		private void StoreWord(ushort address, ushort value)
		{
			_memory[address] = (byte)(value >> 8);
			_memory[address + 1] = (byte)value;
		}
	}
}
