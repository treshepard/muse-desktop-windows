-- Muse - 1 Project Setup
-- Creates the standard bin structure in the CURRENT project.
-- Safe to run more than once: bins that already exist are kept.
-- Edit the BINS list below if a video needs different bins.
-- Run from: Workspace > Scripts > "Muse - 1 Project Setup". Open Workspace > Console to see the report.

local BINS = {
    "A-Roll",
    "B-Roll",
    "Reference",
    "Audio",
    "Graphics",
    "Exports",
}

local resolve = Resolve()
if not resolve then print("[Muse Setup] Could not talk to Resolve.") return end
local pm = resolve:GetProjectManager()
local proj = pm:GetCurrentProject()
if not proj then print("[Muse Setup] No project is open. Open or create a project first.") return end

local mp = proj:GetMediaPool()
local root = mp:GetRootFolder()

local function findBin(parent, name)
    for _, f in ipairs(parent:GetSubFolderList()) do
        if f:GetName() == name then return f end
    end
    return nil
end

local made, kept = 0, 0
for _, name in ipairs(BINS) do
    if findBin(root, name) then
        kept = kept + 1
        print("[Muse Setup] exists: " .. name)
    else
        if root:AddSubFolder(name) then
            made = made + 1
            print("[Muse Setup] created: " .. name)
        else
            print("[Muse Setup] FAILED to create: " .. name)
        end
    end
end
pm:SaveProject()
print(string.format("[Muse Setup] Done. %d created, %d already there. Project saved.", made, kept))
