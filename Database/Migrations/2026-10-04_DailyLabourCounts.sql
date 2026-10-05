-- ============================================================================
-- 2026-10-04_DailyLabourCounts.sql
--
-- WHY: new feature -- supervisors record DAILY LABOUR HEADCOUNTS (male/female,
-- full/half day) per location. No employee names, worker ids, wages or
-- individual worker rows -- the location + date IS the record's identity.
-- The old dbo.LabourLogs (Phase 13) is per-worker with wages (WorkerId NOT
-- NULL, WageRate > 0) and is NOT suitable; it is left completely untouched.
--
-- LOCATION RULE (same classification the app already uses everywhere,
-- DirectSowingRules.MainOfficeAreaType):
--   Area.AreaType = 'MainOffice'  -> one record per Date + Area + Polyhouse
--                                    (PolyhouseId required, must belong to
--                                    that Area through dbo.Polyhouses.AreaId)
--   any other Area                -> one record per Date + Area
--                                    (PolyhouseId must be NULL)
--
-- WHAT THIS DOES:
--   dbo.DailyLabourCounts             new table, four INT counts 0..500
--   UX_DailyLabourCounts_Polyhouse    filtered unique (Date, Area, Polyhouse) WHERE PolyhouseId IS NOT NULL
--   UX_DailyLabourCounts_Area         filtered unique (Date, Area)            WHERE PolyhouseId IS NULL
--   TR_DailyLabourCounts_Location     rejects an Outlet Area and an invalid Area/Polyhouse combination
--                                     (checked on insert, and on update only when
--                                     LabourDate/AreaId/PolyhouseId change -- so a
--                                     past record stays editable if its Polyhouse
--                                     is later moved to another Area)
--   dbo.RolePermissions               Labour.View + Labour.Enter (both already exist,
--                                     Ids seeded by PhaseA_RoleBasedAccess.sql) for
--                                     Sowing Supervisor and Mother Plant Supervisor only
--                                     -- only if missing. Outlet is excluded from Daily
--                                     Labour (approved 2026-10-04): Outlet Sales gets no
--                                     Labour permission.
--   Remarks                           optional NVARCHAR(500) per record.
--
-- Databases that already ran an EARLIER version of this script (with an
-- Outlet Sales grant and no Remarks) are brought to the same final state by
-- 2026-10-04_DailyLabourCounts_RemarksNoOutlet.sql.
--
-- NOT changed: dbo.LabourLogs, any other existing table/column/constraint/
-- trigger/row, any other role, any user role assignment.
--
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy. One
-- transaction. Run with sqlcmd -v MODE=DRYRUN (rolls back) or MODE=COMMIT.
-- Idempotent. Requires QUOTED_IDENTIFIER ON (filtered indexes) -- sqlcmd -I.
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 51701, '2026-10-04_DailyLabourCounts.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 51702, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
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

BEGIN TRANSACTION DailyLabour2026_10_04;

