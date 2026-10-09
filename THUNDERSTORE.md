# LethalConfigSync [BETA]

Players who join your lobby get your LethalConfig settings applied automatically. Everyone needs LethalConfigSync and LethalConfig installed.

1. Set your mods up the way you want everyone to play them.
2. Put what to sync in `SyncedEntries`, in `BepInEx/config/bobabulkerENTERPRISE.LethalConfigSync.cfg` or in LethalConfig. Separate entries with `;` and use a mod GUID or `GUID|section|setting`.

For example: `com.seeya.cameraoverhaul; LethalHUD; MrHat.EladsLethalHUD; ShaosilGaming.GeneralImprovements|Mechanics|AddHealthRechargeStation`