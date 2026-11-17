using System;

namespace InfocomWorkbench.Trs80
{
	/// <summary>
	/// A Zilog Z80 processor: every documented instruction, the undocumented IXH/IXL/IYH/IYL forms,
	/// SLL, the DD CB / FD CB register copies, and the undocumented flag bits 3 and 5 for almost everything.
	/// Interrupts and exact cycle timing are left out because nothing here needs them.
	/// Memory is a flat 64K array; ports and ROM routines are hooks the machine supplies.
	/// </summary>
	public sealed class Z80
	{
		public const int FC = 0x01, FN = 0x02, FPV = 0x04, FX = 0x08, FH = 0x10, FY = 0x20, FZ = 0x40, FS = 0x80;
		const int FXY = FX | FY;

		public readonly byte[] Mem = new byte[65536];

		public int A, F, B, C, D, E, H, L;
		public int A2, F2, B2, C2, D2, E2, H2, L2;
		public int IX, IY, SP, PC, I, R;
		public bool IFF1, IFF2, Halted;
		public int IM;

		/// <summary>Port input. Gets the 16-bit port address (the high byte is A or B, as on the real chip).</summary>
		public Func<int, int> In = port => 0xFF;
		public Action<int, int> Out = (port, value) => { };

		/// <summary>
		/// Called before each instruction is fetched while PC is below this address. Returning true means
		/// the hook did the work (usually standing in for a ROM routine and returning with <see cref="Ret"/>).
		/// </summary>
		public Func<Z80, bool> Trap;
		public int TrapBelow;

		static readonly byte[] SZ = new byte[256];
		static readonly byte[] SZP = new byte[256];

		static Z80()
		{
			for (int i = 0; i < 256; i++)
			{
				int s = (i & 0x80) | (i == 0 ? FZ : 0);
				int bits = 0;
				for (int b = 0; b < 8; b++)
					bits += (i >> b) & 1;
				SZ[i] = (byte)s;
				SZP[i] = (byte)(s | ((bits & 1) == 0 ? FPV : 0));
			}
		}

		// ---------------------------------------------------------------- register pairs

		public int BC { get => (B << 8) | C; set { B = (value >> 8) & 0xFF; C = value & 0xFF; } }
		public int DE { get => (D << 8) | E; set { D = (value >> 8) & 0xFF; E = value & 0xFF; } }
		public int HL { get => (H << 8) | L; set { H = (value >> 8) & 0xFF; L = value & 0xFF; } }
		public int AF { get => (A << 8) | F; set { A = (value >> 8) & 0xFF; F = value & 0xFF; } }

		// ---------------------------------------------------------------- memory helpers

		public int Read16(int addr) => Mem[addr & 0xFFFF] | (Mem[(addr + 1) & 0xFFFF] << 8);

		public void Write16(int addr, int v)
		{
			Mem[addr & 0xFFFF] = (byte)v;
			Mem[(addr + 1) & 0xFFFF] = (byte)(v >> 8);
		}

		int Fetch()
		{
			int v = Mem[PC];
			PC = (PC + 1) & 0xFFFF;
			return v;
		}

		int Fetch16()
		{
			int v = Read16(PC);
			PC = (PC + 2) & 0xFFFF;
			return v;
		}

		void IncR() => R = (R & 0x80) | ((R + 1) & 0x7F);

		public void Push(int v)
		{
			SP = (SP - 2) & 0xFFFF;
			Write16(SP, v);
		}

		public int Pop()
		{
			int v = Read16(SP);
			SP = (SP + 2) & 0xFFFF;
			return v;
		}

		/// <summary>Returns from a subroutine, for hooks that stand in for one.</summary>
		public void Ret() => PC = Pop();

		// ---------------------------------------------------------------- index register handling

		// 0 = HL, 1 = IX, 2 = IY for the instruction being run.
		int _idx;

		int IdxReg
		{
			get => _idx == 0 ? HL : _idx == 1 ? IX : IY;
			set
			{
				value &= 0xFFFF;
				if (_idx == 0) HL = value;
				else if (_idx == 1) IX = value;
				else IY = value;
			}
		}

