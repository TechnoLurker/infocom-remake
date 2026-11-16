using System.IO;
using UnityEngine;

namespace InfocomWorkbench
{
	/// <summary>
	/// Puts the Workbench on screen. It creates itself when Play starts, in any scene,
	/// so there is nothing to set up: open the project and press Play.
	/// Everything is drawn into a 640x256 (PAL) or 640x200 (NTSC) texture and scaled up by whole pixels.
	/// Input goes through IMGUI events, so it works with either Unity input system enabled.
	/// </summary>
	public sealed class WorkbenchHost : MonoBehaviour
	{
		[Tooltip("Folder that holds the story files. Empty = a 'Games' folder next to Assets (in the editor) or next to the .exe (in a build).")]
		public string gamesFolder = "";

		[Tooltip("Folder for saved games. Empty = a 'Saves' folder beside the Games folder.")]
		public string savesFolder = "";

		Workbench _wb;
		Texture2D _texture;
		Color32[] _pixels;
		Color32[] _palette;
		Rect _screenRect;
		bool _quitting;

		const string PrefPal = "InfocomWorkbench.Pal";
		const string PrefTall = "InfocomWorkbench.TallPixels";

		static WorkbenchHost _instance;

		// Unity 6 can enter Play mode without reloading scripts, so statics must be reset by hand.
		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
		static void ResetStatics()
		{
			_instance = null;
		}

		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
		static void AutoStart()
		{
			// A host placed in the scene by hand wins; otherwise make one.
			if (_instance != null)
				return;
			var go = new GameObject("Infocom Workbench");
			go.AddComponent<WorkbenchHost>();
		}

		void Awake()
		{
			if (_instance != null && _instance != this)
			{
				Destroy(gameObject);
				return;
			}
			_instance = this;
			DontDestroyOnLoad(gameObject);
			Application.targetFrameRate = 60;
			Application.runInBackground = true;

			string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
			string games = string.IsNullOrEmpty(gamesFolder) ? Path.Combine(root, "Games") : gamesFolder;
			string saves = string.IsNullOrEmpty(savesFolder) ? Path.Combine(Path.GetDirectoryName(games.TrimEnd('/', '\\')) ?? root, "Saves") : savesFolder;

			_palette = new Color32[Pen.Rgb.Length];
			for (int i = 0; i < Pen.Rgb.Length; i++)
			{
				uint c = Pen.Rgb[i];
				_palette[i] = new Color32((byte)(c >> 16), (byte)(c >> 8), (byte)c, 255);
			}

			bool pal = PlayerPrefs.GetInt(PrefPal, 1) == 1;
			_wb = new Workbench(games, saves, pal);
			_wb.TallPixels = PlayerPrefs.GetInt(PrefTall, 1) == 1;
			_wb.DisplayChanged += OnDisplayChanged;
			_wb.QuitRequested += Quit;
			CreateTexture();

			// The camera only needs to clear to black around the Amiga screen.
			var cam = Camera.main;
			if (cam != null)
			{
				cam.clearFlags = CameraClearFlags.SolidColor;
				cam.backgroundColor = Color.black;
			}
		}

		void CreateTexture()
		{
			if (_texture != null)
				Destroy(_texture);
			_texture = new Texture2D(Workbench.ScreenWidth, _wb.Height, TextureFormat.RGBA32, false)
			{
				filterMode = FilterMode.Point,
				wrapMode = TextureWrapMode.Clamp,
			};
			_pixels = new Color32[Workbench.ScreenWidth * _wb.Height];
		}

		void OnDisplayChanged()
		{
			PlayerPrefs.SetInt(PrefPal, _wb.Pal ? 1 : 0);
			PlayerPrefs.SetInt(PrefTall, _wb.TallPixels ? 1 : 0);
			PlayerPrefs.Save();
			if (_texture != null && _texture.height != _wb.Height)
				CreateTexture();
		}

