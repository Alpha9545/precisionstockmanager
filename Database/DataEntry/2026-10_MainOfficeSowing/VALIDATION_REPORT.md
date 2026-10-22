# Main Office Direct Seed Sowing, 01-10-2026 to 12-10-2026: validation and import package

Batch tag: `MAIN-OFFICE-SOWING-2026-10`. Prepared 14-10-2026.

**Status: NOT READY for any real database.** One owner input is still missing: the variety decisions in `variety_mapping.csv` (section 6).
- Everything else is decided, built and rehearsed end to end on a scratch copy.
- Nothing was written to `PlantsIMS2_Test`, live `PlantsIMS2` or the VPS.

## 1. Owner decisions (14-10-2026)
| Question | Decision |
|---|---|
| What each row is | **Direct Seed Sowing** into trays (not Bed Sowing: a Bed Sowing has no cavity/tray fields) |
| Area / Polyhouse | Main-Office-Areas / Facility 1 Main office |
| Assigned supervisor (approver) | **Maya Shinde** (`Maya`) |
| Recorder | **Prajwal** (the app refuses the assigned supervisor as recorder) |
| Seed received per variety | **= seedlings sown (1 seed = 1 seedling)**. Each lot ends at 0. This is an assumption, not a verified receipt |
| Cutting-like rows (Lantana K.K, Salvia Blue Cutting GP, SR rows) | **Treated as seed**, like the other rows |
| Row 35 (6 × 160) | **150 Cavity = 900** (160 does not exist) |
| Empty trays | **Received at exactly the quantities needed**, at Facility 1, dated 30-09-2026 09:00 IST |
| Growing material | **Cocopeat**, received at exactly the kg needed at Facility 1 |
| Varieties | **Owner fills `variety_mapping.csv`**; nothing guessed. **← still open** |

## 2. Files
| File | Purpose |
|---|---|
| `source_rows.csv` | The 79 rows exactly as supplied (row 35 still 160 / 960). |
| `entry_rows.csv` | The 79 rows to enter (row 35 = 150 / 900, with the decision noted). |
| `variety_mapping.csv` | **Owner worksheet**: one line per source name (65). Fill `Decision` with EXISTING or NEW, and `VarietyName`. For NEW, also fill `NewPlantType` and `NewReadyStockDays`. Candidate columns come from `PlantsIMS2_Test`; Ids 3810–3834 are marked [TEST-ONLY]. |
| `01_Preflight_ReadOnly.sql` | (A) Read-only preflight. Works on test or live. |
| `03_Verify_ReadOnly.sql` | (D) Read-only verification and reconciliation: 11 PASS/FAIL checks. |
| `tools/MainOfficeSowingImport/` | (B + C) The controlled import: **runner, not SQL** (section 7). `check` is read-only; `run` writes through the app. |
| `VALIDATION_REPORT.md` | This report. |

## 3. Verified master data (PlantsIMS2_Test, 14-10-2026)
| Item | Found |
|---|---|
| Area | `dbo.Area` Id 2 "Main-Office-Areas" (A-1, MainOffice, active). `dbo.Areas` is an unused empty table. |
| Polyhouse | `dbo.Polyhouses` Id 6 "Facility 1 Main office" (Area 2) |
| Maya Shinde | `dbo.IMSUsers` Id 9, Username `Maya`, active. Sowing Supervisor, Sowing Operator and Bed Sowing Transplanter in Area 2. She is in the Sowing approver list. |
| Prajwal | `dbo.IMSUsers` Id 21, active. May use Add Seed Stock, Material Stock Add (trays + material), Direct Seed Sowing and Plant Master. |
| Cavities allowed | 9, 24, 42, 70, 102, 150 |
| "Main Office Seed Lot" | Not a field. A seed lot is `dbo.SeedStock`, and "Main Office" is its Area. Lots are received with a blank Lot/Batch No and no supplier, as for the earlier data. |
| Cocopeat | `MaterialTypes` Id 1, conversion factor 5.2. Rules: 150/102 cavity 1.5 kg per tray, **70 cavity 1.0 kg** in `PlantsIMS2_Test` (the migration default, likely on live, is 1.5). The runner reads the rule from the target database. |
| Live ids | **Not verified.** The runner resolves everything **by name** in its target and stops if anything is missing or ambiguous. |

