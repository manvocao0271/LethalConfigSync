# LethalConfigSync [BETA]

Players who join your lobby get your LethalConfig settings applied automatically. Everyone needs LethalConfigSync and LethalConfig installed.

## How it works

1. Set your mods up the way you want everyone to play them.
2. Put what to sync in `SyncedEntries`, in `BepInEx/config/bobabulkerENTERPRISE.LethalConfigSync.cfg` or in LethalConfig. Separate entries with `;` and use a mod GUID or `GUID|section|setting`.

For example: `com.seeya.cameraoverhaul; LethalHUD; MrHat.EladsLethalHUD; ShaosilGaming.GeneralImprovements|Mechanics|AddHealthRechargeStation`

## Good to know

- `SyncedEntries` is empty by default, so nothing is synced until the host fills it in.
- Only the host's list counts. Players see it read-only while in your lobby.
- Players without this mod (or on a different version) are refused when they try to join.
- Synced values **overwrite the player's own settings** for those entries and are saved to their config files.
- LethalConfig's and this mod's own settings are never synced.

## Install

Install through a mod manager (Thunderstore / r2modman). Requires BepInEx and LethalConfig.