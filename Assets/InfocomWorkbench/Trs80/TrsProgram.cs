using System;
using System.IO;

namespace InfocomWorkbench.Trs80
{
	/// <summary>
	/// A machine-language program for the TRS-80 Model I/III, read from a SYSTEM cassette image (.cas)
	/// or a TRSDOS command file (.cmd).
	/// </summary>
	public sealed class TrsProgram
	{
		public readonly byte[] Memory = new byte[65536];
		public int Entry = -1;
		public int Low = 0xFFFF, High;
		public string Name = string.Empty;

		public static bool IsTrsFile(string path)
		{
			string ext = Path.GetExtension(path).ToLowerInvariant();
			return ext == ".cas" || ext == ".cmd";
		}

		/// <summary>True if the file looks like something <see cref="Load"/> can run.</summary>
		public static bool Looks(string path)
		{
			try
			{
				Load(path);
				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}

		public static TrsProgram Load(string path)
		{
			byte[] data = File.ReadAllBytes(path);
			var p = new TrsProgram();
			if (Path.GetExtension(path).ToLowerInvariant() == ".cmd")
				p.ReadCmd(data);
			else
				p.ReadSystemTape(data);
			if (p.Entry < 0 || p.High < p.Low)
				throw new InvalidDataException("No program found in " + Path.GetFileName(path) + ".");
			return p;
		}

		void Put(int addr, byte[] data, int offset, int count)
		{
			for (int i = 0; i < count; i++)
			{
				int a = (addr + i) & 0xFFFF;
				Memory[a] = data[offset + i];
				Low = Math.Min(Low, a);
				High = Math.Max(High, a);
			}
		}

		/// <summary>
		/// Level II SYSTEM tape: leader of zeros, A5 sync, 55, six-letter name, then blocks of
		/// 3C len addr-lo addr-hi data checksum, ending with 78 entry-lo entry-hi.
		/// </summary>
		void ReadSystemTape(byte[] d)
		{
			int p = Array.IndexOf(d, (byte)0xA5);
			if (p < 0 || p + 8 >= d.Length || d[p + 1] != 0x55)
				throw new InvalidDataException("Not a SYSTEM (machine-language) cassette image.");
			Name = System.Text.Encoding.ASCII.GetString(d, p + 2, 6).Trim();
			p += 8;
			while (p < d.Length)
			{
				int type = d[p];
				if (type == 0x3C)
				{
					if (p + 4 >= d.Length)
						throw new InvalidDataException("Cassette image ends in the middle of a block.");
					int n = d[p + 1] == 0 ? 256 : d[p + 1];
					int addr = d[p + 2] | (d[p + 3] << 8);
					if (p + 4 + n >= d.Length)
						throw new InvalidDataException("Cassette image ends in the middle of a block.");
					int sum = d[p + 2] + d[p + 3];
					for (int i = 0; i < n; i++)
						sum += d[p + 4 + i];
					if ((sum & 0xFF) != d[p + 4 + n])
						throw new InvalidDataException(string.Format("Checksum error in the block at {0:X4}.", addr));
					Put(addr, d, p + 4, n);
					p += 5 + n;
				}
				else if (type == 0x78)
				{
					Entry = d[p + 1] | (d[p + 2] << 8);
					return;
				}
				else
					throw new InvalidDataException(string.Format("Unexpected byte {0:X2} on the tape.", type));
			}
		}

		/// <summary>
		/// TRSDOS /CMD: records of type, length, data. Type 01 loads (length counts the two address bytes;
		/// 0, 1 and 2 mean 256, 257 and 258), 02 is the entry address, 05 the name, anything else is skipped.
		/// </summary>
		void ReadCmd(byte[] d)
		{
			int p = 0;
			while (p + 1 < d.Length)
			{
				int type = d[p], len = d[p + 1];
				p += 2;
				if (type == 0x01)
				{
					if (len <= 2)
						len += 256;
					int addr = d[p] | (d[p + 1] << 8);
					Put(addr, d, p + 2, Math.Min(len - 2, d.Length - p - 2));
					p += len;
				}
				else if (type == 0x02)
				{
					Entry = d[p] | (d[p + 1] << 8);
					return;
				}
				else
				{
					if (type == 0x05)
						Name = System.Text.Encoding.ASCII.GetString(d, p, Math.Min(len, d.Length - p)).Trim();
					p += len;
				}
			}
		}
	}
}