## 4. Reconciliation of the entry rows
All 79 rows are complete trays (trays × cavity = seedlings).

| Date | Rows | Trays | Seedlings |
|---|---:|---:|---:|
| 01-10-2026 | 4 | 983 | 147,450 |
| 02-10-2026 | 5 | 450 | 67,500 |
| 03-10-2026 | 5 | 790 | 118,500 |
| 04-10-2026 | 7 | 664 | 99,600 |
| 05-10-2026 | 6 | 555 | 83,250 |
| 06-10-2026 | 5 | 548 | 82,200 |
| 07-10-2026 | 1 | 120 | 18,000 |
| 08-10-2026 | 23 | 799 | **119,850** (supplied 119,910; row 35 is 900, not 960) |
| 09-10-2026 | 7 | 453 | 67,950 |
| 10-10-2026 | 5 | 639 | 95,850 |
| 11-10-2026 | 4 | 469 | 70,350 |
| 12-10-2026 | 7 | 469 | 55,598 |
| **Total** | **79** | **6,939** | **1,026,098** (supplied 1,026,158) |

| Cavity | Trays | Cocopeat (PlantsIMS2_Test rules) |
|---|---:|---:|
| 150 Cavity | 6,725 | 10,087.5 kg |
| 102 Cavity | 74 | 111 kg |
| 70 Cavity | 140 | 140 kg (210 kg at 1.5 kg/tray) |
| **Total** | **6,939** | **10,338.5 kg** = 1,988.173 entered × 5.2 (exact) |

