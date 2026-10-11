-- ============================================================================
-- 2026-10-06_DailyLabourAttendance.sql
--
-- WHY: Daily Labour is now recorded as TWO attendance checkpoints per working
-- day -- 9:00 AM and 2:00 PM -- instead of four manual Full/Half Day counts.
-- Full/Half Day are then ESTIMATED from the two headcounts (individual
-- workers are not identified), separately for male and female workers:
--     Full Day = MIN(9 AM, 2 PM)      Half Day = ABS(9 AM - 2 PM)
-- A day with only one checkpoint is "Incomplete" and is left out of the
-- Full/Half Day totals. The calculation lives in the application
-- (DailyLabourRules.Classify) -- nothing derived is stored.
--
-- LOCATION RULE: identical to dbo.DailyLabourCounts (2026-10-04):
--   Area.AreaType = 'MainOffice' -> per Date + Area + Polyhouse (Polyhouse required,
--                                   must belong to that Area)
--   any other Area               -> per Date + Area (Polyhouse must be NULL)
--   Outlet                       -> not part of Daily Labour
--
-- WHAT THIS DOES:
--   dbo.DailyLabourAttendance          new table: one row per Date + location + AttendanceTime
--                                      (AttendanceTime 'AM09' = 9:00 AM, 'PM14' = 2:00 PM),
--                                      MaleCount / FemaleCount 0..500, optional Remarks.
--   UX_DailyLabourAttendance_Polyhouse filtered unique (Date, Area, Polyhouse, AttendanceTime) WHERE PolyhouseId IS NOT NULL
--   UX_DailyLabourAttendance_Area      filtered unique (Date, Area, AttendanceTime)            WHERE PolyhouseId IS NULL
--   TR_DailyLabourAttendance_Location  the location rule above, PLUS: no attendance for a
--                                      date + location that already has a manual (legacy)
--                                      dbo.DailyLabourCounts record (never counted twice).
--   dbo.DailyLabourAttendanceHistory   audit trail: one row per insert / correction / delete,
--                                      with the old and new counts and who changed it.
--                                      Written ONLY by TR_DailyLabourAttendance_Audit, so the
--                                      application cannot skip it.
--   TR_DailyLabourAttendanceHistory_Protect
--                                      history rows can never be UPDATEd; a history row can only
--                                      be DELETEd once its attendance row no longer exists.
--
-- NOT changed: dbo.DailyLabourCounts (kept, read-only history), dbo.LabourLogs,
-- any permission or role grant (Labour.View / Labour.Enter are reused as they
-- are), any other existing table/column/constraint/trigger/row.
--
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to the
-- server named with -v TARGET_SERVER=<@@SERVERNAME> (the live database is ALSO
-- named PlantsIMS2_Test). One transaction. Idempotent (re-run = no-op).
-- Requires QUOTED_IDENTIFIER ON (filtered indexes) -- sqlcmd -I.
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
--       then MODE=COMMIT.   Undo: 2026-10-06_DailyLabourAttendance_Rollback.sql
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 51721, '2026-10-06_DailyLabourAttendance.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 51722, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 51723, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
IF OBJECT_ID('dbo.DailyLabourCounts', 'U') IS NULL
    THROW 51724, 'dbo.DailyLabourCounts is missing -- run 2026-10-04_DailyLabourCounts.sql first.', 1;
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

