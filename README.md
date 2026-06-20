# StoryLocationsReset

A server-side Vintage Story mod that helps admins reset generated story locations.

`StoryLocationsReset` is a server-side helper mod for Vintage Story 1.22.x.

It scans Vintage Story's registered story structure locations for known story structure codes and can regenerate the chunk range around those locations. The goal is to let multiplayer servers refresh story content so more than one group of players can experience it over time.

The mod is intentionally cautious:

- It only runs on the server
- Clients do not need to install it
- It uses Vintage Story's own worldgen commands for regeneration
- It queues regeneration operations, waiting for vanilla's `wgenregendone` event before starting the next one
- It can skip resets when players are near the target location
- It can optionally evacuate players standing inside the exact reset chunk range to their spawn before regenerating
- It can also evacuate reconnecting players if their saved position is inside a recently reset story area
- Each location has configurable `enabled` and `maxInstancesToReset` values
- Automatic reset on server start is configurable and disabled by default

This mod should be treated as an automation helper, not as a guaranteed full reset of every possible internal story or quest state.

## Usage

1. Install the mod on the server.
2. Start the server once so `ModConfig/storylocationsreset.json` is generated.
3. Review the configuration carefully before enabling automatic resets.
4. Use `storylocationsreset.template.jsonc` as a commented reference for the available settings and safety notes.
5. Use `/storyreset scan` to detect configured story structures.
6. Use `/storyreset list` to list vanilla-registered locations.
7. Use `/storyreset reset resonancearchive` to reset one story location code.
8. Use `/storyreset reset all` to reset all enabled configured locations.

## Commands

`/storyreset reload`

Reloads `ModConfig/storylocationsreset.json` without restarting the server.

`/storyreset scan`

Scans the registered story structure locations and refreshes the mod's in-memory list of configured story locations. This command does not reset anything.

`/storyreset list`

Scans first, then prints the configured story locations currently registered by Vintage Story.

Important: a location appearing here means Vintage Story has registered it in the save. It does not necessarily mean players have discovered it or that its chunks have already been materialized in generated map regions.

`/storyreset reset <code>`

Scans first, then resets matching generated instances for one configured story location code.

Example: `/storyreset reset resonancearchive`

The command queues reset operations. Actual regeneration continues asynchronously on the server, and the mod waits for Vintage Story to publish `wgenregendone` before starting the next operation.

`/storyreset reset all`

Scans first, then resets all configured locations where `enabled` is `true`, respecting `maxInstancesToReset` and the player safety radius.

When multiple locations are enabled, the mod does not launch multiple `/wgen regenrange` operations at once. It runs one location, waits for vanilla to finish regeneration, applies `resetQueueCooldownSeconds`, and only then continues with the next location.

## Do I Need To Run Scan Manually?

No. Manual reset commands and automatic startup resets scan internally before doing anything.

However, `/storyreset scan` and `/storyreset list` are strongly recommended as safety checks:

- Before the first manual reset on a world
- Before enabling `runOnServerStart`
- Before enabling additional location codes
- Before setting `maxInstancesToReset` above `1`
- Always before touching `treasurehunter`

For automatic startup resets, you do not need to run `/storyreset scan` manually on every restart. The mod scans at runtime before executing the reset.

Since `1.0.2`, manual reset commands use the same safe flow as automatic startup reset. With `debugAllowResetForUngeneratedLocations` set to `false`, the mod requires the location to be present in generated map regions and at least one chunk column in the target area to exist. This prevents the mod from forcing locations into existence when vanilla knows about them but their terrain has not been materialized yet.

## Player Evacuation Behavior

By default, the mod skips a reset if players are near or inside the reset area.

If you want resets to happen even when players are inside the exact chunk area to regenerate, configure:

```json
{
  "skipIfPlayersNearby": false,
  "evacuatePlayersInsideResetAreaToSpawn": true
}
```

When this mode is enabled:

- Players inside the exact reset chunk range are moved before regeneration starts.
- The normal destination is the player's own resolved spawn.
- If that resolved spawn is also inside the reset area, the player's personal spawn is cleared.
- After clearing an unsafe personal spawn, the player is moved to the world spawn instead.

