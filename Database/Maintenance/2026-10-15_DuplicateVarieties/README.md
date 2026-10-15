# Duplicate varieties cleanup (2026-10-15)

13 duplicate varieties are retired (`IsActive = 0`, renamed `DUP-<Id>-<name>`). Their history moves to the kept variety, which is always the **oldest Id**. Nothing is deleted.

The cleanup also does three other things:
- trims every variety name
- sets all Lantana varieties to 25 growing days
- adds a unique index on active variety names

Every script works the same way:
- It does not name a database: select the target first (SSMS database dropdown, or `sqlcmd -d`).
- It matches varieties by **name**, never by Id.
- It is safe to run twice.
- Each data change runs in one transaction that rolls back completely on error.

| File | What it does |
|---|---|
| `../../Migrations/2026-10-15_PlantSpeciesIsActive.sql` (+ `_Rollback`) | Adds `PlantSpecies.IsActive BIT NOT NULL DEFAULT 1` |
| `Fix_Duplicate_Varieties.sql` | `@Commit = 0` preview (read-only), `@Commit = 1` save |
| `../../Migrations/2026-10-15_PlantSpeciesUniqueActiveName.sql` (+ `_Rollback`) | Unique index `UX_PlantSpecies_Name_Active` on `Name WHERE IsActive = 1` |
| `Verify_After.sql` | Read-only PASS/FAIL checks (24 on local) |

## Applied on LOCAL (PlantsIMS2_Test), 2026-10-15
- **Backups** (SQL Server `Backup` folder):
  - before: `PlantsIMS2_Test_PreDupVarieties_20261015.bak` (verified with checksum)
  - after: `PlantsIMS2_Test_PostDupVarieties_20261015.bak`
- **Changes:**
  - 13 duplicates retired
  - 13 records moved (7 seed lots, 6 Mother Plants)
  - 10 kept varieties renamed
  - 47 names trimmed
  - Lantana Tangerine 32 → 25 days
- **Totals:** row counts, seed stock (1,076,098) and ledger are identical before and after. Only the growing-days sum changed (−7, Lantana Tangerine).
- **Verify_After.sql:** 24 PASS / 0 FAIL.

## LIVE checklist (owner runs it, in this order)
1. **Full backup** of the live database, then check it:
   `RESTORE VERIFYONLY FROM DISK = N'<backup file>' WITH CHECKSUM;` -- expect "The backup set on file 1 is valid."
2. **`../../Migrations/2026-10-15_PlantSpeciesIsActive.sql`** -- expect `Added PlantSpecies.IsActive` (a second run is a no-op).
3. **`Fix_Duplicate_Varieties.sql`** with `@Commit = 0` -> **send the grids** (Plan, Records to move, Name changes, Lantana
   days, Blockers, Summary); the Blocker grid must say `(none)`. After approval set `@Commit = 1` and run again -> expect
   `=== SAVED ...` and the before/after grids (any error -> `=== ROLLED BACK -- nothing written`).
4. **Old-sowing seed stock** (only if it is not on live yet): `../../DataEntry/2026-10-15_OldSowingSeedStock/01_OldSowingSeedStock.sql`
   with sqlcmd (`-v MODE=PREVIEW TARGET_SERVER="<@@SERVERNAME>"`), check the plan, then `MODE=COMMIT`.
   It looks varieties up by name and creates one only when that name does not exist. After step 3, every Latania /
   Callistephus row on live already carries its Lantana / Aster name, so the seed stock goes onto those and no second
   Lantana / Aster row is created (a name live never had, e.g. Lantana Tangerine, is created once, with 25 days).
5. **`../../Migrations/2026-10-15_PlantSpeciesUniqueActiveName.sql`** -- refuses (and lists the names) if two active
   varieties still share a name.
6. **Deploy the new code** (only after step 2 -- the code reads `PlantSpecies.IsActive`; the old code keeps working after step 2).
7. **`Verify_After.sql`** -> every row `PASS` (Failed = 0); then check the variety dropdowns in the app (Seed Stock /
   Mother Plant / Booking show each kept name once, no `DUP-` names) and Admin > Plant Types & Species >
   "Show inactive varieties (N)" (merged rows say "Merged into ...").

## sqlcmd -I (important)
The unique index is a *filtered* index, so **every write to `dbo.PlantSpecies` needs `QUOTED_IDENTIFIER ON`**.
The app (SqlClient), SSMS and all these scripts set it. When running any script with **sqlcmd, always pass `-I`**
(e.g. `sqlcmd -S <server> -d <db> -E -C -I -b -i <file>`); a hand-run sqlcmd **without `-I`** that inserts or updates
varieties fails with error 1934 ("SET options have incorrect settings: 'QUOTED_IDENTIFIER'").
Also: with sqlcmd, pass `-i` a **relative** path -- a path like `C:/Users/...` is misread as the `/U` switch
("-E and -U/-P options are mutually exclusive").

## Variety master after deploy
- No physical delete: **Deactivate** (IsActive = 0) replaces Delete, refused with a message while the variety still has
  stock with something in it, an active Mother Plant, a pending booking, an active booking allocation, or an open bed
  sowing / seed sowing / propagation batch / pot batch.
- **Reactivate** only for varieties deactivated normally. Merged duplicates ("DUP-...") show "Merged into <kept name>",
  cannot be edited or reactivated, and the "DUP-" prefix is reserved (Add / Edit refuse it).
