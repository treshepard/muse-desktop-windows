-- Muse - 6 Copy Hero Grade
-- The hero-grade pattern: YOU grade one A-Roll clip by hand until it is right.
-- This script copies that clip's grade to every other clip on the current timeline
-- whose source bin matches APPLY_WITHIN (so trailer/film clips in other bins are untouched).
-- HERO_CLIP is a piece of the hero clip's timeline name.
--
-- Note: Resolve scripting cannot build a grade from nothing (no curves/qualifiers by script) -
-- copying your hand-made grade is exactly what this is for.

-- ===== EDIT THESE =====
local HERO_CLIP = "My Hero Clip"      -- part of the hero clip's name on the timeline
local APPLY_WITHIN = "A-Roll"        -- only clips whose media pool bin is this one; set "" for all clips
-- ======================

local resolve = Resolve()
if not resolve then print("[Muse Grade] Could not talk to Resolve.") return end
local pm = resolve:GetProjectManager()
local proj = pm:GetCurrentProject()
if not proj then print("[Muse Grade] No project is open.") return end
local tl = proj:GetCurrentTimeline()
if not tl then print("[Muse Grade] No current timeline.") return end

local function binOf(item)
    local mpi = item:GetMediaPoolItem()
    if not mpi then return "" end
    local folder = mpi:GetMediaPoolFolder()
    if folder then return folder:GetName() or "" end
    return ""
end

local hero, targets = nil, {}
for ti = 1, tl:GetTrackCount("video") do
    for _, item in ipairs(tl:GetItemListInTrack("video", ti)) do
        if string.find(string.lower(item:GetName()), string.lower(HERO_CLIP), 1, true) then
            hero = item
        elseif APPLY_WITHIN == "" or binOf(item) == APPLY_WITHIN then
            targets[#targets + 1] = item
        end
    end
end

if not hero then
    print("[Muse Grade] Hero clip not found on the timeline: " .. HERO_CLIP)
    return
end
if #targets == 0 then
    print("[Muse Grade] No target clips matched bin '" .. APPLY_WITHIN .. "'.")
    return
end

if hero:CopyGradesTo(targets) then
    pm:SaveProject()
    print(string.format("[Muse Grade] Grade from '%s' copied to %d clip(s). Project saved.", hero:GetName(), #targets))
else
    print("[Muse Grade] CopyGradesTo failed - make sure the hero clip actually has a grade on it.")
end
