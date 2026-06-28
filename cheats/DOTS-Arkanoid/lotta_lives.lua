-- Cheat Engine Lua script: single-run AoB scan for PlayerData in Unity DOTS
--
-- Attack premise:
--   The attacker knows the struct layout from il2cppdumper / dnSpy / Mono dissector:
--     PlayerData : IComponentData {
--       int Lives;  // offset 0x0
--       int Score;  // offset 0x4
--     }
--   They exploit this knowledge by scanning for the known byte pattern
--   of {Lives=3, Score=90} immediately after the first block is broken.
--
-- Struct entropy defeats this: Lives has been relocated to a different struct,
-- so the pattern {Lives, Score} as adjacent int32s no longer exists in memory.
--
-- Usage:
--   1. Build the game (Mono or IL2CPP)
--   2. Launch, start a round, break the FIRST block (awards 90 points)
--   3. Run this script immediately (Sets Lives=3, Score=90)

local LIVES = 3
local SCORE = 90

-- Build AoB: Lives (int32 LE) followed by Score (int32 LE)
local function int32ToAoB(v)
    local b0 = string.format("%02X", v % 256)
    local b1 = string.format("%02X", math.floor(v / 256) % 256)
    local b2 = string.format("%02X", math.floor(v / 65536) % 256)
    local b3 = string.format("%02X", math.floor(v / 16777216) % 256)
    return b0 .. " " .. b1 .. " " .. b2 .. " " .. b3
end

local pattern = int32ToAoB(LIVES) .. " " .. int32ToAoB(SCORE)

print("=== PlayerData Cheat (Struct Offset Exploit) ===")
print("")
print("Known struct layout:")
print("  PlayerData { int Lives; /* +0x0 */ int Score; /* +0x4 */ }")
print("")
print("Scanning for AoB: " .. pattern)
print("  (Lives=" .. LIVES .. ", Score=" .. SCORE .. ")")
print("")

-- Scan writable, non-code memory
local results = AOBScan(pattern, "+W-C")

if results == nil then
    print("ERROR: No matches found.")
    print("Make sure you broke exactly one block (Score=90) and still have 3 lives.")
    return
end

local count = results.getCount()
print("Found " .. count .. " match(es):\n")

for i = 0, math.min(count - 1, 49) do
    local addr = tonumber(results[i], 16)
    local lives = readInteger(addr)
    local score = readInteger(addr + 4)

    -- Read surrounding 16 bytes for context
    local context_before = ""
    local context_after = ""
    for off = -8, -1 do
        local b = readBytes(addr + off, 1)
        context_before = context_before .. (b and string.format("%02X ", b) or "?? ")
    end
    for off = 8, 15 do
        local b = readBytes(addr + off, 1)
        context_after = context_after .. (b and string.format("%02X ", b) or "?? ")
    end

    print(string.format("  [%d] Address: %X", i + 1, addr))
    print(string.format("       Lives=%d (offset +0x0)  Score=%d (offset +0x4)", lives, score))
    print(string.format("       Context: %s| %s |%s", context_before, pattern, context_after))
    print("")
end

-- If only a few matches, try to set infinite lives
if count <= 10 then
    print("--- Low match count. Attempting to set Lives=99 on all candidates. ---")
    print("")
    for i = 0, count - 1 do
        local addr = tonumber(results[i], 16)
        writeInteger(addr, 99)
        print(string.format("  [%d] Wrote Lives=99 at %X", i + 1, addr))
    end
    print("")
    print("Check in-game: if lives shows 99, the cheat succeeded.")
    print("The correct address can be frozen to maintain infinite lives.")
    else
    print("Too many matches (" .. count .. "). Try after scoring more points for a unique pattern.")
    print("Or modify SCORE variable in this script to match your current score.")
end

results.destroy()
print("")
print("=== Done ===")
print("NOTE: This exploit relies on knowing PlayerData's field layout.")
print("Struct entropy relocates Lives to a different struct, breaking this pattern.")