This behavior is intentional anti-abuse protection. It prevents players from blocking story location resets by camping inside the area or placing their personal spawn inside it.

Server owners should disclose this rule clearly to players before enabling automatic resets.

Join-time evacuation:

- When `evacuatePlayersOnJoinFromRecentlyResetAreas` is `true`, the mod remembers recently reset chunk ranges.
- If a player reconnects inside one of those ranges, they are moved to spawn using the same safe-spawn rules.
- Records are kept for `recentResetAreaRetentionHours` and pruned automatically.
- This protects players who logged out inside a story location before a server restart reset happened.

## Safety Notes

- Back up your world before using this mod.
- The reset operation regenerates chunk ranges.
- Listed locations can be vanilla-registered locations, not necessarily player-discovered areas.
- For real worlds, keep `debugAllowResetForUngeneratedLocations` false.
- Enable `debugAllowResetForUngeneratedLocations` only for tests: it can force generation of registered locations even when their chunks do not exist yet. With the normal `false` value, the mod checks that the location is present in generated map regions and that at least one chunk column in the target area already exists before regenerating.
- Players near a configured location can block the reset if `skipIfPlayersNearby` is enabled.
- The regenerated chunk range is calculated from the generated structure's real bounding box.
- `evacuatePlayersInsideResetAreaToSpawn` is disabled by default. Enable it only if you prefer moving players instead of skipping a reset when they are inside the exact regenerated chunk area.
- Players are normally moved to their own spawn. If their resolved spawn is inside the reset area, their personal spawn is cleared and they are moved to world spawn instead.
- `treasurehunter` can exist many times in a world, so it is disabled and capped to `maxInstancesToReset: 0` by default.
- Increasing `maxInstancesToReset` above `1` can trigger multiple `/wgen regenrange` operations in one command or server start. Use high values only after checking `/storyreset list`.
- Since `1.0.1`, those multiple operations are queued to avoid overlapping regeneration work in Vintage Story's chunk system.
- Be especially careful with `treasurehunter`: enabling it without a strict instance limit can regenerate many discovered locations.

## Compatibility

- Vintage Story `1.22.x`

## Changelog 1.0.2

- Unified `/storyreset reset <code|all>` with the same safe flow used by `runOnServerStart`
- `runOnServerStart: false` can now keep automatic restart resets disabled while still allowing admins to run the same flow manually when desired
- Added `debugAllowResetForUngeneratedLocations` as the correct debug flag name for any reset, not only startup
- Kept compatibility with the old `debugAllowServerStartForUngeneratedLocations` key
- With `debugAllowResetForUngeneratedLocations: false`, manual and automatic resets require the location to be present in generated map regions and at least one chunk column to exist before regeneration
- Updated documentation to explain the safe manual flow and debug mode

## Changelog 1.0.1

- Fixed story location discovery for Vintage Story 1.22.x by reading the registered vanilla story structure locations first
- Kept generated-structure scanning as a fallback
- Added startup safety to avoid forcing completely unmaterialized areas unless debug mode is enabled
- Added a regeneration queue: the mod waits for `wgenregendone` before launching the next `/wgen regenrange`
- Added `resetQueueCooldownSeconds` and `regenCompletionTimeoutSeconds`
- Manual commands can still use registered vanilla story locations
- Updated documentation wording to distinguish vanilla-registered locations from player-discovered/materialized map locations

## Changelog 1.0.0

- Initial server-side prototype
- Added configurable story location codes
- Added per-location `enabled` and `maxInstancesToReset` settings
- Capped `treasurehunter` to zero resets by default because it can have many instances
- Added clear safety warning for multi-instance resets
- Added commented configuration template
- Added scan, list, reload and reset commands
- Added optional reset on server start
- Added player proximity safety check
- Added optional evacuation to spawn for players inside the exact reset area
- Clears personal spawn points inside the reset area and falls back to world spawn
- Added join-time evacuation from recently reset areas
- Added small persistent reset-area state with automatic retention pruning
- Changed reset range calculation to use the generated structure bounding box directly
- Uses `/wgen story removeschematiccount` and `/wgen regenrange` internally
