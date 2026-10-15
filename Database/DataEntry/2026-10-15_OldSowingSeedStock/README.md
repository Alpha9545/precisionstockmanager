# Old sowing seed stock (2026-10-15)

64 seed rows from the owner, 1,026,098 seeds, added to Seed Stock in one go. Each row is entered exactly as **Add Seed Stock** would enter it.

| Field | Value |
|---|---|
| Source | In Office |
| Batch No | Lot2026 |
| Unit | Seeds |
| Received On | time the script runs |
| Notes | old sowing (Salvia Blue: "old sowing (cutting - not seed)") |
| Plant type | Smola White, Vido Red = CHRYSANTHEMUM; every other row = SEASONAL VARITIES |

`01_OldSowingSeedStock.sql` looks everything up by **name**, so the same file works on local and on live. Varieties that are missing get created. A row that was already added gets skipped.

## Applied
- **Local** (`LAPTOP-R87BKQGH\MSSQLSERVER02`): 2026-10-15. 53 new varieties (Ids 3921-3973), 64 Lot2026 lots (SeedStock 165-228).
- **Live**: not yet.

## Live steps (run on the VPS)
1. Back up `PlantsIMS2_Test`.
2. Run `SELECT @@SERVERNAME;` and note the name.
3. Preview. This is read-only and writes nothing:
   `sqlcmd -S <server> -d PlantsIMS2_Test -E -C -I -b -W -s"|" -v MODE=PREVIEW TARGET_SERVER="<@@SERVERNAME>" -i 01_OldSowingSeedStock.sql`
   Check the plan. The script stops instead of guessing when:
   - a variety is found under a different plant type
   - a name matches twice
   - the Seed Source "In Office" is missing
   - there is not exactly one active Main Office Area
4. If the plan looks right, run the same command with `MODE=COMMIT`. Everything runs in one transaction and is checked before it commits.
