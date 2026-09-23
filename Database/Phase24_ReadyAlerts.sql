/* ============================================================
   Phase 24: Ready Alerts (Phase J)
   ------------------------------------------------------------
   INSPECTION SUMMARY (per the standing "inspect before creating"
   rule):

   Ready Alerts is notification/alert functionality ONLY. It must
   NOT create a "Ready Stock" table, must NOT add a 'Ready' value
   to dbo.SeedSowings' Status CHECK, and must NOT write anything at
   all when an alert is displayed -- it is a pure, read-only query
   over data that (after this script) already exists. Confirmed by
   inspection that no notification/alert table exists anywhere in
   this app today, and that the entire Ready Alerts page can be
   built as a filtered SELECT against dbo.SeedSowings -- so, per
   the Phase J spec's own instruction ("do not immediately create a
   large generic notification system... a read-only/query-driven
   Ready Alert page based on SeedSowings is acceptable"), this
   script creates NO new table.

   ReadyStockDays -- inspected for an existing equivalent field
   first (ReadyStockDays / Ready Days / Crop Days / Production Days
   / Days to Ready / Days to Finish / Days to Sale / Crop Duration):
   none exist anywhere in the schema or code. dbo.PlantSpecies (the
   existing plant/variety master -- Models/PlantSpecies.cs,
   Data/PlantSpeciesRepository.cs, managed today via the legacy
   Pages/Admin/Plant.cshtml(.cs) Plant Types & Species page) is the
   correct, existing master to extend -- NOT a new table, per the
   spec's own "do not create a separate ReadyStockDays table unless
   the existing architecture genuinely requires one" instruction (it
   does not: a variety-level integer is exactly the right grain for
   a column on the variety master it already has). Adds
   dbo.PlantSpecies.ReadyStockDays (nullable INT -- a species left
   unconfigured simply falls back to Phase I's original manual-entry
   ExpectedReadyDate behavior, so no existing species row is broken
   by this being unset).

   Historical date preservation (Phase J spec Section 4) -- CRITICAL:
   dbo.SeedSowings.ExpectedReadyDate (Phase 23/I) was, until now, a
   purely manual, optional field. SeedSowingRepository.InsertAsync
   is corrected (this is the "small Phase I correction," made to the
   minimum degree required, per the spec's own instruction) to
   compute ExpectedReadyDate = SowingDate + ReadyStockDays whenever
   the sown species has a ReadyStockDays value configured AT THE
   MOMENT OF SOWING, falling back to the original manual entry only
   when it does not. Critically, the ReadyStockDays value actually
   used is copied onto the SeedSowings row itself (this script's
   second additive column, dbo.SeedSowings.ReadyStockDays) rather
   than being re-read from dbo.PlantSpecies every time the row is
   displayed -- so editing a variety's ReadyStockDays later can
   NEVER silently rewrite an existing Sowing's historical
   ExpectedReadyDate/ReadyStockDays. This mirrors the exact
   "denormalize onto the header row at insert time, never re-derive
   on read" convention every other traceability field on
   dbo.SeedSowings (SpeciesId/AreaId/BatchNo/SeedSourceId) already
   uses.

   Alert categories (Ready Soon / Ready Today / Overdue) are
   DERIVED, at read time, from Status + ExpectedReadyDate --
   SeedSowingRepository.ClassifyReadyAlert(...) is the single shared
   function both new/changed pages call. No status value is stored
   for "alert category," and no new Status value ('Ready') is added
   to CK_SeedSowings_Status -- the Sowing stays 'Sown' (or
   'Cancelled') throughout Phase J, exactly per the spec's explicit
   "do not invent a new Ready status" rule. A Cancelled Sowing is
   excluded from every alert query at the SQL level (WHERE Status =
   'Sown'), so it can never appear as an alert regardless of its
   ExpectedReadyDate.

   Polyhouse display: reuses the EXISTING Area.PolyhouseId ->
   Polyhouses.Id relationship (Phase 2/14) via a LEFT JOIN added to
   SeedSowingRepository's own BaseSelect -- no second Polyhouse
   relationship, no schema change needed for this part at all. Per
   Decision 19/Phase H, AreaType still determines operational
   responsibility (Kunjir/Kiran/etc.) and PolyhouseId still only
   identifies the physical building; this script does not touch or
   blur that split.

   No ReadyStock table, no Ready Confirmation workflow, no automatic
   Sowing-status change, no stock mutation of any kind -- all
   explicitly out of scope for Phase J (reserved for Phase K), per
   the user's own instruction.

   ADDITIVE ONLY. Does not drop, rename, or alter the type of any
   existing column; does not touch CK_SeedSowings_Status,
   CK_SeedSowings_CavityType, CK_SeedStockTx_Type, or any Phase
   16/18/19/20/21/22/23 object. Every existing dbo.PlantSpecies row
   and every existing dbo.SeedSowings row is unaffected -- both new
   columns default to NULL, which is exactly Phase 23/I's original
   behavior for a Sowing with no configured/entered Ready date.
   Guarded so this script is safe to run more than once.

   Run this AFTER Phase23_SeedSowing.sql has been applied.
   Take a backup first per your own DB safety rules.
   ============================================================ */

-- ------------------------------------------------------------
-- 1) Extend the EXISTING plant/variety master, dbo.PlantSpecies,
--    with the variety-specific Ready Stock Days value (additive,
--    nullable -- no existing row is affected).
-- ------------------------------------------------------------
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.PlantSpecies') AND name = 'ReadyStockDays'
)
BEGIN
    ALTER TABLE dbo.PlantSpecies ADD ReadyStockDays INT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_PlantSpecies_ReadyStockDays')
BEGIN
    ALTER TABLE dbo.PlantSpecies ADD CONSTRAINT CK_PlantSpecies_ReadyStockDays
        CHECK (ReadyStockDays IS NULL OR ReadyStockDays > 0);
END
GO

-- ------------------------------------------------------------
-- 2) Denormalize the ReadyStockDays value actually applied at
--    Sowing time onto dbo.SeedSowings itself (Phase 23/I), so a
--    later edit to dbo.PlantSpecies.ReadyStockDays can never
--    rewrite an existing Sowing's historical figures. Additive,
--    nullable -- every existing Phase 23 row is unaffected (NULL,
--    exactly like its existing ExpectedReadyDate for a row that
--    was never given one).
-- ------------------------------------------------------------
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.SeedSowings') AND name = 'ReadyStockDays'
)
BEGIN
    ALTER TABLE dbo.SeedSowings ADD ReadyStockDays INT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_SeedSowings_ReadyStockDays')
BEGIN
    ALTER TABLE dbo.SeedSowings ADD CONSTRAINT CK_SeedSowings_ReadyStockDays
        CHECK (ReadyStockDays IS NULL OR ReadyStockDays > 0);
END
GO

-- No new table. No new Status value. No ReadyStock/Ready Confirmation
-- object of any kind. Ready Alerts itself is entirely query-driven
-- against dbo.SeedSowings (see SeedSowingRepository.GetAlertCandidatesAsync
-- and .ClassifyReadyAlert) -- there is nothing further for this script
-- to create.
