-- ============================================================================
-- 2026-10-06_LabourOvertime_Rollback.sql
--
-- Undoes 2026-10-06_LabourOvertime.sql: drops its triggers, then
-- dbo.LabourOvertimeHistory and dbo.LabourOvertime.
-- REFUSES to run while dbo.LabourOvertime has any row, so recorded overtime can
-- never be lost by a rollback (restore the pre-migration backup instead if that
-- is really intended). The application code that uses these tables must be
-- rolled back first.
--
-- NOT changed: every object that existed before the migration.
-- SAFETY: same guards as the migration. One transaction. Idempotent.
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 51751, 'This rollback may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 51752, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 51753, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION LabourOvertimeRollback;
BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';

    IF OBJECT_ID('dbo.LabourOvertime', 'U') IS NOT NULL
    BEGIN
        DECLARE @Rows INT;
        EXEC sp_executesql N'SELECT @n = COUNT(*) FROM dbo.LabourOvertime', N'@n INT OUTPUT', @n = @Rows OUTPUT;
        IF @Rows > 0
            THROW 51754, 'dbo.LabourOvertime has overtime rows -- refusing to drop it. Restore the pre-migration backup if that is really intended.', 1;
    END

    IF OBJECT_ID('dbo.TR_LabourOvertimeHistory_Protect', 'TR') IS NOT NULL DROP TRIGGER dbo.TR_LabourOvertimeHistory_Protect;
    IF OBJECT_ID('dbo.TR_LabourOvertime_Audit', 'TR') IS NOT NULL          DROP TRIGGER dbo.TR_LabourOvertime_Audit;
    IF OBJECT_ID('dbo.TR_LabourOvertime_Location', 'TR') IS NOT NULL       DROP TRIGGER dbo.TR_LabourOvertime_Location;
    IF OBJECT_ID('dbo.LabourOvertimeHistory', 'U') IS NOT NULL             DROP TABLE dbo.LabourOvertimeHistory;
    IF OBJECT_ID('dbo.LabourOvertime', 'U') IS NOT NULL                    DROP TABLE dbo.LabourOvertime;

    SELECT OBJECT_ID('dbo.LabourOvertime') AS OvertimeTable, OBJECT_ID('dbo.LabourOvertimeHistory') AS HistoryTable,
           (SELECT COUNT(*) FROM dbo.DailyLabourAttendance) AS DailyLabourAttendanceRows;

    IF N'$(MODE)' = N'COMMIT'
    BEGIN
        COMMIT TRANSACTION LabourOvertimeRollback;
        PRINT '=== COMMITTED ===';
    END
    ELSE
    BEGIN
        ROLLBACK TRANSACTION LabourOvertimeRollback;
        PRINT '=== DRY RUN -- rolled back, nothing changed ===';
    END
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH
GO
