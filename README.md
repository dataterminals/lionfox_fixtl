# lionfox Fix TL

Gets players through translocators on LionFox (Vintage Story 1.22.6). modid `lionfoxfixtl`, server only: players
don't need it.

Stand in the translocator that won't take you and type `/fixtl`. It checks you and that translocator, fixes what it
safely can, says in plain words what was wrong, and sends you through when the translocator has somewhere real to
send you. It also fixes, by itself, the game bug behind most "it plays the warm-up but never sends me" reports.

| Command | Who | What |
|---|---|---|
| `/fixtl` | everyone | check you and the translocator you're in, fix, explain, send you through |
| `/fixtl check` | everyone | the same checks, changing nothing |
| `/fixtl go` | everyone | clear your own stuck state and go straight where this translocator leads |
| `/fixtl unstick` | everyone | only your own stuck state, no translocator needed |
| `/fixtl diag` | everyone | raw state dump, also written to the server log |
| `/fixtl status` | everyone | every online player's teleport state |
| `/fixtl player NAME` | admin | `/fixtl` for someone else; they see the report too |
| `/fixtl all` | admin | clear stuck state on everyone online |
| `/fixtl keys` | admin | Translocator Relocator keys: alone, unkeyed, shared by 3+, look-alikes |
| `/fixtl reroll` | admin | the stone translocator you're in forgets its partner and finds a new one |

## What leaves a player stuck, and what /fixtl does

Found by decompiling 1.22.6 and Translocator Relocator Fork 1.0.5 (2026-11-12), then matched to LionFox's incidents.

**You**
| State | What you see | /fixtl |
|---|---|---|
| The server's noclip left on after `/gm` (game bug, below) | warm-up sound and shake, never sent | fixes it; the mod also prevents it |
| Spectator or noclip | nothing happens | tells you to switch back |
| Marked mid-teleport (`Entity.Teleporting`), its chunk load never answered | warm-up, never sent | clears it |
| Every teleport ignored (`ignoreTeleportCall`, left by a teleport that failed half-way) | sent nowhere, though the log says it teleported you | clears it |
| A translocator warming up an old copy of you (from before you rejoined) | that one never sends you | drops the old entry |

**Stone translocators** (found in ruins)
| State | /fixtl |
|---|---|
| Broken, or not all temporal gears in | says what it still needs |
| Searching for its partner ("Warping spacetime") | says to give it a minute; "Regen chunk but broken translocator is gone... Searching again" in the log is that search retrying, and normal |
| Its search never answered | waits two minutes from when it first saw it, then restarts the search and tells you |
| "Linked" with no destination (errors every tick in the log) | sets it searching again |
| Its partner is gone (picked up or broken) | won't drop you at the empty spot; an admin can `/fixtl reroll`, or pick it up and key it |

**Keyed translocators** (Translocator Relocator: picked up and placed, paired by "Edit Key")
| State | /fixtl |
|---|---|
| No key | says how to set one |
| No other translocator has the same key | says so, and names look-alike keys (capitals and spaces count) |
| The mod's list doesn't match the key shown on the block (it never links, and typing the same key again does nothing) | puts it back in the list under the key it shows |
| The list still has a translocator where nothing stands now | removes it, so nobody's sent to an empty spot |
| Three or more share a key | says it picks one at random each time |

## The game bug

`/gm` changes a player's game mode and noclip in their world data, but not the server's own copy of their controls
(`Entity.Controls.NoClip`). Only the game's mode-change packet syncs that, and the game sends one when you change your
view distance, toggle fly or cycle fly modes, use `.fly` or `.noclip`, or toggle meta blocks. So: go spectator (or
creative straight after spectator, which keeps noclip), do any of those, go back to survival, and the server still
thinks you walk through walls. It then skips your block collisions, so no translocator learns you're standing in it,
while your own game still plays the warm-up. Properly paired translocators "break" for you and only you, until you
rejoin (or happen to send that packet again in survival, by changing your view distance, say). That's Sylvia's "it always happens when I switch in and out of spectator, creative and survival a bunch".
On 2026-11-07: `/gm 3`, a view-distance change at 04:16:21 (the packet), `/gm 1`: stuck.

The mod now re-syncs it on every game-mode change and logs when vanilla had left it wrong; `/fixtl` fixes it too.
The switch is `SyncNoClipOnGameModeChange` in `ModConfig/lionfoxfixtl.json` (on). Our SpecReturn plays no part: it
only teleports you back to where you went spectator, an ordinary teleport.

## LionFox's two incidents

- **2026-10-21**: the translocator keyed "wip" was the only one in the world with that key (Translocator Relocator's
  own `/tr printAllData`, 2026-11-07), so it had no partner. `/fixtl` 1.2.0 only knew the mid-teleport flag.
- **2026-11-07 04:17 UTC**: the `/gm` noclip bug above. The ruin's stone translocator had linked fine at 04:15:11; its
  two "Regen chunk" warnings were just its search retrying.

## Building and testing

    dotnet build -c Release                                    # lionfox_fixtl-<version>.dll and build/lionfoxfixtl_<version>.zip
    dotnet build -c Release tests/TestKit/FixtlTestKit.csproj
    dotnet build -c Release tests/FakeClient/FakeClient.csproj
    python tests/boot_test.py --fresh                          # or on SylG5: lionfox_testlane's `job fixtl-boot [--modset live]`

`tests/boot_test.py` boots a throwaway server with the mod, Translocator Relocator and the test kit, joins it as FixFox
with a fake client over TCP, and runs a lane per stuck state through the server's own collision and translocator code.
The fake client can send the real mode-change packet, so the `/gm` bug is reproduced, not imitated. `--specreturn ZIP`
loads SpecReturn too (lionfox_testlane job `fixtl-boot-specreturn`). The third-party zips live in `tests/deps`, not
committed: copy `TranslocatorRelocatorFork_1.0.5.zip` (and `lionfoxspecreturn_1.1.1.zip`) there from the live set.
Never against LionFox.

The DLL is named after the version (`Directory.Build.props`), like our other mods. The mod reaches the game's
translocators and Translocator Relocator by name, not by reference; a startup self-check in the server log says
whether every member resolved.
