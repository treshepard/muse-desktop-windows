-- Muse - 7 Project Report
-- Writes a plain-text report of the current project (bins, clip counts, timelines, markers)
-- to Documents so Muse can read it back over the bridge and tell you what state the project is in.

local REPORT_PATH = [[C:\Users\YourName\Documents\Muse Resolve Report.txt]]

local resolve = Resolve()
if not resolve then print("[Muse Report] Could not talk to Resolve.") return end
local pm = resolve:GetProjectManager()
local proj = pm:GetCurrentProject()
if not proj then print("[Muse Report] No project is open.") return end
local mp = proj:GetMediaPool()

local lines = {}
local function add(s) lines[#lines + 1] = s end

add("Project: " .. proj:GetName())
add("Timeline frame rate: " .. tostring(proj:GetSetting("timelineFrameRate")))
add("Resolution: " .. tostring(proj:GetSetting("timelineResolutionWidth")) .. "x" .. tostring(proj:GetSetting("timelineResolutionHeight")))
add("")
add("== Media Pool ==")
local totalClips = 0
local function walk(folder, depth)
    local clips = folder:GetClipList()
    totalClips = totalClips + #clips
    add(string.rep("  ", depth) .. folder:GetName() .. " (" .. #clips .. " clips)")
    for _, c in ipairs(clips) do
        add(string.rep("  ", depth) .. "  - " .. c:GetName())
    end
    for _, f in ipairs(folder:GetSubFolderList()) do walk(f, depth + 1) end
end
walk(mp:GetRootFolder(), 0)
add("Total clips: " .. totalClips)
add("")
add("== Timelines ==")
for _, t in ipairs(proj:GetTimelineList()) do
    add("- " .. t:GetName() .. "  (" .. t:GetStartTimecode() .. " -> " .. t:GetEndTimecode() .. ")")
    local markers = t:GetMarkers()
    if markers then
        local frames = {}
        for fr, _ in pairs(markers) do frames[#frames + 1] = fr end
        table.sort(frames)
        for _, fr in ipairs(frames) do
            local mk = markers[fr]
            add("    marker @frame " .. fr .. ": " .. (mk.name or "") .. " - " .. (mk.note or ""))
        end
    end
end

local f = io.open(REPORT_PATH, "w")
if not f then print("[Muse Report] Could not write: " .. REPORT_PATH) return end
f:write(table.concat(lines, "\n"))
f:close()
print("[Muse Report] Report written to: " .. REPORT_PATH)
