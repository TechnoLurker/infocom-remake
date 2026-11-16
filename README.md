# infocom-remake

Play Infocom story files in a Unity app that looks like Amiga Workbench 1.3.

## Set up

1. Clone this repository.
2. In Unity Hub choose **Add > Add project from disk** and pick the repository folder. Use Unity 2021.3 or newer. On first open Unity creates `ProjectSettings`, `Packages` and a `.meta` file for each asset; commit those.
3. Make a folder called `Games` in the repository root (beside `Assets`; Unity creates it on first run if you skip this) and copy your story files into it: `.z3`, `.z4`, `.z5`, `.z6`, `.dat` or `.data`. The icon label is the file name, so rename them how you like (for example `Zork I.z3`). `Games` and `Saves` are ignored by git, so story files never get committed.
4. Press Play. No scene setup is needed: the Workbench starts by itself in any scene.

For a full-screen app: File > Build Settings > Windows, then Build. Put the `Games` folder next to the built `.exe`.

![Zork I in the game window](docs/preview/zork-window.png)

## Using it

- Double-click a disk icon to start that story. Several can run at once.
- Drag windows by the title bar. The top-right gadgets send a window to the front or back, the top-left one closes it, and the bottom-right one resizes it.
- Hold the **right mouse button** for the menu bar: **Workbench** (Open, Rescan games, About, Quit), **Story** (Save, Restore, Restart, Quit story, Close window), **Display** (PAL 640x256, NTSC 640x200, Tall pixels).
- Typing: Up and Down recall earlier commands. Page Up/Page Down or the mouse wheel scroll back. Any key continues past `[MORE]`.
- Saves go in a `Saves` folder beside `Games`. You're asked for a file name, as in the originals.

## What's inside

| Folder | What it does |
|---|---|
| `ZMachineLib/` | Based on Brian Peek's MIT-licensed C# Z-machine, reworked against the Z-Machine Standard 1.1 for versions 1-6: version 6 opcodes and windows, output stream 3, catch/throw, undo, timed input, custom alphabets, dictionary lookup by encoded text, a binary save format, and fixes to object removal, properties and attributes. |
| `Core/` | Plain C#, no Unity: the 640-pixel indexed frame buffer, Topaz font, game windows, Workbench desktop, menus, Guru Meditation alert. |
| `Core/V6Screen.cs`, `Core/V6Session.cs` | The version 6 screen: eight pixel windows the game lays out itself, drawn in Topaz, with the Z-machine colours, [MORE], and mouse clicks. |
| `Unity/WorkbenchHost.cs` | The only Unity code: turns the frame buffer into a texture, scales it by whole pixels, and passes mouse and keys in. |

## Known limits

- Version 6 games (Zork Zero, Shogun, Journey, Arthur) run as text only for now. Pictures aren't drawn yet, so Zork Zero has no status bar, no drop capitals and no picture puzzles. The Amiga picture file (`Pic.data` on the Zork Zero disk) uses a compression that isn't decoded yet. Only Zork Zero has been tested.
- Version 4 and 5 games (A Mind Forever Voyaging, Trinity, Beyond Zork, Border Zone, Sherlock, and others) may hit bugs. If one does, the Guru Meditation alert shows and the error is printed in the game window.
- No sound effects (only The Lurking Horror and Sherlock used them).
- Desktop and window details are a first pass and will be tuned against real Workbench 1.3 screenshots.

## Credits

- ZMachineLib (c) 2016 Brian Peek, MIT licence (`ZMachineLib/LICENSE.txt`).
- Topaz font data from TrueSchool Ascii's Multi Platform Amiga Fonts v1.02 by dMG/t!s^dS!, GPL with font exception. Topaz is (c) Amiga Inc.