		/// <summary>The address (HL), or (IX+d)/(IY+d) with the displacement fetched now.</summary>
		int MemOperand()
		{
			if (_idx == 0)
				return HL;
			int d = (sbyte)Fetch();
			return (IdxReg + d) & 0xFFFF;
		}

		/// <summary>Register r (B C D E H L - A). With useIdx, H and L mean IXH/IXL or IYH/IYL.</summary>
		int GetR(int r, bool useIdx)
		{
			switch (r)
			{
				case 0: return B;
				case 1: return C;
				case 2: return D;
				case 3: return E;
				case 4: return useIdx && _idx != 0 ? (IdxReg >> 8) & 0xFF : H;
				case 5: return useIdx && _idx != 0 ? IdxReg & 0xFF : L;
				case 7: return A;
			}
			throw new InvalidOperationException("register 6");
		}

		void SetR(int r, int v, bool useIdx)
		{
			v &= 0xFF;
			switch (r)
			{
				case 0: B = v; return;
				case 1: C = v; return;
				case 2: D = v; return;
				case 3: E = v; return;
				case 4:
					if (useIdx && _idx != 0) IdxReg = (IdxReg & 0x00FF) | (v << 8);
					else H = v;
					return;
				case 5:
					if (useIdx && _idx != 0) IdxReg = (IdxReg & 0xFF00) | v;
					else L = v;
					return;
				case 7: A = v; return;
			}
			throw new InvalidOperationException("register 6");
		}

		int GetRP(int p)
		{
			switch (p)
			{
				case 0: return BC;
				case 1: return DE;
				case 2: return IdxReg;
				default: return SP;
			}
		}

		void SetRP(int p, int v)
		{
			v &= 0xFFFF;
			switch (p)
			{
				case 0: BC = v; break;
				case 1: DE = v; break;
				case 2: IdxReg = v; break;
				default: SP = v; break;
			}
		}

		bool Cond(int y)
		{
			switch (y)
			{
				case 0: return (F & FZ) == 0;
				case 1: return (F & FZ) != 0;
				case 2: return (F & FC) == 0;
				case 3: return (F & FC) != 0;
				case 4: return (F & FPV) == 0;
				case 5: return (F & FPV) != 0;
				case 6: return (F & FS) == 0;
				default: return (F & FS) != 0;
			}
		}

		// ---------------------------------------------------------------- arithmetic

		void Add8(int v, int carry)
		{
			int r = A + v + carry;
			F = SZ[r & 0xFF] | ((r >> 8) & FC) | ((A ^ v ^ r) & FH) | (((A ^ ~v) & (A ^ r) & 0x80) >> 5) | (r & FXY);
			A = r & 0xFF;
		}

		int Sub8(int v, int carry)
		{
			int r = A - v - carry;
			F = SZ[r & 0xFF] | FN | ((r >> 8) & FC) | ((A ^ v ^ r) & FH) | (((A ^ v) & (A ^ r) & 0x80) >> 5) | (r & FXY);
			return r & 0xFF;
		}

		void Alu(int op, int v)
		{
			switch (op)
			{
				case 0: Add8(v, 0); break;
				case 1: Add8(v, F & FC); break;
				case 2: A = Sub8(v, 0); break;
				case 3: A = Sub8(v, F & FC); break;
				case 4: A &= v; F = SZP[A] | FH | (A & FXY); break;
				case 5: A ^= v; F = SZP[A] | (A & FXY); break;
				case 6: A |= v; F = SZP[A] | (A & FXY); break;
				default: Sub8(v, 0); F = (F & ~FXY) | (v & FXY); break; // CP takes bits 3 and 5 from the operand
			}
		}

		int Inc8(int v)
		{
			int r = (v + 1) & 0xFF;
			F = (F & FC) | SZ[r] | (r & FXY) | ((r & 0x0F) == 0 ? FH : 0) | (r == 0x80 ? FPV : 0);
			return r;
		}

