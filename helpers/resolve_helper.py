#!/usr/bin/env python3
"""
Muse Bridge helper for DaVinci Resolve (Studio external scripting route).

Called by Muse.exe /resolve/python with:
  --action <name> --params <params.json> --out <result.json>

Always writes a JSON result file: {"ok": true, "data": ...} or
{"ok": false, "error": "..."}. Exits 0 either way; the bridge reads the file.

Supported actions (all wrap documented scripting-API calls and report
False/None returns instead of pretending they worked):
  info                 product name/version, current project/page
  list_projects        projects in the current database folder (names)
  open_project         {name}
  create_project       {name}
  import_media         {paths: [..]} -> clip ids/names added to media pool
  create_bin           {name, parent?} create media-pool subfolder
  list_media           media pool tree (folders + clip names)
  create_timeline      {name, from_clips?: [names]} empty or from clips
  list_timelines       timeline names in current project
  append_clips         {timeline, clips: [media-pool clip names]}
  add_marker           {timeline, frame, name, note?, color?}
  get_markers          {timeline}
  apply_lut            {timeline, lut_path} timeline-level LUT (node graph)
  apply_cdl            {timeline_item_index?, cdl: {slope, offset, power, saturation}}
  set_timeline_setting {setting, value}
  render_presets       list render preset names
  add_render_job       {preset} -> job id
  start_render         {}
  render_status        job list with statuses
  save_project         {}
  close_project        {}

API ceiling (Blackmagic's): no scripted primary grades/curves/qualifiers,
no trimming/moving existing timeline clips, no speed ramps. Applying saved
grades/LUTs/CDLs and building timelines from clips ARE supported.
"""
import argparse
import json
import os
import sys


def fail(msg):
    return {"ok": False, "error": msg}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--action", required=True)
    ap.add_argument("--params", required=True)
    ap.add_argument("--out", required=True)
    args = ap.parse_args()

    with open(args.params, "r", encoding="utf-8") as f:
        params = json.load(f)

    result = run(args.action, params)

    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(result, f, indent=2, default=str)


def connect():
    try:
        import DaVinciResolveScript as dvr
    except Exception as e:
        return None, fail(
            "DaVinciResolveScript module not importable (%s). Check RESOLVE_SCRIPT_API / "
            "PYTHONPATH and that Python 3.10-3.12 is used." % e)
    resolve = dvr.scriptapp("Resolve")
    if resolve is None:
        return None, fail(
            "Could not connect to a running DaVinci Resolve. Open Resolve first, and on "
            "Studio set Preferences > System > General > 'External scripting using' = Local. "
            "On the Free edition external scripting is blocked by Blackmagic (Studio-only "
            "since 19.1) — use the bridge's Lua queue-script route instead.")
    return resolve, None


