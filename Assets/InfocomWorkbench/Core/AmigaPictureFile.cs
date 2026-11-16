using System;
using System.Collections.Generic;
using System.IO;

namespace InfocomWorkbench
{
	/// <summary>
	/// Reads the picture file from Infocom's Amiga version 6 disks (Pic.data on the Zork Zero disk).
	/// Worked out from the file and the Amiga interpreter on the disk:
	///
	/// Header (16 bytes, big-endian): byte 1 flags; word 2 = offset/2 of a 256-byte decoding tree;
	/// word 4 = number of pictures; byte 8 = directory entry size; word 14 = version.
	/// Directory entry: number, width, height, flags (words), data offset (3 bytes), palette offset
	/// (3 bytes, 0 = keep the current palette). When header flags &amp; 6 == 2 each entry instead
	/// carries its own tree offset/2 in a word after the palette offset.
	/// Palette: count byte (at most 14), then count RGB triples (4 bits used each) for colour registers 2 up.
	/// Picture data: 3-byte packed length, 3-byte number of codes, then a bit stream read most significant bit
	/// first. Walk the tree from node 0: next = tree[node * 2 + bit]. A value below 0x80 is the next node;
	/// 0x80-0x8F is a colour (0-15) and becomes the current colour; 0x90-0xFF repeats the current colour
	/// (value - 0x8F) times. Each colour is XORed with the pixel above it (the first row with 0).
	/// Each picture pixel is two Amiga hi-res pixels wide, so widths are doubled on screen.
	/// Flags bit 0: colour 0 is transparent.
	/// </summary>
	public sealed class AmigaPictureFile
	{
		public struct Entry
		{
			public int Number, Width, Height, Flags, DataOffset, PaletteOffset, TreeOffset;
		}

		public sealed class Picture
		{
			public int Width, Height;
			public bool Transparent;
			/// <summary>Colour register (0-15) per pixel, row by row.</summary>
			public byte[] Pixels;
			/// <summary>RGB for registers 2 and up, or null to keep the current colours.</summary>
			public uint[] Palette;
		}

		readonly byte[] _data;
		readonly int _treeOffset;
		readonly bool _treePerPicture;
		readonly Dictionary<int, Entry> _entries = new Dictionary<int, Entry>();
		readonly Dictionary<int, Picture> _cache = new Dictionary<int, Picture>();

		public int Count { get; }
		public int Version { get; }

		public AmigaPictureFile(byte[] data)
		{
			_data = data;
			if (data.Length < 16)
				throw new InvalidDataException("Picture file too short");
			int flags = data[1];
			Count = Word(4);
			int entrySize = data[8];
			Version = Word(14);
			_treePerPicture = (flags & 6) == 2;
			_treeOffset = Word(2) * 2;
			if (entrySize < (_treePerPicture ? 16 : 14) || 16 + Count * entrySize > data.Length
				|| (!_treePerPicture && _treeOffset + 256 > data.Length))
				throw new InvalidDataException("Not an Amiga Infocom picture file");

			for (int i = 0; i < Count; i++)
			{
				int e = 16 + i * entrySize;
				var entry = new Entry
				{
					Number = Word(e),
					Width = Word(e + 2),
					Height = Word(e + 4),
					Flags = Word(e + 6),
					DataOffset = Triple(e + 8),
					PaletteOffset = Triple(e + 11),
					TreeOffset = _treePerPicture ? Word(e + 14) * 2 : _treeOffset,
				};
				_entries[entry.Number] = entry;
			}
		}

		/// <summary>Looks for the picture file beside a story: "Name.pic", "Name.pic.data", then "Pic.data".</summary>
		public static AmigaPictureFile FindFor(string storyPath)
		{
			string dir = Path.GetDirectoryName(storyPath) ?? ".";
			string name = Path.GetFileNameWithoutExtension(storyPath);
			foreach (string candidate in new[] { name + ".pic", name + ".pic.data", "Pic.data" })
			{
				string path = Path.Combine(dir, candidate);
				if (!File.Exists(path))
					continue;
				try
				{
					return new AmigaPictureFile(File.ReadAllBytes(path));
				}
				catch (Exception)
				{
				}
			}
			return null;
		}

		int Word(int at) => _data[at] << 8 | _data[at + 1];
		int Triple(int at) => _data[at] << 16 | _data[at + 1] << 8 | _data[at + 2];

		public bool TryGetEntry(int number, out Entry entry) => _entries.TryGetValue(number, out entry);

		public Picture Get(int number)
		{
			if (_cache.TryGetValue(number, out var cached))
				return cached;
			if (!_entries.TryGetValue(number, out var e) || e.Width == 0 || e.Height == 0 || e.DataOffset == 0
				|| e.TreeOffset + 256 > _data.Length)
				return null;
			var pic = new Picture
			{
				Width = e.Width,
				Height = e.Height,
				Transparent = (e.Flags & 1) != 0,
				Pixels = Decode(e),
				Palette = e.PaletteOffset != 0 ? ReadPalette(e.PaletteOffset) : null,
			};
			_cache[number] = pic;
			return pic;
		}

		uint[] ReadPalette(int at)
		{
			int count = Math.Min(14, (int)_data[at]);
			var rgb = new uint[count];
			for (int i = 0; i < count; i++)
			{
				int r = _data[at + 1 + i * 3] >> 4, g = _data[at + 2 + i * 3] >> 4, b = _data[at + 3 + i * 3] >> 4;
				rgb[i] = (uint)(r * 17 << 16 | g * 17 << 8 | b * 17);
			}
			return rgb;
		}

		byte[] Decode(Entry e)
		{
			int w = e.Width, total = e.Width * e.Height;
			var output = new byte[total];
			int codes = Triple(e.DataOffset + 3);
			int p = e.DataOffset + 6;
			int tree = e.TreeOffset;
			int i = 0, node = 0;
			byte colour = 0;
			while (codes > 0 && i < total && p < _data.Length)
			{
				int b = _data[p++];
				for (int bit = 7; bit >= 0 && codes > 0 && i < total; bit--)
				{
					node = _data[tree + ((node * 2 + ((b >> bit) & 1)) & 0xff)];
					if (node < 0x80)
						continue;
					if (node < 0x90)
					{
						colour = (byte)(node - 0x80);
						output[i] = (byte)((i >= w ? output[i - w] : 0) ^ colour);
						i++;
					}
					else
					{
						for (int n = node - 0x8f; n > 0 && i < total; n--, i++)
							output[i] = (byte)((i >= w ? output[i - w] : 0) ^ colour);
					}
					node = 0;
					codes--;
				}
			}
			return output;
		}
	}
}
