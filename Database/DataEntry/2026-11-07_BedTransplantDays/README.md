# Bed Transplant Days (Bed Sowing Alerts), 2026-11-07

Loads the owner's per-group "Bed Sowing Ready Date" values into `dbo.PlantSpecies.BedTransplantDays`.
**Bed Sowing Alerts** then compute each open bed's due date as **Transplant Due Date = bed Sowing Date + Bed Transplant Days**.

| File | What it is |
|---|---|
| `01_BedTransplantDays.sql` | **Generated** PREVIEW / COMMIT script. It is name-based, so the same file runs on local and on live. Do not edit it by hand. |
| `workbook_check.csv` | Generated check report: every variety row with its group, days and status, then every issue found. |
| `01_BedTransplantDays_Live.sql` | Standalone copy of `01_BedTransplantDays.sql` for live (same rows, match and plan), with the full `TARGET_*` guards. |

Requires `Database/Migrations/2026-11-07_BedTransplantDays.sql`. Both scripts refuse to run without it.

## Live (`01_BedTransplantDays_Live.sql`)

- **Guards:** `MODE` = `PREVIEW` / `COMMIT`. `TARGET_SERVER`, `TARGET_MACHINE`, `TARGET_DB` and `TARGET_DB_CREATED` must match the server.
  It refuses if `dbo.PlantSpecies.BedTransplantDays` or its CHECK is missing.
- **Both modes** run the locked update and its verification. PREVIEW then rolls back. Each run prints:
  - the counts for SET / CHANGE / SAME / NOT IN DATABASE / INACTIVE ONLY / ERROR, zeros included;
  - the skipped names.
- **Errors:** any ERROR row stops the run and nothing is written.
- **Run on live** (Linux, bash). First the identity query (CLAUDE.md C) and a COPY_ONLY backup:
  ```bash
  sqlcmd -S <server> -d PlantsIMS2_Test -U <login> -C -I -b -W -s"|" \
    -v MODE=PREVIEW TARGET_SERVER=srv1202245 TARGET_MACHINE=srv1202245 \
       TARGET_DB=PlantsIMS2_Test TARGET_DB_CREATED=2026-10-14T23:41:07 \
    -i 01_BedTransplantDays_Live.sql > preview.txt
  ```
  Then run the same command with `MODE=COMMIT`, only with the owner's approval.
- **If the workbook changes:** regenerate `01_BedTransplantDays.sql`, then rebuild this file from it.
  Its header records the SHA-256 of the `01_BedTransplantDays.sql` it was made from.
- **Tested 2026-11-09 on local PlantsIMS2_Test:**
  - PREVIEW: 297 SAME, 58 NOT IN DATABASE, 0 ERROR, rolled back.
  - The live `TARGET_*` values were refused there (54603 / 54607 / 54609), and so was a lower-case MODE (54602).
- **Live status (per the owner, 2026-11-09):** the migration is applied (778 varieties, 0 with days) and the code is deployed.
  This data script has **not been run on live** yet.
  - The skip count on live may differ from 58: live's variety list is not the same as local's.

## Source workbook