		int Dec8(int v)
		{
			int r = (v - 1) & 0xFF;
			F = (F & FC) | FN | SZ[r] | (r & FXY) | ((r & 0x0F) == 0x0F ? FH : 0) | (r == 0x7F ? FPV : 0);
			return r;
		}

		int Add16(int a, int b)
		{
			int r = a + b;
			F = (F & (FS | FZ | FPV)) | ((r >> 16) & FC) | (((a ^ b ^ r) >> 8) & FH) | ((r >> 8) & FXY);
			return r & 0xFFFF;
		}

		void Adc16(int v)
		{
			int hl = HL;
			int r = hl + v + (F & FC);
			F = ((r >> 16) & FC) | (((hl ^ v ^ r) >> 8) & FH) | (((hl ^ ~v) & (hl ^ r) & 0x8000) >> 13)
				| ((r >> 8) & (FS | FXY)) | ((r & 0xFFFF) == 0 ? FZ : 0);
			HL = r & 0xFFFF;
		}

		void Sbc16(int v)
		{
			int hl = HL;
			int r = hl - v - (F & FC);
			F = FN | ((r >> 16) & FC) | (((hl ^ v ^ r) >> 8) & FH) | (((hl ^ v) & (hl ^ r) & 0x8000) >> 13)
				| ((r >> 8) & (FS | FXY)) | ((r & 0xFFFF) == 0 ? FZ : 0);
			HL = r & 0xFFFF;
		}

		int Rot(int op, int v)
		{
			int r, c;
			switch (op)
			{
				case 0: c = v >> 7; r = (v << 1) | c; break;                 // RLC
				case 1: c = v & 1; r = (v >> 1) | (c << 7); break;           // RRC
				case 2: c = v >> 7; r = (v << 1) | (F & FC); break;          // RL
				case 3: c = v & 1; r = (v >> 1) | ((F & FC) << 7); break;    // RR
				case 4: c = v >> 7; r = v << 1; break;                       // SLA
				case 5: c = v & 1; r = (v >> 1) | (v & 0x80); break;         // SRA
				case 6: c = v >> 7; r = (v << 1) | 1; break;                 // SLL (undocumented)
				default: c = v & 1; r = v >> 1; break;                       // SRL
			}
			r &= 0xFF;
			F = SZP[r] | c | (r & FXY);
			return r;
		}

		void Daa()
		{
			int a = A, diff = 0, carry = F & FC;
			if ((F & FH) != 0 || (a & 0x0F) > 9)
				diff = 6;
			if (carry != 0 || a > 0x99)
			{
				diff |= 0x60;
				carry = FC;
			}
			int h;
			if ((F & FN) != 0)
			{
				h = (F & FH) != 0 && (a & 0x0F) < 6 ? FH : 0;
				a = (a - diff) & 0xFF;
			}
			else
			{
				h = (a & 0x0F) > 9 ? FH : 0;
				a = (a + diff) & 0xFF;
			}
			A = a;
			F = SZP[a] | (F & FN) | carry | h | (a & FXY);
		}

		// ---------------------------------------------------------------- execution

		/// <summary>Runs one instruction (or one trapped ROM routine).</summary>
		public void Step()
		{
			if (PC < TrapBelow && Trap != null && Trap(this))
				return;
			if (Halted)
				return;
			_idx = 0;
			int op = Fetch();
			IncR();
			while (op == 0xDD || op == 0xFD)
			{
				_idx = op == 0xDD ? 1 : 2;
				op = Fetch();
				IncR();
			}
			if (op == 0xCB)
				ExecCB();
			else if (op == 0xED)
			{
				_idx = 0;
				IncR();
				ExecED(Fetch());
			}
			else
				ExecMain(op);
		}

