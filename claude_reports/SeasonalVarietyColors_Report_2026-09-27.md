# Seasonal Variety Color Update — Report (2026-09-27)

Database confirmed as `PlantsIMS2_Test` before every step. Dry run (`BEGIN TRANSACTION` → verify → `ROLLBACK`) executed clean; live DB is unchanged (283 target rows still have their pre-update colors — 282 are `NULL`, 1 is `Green`).

## A/B/C. Source parse and summary

- **Total source rows (all PlantTypes):** 356 (Ids 1–416, with gaps in the source's own numbering — not our concern).
- **Total source rows for `SEASONAL VARITIES` (PlantTypeId = 4):** **289**.
- **Unique variety names in that subset:** 289 (no duplicates).
- **Duplicate source names:** **0**.
- **Matching Seasonal Varieties found in `dbo.PlantSpecies`:** **283**.
- **Missing (source variety not found by name):** **6**.
- **Records whose Color will change:** **283** (of which 282 go from `NULL` → a color; 1, Basil #3179, goes from an existing `Green` → `Mix` per your uploaded list).
- **Records whose Color was already correct:** **0**.

Matching was done by name, whitespace-normalized and case-insensitive only — **no fuzzy/semantic matching**, per your instructions.

## Missing (in the uploaded list, not found in `PlantSpecies`) — reported, not guessed

| Source Id | Source name | Source color | Why it doesn't match |
|---|---|---|---|
| 236 | Pansy Super Majestic Giants Mix | Mix | DB has "Pansy Super Majestic Giants 2" |
| 239 | Marigold Vanilla White | White | DB has "Marigold Vanilla (White)" (parentheses) |
| 277 | Geranium Ringo 2000 Mix | Mix | DB has "Geranium Ringo 2000" (no "Mix") |
| 291 | Salvia Flamex 2000 Mix | Mix | DB has "Salvia Flamex 2000" (no "Mix") |
| 328 | Salvia Alert Red | Red | DB has "Salvia Red Alert" (word order) |
| 360 | Begonia Semperflorens Ambassador Mix | Mix | DB has "Begonia Semperflorens Ambassador" (no "Mix") |

I checked each by hand — none are whitespace/case artifacts; all 6 are genuine wording differences between this list and the names actually stored (the DB names came from the earlier insert/correction tasks, which used a slightly different source spreadsheet). No Color is set for these 6; nothing is inserted or guessed.

## One case worth your attention: Basil (Id 3179)

Currently `Color = 'Green'` (set by someone using the app between sessions — not by me, confirmed by re-checking the live DB just before this dry run). The uploaded list gives Basil `Mix`. Per your rule 7 ("replace with the uploaded Color"), this will be overwritten to `Mix`. Flagging because it's the only case where a specific existing value is being replaced with a more generic one — let me know if you want Basil excluded from this run.

## D. Backup
`PlantsIMS2_Test_PreSeasonalVarietyColors_20260927_232540.bak` — COPY_ONLY, CHECKSUM, verified via `RESTORE VERIFYONLY`.

## E. SQL script
`Database/UpdateSeasonalVarietyColors_2026-09-27.sql` — guarded to `PlantsIMS2_Test`, transactional, idempotent (driven by a fixed (Id, Color) list, not name patterns — a second run re-applies the same values with no side effects), full verification block, currently ends in `ROLLBACK`.

## Dry-run verification (all passed)

| Check | Result |
|---|---|
| Rows updated | 283 / 283 expected |
| Every target row now holds its expected Color | ✓ 0 mismatches |
| Name / ScientificName / ReadyStockDays unchanged on every updated row | ✓ 0 changed |
| Duplicate names under PlantTypeId=4 | ✓ none |
| PlantTypes 1/2/3 | ✓ unchanged — 28 / 78 / 18 |
| Total PlantSpecies count | ✓ unchanged — 413 |
| Live DB after rollback | ✓ unchanged (282 still NULL, Basil still Green) |

## Full preview table (all 283 rows to be updated)

See `Database/UpdateSeasonalVarietyColors_2026-09-27.sql`'s `@ColorUpdates` table — it *is* the complete, exact (Id → new Color) list that will be applied, already reviewed above via the dry run. A duplicate rendering as a markdown table is available on request but would just repeat the same 283 pairs.

## Next step
Script currently ends in `ROLLBACK` — nothing has been changed. Confirm you want to proceed (including the Basil overwrite) and I'll flip to `COMMIT`, re-run, do full post-commit verification, run `dotnet test`, and report back.