### Totals by variety (as written)
| Variety (as written) | Cavity | Dates | Trays | Seedlings |
|---|---|---|---:|---:|
| Antirrhinum Snapdragon Snapshot Mix | 150 | 03-10, 04-10 | 588 | 88,200 |
| Antirrhinum Snaptini Mix | 150 | 05-10 | 106 | 15,900 |
| Aster Chinensis Gala Mix | 150 | 08-10 | 6 | 900 |
| Aster Chinensis Standy Mix | 150 | 08-10 | 7 | 1,050 |
| Begonia Super Cool Lipstick | 150 | 11-10 | 128 | 19,200 |
| Dianthus Ideal Mix | 150 | 01-10, 06-10 | 399 | 59,850 |
| Dianthus Mix | 150 | 05-10 | 130 | 19,500 |
| Impatiens Imara XDR Mix | 150 | 09-10 | 96 | 14,400 |
| Lantana Beach (K.K) | 150 | 07-10 | 120 | 18,000 |
| Lantana Coconut (K.K) | 150 | 02-10, 09-10 | 66 | 9,900 |
| Lantana Ibis (K.K) | 150 | 02-10 | 34 | 5,100 |
| Lantana Litchi (K.K) | 150 | 09-10 | 30 | 4,500 |
| Lantana Nandu (K.K) | 150 | 04-10 | 52 | 7,800 |
| Lantana Pitaya (K.K) | 150 | 04-10 | 90 | 13,500 |
| Lantana Tangerine (K.K) | 150 | 06-10 | 92 | 13,800 |
| Lobularia Maritima Giga White | 150 | 08-10 | 8 | 1,200 |
| Pansy Inspire Deluxe Maxi Mix | 150 | 10-10, 11-10 | 269 | 40,350 |
| Pansy Majus GTS Mix | 150 | 10-10 | 211 | 31,650 |
| Pansy Viola Admire Maxi Mix | 150 | 09-10, 10-10 | 230 | 34,500 |
| Pansy Viola Cats Plus Mix | 150 | 08-10 | 3 | 450 |
| Pansy Viola Cornuta Admire Maxi Mix | 150 (supplied 160) | 08-10 | 6 | 900 |
| Pansy Viola Deluxe Maxi Mix | 150 | 08-10 | 5 | 750 |
| Pansy Viola Highflyer Mix | 150 | 08-10 | 6 | 900 |
| Pansy Viola Highflyer Violet Face | 150 | 12-10 | 47 | 7,050 |
| Pansy Viola Highflyer Yellow | 150 | 12-10 | 44 | 6,600 |
| Pansy Viola Highflyer Yellow Red Wing (Trailing) | 102 | 12-10 | 74 | 7,548 |
| Pansy Viola Hot Cakes Mix | 150 | 05-10 | 122 | 18,300 |
| Pansy Viola Wittrockiana Inspire Deluxe Maxi Mix | 150 | 12-10 | 164 | 24,600 |
| Petunia 360 Suc. Pink | 150 | 01-10 | 138 | 20,700 |
| Petunia 360 Success Violet | 150 | 02-10 | 141 | 21,150 |
| Petunia Bello Mix | 150 | 01-10 | 522 | 78,300 |
| Petunia Boom HD Burgundy | 150 | 02-10 | 125 | 18,750 |
| Petunia Boom HD Pink | 150 | 04-10 | 72 | 10,800 |
| Petunia Hy. Boom Mix | 150 | 08-10 | 8 | 1,200 |
| Petunia S.C. White | 150 | 09-10 | 43 | 6,450 |
| Petunia Spoty Mix | 150 | 05-10 | 20 | 3,000 |
| Petunia Suc. 360 Blue | 150 | 08-10, 09-10, 10-10 | 414 | 62,100 |
| Petunia Suc. 360 Burgundy | 150 | 03-10, 04-10, 08-10, 11-10 | 286 | 42,900 |
| Petunia Suc. 360 Purple | 150 | 03-10, 08-10 | 232 | 34,800 |
| Petunia Suc. 360 Rose Star | 150 | 03-10 | 136 | 20,400 |
| Petunia Suc. 360 White | 150 | 10-10, 11-10 | 154 | 23,100 |
| Petunia Suc. HD Maxi Mix | 150 | 05-10 | 77 | 11,550 |
| Petunia Suc. HD Pink | 150 | 03-10 | 129 | 19,350 |
| Petunia Suc. Maxi Mix | 150 | 08-10 | 14 | 2,100 |
| Petunia Success 360 Rose Star | 150 | 02-10 | 120 | 18,000 |
| Petunia Tritunia Plum | 150 | 09-10 | 72 | 10,800 |
| Portulaca Sundial Mix | 150 | 08-10 | 10 | 1,500 |
| Ptilotus Exaltatus Joey | 150 | 08-10 | 8 | 1,200 |
| Rudbeckia Amarillo Gold | 150 | 08-10 | 9 | 1,350 |
| Rudbeckia Pavnee Spirit | 150 | 08-10 | 10 | 1,500 |
| Rudbeckia Sonata Begreen | 150 | 08-10 | 11 | 1,650 |
| Salvia Blue (Cutting GP) | 150 | 01-10 | 64 | 9,600 |
| Salvia Salvano | 150 | 06-10, 08-10 | 271 | 40,650 |
| Salvia Vista Mix | 150 | 04-10, 08-10 | 157 | 23,550 |
| Silver Dust | 150 | 08-10 | 3 | 450 |
| Silver Dust Millar | 150 | 06-10 | 194 | 29,100 |
| Smola White (SR) | 70 | 12-10 | 33 | 2,310 |
| Stock Hot Cakes Mix | 150 | 05-10 | 100 | 15,000 |
| Verbena Apex Raw | 150 | 08-10 | 10 | 1,500 |
| Verbena Quartz Mix | 150 | 06-10 | 42 | 6,300 |
| Verbena Quartz XP Mix | 150 | 04-10 | 34 | 5,100 |
| Verbena Speciosa Imagination | 150 | 08-10 | 9 | 1,350 |
| Vido Red (SR) | 70 | 12-10 | 26 | 1,820 |
| Vinca Cora XDR Mix | 150 | 08-10 | 31 | 4,650 |
| Vinca XDR Red (SR) | 70 | 12-10 | 81 | 5,670 |
| **65 lines** | | | **6,939** | **1,026,098** |

## 5. Discrepancies and items kept out
1. **Row 35:** supplied as "6 × 160 = 900"; the arithmetic is 960 and 160 Cavity does not exist. Resolved as 150 Cavity = 900. Both CSVs keep the trail.
2. **08-10, Petunia Suc. 360 Blue:** the row is 150 × 150 = 22,500. The supplied 08-10 totals reconcile with the rows given. No inconsistency was found; if the original note differs, send that figure.
3. **"20i. Celosia glorious mix - 18": excluded.** Its date and cavity are unclear (Celosia Glorious Mix exists, Id 3107 in test).
4. **10-10 SR cuttings** (Salvia Blue 56, Rockin Deep Purple 277, Rockin Flamingo Red 233, Dahlia Magenta 407, Grand White 270 = 1,243): **excluded**. These are cuttings, outside the 6,939-tray total, and belong to the Cutting workflow.
5. **Cutting-like rows inside the total** are treated as seed by owner decision. Note that Vido Red and Smola White are **CHRYSANTHEMUM** varieties in the master list.

