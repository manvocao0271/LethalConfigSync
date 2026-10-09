# LethalConfigSync [BETA]

Players who join your lobby get your LethalConfig settings applied automatically. Everyone needs LethalConfigSync and LethalConfig installed.

## How it works

1. Set your mods up the way you want everyone to play them.
2. Put what to sync in `SyncedEntries`, in `BepInEx/config/bobabulkerENTERPRISE.LethalConfigSync.cfg` or in LethalConfig. Separate entries with `;` and use a mod GUID or `GUID|section|setting`.

For example: `com.seeya.cameraoverhaul; LethalHUD; MrHat.EladsLethalHUD; ShaosilGaming.GeneralImprovements|Mechanics|AddHealthRechargeStation`

Players are synced when they join. If you change settings after that, check `SyncNow` in LethalConfig and press Apply to send your current settings to everyone already in the lobby. It unchecks itself when done.

To make everyone use your in-game head bobbing setting (the game's own, not a mod's), check `SyncHeadBobbing`. It is applied when players join and on `SyncNow`. It isn't saved to their game, and their own setting comes back when they leave.

## Good to know

- `SyncedEntries` is empty by default, so nothing is synced until the host fills it in.
- Only the host's list counts. Players see it in the `Host` section (display only). A player's own `SyncedEntries` is never changed by a host.
- If the host has this mod, players without it (or on a different version) are refused when they try to join.
- If the host does not have this mod, it does nothing for you.
- Synced values **overwrite the player's own settings** and stay saved after they leave (restoring is planned).
- LethalConfig's and this mod's own settings are never synced.

## Minor details

- Players trust the host: whatever the host picks is applied to their settings. Only join lobbies you trust.
- Syncing relies on LethalConfig's internals. A LethalConfig update may break it until this mod is updated (the log will warn you).
- It also hooks into the game's networking, so a game update may break it until this mod is updated.
- Some mods only read their settings at startup, so a synced value may not take effect until the player restarts the game.
- Mods a player doesn't have are skipped, and mods that register with LethalConfig very late may be missed.
- A typo in `SyncedEntries` syncs nothing (check the log), and `*` matches everything.

## Planned

- Put players' own settings back when they leave the lobby.
- Better compatibility with mods that have their own sync (CSync).