		void ExecMain(int op)
		{
			int x = op >> 6, y = (op >> 3) & 7, z = op & 7, p = y >> 1, q = y & 1;
			switch (x)
			{
				case 0:
					switch (z)
					{
						case 0:
							switch (y)
							{
								case 0: break;
								case 1:
									Swap(ref A, ref A2); Swap(ref F, ref F2);
									break;
								case 2:
								{
									int d = (sbyte)Fetch();
									B = (B - 1) & 0xFF;
									if (B != 0) PC = (PC + d) & 0xFFFF;
									break;
								}
								case 3:
								{
									int d = (sbyte)Fetch();
									PC = (PC + d) & 0xFFFF;
									break;
								}
								default:
								{
									int d = (sbyte)Fetch();
									if (Cond(y - 4)) PC = (PC + d) & 0xFFFF;
									break;
								}
							}
							break;
						case 1:
							if (q == 0) SetRP(p, Fetch16());
							else IdxReg = Add16(IdxReg, GetRP(p));
							break;
						case 2:
							Ld2(p, q);
							break;
						case 3:
							SetRP(p, GetRP(p) + (q == 0 ? 1 : -1));
							break;
						case 4:
							if (y == 6) { int a = MemOperand(); Mem[a] = (byte)Inc8(Mem[a]); }
							else SetR(y, Inc8(GetR(y, true)), true);
							break;
						case 5:
							if (y == 6) { int a = MemOperand(); Mem[a] = (byte)Dec8(Mem[a]); }
							else SetR(y, Dec8(GetR(y, true)), true);
							break;
						case 6:
							if (y == 6) { int a = MemOperand(); Mem[a] = (byte)Fetch(); }
							else SetR(y, Fetch(), true);
							break;
						case 7:
							Acc(y);
							break;
					}
					break;

				case 1:
					if (y == 6 && z == 6)
					{
						Halted = true;
						PC = (PC - 1) & 0xFFFF;
					}
					else if (y == 6)
						Mem[MemOperand()] = (byte)GetR(z, false);
					else if (z == 6)
						SetR(y, Mem[MemOperand()], false);
					else
						SetR(y, GetR(z, true), true);
					break;

				case 2:
					Alu(y, z == 6 ? Mem[MemOperand()] : GetR(z, true));
					break;

				default:
					ExecX3(op, y, z, p, q);
					break;
			}
		}

		void Ld2(int p, int q)
		{
			// The x=0, z=2 group: LD (BC),A  LD A,(BC)  LD (DE),A  LD A,(DE)  LD (nn),HL  LD HL,(nn)  LD (nn),A  LD A,(nn)
			switch (p * 2 + q)
			{
				case 0: Mem[BC] = (byte)A; break;
				case 1: A = Mem[BC]; break;
				case 2: Mem[DE] = (byte)A; break;
				case 3: A = Mem[DE]; break;
				case 4: Write16(Fetch16(), IdxReg); break;
				case 5: IdxReg = Read16(Fetch16()); break;
				case 6: Mem[Fetch16()] = (byte)A; break;
				case 7: A = Mem[Fetch16()]; break;
			}
		}

		void Acc(int y)
		{
			switch (y)
			{
				case 0: // RLCA
				{
					int c = A >> 7;
					A = ((A << 1) | c) & 0xFF;
					F = (F & (FS | FZ | FPV)) | c | (A & FXY);
					break;
				}
				case 1: // RRCA
				{
					int c = A & 1;
					A = (A >> 1) | (c << 7);
					F = (F & (FS | FZ | FPV)) | c | (A & FXY);
					break;
				}
				case 2: // RLA
				{
					int c = A >> 7;
					A = ((A << 1) | (F & FC)) & 0xFF;
					F = (F & (FS | FZ | FPV)) | c | (A & FXY);
					break;
				}
				case 3: // RRA
				{
					int c = A & 1;
					A = (A >> 1) | ((F & FC) << 7);
					F = (F & (FS | FZ | FPV)) | c | (A & FXY);
					break;
				}
				case 4: Daa(); break;
				case 5: // CPL
					A ^= 0xFF;
					F = (F & (FS | FZ | FPV | FC)) | FH | FN | (A & FXY);
					break;
				case 6: // SCF
					F = (F & (FS | FZ | FPV)) | FC | (A & FXY);
					break;
				default: // CCF
					F = (F & (FS | FZ | FPV)) | ((F & FC) != 0 ? FH : FC) | (A & FXY);
					break;
			}
		}

