-- Muse - 5 Apply LUT
-- Applies a .cube LUT to every video clip on the CURRENT timeline (node 1 of each clip's grade).
-- Muse edits LUT_PATH for the look you picked, or export a LUT from your hero clip
-- (right-click the clip thumbnail in the Gallery/Color page > Generate LUT) and tell it where it is.
-- Set ONLY_TRACK to a track number (like 1) to limit it, or leave nil for all video tracks.

-- ===== EDIT THIS =====
local LUT_PATH = [[C:\Users\YourName\Documents\MyLook.cube]]
local ONLY_TRACK = nil
-- =====================

local resolve = Resolve()
if not resolve then print("[Muse LUT] Could not talk to Resolve.") return end
local pm = resolve:GetProjectManager()
local proj = pm:GetCurrentProject()
if not proj then print("[Muse LUT] No project is open.") return end
local tl = proj:GetCurrentTimeline()
if not tl then print("[Muse LUT] No current timeline.") return end

local done, failed = 0, 0
local trackCount = tl:GetTrackCount("video")
for ti = 1, trackCount do
    if (not ONLY_TRACK) or ONLY_TRACK == ti then
        for _, item in ipairs(tl:GetItemListInTrack("video", ti)) do
            if item:SetLUT(1, LUT_PATH) then done = done + 1 else failed = failed + 1 end
        end
    end
end
pm:SaveProject()
print(string.format("[Muse LUT] LUT applied to %d clips (%d failed) on '%s'. If a clip shows no change, check the LUT path exists.", done, failed, tl:GetName()))
