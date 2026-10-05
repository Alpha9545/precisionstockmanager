-- ============================================================================
-- 2026-10-04_DailyLabourCounts_RemarksNoOutlet.sql
--
-- WHY: approved clarifications to the Daily Labour Count module, for
-- databases that ALREADY ran 2026-10-04_DailyLabourCounts.sql (PlantsIMS2_Test
-- and its scratch copies). A fresh database gets the same final state from
-- the (updated) first script alone -- on it, this script is a no-op.
--   1. Optional Remark per daily record.
--   2. Outlet is excluded from Daily Labour: Outlet Sales loses the
--      Labour.View / Labour.Enter grants, and the location trigger rejects
--      any Outlet Area (AreaType = 'Outlet', OutletRules.AreaType).
--
-- WHAT THIS DOES:
--   dbo.DailyLabourCounts.Remarks       NVARCHAR(500) NULL (only if missing)
--   dbo.RolePermissions                 deletes ONLY (Outlet Sales, Labour.View)
--                                       and (Outlet Sales, Labour.Enter), if present
--   TR_DailyLabourCounts_Location       + rejects Outlet Areas (CREATE OR ALTER)
--
-- REFUSES TO RUN if any dbo.DailyLabourCounts row belongs to an Outlet Area
-- (nothing is deleted from the labour data, ever).
--
-- NOT changed: dbo.LabourLogs, any existing labour row, any other role,
-- permission, table, column, constraint or trigger.
--
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy. One
-- transaction. Run with sqlcmd -I -v MODE=DRYRUN (rolls back) or MODE=COMMIT.
-- Idempotent.
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 51721, '2026-10-04_DailyLabourCounts_RemarksNoOutlet.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 51722, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF OBJECT_ID('dbo.DailyLabourCounts', 'U') IS NULL
    THROW 51723, 'dbo.DailyLabourCounts does not exist -- run 2026-10-04_DailyLabourCounts.sql first.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION DailyLabourFix2026_10_04;

BEGIN TRY

    PRINT '=== Before: Labour grants ===';
    SELECT COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) AS RoleName, p.Code
    FROM dbo.RolePermissions rp
    JOIN dbo.Roles r ON r.Id = rp.RoleId
    JOIN dbo.Permissions p ON p.Id = rp.PermissionId
    WHERE p.Code LIKE N'Labour.%'
    ORDER BY RoleName, p.Code;

    PRINT '=== Before: row counts ===';
    SELECT 'DailyLabourCounts' AS TableName, COUNT(*) AS Rows FROM dbo.DailyLabourCounts
    UNION ALL SELECT 'LabourLogs', COUNT(*) FROM dbo.LabourLogs
    UNION ALL SELECT 'RolePermissions', COUNT(*) FROM dbo.RolePermissions
    UNION ALL SELECT 'UserRoles', COUNT(*) FROM dbo.UserRoles;

    IF EXISTS (SELECT 1 FROM dbo.DailyLabourCounts l INNER JOIN dbo.Area a ON a.Id = l.AreaId WHERE a.AreaType = N'Outlet')
        THROW 51724, 'Labour records exist for an Outlet Area -- review them before excluding Outlet. Nothing was changed.', 1;

    IF COL_LENGTH('dbo.DailyLabourCounts', 'Remarks') IS NULL
    BEGIN
        ALTER TABLE dbo.DailyLabourCounts ADD Remarks NVARCHAR(500) NULL;
        PRINT '=== Remarks column added ===';
    END
    ELSE
        PRINT '=== Remarks column already exists -- no-op ===';

    DELETE rp
    FROM dbo.RolePermissions rp
    JOIN dbo.Roles r ON r.Id = rp.RoleId
    JOIN dbo.Permissions p ON p.Id = rp.PermissionId
    WHERE COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = N'Outlet Sales'
      AND p.Code IN (N'Labour.View', N'Labour.Enter');
    PRINT CONCAT('Outlet Sales Labour grants removed: ', @@ROWCOUNT, ' (expect 2 on first run, 0 on re-run)');

    EXEC (N'
CREATE OR ALTER TRIGGER dbo.TR_DailyLabourCounts_Location ON dbo.DailyLabourCounts
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
    PRINT '=== TR_DailyLabourCounts_Location updated (Outlet rejected) ===';

    PRINT '=== After: Labour grants (expect Sowing Supervisor + Mother Plant Supervisor only) ===';
    SELECT COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) AS RoleName, p.Code
    FROM dbo.RolePermissions rp
    JOIN dbo.Roles r ON r.Id = rp.RoleId
    JOIN dbo.Permissions p ON p.Id = rp.PermissionId
    WHERE p.Code LIKE N'Labour.%'
    ORDER BY RoleName, p.Code;

    PRINT '=== After: DailyLabourCounts columns ===';
    SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'DailyLabourCounts' ORDER BY ORDINAL_POSITION;

    PRINT '=== After: row counts (DailyLabourCounts/LabourLogs/UserRoles unchanged; RolePermissions -2 on first run) ===';
    SELECT 'DailyLabourCounts' AS TableName, COUNT(*) AS Rows FROM dbo.DailyLabourCounts
    UNION ALL SELECT 'LabourLogs', COUNT(*) FROM dbo.LabourLogs
    UNION ALL SELECT 'RolePermissions', COUNT(*) FROM dbo.RolePermissions
    UNION ALL SELECT 'UserRoles', COUNT(*) FROM dbo.UserRoles;

END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION DailyLabourFix2026_10_04;
    THROW;
END CATCH;

----------------------------------------------------------------------------
-- Approved 2026-10-04 (user: "Apply the Daily Labour changes exactly as proposed").
-- Verified backup: PlantsIMS2_Test_PreDailyLabourRemarksNoOutlet_20261004_140247.bak
----------------------------------------------------------------------------
IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION DailyLabourFix2026_10_04;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION DailyLabourFix2026_10_04;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
