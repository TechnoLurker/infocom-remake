namespace InfocomWorkbench
{
	/// <summary>Hand-drawn pixel art in the Workbench 1.3 style. Characters map to pens in PenFor.</summary>
	public static class Art
	{
		// B blue, W white, K black, O orange; R/D/L pointer red, dark, light. Anything else is transparent.
		public static int PenFor(char c)
		{
			switch (c)
			{
				case 'B': return Pen.Blue;
				case 'W': return Pen.White;
				case 'K': return Pen.Black;
				case 'O': return Pen.Orange;
				case 'R': return Pen.PointerRed;
				case 'D': return Pen.PointerDark;
				case 'L': return Pen.PointerLight;
				default: return -1;
			}
		}

		/// <summary>3.5" disk icon, one per story file. Drawn on hi-res (tall) pixels.</summary>
		public static readonly string[] Disk =
		{
			"KKKKKKKKKKKKKKKKKKKKKKKKKKKKKK",
			"KKKKKKKWWWWWWWWWWWWWWWWKKKKKKK",
			"KKKKKKKWWWWWWKKKWWWWWWWKKKKKKK",
			"KKKKKKKWWWWWWKKKWWWWWWWKKKKKKK",
			"KKKKKKKWWWWWWKKKWWWWWWWKKKKKKK",
			"KKKKKKKWWWWWWWWWWWWWWWWKKKKKKK",
			"KKKKKKKKKKKKKKKKKKKKKKKKKKKKKK",
			"KKKKKKKKKKKKKKKKKKKKKKKKKKKKKK",
			"KKKWWWWWWWWWWWWWWWWWWWWWWWWKKK",
			"KKKWOOOOOOOOOOOOOOOOOOOOOOWKKK",
			"KKKWWWWWWWWWWWWWWWWWWWWWWWWKKK",
			"KKKWOOOOOOOOOOOOOOOOOOOOOOWKKK",
			"KKKWWWWWWWWWWWWWWWWWWWWWWWWKKK",
			"KKKWWWWWWWWWWWWWWWWWWWWWWWWKKK",
			" KKKKKKKKKKKKKKKKKKKKKKKKKKKKK",
		};

		/// <summary>Mouse pointer. Drawn on lo-res pixels (each one two screen pixels wide).</summary>
		public static readonly string[] Pointer =
		{
			"RR         ",
			"DRRR       ",
			" DRRRR     ",
			" DRRRRRR   ",
			"  DRRRRRRR ",
			"  DRRRRRRRR",
			"   DRRRL   ",
			"   DRRLRR  ",
			"    DR DRR ",
			"    DR  DRR",
			"     R   DR",
		};

		/// <summary>Busy pointer, shown while a story is starting.</summary>
		public static readonly string[] Busy =
		{
			"  DDDDDDD  ",
			" DLLLLLLLD ",
			"  DRRRRRD  ",
			"   DRRRD   ",
			"    DRD    ",
			"    DLD    ",
			"   DL LD   ",
			"  DL R LD  ",
			" DLRRRRRLD ",
			"  DDDDDDD  ",
		};

		public static readonly string[] CheckMark =
		{
			"      BB",
			"     BB ",
			"B   BB  ",
			"BB BB   ",
			" BBB    ",
			"  B     ",
		};
	}
}