BEGIN TRY

    PRINT '=== Before: row counts of tables this script must NOT change ===';
    SELECT 'LabourLogs' AS TableName, COUNT(*) AS Rows FROM dbo.LabourLogs
    UNION ALL SELECT 'Area', COUNT(*) FROM dbo.Area
    UNION ALL SELECT 'Polyhouses', COUNT(*) FROM dbo.Polyhouses
    UNION ALL SELECT 'UserRoles', COUNT(*) FROM dbo.UserRoles
    UNION ALL SELECT 'RolePermissions', COUNT(*) FROM dbo.RolePermissions;

    IF NOT EXISTS (SELECT 1 FROM dbo.Permissions WHERE Code = N'Labour.View')
       OR NOT EXISTS (SELECT 1 FROM dbo.Permissions WHERE Code = N'Labour.Enter')
        THROW 51703, 'Permissions Labour.View / Labour.Enter are missing -- run PhaseA_RoleBasedAccess.sql first.', 1;

    IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'DailyLabourCounts')
    BEGIN
        CREATE TABLE dbo.DailyLabourCounts (
            Id            INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DailyLabourCounts PRIMARY KEY,
            LabourDate    DATE      NOT NULL,
            AreaId        INT       NOT NULL,
            PolyhouseId   INT       NULL,
            MaleFullDay   INT       NOT NULL,
            MaleHalfDay   INT       NOT NULL,
            FemaleFullDay INT       NOT NULL,
            FemaleHalfDay INT       NOT NULL,
            Remarks       NVARCHAR(500) NULL,
            CreatedDate   DATETIME2 NOT NULL CONSTRAINT DF_DailyLabourCounts_CreatedDate DEFAULT (SYSUTCDATETIME()),
            CreatedById   INT       NULL,
            ModifiedDate  DATETIME2 NULL,
            ModifiedById  INT       NULL,
            CONSTRAINT FK_DailyLabourCounts_Area       FOREIGN KEY (AreaId)       REFERENCES dbo.Area(Id),
            CONSTRAINT FK_DailyLabourCounts_Polyhouse  FOREIGN KEY (PolyhouseId)  REFERENCES dbo.Polyhouses(Id),
            CONSTRAINT FK_DailyLabourCounts_CreatedBy  FOREIGN KEY (CreatedById)  REFERENCES dbo.IMSUsers(Id),
            CONSTRAINT FK_DailyLabourCounts_ModifiedBy FOREIGN KEY (ModifiedById) REFERENCES dbo.IMSUsers(Id),
            CONSTRAINT CK_DailyLabourCounts_MaleFullDay   CHECK (MaleFullDay   BETWEEN 0 AND 500),
            CONSTRAINT CK_DailyLabourCounts_MaleHalfDay   CHECK (MaleHalfDay   BETWEEN 0 AND 500),
            CONSTRAINT CK_DailyLabourCounts_FemaleFullDay CHECK (FemaleFullDay BETWEEN 0 AND 500),
            CONSTRAINT CK_DailyLabourCounts_FemaleHalfDay CHECK (FemaleHalfDay BETWEEN 0 AND 500)
        );
        PRINT '=== dbo.DailyLabourCounts created ===';
    END
    ELSE
        PRINT '=== dbo.DailyLabourCounts already exists -- no-op ===';

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_DailyLabourCounts_Polyhouse' AND object_id = OBJECT_ID('dbo.DailyLabourCounts'))
        CREATE UNIQUE INDEX UX_DailyLabourCounts_Polyhouse ON dbo.DailyLabourCounts (LabourDate, AreaId, PolyhouseId) WHERE PolyhouseId IS NOT NULL;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_DailyLabourCounts_Area' AND object_id = OBJECT_ID('dbo.DailyLabourCounts'))
        CREATE UNIQUE INDEX UX_DailyLabourCounts_Area ON dbo.DailyLabourCounts (LabourDate, AreaId) WHERE PolyhouseId IS NULL;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_DailyLabourCounts_Date' AND object_id = OBJECT_ID('dbo.DailyLabourCounts'))
        CREATE INDEX IX_DailyLabourCounts_Date ON dbo.DailyLabourCounts (LabourDate) INCLUDE (AreaId, PolyhouseId);

    IF OBJECT_ID('dbo.TR_DailyLabourCounts_Location', 'TR') IS NULL
    BEGIN
        EXEC (N'
CREATE TRIGGER dbo.TR_DailyLabourCounts_Location ON dbo.DailyLabourCounts
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    -- Only new rows, or rows whose date/location changed, are checked.
    ;WITH changed AS (
        SELECT i.Id, i.AreaId, i.PolyhouseId
        FROM inserted i
        LEFT JOIN deleted d ON d.Id = i.Id
        WHERE d.Id IS NULL
           OR d.LabourDate <> i.LabourDate
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
        PRINT '=== TR_DailyLabourCounts_Location created ===';
    END

    PRINT '=== Role grants (only missing rows are inserted) ===';
    INSERT INTO dbo.RolePermissions (RoleId, PermissionId)
    SELECT r.Id, p.Id
    FROM dbo.Roles r
    CROSS JOIN dbo.Permissions p
    WHERE COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) IN (N'Sowing Supervisor', N'Mother Plant Supervisor')
      AND p.Code IN (N'Labour.View', N'Labour.Enter')
      AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.RoleId = r.Id AND rp.PermissionId = p.Id);
    PRINT CONCAT('RolePermissions rows inserted: ', @@ROWCOUNT, ' (expect 4 on first run, 0 on re-run)');

    PRINT '=== After: Labour grants ===';
    SELECT COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) AS RoleName, p.Code
    FROM dbo.RolePermissions rp
    JOIN dbo.Roles r ON r.Id = rp.RoleId
    JOIN dbo.Permissions p ON p.Id = rp.PermissionId
    WHERE p.Code LIKE N'Labour.%'
    ORDER BY RoleName, p.Code;

    PRINT '=== After: DailyLabourCounts columns / indexes ===';
    SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'DailyLabourCounts' ORDER BY ORDINAL_POSITION;
    SELECT name, is_unique, filter_definition FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.DailyLabourCounts') AND name IS NOT NULL;

    PRINT '=== After: row counts (only RolePermissions may differ, by the grants above) ===';
    SELECT 'LabourLogs' AS TableName, COUNT(*) AS Rows FROM dbo.LabourLogs
    UNION ALL SELECT 'Area', COUNT(*) FROM dbo.Area
    UNION ALL SELECT 'Polyhouses', COUNT(*) FROM dbo.Polyhouses
    UNION ALL SELECT 'UserRoles', COUNT(*) FROM dbo.UserRoles
    UNION ALL SELECT 'RolePermissions', COUNT(*) FROM dbo.RolePermissions
    UNION ALL SELECT 'DailyLabourCounts', COUNT(*) FROM dbo.DailyLabourCounts;

END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION DailyLabour2026_10_04;
    THROW;
END CATCH;

----------------------------------------------------------------------------
-- Approved 2026-10-04 (user: "go ahead") for PlantsIMS2_Test and scratch copies.
-- Verified backup: PlantsIMS2_Test_PreDailyLabourCounts_20261004_124236.bak
----------------------------------------------------------------------------
IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION DailyLabour2026_10_04;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION DailyLabour2026_10_04;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