`BedSowing_ReadyDate.xlsx` in the repository root. The same file (same SHA-256) is in
`Precisoin Agritech Pvt. Ltd\` and `…\01_Discovery\`.
SHA-256 `5F3251AD6F2A715CA24D3A47347D69BA7A4C1AD014D930445FA48500870AAF8F`. The tool only reads the workbook and never changes it.

| Sheet | Headers | Used for |
|---|---|---|
| `Sowing Ready Dates` | Sr. No. / **Variety** (the group, e.g. "Petunia") / **Variety Name** / Bed Sowing Ready Date | The variety → group list (466 rows). Its "Bed Sowing Ready Date" column is **empty**. |
| `Sheet1` | Sr.no / **Variety** (group) / **Bed Sowing Ready Date** | The days per group: a **whole number of days** (15–50), not a date. 58 groups; 32 have a value, 26 are blank. |
| `Sheet2` | Date / Variety / Quantity | A dated list of 74 sowings (16-08-2026 … 18-10-2026). **Not used**: it holds no schedule. |

Owner decision (2026-11-07): an alert means the bed is **ready to transplant**, and the value is the number of days from bed sowing.
The workbook has no per-variety value, so each variety gets its group's days.

## How it was generated and how to apply a changed workbook

```powershell
# 1. regenerate (reads the workbook; never connects to a database)
dotnet run --project tools/BedTransplantDaysImport -c Release -- BedSowing_ReadyDate.xlsx Database/DataEntry/2026-11-07_BedTransplantDays
# 2. on the target: identity query (CLAUDE.md C), COPY_ONLY backup, then
sqlcmd -S <server> -d <db> -E -C -I -b -W -s"|" -v MODE=PREVIEW TARGET_SERVER="<@@SERVERNAME>" -i 01_BedTransplantDays.sql
# 3. only with the owner's approval for THAT database:
sqlcmd -S <server> -d <db> -E -C -I -b -W -s"|" -v MODE=COMMIT  TARGET_SERVER="<@@SERVERNAME>" -i 01_BedTransplantDays.sql
```

- **SET** fills an empty value. **CHANGE** replaces a different value, so the workbook wins (this is how a change reaches the app). **SAME** is left untouched.
- **NOT IN DATABASE** and **INACTIVE ONLY** rows are skipped and listed. A name that matches two active varieties is an **ERROR** and stops the run.
- Varieties that are not in the workbook keep their value. The script never clears a value.
- A single variety can also be changed in **Administration > Plant Types/Varieties > Edit > Bed Transplant Days**.
  The next time the workbook is applied, it overwrites that edit.
- The test `BedTransplantScheduleWorkbookTests.CommittedDataEntryScript_MatchesTheWorkbook` fails when the workbook changed
  and the script was not regenerated.

## Rehearsal (scratch only)

On `PlantsIMS2_Scratch_BedAlerts_20261107`, a restore of the verified backup `PlantsIMS2_Test_PreBedTransplantAlerts_20261107_093613.bak`:

- PREVIEW: **297 SET**, **58 NOT IN DATABASE**, no errors, nothing written.
- COMMIT: 297 varieties set.
- Re-run PREVIEW: 297 SAME.
- One value changed to 99, then PREVIEW showed CHANGE, and COMMIT restored 25.
- A wrong `TARGET_SERVER` is refused.

**Not run on PlantsIMS2_Test (local) or on live.**

## Data issues for the owner (nothing was guessed)

Names are matched with Add Variety's rule: capitals and extra spaces are ignored; **spelling and punctuation are not**.

**1. Varieties WITH days whose name is not in the database (58 rows, skipped).** The "closest name" column is only a
suggestion for your decision. **None of them was applied.** Several are clearly different varieties (e.g. "TR Pink" vs "HD Pink").

| Group | Workbook name | Days | Closest database name (not applied) |
|---|---|---|---|
| Antirrhinum | Antirrhinum Majus Orleans Light Orange / Red / Yellow; Toulon Apricot / Deep Red / Lavender / Rose (7) | 20 | – |
| Begonia | Begonia Big **Deluxxe** Pink Green Leaf / Red Bronze Leaf / Rose Bronze Leaf / Rose espresso / Rose Green Leaf / Salmon Bronze Leaf / white green Leaf (7) | 45 | Begonia Big **Deluxe** … (same colours), spelling only |
| Begonia | Begonia **Bonariensis** Big Red Green Leaf / Big Rose Bronze Leaf | 45 | Begonia **benariensis** … , spelling only |
| Begonia | Begonia Nonstop Mega Cool Mix; Begonia Super Cool Blush; Begonia Super Cool Pink | 45 | – |
| Callistephus | Callistephus Gala Mix | 22 | – |
| Celosia | Celosia Pinnatus Casanova Red; plumosa Fresh Look Orange / Red; Plumosa Glorious Orange; Wizard Coral Sunrise | 25 | – |
| Coleus | Coleus Extra DWF fairway Rose; Festive Dance; Orange; Rainbow Volcano; Ruby; Wizard Coral Sunrise | 20 | – |
| Dianthus | Dianthus Ideal Select Mix | 15 | Dianthus Ideal Select |
| Pansy | Pansy Viola Admire Orange Purple Wing; Pansy Viola Highflyer White | 30 | – |
| Petunia | Petunia Success 360 **Redstar** / **RoseStar** | 25 | Petunia Success 360 Red Star / Rose Star (spacing) |
| Petunia | Petunia **Sucess** 360 Purple | 25 | Petunia Success 360 Purple (spelling) |
| Petunia | Petunia Success 360; Success 360 maximix; Success TR Blue / Burgundy / Maximix / Pink / Pink Morn / Red / Voilet / White; Supercascade White; T. Pink Morn; T. Violet; Tritunia Mix; Tritunia Starmix | 25 | – (no exact counterpart) |
| Salvia | Salvia Evolution White; Salvia Farinacea Victoria Blue; Salvia Victoria Blue | 20 | Salvia F. Victoria Blue (?) |
| Torenia | Torenia Hi-Lite Magenta | 35 | – |
| Viola | Viola Highflyer Blue Heaven Begreen; Viola Highflyer Yellow Purple Wing | 30 | – |

**2. Varieties with NO days in the workbook (111 rows).** These are listed under "No transplant schedule" on the alerts page:
- 106 rows whose group's value is blank: Brocoli, Cabbage, Calendula, Carnation, Catalina, Cosmos, Dahlia, Flowering, Geranium,
  Gerbera, Gomphrena, Lantana, Latania, M.G, Marigold, Melampodium, Mg., Ornamental, Osteospermum, Papaya, Ptilotus, Stock,
  Strawberry, Sunflower, Tulsi, Zinnia.
- 5 rows whose group is not in `Sheet1` at all: Aronaa, Basil, Chilli, Chrysanthemum (2).
- `Sheet1` also has 4 numbered rows without a group name (Sr.no 3, 5, 14, 15), all blank.

**3. Open beds on the local database (43, all Pending transplant) after this script:** 37 get a due date. **6 have no schedule**:
Melampodium Showstar, Dahlia Garden Pride, Gomphrena Buddy Purple, Marigold Taishan Orange, Stock Dwarf Hot Cakes Mix
(their groups are blank), and Zinnia Zahara Dbl Mix (not in the workbook).

**4. Other database notes:** 86 active Seasonal varieties are not in the workbook at all, and no Chrysanthemum, Meri Gold or Zinnia
variety has days. Those stay "No transplant schedule" until a value is set.