## 6. Open item: the variety worksheet
- Only **7 of 65** names match an existing variety exactly in `PlantsIMS2_Test`: Dianthus Ideal Mix, Petunia Bello Mix, Salvia Vista Mix, Verbena Quartz Mix, Vinca Cora XDR Mix, Vido Red and Smola White.
- **Petunia Bello Mix (3828) is TEST-ONLY.** It was created for the test Bed Sowing data, so live will not have it.
- Several names look like spellings of one variety (for example "Petunia Success 360 Rose Star" / "Petunia Suc. 360 Rose Star", and "Silver Dust" / "Silver Dust Millar" / "Cineraria Silver Dust"). Mapping both to the same `VarietyName` merges them into one lot.
- **Fill it against live's variety names** if live is the final target. The runner matches names in whichever database it runs against.

## 7. Why a runner instead of insert SQL (scripts B and C)
- Direct Seed Sowing in this application does all of the following in one transaction:
  - takes a sowing code from `BatchNumberSequences` (for example `2026-10-01-J-012`);
  - checks and locks the seed lot, and writes the Sown ledger row with Before/After balances;
  - consumes the CEILING(seeds ÷ cavity) empty trays from the Facility 1 pool, writing its ledger row;
  - consumes the kg of Cocopeat for those trays, writing its ledger row;
  - computes the Expected Ready Date from the variety's growing days;
  - checks that the assigned supervisor is an active Sowing Supervisor and not the recorder;
  - enforces a one-time submission token.
- Triggers (for example `TR_SeedSowings_ImmutableTrayData`) and CHECK constraints guard the result.
- Re-implementing that in SQL would bypass and drift from the business rules. So the import is `tools/MainOfficeSowingImport`, which **drives the application's own pages** as Prajwal (signed in from the database by the app's own claims factory, no password) and never writes a row itself:

| Step | What it calls | Idempotency on rerun |
|---|---|---|
| A. New varieties (NEW lines only) | Plant Master "Add variety" | Skips a name that already exists exactly once |
| B. Seed receipts: one blank-lot Main Office lot per variety, seeds = Σ seedlings, received 30-09-2026 09:00 IST | Add Seed Stock | Skips a lot that already has this batch's tagged receipt (and checks the quantity) |
| C. Empty trays per cavity at Facility 1, dated 30-09-2026 09:00 IST | Material Stock > Add Trays | Deterministic token: "already saved, nothing added twice" |
| D. Cocopeat at Facility 1 | Material Stock > Add | Deterministic token |
| E. 79 Direct Seed Sowings (date, then row order), Supervisor Maya, Cocopeat | Seed Sowing > Create | Deterministic token per row; each saved row is read back and compared |

`check` is read-only. It verifies the target, the names, users, permissions, approver, cavities, rules and mapping. It also stops if an existing blank-lot lot of these varieties holds other seeds. Then it prints the full plan.

`run` stops at the first refusal and never retries.

Notes on dates:
- `SowingDate` is the business date (01–12 Oct). The expected ready date = sowing date + growing days, so many batches will show as ready or overdue at once.
- As the screens do today, the sowing's seed/tray/Cocopeat ledger rows are time-stamped when entered.
- The Cocopeat receipt has no date field in the app, so it is stamped at entry time, before the sowings.

## 8. Rehearsal on a scratch copy (14-10-2026)
**Copy:** `PlantsIMS2_Scratch_MoSowing_20261014`, restored from `PlantsIMS2_Test_PostApply1022BedSowingEdit_20261013_210632.bak`.

**Mapping:** a throwaway rehearsal mapping, **not the owner's**:
- the 7 exact names as EXISTING;
- the other 58 as NEW, named as written, SEASONAL VARITIES, 32 days.

