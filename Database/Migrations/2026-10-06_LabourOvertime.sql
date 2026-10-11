-- ============================================================================
-- 2026-10-06_LabourOvertime.sql
--
-- WHY: Labour Overtime -- workers who work beyond the normal working day.
-- One row per overtime ENTRY (a work period of a worker group at one
-- location). Several entries on the same date and location are normal (other
-- periods, other tasks, even overlapping times for different groups); they are
-- never merged. Headcounts only -- no worker names, ids or wages.
--
-- LOCATION RULE: identical to Daily Labour (2026-10-04 / 2026-10-06):
--   Area.AreaType = 'MainOffice' -> Area + Polyhouse (Polyhouse required, must belong to that Area)
--   any other Area               -> Area only (Polyhouse must be NULL)
--   Outlet                       -> not part of Daily Labour
--
-- WHAT THIS DOES:
--   dbo.LabourOvertime                 new table: Date, Area, Polyhouse, MaleWorkers / FemaleWorkers
--                                      (0..500, at least one worker), StartTime < EndTime (whole minutes,
--                                      same day), WorkDescription (required), Remarks, audit fields.
--     DurationMinutes                  PERSISTED computed = minutes from StartTime to EndTime
--     MaleWorkerMinutes                PERSISTED computed = DurationMinutes x MaleWorkers
--     FemaleWorkerMinutes              PERSISTED computed = DurationMinutes x FemaleWorkers
--                                      (derived from the stored times -- nobody can type them; the
--                                      application shows them as hours, e.g. 150 min x 4 = 10 worker-hours)
--     CK_LabourOvertime_LabourDate     not in the future (India date, UTC+05:30) and not before 2000
--   TR_LabourOvertime_Location         the location rule above (same error numbers as Daily Labour)
--   dbo.LabourOvertimeHistory          audit trail: one row per insert / correction / delete with the
--                                      old and new values and who changed it, written ONLY by
--                                      TR_LabourOvertime_Audit
--   TR_LabourOvertimeHistory_Protect   history rows can never be UPDATEd; a history row can only be
--                                      DELETEd once its overtime row no longer exists
--
-- NOT changed: dbo.DailyLabourAttendance, dbo.DailyLabourCounts, dbo.LabourLogs, any permission or
-- role grant (Labour.View / Labour.Enter are reused), any other existing object or row.
--
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to the server named with
-- -v TARGET_SERVER=<@@SERVERNAME> (the live database is ALSO named PlantsIMS2_Test). One
-- transaction. Idempotent (re-run = no-op).
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
--       then MODE=COMMIT.   Undo: 2026-10-06_LabourOvertime_Rollback.sql
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 51741, '2026-10-06_LabourOvertime.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 51742, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 51743, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION LabourOvertime2026_10_06;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';

    PRINT '=== Before: row counts of tables this script must NOT change ===';
    SELECT 'DailyLabourAttendance' AS TableName, COUNT(*) AS Rows FROM dbo.DailyLabourAttendance
    UNION ALL SELECT 'DailyLabourCounts', COUNT(*) FROM dbo.DailyLabourCounts
    UNION ALL SELECT 'Area', COUNT(*) FROM dbo.Area
    UNION ALL SELECT 'Polyhouses', COUNT(*) FROM dbo.Polyhouses
    UNION ALL SELECT 'RolePermissions', COUNT(*) FROM dbo.RolePermissions;

    IF OBJECT_ID('dbo.LabourOvertime', 'U') IS NULL
    BEGIN
        CREATE TABLE dbo.LabourOvertime (
            Id                  INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_LabourOvertime PRIMARY KEY,
            LabourDate          DATE          NOT NULL,
            AreaId              INT           NOT NULL,
            PolyhouseId         INT           NULL,
            MaleWorkers         INT           NOT NULL,
            FemaleWorkers       INT           NOT NULL,
            StartTime           TIME(0)       NOT NULL,
            EndTime             TIME(0)       NOT NULL,
            DurationMinutes     AS (DATEDIFF(MINUTE, StartTime, EndTime)) PERSISTED,
            MaleWorkerMinutes   AS (DATEDIFF(MINUTE, StartTime, EndTime) * MaleWorkers) PERSISTED,
            FemaleWorkerMinutes AS (DATEDIFF(MINUTE, StartTime, EndTime) * FemaleWorkers) PERSISTED,
            WorkDescription     NVARCHAR(500) NOT NULL,
            Remarks             NVARCHAR(500) NULL,
            CreatedDate         DATETIME2     NOT NULL CONSTRAINT DF_LabourOvertime_CreatedDate DEFAULT (SYSUTCDATETIME()),
            CreatedById         INT           NULL,
            ModifiedDate        DATETIME2     NULL,
            ModifiedById        INT           NULL,
            CONSTRAINT FK_LabourOvertime_Area       FOREIGN KEY (AreaId)       REFERENCES dbo.Area(Id),
            CONSTRAINT FK_LabourOvertime_Polyhouse  FOREIGN KEY (PolyhouseId)  REFERENCES dbo.Polyhouses(Id),
            CONSTRAINT FK_LabourOvertime_CreatedBy  FOREIGN KEY (CreatedById)  REFERENCES dbo.IMSUsers(Id),
            CONSTRAINT FK_LabourOvertime_ModifiedBy FOREIGN KEY (ModifiedById) REFERENCES dbo.IMSUsers(Id),
            CONSTRAINT CK_LabourOvertime_MaleWorkers   CHECK (MaleWorkers   BETWEEN 0 AND 500),
            CONSTRAINT CK_LabourOvertime_FemaleWorkers CHECK (FemaleWorkers BETWEEN 0 AND 500),
            CONSTRAINT CK_LabourOvertime_AnyWorker     CHECK (MaleWorkers + FemaleWorkers > 0),
            CONSTRAINT CK_LabourOvertime_Times         CHECK (EndTime > StartTime),
            CONSTRAINT CK_LabourOvertime_WholeMinutes  CHECK (DATEPART(SECOND, StartTime) = 0 AND DATEPART(SECOND, EndTime) = 0),
            CONSTRAINT CK_LabourOvertime_LabourDate    CHECK (LabourDate >= '2000-01-01' AND LabourDate <= CAST(DATEADD(MINUTE, 330, SYSUTCDATETIME()) AS DATE)),
            CONSTRAINT CK_LabourOvertime_WorkDescription CHECK (LEN(LTRIM(RTRIM(WorkDescription))) > 0)
        );
        CREATE INDEX IX_LabourOvertime_Date ON dbo.LabourOvertime (LabourDate) INCLUDE (AreaId, PolyhouseId);
        CREATE INDEX IX_LabourOvertime_Area ON dbo.LabourOvertime (AreaId, LabourDate);
        PRINT '=== dbo.LabourOvertime created ===';
    END
    ELSE PRINT '=== dbo.LabourOvertime already exists -- no-op ===';

    IF OBJECT_ID('dbo.LabourOvertimeHistory', 'U') IS NULL
    BEGIN
        -- No FK to dbo.LabourOvertime: the audit trail must outlive the row it describes.
        CREATE TABLE dbo.LabourOvertimeHistory (
            Id                 INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_LabourOvertimeHistory PRIMARY KEY,
            OvertimeId         INT           NOT NULL,
            Action             VARCHAR(10)   NOT NULL,
            LabourDate         DATE          NOT NULL,
            AreaId             INT           NOT NULL,
            PolyhouseId        INT           NULL,
            OldMaleWorkers     INT           NULL,
            OldFemaleWorkers   INT           NULL,
            OldStartTime       TIME(0)       NULL,
            OldEndTime         TIME(0)       NULL,
            OldWorkDescription NVARCHAR(500) NULL,
            OldRemarks         NVARCHAR(500) NULL,
            NewMaleWorkers     INT           NULL,
            NewFemaleWorkers   INT           NULL,
            NewStartTime       TIME(0)       NULL,
            NewEndTime         TIME(0)       NULL,
            NewWorkDescription NVARCHAR(500) NULL,
            NewRemarks         NVARCHAR(500) NULL,
            ChangedById        INT           NULL,
            ChangedDate        DATETIME2     NOT NULL CONSTRAINT DF_LabourOvertimeHistory_ChangedDate DEFAULT (SYSUTCDATETIME()),
            CONSTRAINT CK_LabourOvertimeHistory_Action CHECK (Action IN ('Created', 'Corrected', 'Deleted'))
        );
        CREATE INDEX IX_LabourOvertimeHistory_Overtime ON dbo.LabourOvertimeHistory (OvertimeId, Id);
        PRINT '=== dbo.LabourOvertimeHistory created ===';
    END
    ELSE PRINT '=== dbo.LabourOvertimeHistory already exists -- no-op ===';

    IF OBJECT_ID('dbo.TR_LabourOvertime_Location', 'TR') IS NULL
    BEGIN
        EXEC (N'
CREATE TRIGGER dbo.TR_LabourOvertime_Location ON dbo.LabourOvertime
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    -- Only new rows, or rows whose location changed, are checked (a past entry stays
    -- correctable if its Polyhouse is later moved to another Area).
    ;WITH changed AS (
        SELECT i.Id, i.AreaId, i.PolyhouseId
        FROM inserted i
        LEFT JOIN deleted d ON d.Id = i.Id
        WHERE d.Id IS NULL
           OR d.AreaId <> i.AreaId
           OR ISNULL(d.PolyhouseId, -1) <> ISNULL(i.PolyhouseId, -1)
    )
    SELECT c.Id, c.AreaId, c.PolyhouseId, a.AreaType, p.AreaId AS PolyhouseAreaId
    INTO #chk
    FROM changed c
    INNER JOIN dbo.Area a ON a.Id = c.AreaId
    LEFT JOIN dbo.Polyhouses p ON p.Id = c.PolyhouseId;

    IF EXISTS (SELECT 1 FROM #chk WHERE AreaType = N''Outlet'')
        THROW 51714, ''Outlet is not part of Daily Labour.'', 1;
    IF EXISTS (SELECT 1 FROM #chk WHERE AreaType = N''MainOffice'' AND PolyhouseId IS NULL)
        THROW 51711, ''No Polyhouse selected. Please select a Polyhouse for this Main Office Area.'', 1;
    IF EXISTS (SELECT 1 FROM #chk WHERE ISNULL(AreaType, N'''') <> N''MainOffice'' AND PolyhouseId IS NOT NULL)
        THROW 51712, ''Labour for this Area is recorded Area-wise -- Polyhouse must be empty.'', 1;
    IF EXISTS (SELECT 1 FROM #chk WHERE PolyhouseId IS NOT NULL AND ISNULL(PolyhouseAreaId, -1) <> AreaId)
        THROW 51713, ''This Polyhouse does not belong to the selected Area.'', 1;
END');
        PRINT '=== TR_LabourOvertime_Location created ===';
    END

    IF OBJECT_ID('dbo.TR_LabourOvertime_Audit', 'TR') IS NULL
    BEGIN
        EXEC (N'
CREATE TRIGGER dbo.TR_LabourOvertime_Audit ON dbo.LabourOvertime
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.LabourOvertimeHistory (OvertimeId, Action, LabourDate, AreaId, PolyhouseId,
        NewMaleWorkers, NewFemaleWorkers, NewStartTime, NewEndTime, NewWorkDescription, NewRemarks, ChangedById)
    SELECT i.Id, ''Created'', i.LabourDate, i.AreaId, i.PolyhouseId,
           i.MaleWorkers, i.FemaleWorkers, i.StartTime, i.EndTime, i.WorkDescription, i.Remarks, i.CreatedById
    FROM inserted i
    WHERE NOT EXISTS (SELECT 1 FROM deleted d WHERE d.Id = i.Id);

    -- Corrected: only when a value really changed.
    INSERT INTO dbo.LabourOvertimeHistory (OvertimeId, Action, LabourDate, AreaId, PolyhouseId,
        OldMaleWorkers, OldFemaleWorkers, OldStartTime, OldEndTime, OldWorkDescription, OldRemarks,
        NewMaleWorkers, NewFemaleWorkers, NewStartTime, NewEndTime, NewWorkDescription, NewRemarks, ChangedById)
    SELECT i.Id, ''Corrected'', i.LabourDate, i.AreaId, i.PolyhouseId,
           d.MaleWorkers, d.FemaleWorkers, d.StartTime, d.EndTime, d.WorkDescription, d.Remarks,
           i.MaleWorkers, i.FemaleWorkers, i.StartTime, i.EndTime, i.WorkDescription, i.Remarks, i.ModifiedById
    FROM inserted i
    INNER JOIN deleted d ON d.Id = i.Id
    WHERE d.MaleWorkers <> i.MaleWorkers OR d.FemaleWorkers <> i.FemaleWorkers
       OR d.StartTime <> i.StartTime OR d.EndTime <> i.EndTime
       OR d.WorkDescription <> i.WorkDescription
       OR ISNULL(d.Remarks, N'''') <> ISNULL(i.Remarks, N'''')
       OR d.LabourDate <> i.LabourDate OR d.AreaId <> i.AreaId OR ISNULL(d.PolyhouseId, -1) <> ISNULL(i.PolyhouseId, -1);

    -- Deleted: the application never deletes overtime; recorded in case anyone does.
    INSERT INTO dbo.LabourOvertimeHistory (OvertimeId, Action, LabourDate, AreaId, PolyhouseId,
        OldMaleWorkers, OldFemaleWorkers, OldStartTime, OldEndTime, OldWorkDescription, OldRemarks)
    SELECT d.Id, ''Deleted'', d.LabourDate, d.AreaId, d.PolyhouseId,
           d.MaleWorkers, d.FemaleWorkers, d.StartTime, d.EndTime, d.WorkDescription, d.Remarks
    FROM deleted d
    WHERE NOT EXISTS (SELECT 1 FROM inserted i WHERE i.Id = d.Id);
END');
        PRINT '=== TR_LabourOvertime_Audit created ===';
    END

    IF OBJECT_ID('dbo.TR_LabourOvertimeHistory_Protect', 'TR') IS NULL
    BEGIN
        EXEC (N'
CREATE TRIGGER dbo.TR_LabourOvertimeHistory_Protect ON dbo.LabourOvertimeHistory
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM inserted)
        THROW 51746, ''Labour overtime history cannot be changed.'', 1;
    IF EXISTS (SELECT 1 FROM deleted d WHERE EXISTS (SELECT 1 FROM dbo.LabourOvertime o WHERE o.Id = d.OvertimeId))
        THROW 51747, ''Labour overtime history cannot be deleted while its overtime entry exists.'', 1;
END');
        PRINT '=== TR_LabourOvertimeHistory_Protect created ===';
    END

    PRINT '=== After: LabourOvertime columns / constraints / indexes / triggers ===';
    SELECT c.name AS ColumnName, TYPE_NAME(c.user_type_id) AS DataType, c.is_nullable AS IsNullable, c.is_computed AS IsComputed
    FROM sys.columns c WHERE c.object_id = OBJECT_ID('dbo.LabourOvertime') ORDER BY c.column_id;
    SELECT name AS CheckConstraint FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID('dbo.LabourOvertime') ORDER BY name;
    SELECT name AS IndexName FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.LabourOvertime') AND name IS NOT NULL;
    SELECT name AS TriggerName FROM sys.triggers WHERE parent_id IN (OBJECT_ID('dbo.LabourOvertime'), OBJECT_ID('dbo.LabourOvertimeHistory')) ORDER BY name;

    PRINT '=== After: row counts (must equal Before) ===';
    SELECT 'DailyLabourAttendance' AS TableName, COUNT(*) AS Rows FROM dbo.DailyLabourAttendance
    UNION ALL SELECT 'DailyLabourCounts', COUNT(*) FROM dbo.DailyLabourCounts
    UNION ALL SELECT 'Area', COUNT(*) FROM dbo.Area
    UNION ALL SELECT 'Polyhouses', COUNT(*) FROM dbo.Polyhouses
    UNION ALL SELECT 'RolePermissions', COUNT(*) FROM dbo.RolePermissions
    UNION ALL SELECT 'LabourOvertime', COUNT(*) FROM dbo.LabourOvertime
    UNION ALL SELECT 'LabourOvertimeHistory', COUNT(*) FROM dbo.LabourOvertimeHistory;

END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION LabourOvertime2026_10_06;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION LabourOvertime2026_10_06;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION LabourOvertime2026_10_06;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
