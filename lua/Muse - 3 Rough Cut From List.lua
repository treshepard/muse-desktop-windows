-- Muse - 3 Rough Cut From List
-- Builds a new timeline by laying clips end to end in the exact order of the SHOTS list.
-- Each entry: { clip = "part of the clip name", inSec = start seconds in the source clip (optional, default 0),
--               outSec = end seconds (optional, default = whole clip) }
-- Muse fills this list from the filming kit / shot order for each video. This is an assembly tool:
-- it lays the clips you name, in the order you name them. It does not pick takes.
--
-- The example below is a generic section order with PLACEHOLDER clip names — your Muse
-- replaces them with your real clip names before you run it.

-- ===== EDIT THESE =====
local TIMELINE_NAME = "My Rough Cut"
local SHOTS = {
    { clip = "Intro" },
    { clip = "Section 1" },
    { clip = "Section 2" },
    { clip = "Section 3" },
    { clip = "Outro" },
}
-- ======================

local resolve = Resolve()
if not resolve then print("[Muse RoughCut] Could not talk to Resolve.") return end
local pm = resolve:GetProjectManager()
local proj = pm:GetCurrentProject()
if not proj then print("[Muse RoughCut] No project is open.") return end
local mp = proj:GetMediaPool()

-- index every clip in the pool by name
local byName = {}
local function walk(folder)
    for _, c in ipairs(folder:GetClipList()) do
        byName[string.lower(c:GetName())] = c
    end
    for _, f in ipairs(folder:GetSubFolderList()) do walk(f) end
end
walk(mp:GetRootFolder())

local function findClip(needle)
    needle = string.lower(needle)
    if byName[needle] then return byName[needle] end
    for name, c in pairs(byName) do
        if string.find(name, needle, 1, true) then return c end
    end
    return nil
end

-- refuse to overwrite an existing timeline of the same name
for _, t in ipairs(proj:GetTimelineList()) do
    if t:GetName() == TIMELINE_NAME then
        print("[Muse RoughCut] A timeline named '" .. TIMELINE_NAME .. "' already exists. Rename or delete it, or change TIMELINE_NAME.")
        return
    end
end

local tl = mp:CreateEmptyTimeline(TIMELINE_NAME)
if not tl then print("[Muse RoughCut] Could not create timeline.") return end
proj:SetCurrentTimeline(tl)

local placed, missing = 0, {}
for _, shot in ipairs(SHOTS) do
    local clip = findClip(shot.clip)
    if not clip then
        missing[#missing + 1] = shot.clip
        print("[Muse RoughCut] NOT FOUND in media pool: " .. shot.clip)
    else
        local srcFps = tonumber(clip:GetClipProperty("FPS")) or tonumber(proj:GetSetting("timelineFrameRate")) or 24
        local totalFrames = tonumber(clip:GetClipProperty("Frames")) or 0
        local startFrame = 0
        local endFrame = totalFrames - 1
        if shot.inSec then startFrame = math.floor(shot.inSec * srcFps) end
        if shot.outSec then endFrame = math.floor(shot.outSec * srcFps) - 1 end
        if endFrame > totalFrames - 1 then endFrame = totalFrames - 1 end
        local ok = mp:AppendToTimeline({ { mediaPoolItem = clip, startFrame = startFrame, endFrame = endFrame } })
        if ok then placed = placed + 1 else print("[Muse RoughCut] FAILED to append: " .. shot.clip) end
    end
end

pm:SaveProject()
print(string.format("[Muse RoughCut] '%s' built: %d clips placed, %d missing. Timeline length: %s",
    TIMELINE_NAME, placed, #missing, tl:GetEndTimecode()))
if #missing > 0 then print("[Muse RoughCut] Missing: " .. table.concat(missing, " | ")) end