		void ExecX3(int op, int y, int z, int p, int q)
		{
			switch (z)
			{
				case 0:
					if (Cond(y)) Ret();
					break;
				case 1:
					if (q == 0)
					{
						int v = Pop();
						if (p == 3) AF = v;
						else SetRP(p, v);
					}
					else
					{
						switch (p)
						{
							case 0: Ret(); break;
							case 1:
								Swap(ref B, ref B2); Swap(ref C, ref C2);
								Swap(ref D, ref D2); Swap(ref E, ref E2);
								Swap(ref H, ref H2); Swap(ref L, ref L2);
								break;
							case 2: PC = IdxReg; break;
							default: SP = IdxReg; break;
						}
					}
					break;
				case 2:
				{
					int nn = Fetch16();
					if (Cond(y)) PC = nn;
					break;
				}
				case 3:
					switch (y)
					{
						case 0: PC = Fetch16(); break;
						case 1: break; // CB is handled in Step
						case 2: Out(Fetch() | (A << 8), A); break;
						case 3: A = In(Fetch() | (A << 8)) & 0xFF; break;
						case 4:
						{
							int v = Read16(SP);
							Write16(SP, IdxReg);
							IdxReg = v;
							break;
						}
						case 5:
						{
							int de = DE;
							DE = HL;
							HL = de;
							break;
						}
						case 6: IFF1 = IFF2 = false; break;
						default: IFF1 = IFF2 = true; break;
					}
					break;
				case 4:
				{
					int nn = Fetch16();
					if (Cond(y))
					{
						Push(PC);
						PC = nn;
					}
					break;
				}
				case 5:
					if (q == 0)
						Push(p == 3 ? AF : GetRP(p));
					else if (p == 0)
					{
						int nn = Fetch16();
						Push(PC);
						PC = nn;
					}
					break; // DD, ED and FD are handled in Step
				case 6:
					Alu(y, Fetch());
					break;
				default:
					Push(PC);
					PC = y * 8;
					break;
			}
		}

		void ExecCB()
		{
			int addr = -1;
			if (_idx != 0)
				addr = MemOperand(); // DD CB d op: the displacement comes before the opcode
			int op = Fetch();
			if (_idx == 0)
				IncR();
			int x = op >> 6, y = (op >> 3) & 7, z = op & 7;
			if (_idx == 0 && z == 6)
				addr = HL;

			int v = addr >= 0 ? Mem[addr] : GetR(z, false);
			int r;
			switch (x)
			{
				case 0: r = Rot(y, v); break;
				case 1:
				{
					int bit = v & (1 << y);
					F = (F & FC) | FH | (bit == 0 ? FZ | FPV : 0) | (bit & FS);
					// Bits 3 and 5 come from the value for a register, from the address high byte for memory.
					F |= addr >= 0 ? (addr >> 8) & FXY : v & FXY;
					return;
				}
				case 2: r = v & ~(1 << y); break;
				default: r = v | (1 << y); break;
			}
			if (addr >= 0)
			{
				Mem[addr] = (byte)r;
				if (_idx != 0 && z != 6)
					SetR(z, r, false); // undocumented: the result is also copied into a register
			}
			else
				SetR(z, r, false);
		}