| Check | Result |
|---|---|
| `check` | Passed, nothing written |
| `run` | 58 varieties, 65 seed receipts (1,026,098), 3 tray receipts (74 / 6,725 / 140), Cocopeat 1,988.173 × 5.2 = 10,338.500 kg, **79 sowings**, no failures |
| `run` again (twice) | Everything recognised as already saved; **data fingerprint unchanged**, so nothing was entered twice |
| Tables changed by the run | Only PlantSpecies (+58), SeedStock (+62 new lots, 3 existing empty test lots reused), SeedStockTransactions (+144), TrayStock (+3) / TrayStockTransactions (+82), MaterialStock (+1) / MaterialTransactions (+80), SeedSowings (+79) and BatchNumberSequences. **No other table changed**: the 113-table fingerprint shows no other difference, and the 43 test Bed Sowings are untouched. |
| `03_Verify_ReadOnly.sql` | **11/11 PASS**: per-date and per-row match, Area / Facility 1 / Maya / Prajwal / Cocopeat / Seed source on all 79, seeds in = out = 1,026,098, trays in = out = 6,939, Cocopeat 10,338.5 kg in = out, no duplicates / orphans / negative stock, all 79 awaiting approval |

## 9. Correction procedure (script E)
A delete-based rollback is **not provided**: the seed, tray and material ledgers are append-only. Correction is forward-only, through the application:
- **A wrong sowing, before Maya approves it:** Seed Sowing > Details > **Cancel**. In one transaction this returns exactly that sowing's seeds (ReversalReturn), trays and Cocopeat to the same lot and pools, and marks it Cancelled. It is refused once a Supervisor Approval exists; cancel the approval first.
- **Re-entry:** correct `entry_rows.csv` / the mapping and use a **new row number**, because a cancelled row's token is already used.
- **Leftover reconstructed stock** after cancellations (seeds, trays, Cocopeat) stays visible in the lots and pools. Record it with the normal wastage/adjustment screens if it should not remain.
- **Unrelated data:** take a backup and a row-count/fingerprint before `run`, then compare after. The rehearsal did exactly this.

## 10. Execution order (later; each step needs the owner's go-ahead)
1. **Owner fills `variety_mapping.csv`** with live's variety names.
2. Live must first have **every migration the current code needs** (the 29 listed for the deployment, including 10-08 Material Stock, 10-19 Cavity70 and 10-21/10-22), applied and verified, with the matching code deployed.
3. Take a verified backup of live, then a row-count/fingerprint.
4. `01_Preflight_ReadOnly.sql` on live: read-only review.
5. Rehearse on a **scratch copy restored from that live backup**: `check`, then `run`, then `run` again, then `03_Verify_ReadOnly.sql` (expect 11/11 PASS).
6. On live: `check --allow-live-target "<server>/<database>"`, then (owner approval) `run` with the same flag, then `03_Verify_ReadOnly.sql` and the fingerprint comparison. The runner decides the target from the server's own identity, not the database name (live on the VPS is also named `PlantsIMS2_Test`); see `tools/MainOfficeSowingImport/ImportTargetGuard.cs`.
7. Maya approves the 79 batches in Sowing Approvals. This is a normal application step and is not part of the import.

## 11. Review and preflight commands
```
cd C:\Users\hp\OneDrive\Desktop\plantstockmanager2
type Database\DataEntry\2026-10_MainOfficeSowing\01_Preflight_ReadOnly.sql
type Database\DataEntry\2026-10_MainOfficeSowing\03_Verify_ReadOnly.sql
type tools\MainOfficeSowingImport\Program.cs

cd Database\DataEntry\2026-10_MainOfficeSowing
sqlcmd -S LAPTOP-R87BKQGH\MSSQLSERVER02 -d PlantsIMS2_Test -E -C -I -W -s "|" -i 01_Preflight_ReadOnly.sql -o preflight_test.txt

cd C:\Users\hp\OneDrive\Desktop\plantstockmanager2
dotnet build tools\MainOfficeSowingImport -c Release
set PSM_IMPORT_CONNECTION=Server=LAPTOP-R87BKQGH\MSSQLSERVER02;Database=PlantsIMS2_Test;Integrated Security=True;TrustServerCertificate=True
dotnet tools\MainOfficeSowingImport\bin\Release\net8.0\MainOfficeSowingImport.dll check
```
`check` writes nothing. Do **not** run `run` against `PlantsIMS2_Test` or live without an explicit decision.
