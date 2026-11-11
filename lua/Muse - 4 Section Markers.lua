-- Muse - 4 Section Markers
-- Adds markers to the CURRENT timeline at the positions in the MARKERS list.
-- Each entry: { at = "HH:MM:SS" (timeline time) OR seconds as a number, name = "...", note = "...", color = "Blue" }
-- Prefilled with example markers. The TIMES are placeholders (00:00:00) —
-- after the rough cut exists, tell your Muse the start time of each section and it can update the list with real times.
-- Colors Resolve accepts: Blue, Cyan, Green, Yellow, Red, Pink, Purple, Fuchsia, Rose, Lavender,
-- Sky, Mint, Lemon, Sand, Cocoa, Cream.

-- ===== EDIT THE TIMES (and list if a different video) =====
local MARKERS = {
    { at = "00:00:00", color = "Blue",   name = "0 Intro",    note = "Opening hook" },
    { at = "00:00:00", color = "Green",  name = "1 Topic A",  note = "First section" },
    { at = "00:00:00", color = "Yellow", name = "2 Topic B",  note = "Second section" },
    { at = "00:00:00", color = "Purple", name = "3 Topic C",  note = "Third section" },
    { at = "00:00:00", color = "Sand",   name = "4 Outro",    note = "Wrap-up" },
}
-- ==========================================================

local resolve = Resolve()
if not resolve then print("[Muse Markers] Could not talk to Resolve.") return end
local pm = resolve:GetProjectManager()
local proj = pm:GetCurrentProject()
if not proj then print("[Muse Markers] No project is open.") return end
local tl = proj:GetCurrentTimeline()
if not tl then print("[Muse Markers] No current timeline. Click the timeline first.") return end

local fps = tonumber(proj:GetSetting("timelineFrameRate")) or 24
local startFrame = tl:GetStartFrame()

local function toFrame(at)
    if type(at) == "number" then return startFrame + math.floor(at * fps) end
    local h, m, s = string.match(at, "^(%d+):(%d+):(%d+)$")
    if not h then print("[Muse Markers] Bad time format (want HH:MM:SS): " .. tostring(at)) return nil end
    local secs = tonumber(h) * 3600 + tonumber(m) * 60 + tonumber(s)
    return startFrame + math.floor(secs * fps)
end

local added = 0
for _, mk in ipairs(MARKERS) do
    local frame = toFrame(mk.at)
    if frame then
        if tl:AddMarker(frame, mk.color or "Blue", mk.name or "", mk.note or "", 1) then
            added = added + 1
        else
            print("[Muse Markers] Failed (a marker may already sit at that frame): " .. (mk.name or ""))
        end
    end
end
pm:SaveProject()
print(string.format("[Muse Markers] %d of %d markers added to '%s'. Project saved.", added, #MARKERS, tl:GetName()))
