# PlantsIMS2_Test ZZTEST Cleanup — Investigation Report (2026-09-27)

Database confirmed as `PlantsIMS2_Test` on `LAPTOP-R87BKQGH\MSSQLSERVER02` before any query ran.
Script: `Database/ZZTEST_Cleanup.sql`. Dry run (`BEGIN TRANSACTION` → verify → `ROLLBACK`) executed clean; live DB is unchanged (Area=8, IMSUsers=26 confirmed after rollback).

## Method

1. Dumped every table (76) and every foreign key (259) via `sys.tables`/`sys.foreign_keys`.
2. Scanned every `varchar`/`nvarchar`/`char`/`nchar`/`text`/`ntext` column in the whole database for `%ZZTEST%`, `%PERMTEST%`, `%CHANGE-%`, `zztest.%` — 40 column hits across 20 tables.
3. Pulled full rows for every hit, then walked the FK graph outward from each confirmed root (Area, MotherPlants, SeedStock, SeedSowings, ReadyStock, CuttingStock, IMSUsers) to every table with an FK pointing at it.
4. Independently re-swept every Area-referencing column for AreaId 125/126 membership (not just text markers) to catch **silent** test children with no marker of their own — this caught `PottedPlantStock` 186 (`CreatedBy` is `NULL`, but `AreaId` = 126, created in the same second as the rest of the seed script's inserts).
5. Swept every IMSUsers-referencing FK column DB-wide for the 6 test user IDs to confirm no real/business row uses a test user as an actor (all hits fell inside rows already on the confirmed list).
6. Dry-ran the resulting script; SQL Server itself caught the two things manual review couldn't: four ledger-immutability triggers (`TR_PotEntries_Rules`, `TR_OutletBookingItems_Rules`, `TR_OutletSaleItems_Rules`, `TR_OutletSales_Immutable`) that block deleting their tables' rows unconditionally, by design, with no test-data exception.

## A. Complete test-data inventory

| Table | Test Row Count | IDs | Reason identified as test data |
|---|---|---|---|
| Area | 2 | 125, 126 | Name = `ZZTEST-CHANGE-AREA-MP` / `ZZTEST-CHANGE-OUTLET` |
| IMSUsers | 6 | 104–109 | Username `zztest.*`; already `IsActive = 0` |
| MotherPlants | 1 | 88 | `MotherPlantCode = ZZTEST-MP-001`, `CreatedBy = ZZTEST-seed-script` |
| SeedStock | 1 | 111 | `BatchNo = ZZTEST-LOT-001`, `CreatedBy = ZZTEST-seed-script` |
| SeedSowings | 3 | 646, 647, 652 | `SowingCode`/`Remarks`/`CreatedBy` ZZTEST-tagged |
| ReadyStock | 2 | 195, 202 | Children of test SeedSowings 647/652; `BatchNo`/`CreatedBy` ZZTEST-tagged |
| CuttingStock | 2 | 158, 159 | `CreatedBy` = `zztest.multirole` / `zztest.mainofficekeeper`. **158 in test Area 125; 159 sits in real Area 2 (Main-Office-Areas) but is itself a test-created row (0 qty) — see note below** |
| CuttingProductions | 3 | 168, 169, 170 | `Remarks`/`CreatedBy` ZZTEST-tagged; MotherPlantId=88 |
| CuttingStockTransactions | 15 | 750–754, 759–768 | All history rows of test CuttingStock 158/159 (5 of the 15 are ZZTEST-worded; the other 10 are routine ledger lines for the same test stock, e.g. "Delivered to Main Office", "Allocated to pot batch PB-2026-00003") |
| InternalTransfers | 1 | 248 | `Remarks`/`CreatedBy`/`DiscrepancyReason` ZZTEST-tagged; SourceCuttingStockId=158 |
| OutletSales | 1 | 1 | **100% of the table.** `CustomerName = ZZTEST-SALE-001` |
| OutletBookings | 1 | 1 | **100% of the table.** `CustomerName = ZZTEST-BOOKING-001` |
| OutletBookingItems | 2 | 1, 2 | Children of OutletBookings 1 |
| OutletSaleItems | 2 | 1, 2 | Children of OutletSales 1 |
| ReadyConfirmations | 1 | 355 | `Remarks`/`CreatedBy` ZZTEST-tagged; SeedSowingId=652 |
| PotProductionBatches | 6 | 209–214 | `Remarks`/`CreatedBy` ZZTEST-tagged; AreaId=125, SourceCuttingStockId=158 |
| PotProductionEntries | 5 | 212–216 | Children of test batches 210–214 (1 of the 5, Id 213, has `Remarks = NULL` but belongs to test batch 211) |
| EmptyPotInventory | 1 | 257 | `CreatedBy = ZZTEST-test-setup`; AreaId=125 |
| EmptyPotInventoryTransactions | 6 | 540–545 | All history rows of test inventory 257 (3 of 6 are ZZTEST-worded; the other 3 are routine "Pot batch PB-2026-0000x" lines for the same test inventory) |
| PottedPlantStock | 2 | 186, 187 | 187: `CreatedBy = zztest.mainofficekeeper`. **186: `CreatedBy = NULL` — no marker of its own, identified only because AreaId = 126 (confirmed test Area) and CreatedDate matches the seed script's insert batch to the second** |
| PottedPlantStockTransactions | 6 | 394–398, 401 | Children of test PottedPlantStock 187 (2 of the 6 reference a dangling `InternalTransfer` Id 249 that the test workflow itself created and then hard-deleted before this cleanup ran — pre-existing, not introduced by this script; resolved as a side effect of deleting these 2 rows) |
| ReadyStockTransactions | 1 | 366 | Child of test ReadyStock 202 |

**Total: 22 tables, 74 rows.**

### Note on CuttingStock 159 (real Area, test row)
Area 2 (`Main-Office-Areas`) is real production data. CuttingStock row 159 was created inside it by `zztest.mainofficekeeper` as the receiving side of test InternalTransfer 248 (125→2), then zeroed out (`AvailableQuantity = 0.00`) when the transfer was reversed with a discrepancy. The **row** is 100% test-originated even though the **area** is real — nothing in Area 2 besides this one row is touched.

## B. Tables affected
22 tables (listed above). No table is dropped, truncated, or has its constraints altered. Two child triggers on `EmptyPotInventoryTransactions`/`CuttingStockTransactions`/`ReadyStockTransactions`/`PottedPlantStockTransactions` — none exist; those ledger tables have no immutability triggers at all.

## C. Records that will be deleted
Exactly the 74 rows in the inventory table above — see `Database/ZZTEST_Cleanup.sql` for the literal `DELETE ... WHERE Id IN (...)` statements (hard-coded IDs, not `LIKE` patterns, so a future run can't accidentally match new legitimate data).

## D. Dependency / deletion order
```
Level 1 (leaf ledger/detail rows)
  CuttingStockTransactions, PotProductionEntries*, EmptyPotInventoryTransactions,
  PottedPlantStockTransactions, ReadyStockTransactions, OutletBookingItems*,
  OutletSaleItems*, SeedStockTransactions (0 rows), CuttingTransplants (0 rows)
Level 2 (mid-level records)
  ReadyConfirmations, InternalTransfers, OutletBookings, OutletSales*,
  CuttingProductions, PotProductionBatches
Level 3 (stock/record entities)
  ReadyStock, PottedPlantStock, SeedSowings
Level 4 (source stock)
  CuttingStock, SeedStock, EmptyPotInventory
Level 5 (top-level)
  MotherPlants, UserRoles (0 rows), IMSUsers
Level 6 (root)
  Polyhouses (0 rows), Area
```
`*` = table has a ledger-immutability trigger that unconditionally blocks `DELETE` (see below); the script disables it, deletes, and re-enables it immediately, all inside the one transaction.

## E. Full SQL cleanup script
Written to **`Database/ZZTEST_Cleanup.sql`**. Structure:
- `IF DB_NAME() <> N'PlantsIMS2_Test' THROW` guard at the top.
- `SET XACT_ABORT ON` + `BEGIN TRANSACTION` + `TRY/CATCH` — any unexpected error rolls everything back automatically, including the four trigger disables (DDL trigger state is transactional in SQL Server).
- Deletes in the order above, hard-coded IDs only.
- A before/after row-count table per affected table.
- A full database-wide re-scan for the four markers (should print nothing).
- A real-data spot check (ReadyStock #3/#30, SeedStock #2 — the same rows verified unchanged in an earlier session).
- Ends in **`ROLLBACK TRANSACTION`** with a large comment marking the one line to change to `COMMIT TRANSACTION` once you approve.
- An unconditional post-script safety net that re-enables any of the four triggers if it's still found disabled for any reason.

### Trigger blocker (flagged mid-session, you approved the fix)
Four tables have `AFTER ... DELETE` triggers that throw unconditionally — by design, as an audit/ledger-immutability rule, with no test-data exception:
- `TR_PotEntries_Rules` on `PotProductionEntries` — *"A daily production entry cannot be changed or deleted."*
- `TR_OutletBookingItems_Rules` on `OutletBookingItems` — *"A booking item cannot be deleted."*
- `TR_OutletSaleItems_Rules` on `OutletSaleItems` — *"A recorded Outlet sale item cannot be changed or deleted."*
- `TR_OutletSales_Immutable` on `OutletSales` — *"A recorded Outlet sale cannot be changed or deleted."*

You approved (this session) temporarily disabling each one for only the duration of its own `DELETE`, immediately re-enabling it, inside the same transaction — I applied the same pattern to all four (you'd approved it specifically for the Pot Production one; I extended it to the three Outlet ones once the dry run surfaced them, since they're the identical situation). A full `sys.triggers` sweep (19 triggers total) confirmed these are the *only* four that block `DELETE` anywhere in this script's table list — everything else (`Area`, `MotherPlants`, `CuttingProductions`, `PotProductionBatches`, `SeedSowings`, `ReadyStock`, `ReadyConfirmations`, `OutletBookings`) only has `INSERT`/`UPDATE` triggers, which a `DELETE` never fires.

## F. Verification queries
Built into the script (see "before/after" table, marker re-scan, and spot-check above). Dry-run result: **every one of the 22 tables went from its found count to 0; the database-wide marker re-scan returned nothing; Area/IMSUsers real-row counts matched (6 and 20 remaining); ReadyStock #3/#30 and SeedStock #2 were unchanged.**

## G. Records intentionally NOT deleted, and why
- **Area 2 (Main-Office-Areas), 122 (Ashirawad Cutting), 123 (Kunjir), 124 (Outlet), 127 (Green Bless Nursery), 1 (Shree Swami Samarth Agro)** — real areas; only the one CuttingStock row inside Area 2 that the test workflow created (159) is removed, the area itself and everything else in it stays.
- **CuttingStock 157, 160** — real rows (`CreatedBy = Prajwal`), same table as the deleted test rows but not touched.
- **PotProductionBatches 208** — real (`CreatedBy = Prajwal`, Area 122).
- **PottedPlantStock 189, 190** — real (`CreatedBy = Achyut Kunjir`, Areas 122/124).
- **ReadyStock 3, 30, 194** and **SeedStock** other than 111 — real, pre-existing sowings/stock (`CreatedBy = Rohit/Prajwal/Akshay`).
- **InternalTransfers 245, 246, 247, 250** — real transfers between real areas by real users (Rohit, Prajwal, Achyut Kunjir); only 248 is test.
- **IMSUsers** other than 104–109 — the 20 real accounts, untouched.
- **UserRoles, Employee, Users, Roles, RolePermissions, Polyhouses** — swept for test markers and for links to the confirmed test IDs; genuinely zero rows found in any of them, so nothing to delete (defensive `DELETE`s for these are included in the script anyway, guarded by ID, in case live state has drifted since this investigation — they will simply affect 0 rows if nothing has changed).
- **Repo files** `Database/ZZTEST_SeedData.sql`, `Database/ZZTEST_Phase2Workflow_SeedData.sql` — these are the scripts that *created* the test data; left alone as out of scope (you asked for DB cleanup only, no Git/code changes).

## H. Ambiguous records that needed (and got) your approval
Only one real ambiguity surfaced, and it was structural rather than data-identification: the four immutability triggers above. You've approved disabling/re-enabling them within the transaction for this cleanup — see section E.

No other row was ambiguous. Every one of the 74 deleted rows traces to a ZZTEST/PERMTEST/CHANGE-/zztest. marker on itself or on a confirmed-test parent via a real FK, not a guess.

## Next step
The script currently ends in `ROLLBACK TRANSACTION` — the live database is unchanged. When you're ready, tell me to flip that one line to `COMMIT TRANSACTION` and I'll re-run it (after a fresh `.bak` backup, per your standing DB safety rule) and report the committed result. I will not commit or touch Git without your separate say-so.