def run(action, p):
    resolve, err = connect()
    if err:
        return err
    pm = resolve.GetProjectManager()
    project = pm.GetCurrentProject()

    def need_project():
        if project is None:
            raise RuntimeError("no project is open in Resolve")
        return project

    try:
        if action == "info":
            return ok({
                "product": resolve.GetProductName(),
                "version": resolve.GetVersionString(),
                "current_page": resolve.GetCurrentPage(),
                "current_project": project.GetName() if project else None,
            })

        if action == "list_projects":
            folder = pm.GetCurrentFolder()
            names = pm.GetProjectListInCurrentFolder() or []
            return ok({"folder": folder, "projects": names})

        if action == "open_project":
            proj = pm.LoadProject(p["name"])
            return ok({"opened": proj.GetName()}) if proj else fail("LoadProject returned None for %r" % p["name"])

        if action == "create_project":
            proj = pm.CreateProject(p["name"])
            return ok({"created": proj.GetName()}) if proj else fail("CreateProject failed for %r" % p["name"])

        if action == "import_media":
            need_project()
            mp = project.GetMediaPool()
            clips = mp.ImportMedia(p["paths"])
            if not clips:
                return fail("ImportMedia added nothing (check paths / codecs)")
            return ok({"imported": [c.GetName() for c in clips], "count": len(clips)})

        if action == "create_bin":
            need_project()
            mp = project.GetMediaPool()
            parent = mp.GetRootFolder()
            if p.get("parent"):
                parent = find_folder(parent, p["parent"]) or parent
            folder = mp.AddSubFolder(parent, p["name"])
            return ok({"created": folder.GetName()}) if folder else fail("AddSubFolder failed")

        if action == "list_media":
            need_project()
            root = project.GetMediaPool().GetRootFolder()
            return ok({"tree": folder_tree(root)})

        if action == "create_timeline":
            need_project()
            mp = project.GetMediaPool()
            if p.get("from_clips"):
                clips = []
                root = mp.GetRootFolder()
                for name in p["from_clips"]:
                    clip = find_clip(root, name)
                    if clip is None:
                        return fail("clip not found in media pool: %r" % name)
                    clips.append(clip)
                tl = mp.CreateTimelineFromClips(p["name"], clips)
            else:
                tl = mp.CreateEmptyTimeline(p["name"])
            return ok({"created": tl.GetName()}) if tl else fail("timeline creation failed")

        if action == "list_timelines":
            proj = need_project()
            names = []
            for i in range(1, (proj.GetTimelineCount() or 0) + 1):
                tl = proj.GetTimelineByIndex(i)
                if tl:
                    names.append(tl.GetName())
            return ok({"timelines": names, "current": proj.GetCurrentTimeline().GetName()
                       if proj.GetCurrentTimeline() else None})

        if action == "append_clips":
            proj = need_project()
            tl = find_timeline(proj, p["timeline"])
            if tl is None:
                return fail("timeline not found: %r" % p["timeline"])
            proj.SetCurrentTimeline(tl)
            mp = proj.GetMediaPool()
            root = mp.GetRootFolder()
            clips = []
            for name in p["clips"]:
                clip = find_clip(root, name)
                if clip is None:
                    return fail("clip not found in media pool: %r" % name)
                clips.append(clip)
            added = mp.AppendToTimeline(clips)
            return ok({"appended": len(added) if added else 0})

        if action == "add_marker":
            proj = need_project()
            tl = find_timeline(proj, p["timeline"]) if p.get("timeline") else proj.GetCurrentTimeline()
            if tl is None:
                return fail("no timeline selected")
            done = tl.AddMarker(int(p["frame"]), p.get("color", "Blue"),
                                p.get("name", "Muse"), p.get("note", ""), 1)
            return ok({"added": bool(done)})

        if action == "get_markers":
            proj = need_project()
            tl = find_timeline(proj, p["timeline"]) if p.get("timeline") else proj.GetCurrentTimeline()
            if tl is None:
                return fail("no timeline selected")
            return ok({"markers": tl.GetMarkers() or {}})

        if action == "apply_lut":
            proj = need_project()
            tl = find_timeline(proj, p["timeline"]) if p.get("timeline") else proj.GetCurrentTimeline()
            if tl is None:
                return fail("no timeline selected")
            graph = tl.GetNodeGraph()
            if graph is None:
                return fail("timeline node graph unavailable")
            done = graph.SetLUT(1, p["lut_path"])
            return ok({"applied": bool(done)})

        if action == "apply_cdl":
            proj = need_project()
            tl = proj.GetCurrentTimeline()
            items = tl.GetItemListInTrack("video", 1) if tl else []
            idx = int(p.get("timeline_item_index", 0))
            if idx >= len(items):
                return fail("no video item at index %d" % idx)
            cdl = p["cdl"]
            done = items[idx].SetCDL({
                "NodeIndex": "1",
                "Slope": cdl.get("slope", "1 1 1"),
                "Offset": cdl.get("offset", "0 0 0"),
                "Power": cdl.get("power", "1 1 1"),
                "Saturation": cdl.get("saturation", "1"),
            })
            return ok({"applied": bool(done)})

        if action == "set_timeline_setting":
            proj = need_project()
            tl = proj.GetCurrentTimeline()
            done = tl.SetSetting(p["setting"], str(p["value"]))
            return ok({"set": bool(done)})

        if action == "render_presets":
            proj = need_project()
            return ok({"presets": proj.GetRenderPresetList() or []})

        if action == "add_render_job":
            proj = need_project()
            if p.get("preset"):
                if not proj.LoadRenderPreset(p["preset"]):
                    return fail("could not load render preset %r" % p["preset"])
            job = proj.AddRenderJob()
            return ok({"job_id": job}) if job else fail("AddRenderJob returned None")

        if action == "start_render":
            proj = need_project()
            return ok({"rendering": bool(proj.StartRendering())})

        if action == "render_status":
            proj = need_project()
            jobs = []
            for job_id in proj.GetRenderJobList() or []:
                status = proj.GetRenderJobStatus(job_id) or {}
                jobs.append({"job_id": job_id, "status": status})
            return ok({"jobs": jobs, "is_rendering": bool(proj.IsRenderingInProgress())})

        if action == "save_project":
            need_project()
            return ok({"saved": bool(pm.SaveProject())})

        if action == "close_project":
            need_project()
            return ok({"closed": bool(pm.CloseProject(project))})

        return fail("unknown action: %s" % action)
    except Exception as e:
        return fail("%s: %s" % (type(e).__name__, e))


def ok(data):
    return {"ok": True, "data": data}


def folder_tree(folder):
    node = {"name": folder.GetName(),
            "clips": [c.GetName() for c in (folder.GetClipList() or [])],
            "folders": []}
    for sub in folder.GetSubFolderList() or []:
        node["folders"].append(folder_tree(sub))
    return node


def find_folder(folder, name):
    if folder.GetName() == name:
        return folder
    for sub in folder.GetSubFolderList() or []:
        found = find_folder(sub, name)
        if found is not None:
            return found
    return None


def find_clip(folder, name):
    for clip in folder.GetClipList() or []:
        if clip.GetName() == name:
            return clip
    for sub in folder.GetSubFolderList() or []:
        found = find_clip(sub, name)
        if found is not None:
            return found
    return None


def find_timeline(project, name):
    for i in range(1, (project.GetTimelineCount() or 0) + 1):
        tl = project.GetTimelineByIndex(i)
        if tl and tl.GetName() == name:
            return tl
    return None


if __name__ == "__main__":
    main()
