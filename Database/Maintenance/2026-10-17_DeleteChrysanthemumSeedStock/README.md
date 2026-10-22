# Delete CHRYSANTHEMUM seed stock (2026-10-17)

The owner asked to remove every Seed Stock lot of a **CHRYSANTHEMUM** variety. The script deletes those lots and their Seed Stock ledger rows.

The varieties themselves are **kept**, because Mother Plants, cuttings and bookings still use them. After the run they just no longer show any seed stock.

`01_DeleteChrysanthemumSeedStock.sql` finds the plant type by **name**, so the same file works on local and on live. If a lot has already been used (sowing, bed sowing, seed issue, in transit, or any movement other than its StockIn), the script stops and writes nothing.

## Applied
- **Local** (`LAPTOP-R87BKQGH\MSSQLSERVER02`): 2026-10-17. 2 lots deleted, both Lot2026 "old sowing": Smola White 2,310 seeds (SeedStock 220) and Vido Red 1,820 seeds (SeedStock 228). 2 StockIn ledger rows were removed with them.
- **Live**: not yet.

## Live steps (run on the VPS)
1. Back up `PlantsIMS2_Test`.
2. Run `SELECT @@SERVERNAME;` and note the name.
3. Preview. This is read-only and writes nothing:
   `sqlcmd -S <server> -d PlantsIMS2_Test -E -C -I -b -W -s"|" -v MODE=PREVIEW TARGET_SERVER="<@@SERVERNAME>" -i 01_DeleteChrysanthemumSeedStock.sql`
   The output lists every CHRYSANTHEMUM lot. Each one must show `DELETE`; a lot showing `ERROR` stops the run.
4. If the list is right, run the same command with `MODE=COMMIT`.

**Order on live:** `Database/DataEntry/2026-10-15_OldSowingSeedStock/01_OldSowingSeedStock.sql` adds Smola White and Vido Red seed lots. If live gets that script, run **this** script after it. Otherwise those two lots would come back.
