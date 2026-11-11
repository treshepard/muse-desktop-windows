# Lua scripts for DaVinci Resolve (Free edition)

Resolve Free can't be driven from outside the app, but it runs Lua scripts from inside via **Workspace → Scripts**. Copy these seven scripts into:

```
%APPDATA%\Blackmagic Design\DaVinci Resolve\Support\Fusion\Scripts\Utility
```

and they appear in that menu by name.

**To run one:** open the project in Resolve, open **Workspace → Console** (so you can see the report each script prints), then **Workspace → Scripts → "Muse - …"**.

| Script | What it does | What to edit first |
| --- | --- | --- |
| Muse - 1 Project Setup | Creates standard bins (A-Roll, B-Roll, Audio, Graphics, …) in the current project. Safe to re-run. | The BINS list, if a project differs |
| Muse - 2 Import Footage Folder | Imports every media file from a folder (incl. subfolders) into a bin | FOLDER, TARGET_BIN |
| Muse - 3 Rough Cut From List | Builds a timeline laying the named clips end-to-end in list order | Clip names + optional in/out seconds |
| Muse - 4 Section Markers | Adds named/colored markers to the current timeline | Marker names and times |
| Muse - 5 Apply LUT | Applies a .cube LUT to clips on the current timeline | LUT_PATH |
| Muse - 6 Copy Hero Grade | Copies your hand-graded hero clip's grade to the other clips from the same bin | HERO_CLIP name |
| Muse - 7 Project Report | Writes bins/clips/timelines/markers to `Documents\Muse Resolve Report.txt` so your Muse can read it back | — |

Rules the scripts follow:

- They lay the clips **you** name in the order **you** name. They never pick takes.
- Scripting cannot build a grade from nothing (no curves/qualifiers), trim or move clips already on a timeline, or add speed ramps — on any edition. Grades spread by copying a hero clip you graded by hand.
- Every script prints a summary and saves the project when it changes something.

Tip: your Muse can edit the list at the top of a script for each job (footage folder, clip order, marker times) — you just run it from the menu.