		void Update()
		{
			if (_wb == null)
				return;
			_wb.Update(Time.unscaledTimeAsDouble);
			_wb.Render();

			// Version 6 pictures change their colour registers as they load, so refresh the palette.
			for (int i = 0; i < Pen.Rgb.Length && i < _palette.Length; i++)
			{
				uint c = Pen.Rgb[i];
				_palette[i] = new Color32((byte)(c >> 16), (byte)(c >> 8), (byte)c, 255);
			}

			// Indexed pixels to colours. Texture rows run bottom-up, the frame buffer top-down.
			var fb = _wb.Frame;
			byte[] src = fb.Pixels;
			int w = fb.Width, h = fb.Height;
			for (int y = 0; y < h; y++)
			{
				int s = y * w;
				int d = (h - 1 - y) * w;
				for (int x = 0; x < w; x++)
					_pixels[d + x] = _palette[src[s + x]];
			}
			_texture.SetPixels32(_pixels);
			_texture.Apply(false);
		}

		Rect ComputeScreenRect()
		{
			float aspectY = _wb.TallPixels ? 2f : 1f;
			float w = Workbench.ScreenWidth, h = _wb.Height * aspectY;
			float scale = Mathf.Min(Screen.width / w, Screen.height / h);
			if (scale >= 1f)
				scale = Mathf.Floor(scale); // whole pixels stay crisp
			float dw = w * scale, dh = h * scale;
			return new Rect(Mathf.Floor((Screen.width - dw) / 2f), Mathf.Floor((Screen.height - dh) / 2f), dw, dh);
		}

		void OnGUI()
		{
			if (_wb == null || _texture == null)
				return;
			var e = Event.current;
			_screenRect = ComputeScreenRect();

			// Mouse position in Amiga pixels (IMGUI already has a top-left origin).
			Vector2 mp = e.mousePosition;
			int ax = Mathf.FloorToInt((mp.x - _screenRect.x) / _screenRect.width * Workbench.ScreenWidth);
			int ay = Mathf.FloorToInt((mp.y - _screenRect.y) / _screenRect.height * _wb.Height);
			bool inside = _screenRect.Contains(mp);
			Cursor.visible = !inside;
			_wb.MouseMove(Mathf.Clamp(ax, 0, Workbench.ScreenWidth - 1), Mathf.Clamp(ay, 0, _wb.Height - 1));

			switch (e.type)
			{
				case EventType.Repaint:
					GUI.DrawTexture(_screenRect, _texture, ScaleMode.StretchToFill, false);
					break;

				case EventType.MouseDown:
					if (e.button == 0 || e.button == 1)
						_wb.MouseDown(e.button);
					e.Use();
					break;

				case EventType.MouseUp:
					if (e.button == 0 || e.button == 1)
						_wb.MouseUp(e.button);
					e.Use();
					break;

				case EventType.ScrollWheel:
					_wb.Wheel(e.delta.y < 0 ? 1 : -1);
					e.Use();
					break;

				case EventType.KeyDown:
					HandleKey(e);
					break;
			}
		}

		void HandleKey(Event e)
		{
			switch (e.keyCode)
			{
				case KeyCode.Return:
				case KeyCode.KeypadEnter:
					_wb.Key(WbKey.Enter); e.Use(); return;
				case KeyCode.Backspace:
					_wb.Key(WbKey.Backspace); e.Use(); return;
				case KeyCode.UpArrow:
					_wb.Key(WbKey.Up); e.Use(); return;
				case KeyCode.DownArrow:
					_wb.Key(WbKey.Down); e.Use(); return;
				case KeyCode.PageUp:
					_wb.Key(WbKey.PageUp); e.Use(); return;
				case KeyCode.PageDown:
					_wb.Key(WbKey.PageDown); e.Use(); return;
				case KeyCode.Escape:
					_wb.Key(WbKey.Escape); e.Use(); return;
			}

			// IMGUI sends printable characters as a separate KeyDown with keyCode None.
			char c = e.character;
			if (c >= 32 && c < 127)
			{
				_wb.KeyChar(c);
				e.Use();
			}
		}

		void Quit()
		{
#if UNITY_EDITOR
			UnityEditor.EditorApplication.isPlaying = false;
#else
			Application.Quit();
#endif
		}

		void OnApplicationQuit()
		{
			_quitting = true;
			_wb?.Shutdown();
		}

		void OnDestroy()
		{
			if (_instance != this)
				return;
			_instance = null;
			if (!_quitting)
				_wb?.Shutdown();
			Cursor.visible = true;
			if (_texture != null)
				Destroy(_texture);
		}
	}
}