		void ExecED(int op)
		{
			int x = op >> 6, y = (op >> 3) & 7, z = op & 7, p = y >> 1, q = y & 1;
			if (x == 1)
			{
				switch (z)
				{
					case 0:
					{
						int v = In(BC) & 0xFF;
						if (y != 6) SetR(y, v, false);
						F = (F & FC) | SZP[v] | (v & FXY);
						break;
					}
					case 1:
						Out(BC, y == 6 ? 0 : GetR(y, false));
						break;
					case 2:
						if (q == 0) Sbc16(GetRP(p));
						else Adc16(GetRP(p));
						break;
					case 3:
					{
						int nn = Fetch16();
						if (q == 0) Write16(nn, GetRP(p));
						else SetRP(p, Read16(nn));
						break;
					}
					case 4:
					{
						int v = A;
						A = 0;
						A = Sub8(v, 0);
						break;
					}
					case 5:
						IFF1 = IFF2;
						Ret();
						break;
					case 6:
						IM = new[] { 0, 0, 1, 2, 0, 0, 1, 2 }[y];
						break;
					default:
						switch (y)
						{
							case 0: I = A; break;
							case 1: R = A; break;
							case 2: A = I; F = (F & FC) | SZ[A] | (A & FXY) | (IFF2 ? FPV : 0); break;
							case 3: A = R; F = (F & FC) | SZ[A] | (A & FXY) | (IFF2 ? FPV : 0); break;
							case 4: // RRD
							{
								int m = Mem[HL];
								Mem[HL] = (byte)(((A << 4) | (m >> 4)) & 0xFF);
								A = (A & 0xF0) | (m & 0x0F);
								F = (F & FC) | SZP[A] | (A & FXY);
								break;
							}
							case 5: // RLD
							{
								int m = Mem[HL];
								Mem[HL] = (byte)(((m << 4) | (A & 0x0F)) & 0xFF);
								A = (A & 0xF0) | (m >> 4);
								F = (F & FC) | SZP[A] | (A & FXY);
								break;
							}
						}
						break;
				}
			}
			else if (x == 2 && z <= 3 && y >= 4)
				Block(y, z);
			// Everything else in the ED page acts as a two-byte NOP.
		}

		void Block(int y, int z)
		{
			int dir = (y & 1) == 0 ? 1 : -1;
			bool repeat = y >= 6;
			switch (z)
			{
				case 0: // LDI LDD LDIR LDDR
				{
					int v = Mem[HL];
					Mem[DE] = (byte)v;
					HL = (HL + dir) & 0xFFFF;
					DE = (DE + dir) & 0xFFFF;
					BC = (BC - 1) & 0xFFFF;
					int n = v + A;
					F = (F & (FS | FZ | FC)) | (BC != 0 ? FPV : 0) | (n & FX) | ((n << 4) & FY);
					if (repeat && BC != 0)
						PC = (PC - 2) & 0xFFFF;
					break;
				}
				case 1: // CPI CPD CPIR CPDR
				{
					int v = Mem[HL];
					int r = (A - v) & 0xFF;
					int h = (A ^ v ^ r) & FH;
					HL = (HL + dir) & 0xFFFF;
					BC = (BC - 1) & 0xFFFF;
					int n = r - (h != 0 ? 1 : 0);
					F = (F & FC) | FN | SZ[r] | h | (BC != 0 ? FPV : 0) | (n & FX) | ((n << 4) & FY);
					if (repeat && BC != 0 && r != 0)
						PC = (PC - 2) & 0xFFFF;
					break;
				}
				case 2: // INI IND INIR INDR
				{
					Mem[HL] = (byte)In(BC);
					HL = (HL + dir) & 0xFFFF;
					B = (B - 1) & 0xFF;
					F = SZ[B] | FN | (B & FXY) | (F & FC);
					if (repeat && B != 0)
						PC = (PC - 2) & 0xFFFF;
					break;
				}
				default: // OUTI OUTD OTIR OTDR
				{
					int v = Mem[HL];
					B = (B - 1) & 0xFF;
					Out(BC, v);
					HL = (HL + dir) & 0xFFFF;
					F = SZ[B] | FN | (B & FXY) | (F & FC);
					if (repeat && B != 0)
						PC = (PC - 2) & 0xFFFF;
					break;
				}
			}
		}

		static void Swap(ref int a, ref int b)
		{
			int t = a;
			a = b;
			b = t;
		}
	}
}
