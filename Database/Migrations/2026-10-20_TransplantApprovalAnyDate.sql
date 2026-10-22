-- ============================================================================
-- 2026-10-20_TransplantApprovalAnyDate.sql
--
-- WHY (owner requirement 2026-10-12, "Mother Plant Area supervisors can approve on any date"): a tray batch transplanted
--   in a Mother Plant Area (SeedSowings.SourceType 'Transplant') may be approved by an Area Supervisor of its own Area
--   before, on or after its Ready Date. Until now TR_ReadyConfirmations_BedReadyDate refused it before the Ready Date
--   (52208), and the application repeated that rule.
--
-- WHAT: TR_ReadyConfirmations_BedReadyDate (replaced only if it is byte-for-byte the current version, SHA-256):
--   Transplant -> no date check any more; a batch WITHOUT a Ready Date is still refused (52208, eligibility).
--   Bed Sowing  -> unchanged (any date since 2026-10-17_BedSowingApprovalAnyDate.sql); Seed / Cutting -> unchanged (no rule).
--   Who may approve is unchanged: TR_ReadyConfirmations_AssignedSupervisor (Area Supervisor of the batch's Area or a System
--   Administrator; Bed Sowing only the designated approver). The Ready Date itself (SeedSowings.ExpectedReadyDate) is
--   neither changed nor recalculated.
--
-- NOT changed: no table, column, sowing, approval, stock, role or permission row.
-- Requires: 2026-10-17_BedSowingApprovalAnyDate.sql (the trigger version this replaces) and
--   2026-10-17_TransplantApprovalAreaScope.sql (the Area Supervisor rule this relies on).
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to -v TARGET_SERVER=<@@SERVERNAME>.
-- One transaction. Idempotent (re-run = no-op). Undo: 2026-10-20_TransplantApprovalAnyDate_Rollback.sql.
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52611, '2026-10-20_TransplantApprovalAnyDate.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52612, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52613, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION TransplantAnyDate1020;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';
    IF OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_AssignedSupervisor')) NOT LIKE N'%TransplantApproverInBatchArea%'
        THROW 52614, 'Run 2026-10-17_TransplantApprovalAreaScope.sql first (a Transplant batch is approved only by its Area Supervisor).', 1;

    IF OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_BedReadyDate')) LIKE N'%TransplantApprovalAnyDate%'
        PRINT '=== Already applied -- no-op ===';
    ELSE
    BEGIN
        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_BedReadyDate'))), 2)
           <> '0DBAF1E4FFF23864C5C31E851D47BEA53F30D97DC5080795E867620E233501F4'
            THROW 52615, 'TR_ReadyConfirmations_BedReadyDate is not the expected 2026-10-17 version -- schema drift, nothing changed.', 1;

        PRINT '=== Transplant tray batches waiting for approval before their Ready Date (approvable after this change) ===';
        SELECT sw.Id, sw.SowingCode, a.Name AS Area, sw.ExpectedReadyDate
        FROM dbo.SeedSowings sw JOIN dbo.Area a ON a.Id = sw.AreaId
        WHERE sw.SourceType = N'Transplant' AND sw.Status = N'Sown'
          AND sw.ExpectedReadyDate > CAST(SWITCHOFFSET(SYSDATETIMEOFFSET(), '+05:30') AS date);

        EXEC (N'ALTER TRIGGER dbo.TR_ReadyConfirmations_BedReadyDate ON dbo.ReadyConfirmations
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    -- India business date (IST, +05:30, no daylight saving) -- the same day AppClock.Today gives.
    -- 2026-10-17: a tray batch from a Bed Sowing may be approved on ANY date (its Ready Date stays recorded) -- only by
    -- the designated Bed Sowing approver (TR_ReadyConfirmations_AssignedSupervisor).
    -- 2026-10-13: a tray batch transplanted from received Ready Stock (Ready Date = transplant date + Ready Days).
    -- 2026-10-20 (TransplantApprovalAnyDate): it may be approved on ANY date -- before, on or after its Ready Date, which
    -- stays recorded as it is -- by an Area Supervisor of its own Area (TR_ReadyConfirmations_AssignedSupervisor). It must
    -- still HAVE a Ready Date. Direct Seed Sowing and Cutting Sowing are not affected.
    IF EXISTS (SELECT 1 FROM inserted i JOIN dbo.SeedSowings sw ON sw.Id = i.SeedSowingId
               WHERE sw.SourceType = N''Transplant'' AND sw.ExpectedReadyDate IS NULL)
        THROW 52208, ''A tray batch transplanted from Ready Stock has no Ready Date, so it cannot be approved.'', 1;
END');

        IF NOT (OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_BedReadyDate')) LIKE N'%TransplantApprovalAnyDate%')
            THROW 52616, 'Post-check failed: the application probe (WorkflowCorrectionsSchema.TransplantAnyDateProbeSql) would not see the change.', 1;
        PRINT '=== TR_ReadyConfirmations_BedReadyDate: a Transplant batch on any date (still needs a Ready Date); other rules unchanged ===';
    END
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION TransplantAnyDate1020;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION TransplantAnyDate1020;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION TransplantAnyDate1020;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
