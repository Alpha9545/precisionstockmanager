-- ============================================================================
-- 2026-10-17_BedSowingApprovalAnyDate.sql
--
-- WHY (owner requirement 2026-10-12): a tray batch transplanted from a Bed Sowing may be approved on ANY business date
--   -- before, on or after its calculated Ready Date. The Ready Date stays recorded exactly as it is (nothing is
--   rewritten); it no longer blocks the approval of a Bed Sowing batch.
--
-- WHAT: TR_ReadyConfirmations_BedReadyDate is re-created WITHOUT its Bed Sowing branch (THROW 52178). Its other branch is
--   kept byte-for-byte: a tray batch transplanted from received Ready Stock (SourceType 'Transplant') is still never
--   approved before its Ready Date (52208). The trigger fires FIRST again (sp_settriggerorder), as before.
--   Replaced only if it is byte-for-byte the 2026-10-13 version (SHA-256 -- schema-drift guard).
--
-- NOT changed: who approves a Bed Sowing batch (only the designated approver, 2026-10-16_BedSowingApprover.sql), the
--   Bed Sowing maker-checker exception, Direct Seed / Cutting / Transplant rules, any sowing, approval or stock row.
-- Requires: 2026-10-13_TraysToSowingApprovals.sql and 2026-10-16_BedSowingApprover.sql.
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to -v TARGET_SERVER=<@@SERVERNAME>.
-- One transaction. Idempotent (re-run = no-op). Undo: 2026-10-17_BedSowingApprovalAnyDate_Rollback.sql.
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52501, '2026-10-17_BedSowingApprovalAnyDate.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52502, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52503, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION BedAnyDate1017;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';
    IF OBJECT_ID('dbo.BedSowingApprover', 'U') IS NULL
        THROW 52504, 'Run 2026-10-16_BedSowingApprover.sql first (the designated Bed Sowing approver).', 1;
    IF OBJECT_ID('dbo.TR_ReadyConfirmations_BedReadyDate', 'TR') IS NULL
        THROW 52505, 'TR_ReadyConfirmations_BedReadyDate is missing -- run 2026-10-13_TraysToSowingApprovals.sql first.', 1;

    IF OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_BedReadyDate')) NOT LIKE N'%52178%'
        PRINT '=== Already applied -- no-op ===';
    ELSE
    BEGIN
        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_BedReadyDate'))), 2)
           <> 'E867F5775B1BEB1F050E2569E92C5E4413A06C131C650F3F5A282B65426C2045'
            THROW 52506, 'TR_ReadyConfirmations_BedReadyDate is not the expected 2026-10-13 version -- schema drift, nothing changed.', 1;

        PRINT '=== Bed Sowing batches waiting for approval (their Ready Dates stay as recorded) ===';
        SELECT Id, SowingCode, ExpectedReadyDate FROM dbo.SeedSowings WHERE SourceType = N'BedSowing' AND Status = N'Sown' ORDER BY Id;

        DROP TRIGGER dbo.TR_ReadyConfirmations_BedReadyDate;
        EXEC (N'CREATE TRIGGER dbo.TR_ReadyConfirmations_BedReadyDate ON dbo.ReadyConfirmations
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    -- India business date (IST, +05:30, no daylight saving) -- the same day AppClock.Today gives.
    -- 2026-10-17: a tray batch from a Bed Sowing may be approved on ANY date (its Ready Date stays recorded) -- only by
    -- the designated Bed Sowing approver (TR_ReadyConfirmations_AssignedSupervisor).
    -- 2026-10-13: a tray batch transplanted from received Ready Stock (Ready Date = transplant date + Ready Days) is
    -- never approved before its Ready Date. Direct Seed Sowing and Cutting Sowing are not affected.
    IF EXISTS (SELECT 1 FROM inserted i JOIN dbo.SeedSowings sw ON sw.Id = i.SeedSowingId
               WHERE sw.SourceType = N''Transplant''
                 AND (sw.ExpectedReadyDate IS NULL OR CAST(SWITCHOFFSET(SYSDATETIMEOFFSET(), ''+05:30'') AS date) < sw.ExpectedReadyDate))
        THROW 52208, ''A tray batch transplanted from Ready Stock cannot be approved before its Ready Date.'', 1;
END');
        EXEC sp_settriggerorder @triggername = N'dbo.TR_ReadyConfirmations_BedReadyDate', @order = N'First', @stmttype = N'INSERT';
        IF OBJECTPROPERTY(OBJECT_ID('dbo.TR_ReadyConfirmations_BedReadyDate'), 'ExecIsFirstInsertTrigger') <> 1
            THROW 52507, 'TR_ReadyConfirmations_BedReadyDate does not fire first -- rolled back.', 1;
        PRINT '=== TR_ReadyConfirmations_BedReadyDate: Bed Sowing batches approved on any date; Transplant rule kept ===';
    END

    SELECT name, OBJECTPROPERTY(object_id, 'ExecIsFirstInsertTrigger') AS FiresFirst, is_disabled
    FROM sys.triggers WHERE parent_id = OBJECT_ID('dbo.ReadyConfirmations') ORDER BY name;
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION BedAnyDate1017;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION BedAnyDate1017;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION BedAnyDate1017;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