BEGIN TRANSACTION DailyLabourAttendance2026_10_06;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';

    PRINT '=== Before: row counts of tables this script must NOT change ===';
    SELECT 'DailyLabourCounts' AS TableName, COUNT(*) AS Rows FROM dbo.DailyLabourCounts
    UNION ALL SELECT 'LabourLogs', COUNT(*) FROM dbo.LabourLogs
    UNION ALL SELECT 'Area', COUNT(*) FROM dbo.Area
    UNION ALL SELECT 'Polyhouses', COUNT(*) FROM dbo.Polyhouses
    UNION ALL SELECT 'RolePermissions', COUNT(*) FROM dbo.RolePermissions;

    IF OBJECT_ID('dbo.DailyLabourAttendance', 'U') IS NULL
    BEGIN
        CREATE TABLE dbo.DailyLabourAttendance (
            Id            INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DailyLabourAttendance PRIMARY KEY,
            LabourDate    DATE          NOT NULL,
            AreaId        INT           NOT NULL,
            PolyhouseId   INT           NULL,
            AttendanceTime    CHAR(4)       NOT NULL,
            MaleCount     INT           NOT NULL,
            FemaleCount   INT           NOT NULL,
            Remarks       NVARCHAR(500) NULL,
            CreatedDate   DATETIME2     NOT NULL CONSTRAINT DF_DailyLabourAttendance_CreatedDate DEFAULT (SYSUTCDATETIME()),
            CreatedById   INT           NULL,
            ModifiedDate  DATETIME2     NULL,
            ModifiedById  INT           NULL,
            CONSTRAINT FK_DailyLabourAttendance_Area       FOREIGN KEY (AreaId)       REFERENCES dbo.Area(Id),
            CONSTRAINT FK_DailyLabourAttendance_Polyhouse  FOREIGN KEY (PolyhouseId)  REFERENCES dbo.Polyhouses(Id),
            CONSTRAINT FK_DailyLabourAttendance_CreatedBy  FOREIGN KEY (CreatedById)  REFERENCES dbo.IMSUsers(Id),
            CONSTRAINT FK_DailyLabourAttendance_ModifiedBy FOREIGN KEY (ModifiedById) REFERENCES dbo.IMSUsers(Id),
            CONSTRAINT CK_DailyLabourAttendance_AttendanceTime CHECK (AttendanceTime IN ('AM09', 'PM14')),
            CONSTRAINT CK_DailyLabourAttendance_MaleCount   CHECK (MaleCount   BETWEEN 0 AND 500),
            CONSTRAINT CK_DailyLabourAttendance_FemaleCount CHECK (FemaleCount BETWEEN 0 AND 500)
        );
        PRINT '=== dbo.DailyLabourAttendance created ===';
    END
    ELSE PRINT '=== dbo.DailyLabourAttendance already exists -- no-op ===';

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_DailyLabourAttendance_Polyhouse' AND object_id = OBJECT_ID('dbo.DailyLabourAttendance'))
        CREATE UNIQUE INDEX UX_DailyLabourAttendance_Polyhouse ON dbo.DailyLabourAttendance (LabourDate, AreaId, PolyhouseId, AttendanceTime) WHERE PolyhouseId IS NOT NULL;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_DailyLabourAttendance_Area' AND object_id = OBJECT_ID('dbo.DailyLabourAttendance'))
        CREATE UNIQUE INDEX UX_DailyLabourAttendance_Area ON dbo.DailyLabourAttendance (LabourDate, AreaId, AttendanceTime) WHERE PolyhouseId IS NULL;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_DailyLabourAttendance_Date' AND object_id = OBJECT_ID('dbo.DailyLabourAttendance'))
        CREATE INDEX IX_DailyLabourAttendance_Date ON dbo.DailyLabourAttendance (LabourDate) INCLUDE (AreaId, PolyhouseId, AttendanceTime, MaleCount, FemaleCount);

    IF OBJECT_ID('dbo.DailyLabourAttendanceHistory', 'U') IS NULL
    BEGIN
        -- No FK to dbo.DailyLabourAttendance: the audit trail must outlive the row it describes.
        CREATE TABLE dbo.DailyLabourAttendanceHistory (
            Id                INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DailyLabourAttendanceHistory PRIMARY KEY,
            AttendanceId      INT           NOT NULL,
            Action            VARCHAR(10)   NOT NULL,
            LabourDate        DATE          NOT NULL,
            AreaId            INT           NOT NULL,
            PolyhouseId       INT           NULL,
            AttendanceTime        CHAR(4)       NOT NULL,
            OldMaleCount      INT           NULL,
            OldFemaleCount    INT           NULL,
            OldRemarks        NVARCHAR(500) NULL,
            NewMaleCount      INT           NULL,
            NewFemaleCount    INT           NULL,
            NewRemarks        NVARCHAR(500) NULL,
            ChangedById       INT           NULL,
            ChangedDate       DATETIME2     NOT NULL CONSTRAINT DF_DailyLabourAttendanceHistory_ChangedDate DEFAULT (SYSUTCDATETIME()),
            CONSTRAINT CK_DailyLabourAttendanceHistory_Action CHECK (Action IN ('Created', 'Corrected', 'Deleted'))
        );
        CREATE INDEX IX_DailyLabourAttendanceHistory_Attendance ON dbo.DailyLabourAttendanceHistory (AttendanceId, Id);
        CREATE INDEX IX_DailyLabourAttendanceHistory_Location ON dbo.DailyLabourAttendanceHistory (LabourDate, AreaId, PolyhouseId);
        PRINT '=== dbo.DailyLabourAttendanceHistory created ===';
    END
    ELSE PRINT '=== dbo.DailyLabourAttendanceHistory already exists -- no-op ===';

    IF OBJECT_ID('dbo.TR_DailyLabourAttendance_Location', 'TR') IS NULL
    BEGIN
        EXEC (N'
CREATE TRIGGER dbo.TR_DailyLabourAttendance_Location ON dbo.DailyLabourAttendance
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    -- Only new rows, or rows whose date/location changed, are checked (a past
    -- record stays correctable if its Polyhouse is later moved to another Area).
    ;WITH changed AS (
        SELECT i.Id, i.LabourDate, i.AreaId, i.PolyhouseId
        FROM inserted i
        LEFT JOIN deleted d ON d.Id = i.Id
        WHERE d.Id IS NULL
           OR d.LabourDate <> i.LabourDate
           OR d.AreaId <> i.AreaId
           OR ISNULL(d.PolyhouseId, -1) <> ISNULL(i.PolyhouseId, -1)
    )
    SELECT c.Id, c.LabourDate, c.AreaId, c.PolyhouseId, a.AreaType, p.AreaId AS PolyhouseAreaId
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
    IF EXISTS (SELECT 1 FROM #chk c
               WHERE EXISTS (SELECT 1 FROM dbo.DailyLabourCounts l
                             WHERE l.LabourDate = c.LabourDate AND l.AreaId = c.AreaId
                               AND ISNULL(l.PolyhouseId, -1) = ISNULL(c.PolyhouseId, -1)))
        THROW 51725, ''This date and location already has a manual labour record. Attendance cannot be added for it.'', 1;
END');
        PRINT '=== TR_DailyLabourAttendance_Location created ===';
    END

    IF OBJECT_ID('dbo.TR_DailyLabourAttendance_Audit', 'TR') IS NULL
    BEGIN
        EXEC (N'
CREATE TRIGGER dbo.TR_DailyLabourAttendance_Audit ON dbo.DailyLabourAttendance
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    -- Created: a new checkpoint row.
    INSERT INTO dbo.DailyLabourAttendanceHistory (AttendanceId, Action, LabourDate, AreaId, PolyhouseId, AttendanceTime,
        NewMaleCount, NewFemaleCount, NewRemarks, ChangedById)
    SELECT i.Id, ''Created'', i.LabourDate, i.AreaId, i.PolyhouseId, i.AttendanceTime,
           i.MaleCount, i.FemaleCount, i.Remarks, i.CreatedById
    FROM inserted i
    WHERE NOT EXISTS (SELECT 1 FROM deleted d WHERE d.Id = i.Id);

    -- Corrected: only when a count or the remark really changed.
    INSERT INTO dbo.DailyLabourAttendanceHistory (AttendanceId, Action, LabourDate, AreaId, PolyhouseId, AttendanceTime,
        OldMaleCount, OldFemaleCount, OldRemarks, NewMaleCount, NewFemaleCount, NewRemarks, ChangedById)
    SELECT i.Id, ''Corrected'', i.LabourDate, i.AreaId, i.PolyhouseId, i.AttendanceTime,
           d.MaleCount, d.FemaleCount, d.Remarks, i.MaleCount, i.FemaleCount, i.Remarks, i.ModifiedById
    FROM inserted i
    INNER JOIN deleted d ON d.Id = i.Id
    WHERE d.MaleCount <> i.MaleCount OR d.FemaleCount <> i.FemaleCount
       OR ISNULL(d.Remarks, N'''') <> ISNULL(i.Remarks, N'''');

    -- Deleted: the application never deletes attendance; recorded in case anyone does.
    INSERT INTO dbo.DailyLabourAttendanceHistory (AttendanceId, Action, LabourDate, AreaId, PolyhouseId, AttendanceTime,
        OldMaleCount, OldFemaleCount, OldRemarks)
    SELECT d.Id, ''Deleted'', d.LabourDate, d.AreaId, d.PolyhouseId, d.AttendanceTime, d.MaleCount, d.FemaleCount, d.Remarks
    FROM deleted d
    WHERE NOT EXISTS (SELECT 1 FROM inserted i WHERE i.Id = d.Id);
END');
        PRINT '=== TR_DailyLabourAttendance_Audit created ===';
    END

    IF OBJECT_ID('dbo.TR_DailyLabourAttendanceHistory_Protect', 'TR') IS NULL
    BEGIN
        EXEC (N'
CREATE TRIGGER dbo.TR_DailyLabourAttendanceHistory_Protect ON dbo.DailyLabourAttendanceHistory
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM inserted)
        THROW 51726, ''Daily Labour attendance history cannot be changed.'', 1;
    IF EXISTS (SELECT 1 FROM deleted d WHERE EXISTS (SELECT 1 FROM dbo.DailyLabourAttendance a WHERE a.Id = d.AttendanceId))
        THROW 51727, ''Daily Labour attendance history cannot be deleted while its attendance record exists.'', 1;
END');
        PRINT '=== TR_DailyLabourAttendanceHistory_Protect created ===';
    END

    PRINT '=== After: DailyLabourAttendance columns / indexes / triggers ===';
    SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'DailyLabourAttendance' ORDER BY ORDINAL_POSITION;
    SELECT name, is_unique, filter_definition FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.DailyLabourAttendance') AND name IS NOT NULL;
    SELECT name AS TriggerName FROM sys.triggers WHERE parent_id IN (OBJECT_ID('dbo.DailyLabourAttendance'), OBJECT_ID('dbo.DailyLabourAttendanceHistory')) ORDER BY name;

    PRINT '=== After: row counts (must equal Before) ===';
    SELECT 'DailyLabourCounts' AS TableName, COUNT(*) AS Rows FROM dbo.DailyLabourCounts
    UNION ALL SELECT 'LabourLogs', COUNT(*) FROM dbo.LabourLogs
    UNION ALL SELECT 'Area', COUNT(*) FROM dbo.Area
    UNION ALL SELECT 'Polyhouses', COUNT(*) FROM dbo.Polyhouses
    UNION ALL SELECT 'RolePermissions', COUNT(*) FROM dbo.RolePermissions
    UNION ALL SELECT 'DailyLabourAttendance', COUNT(*) FROM dbo.DailyLabourAttendance
    UNION ALL SELECT 'DailyLabourAttendanceHistory', COUNT(*) FROM dbo.DailyLabourAttendanceHistory;

END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION DailyLabourAttendance2026_10_06;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION DailyLabourAttendance2026_10_06;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION DailyLabourAttendance2026_10_06;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
