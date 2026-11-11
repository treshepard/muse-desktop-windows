-- Muse - 2 Import Footage Folder
-- Imports every media file from a folder on disk (including subfolders) into a chosen bin.
-- Your Muse can edit FOLDER and TARGET_BIN below for each video — just tell it the folder.
-- Files already in the media pool with the same name are still imported by Resolve as new clips if run twice, so run once per folder.

-- ===== EDIT THESE TWO LINES =====
local FOLDER    = [[C:\Users\YourName\Videos\My Project Footage]]
local TARGET_BIN = "A-Roll"
-- ================================

local MEDIA_EXT = {
    mp4=true, mov=true, mkv=true, avi=true, m4v=true, webm=true,
    wav=true, mp3=true, m4a=true, aac=true, flac=true,
    jpg=true, jpeg=true, png=true, tga=true, tif=true, tiff=true,
}

local resolve = Resolve()
if not resolve then print("[Muse Import] Could not talk to Resolve.") return end
local pm = resolve:GetProjectManager()
local proj = pm:GetCurrentProject()
if not proj then print("[Muse Import] No project is open. Open or create a project first.") return end

local mp = proj:GetMediaPool()
local ms = resolve:GetMediaStorage()

local function findBin(parent, name)
    for _, f in ipairs(parent:GetSubFolderList()) do
        if f:GetName() == name then return f end
    end
    return nil
end

local bin = findBin(mp:GetRootFolder(), TARGET_BIN)
if not bin then
    print("[Muse Import] Bin not found: " .. TARGET_BIN .. " (run Muse - 1 Project Setup first)")
    return
end

local files = {}
local function collect(folder)
    local list = ms:GetFileList(folder)
    if list then
        for _, path in ipairs(list) do
            local ext = string.lower(string.match(path, "%.([%w]+)$") or "")
            if MEDIA_EXT[ext] then files[#files + 1] = path end
        end
    end
    local subs = ms:GetSubFolderList(folder)
    if subs then
        for _, sub in ipairs(subs) do collect(sub) end
    end
end
collect(FOLDER)

if #files == 0 then
    print("[Muse Import] No media files found in: " .. FOLDER)
    return
end

mp:SetCurrentFolder(bin)
local clips = mp:ImportMedia(files)
local n = 0
if clips then n = #clips end
pm:SaveProject()
print(string.format("[Muse Import] Imported %d of %d files into bin '%s'. Project saved.", n, #files, TARGET_BIN))
