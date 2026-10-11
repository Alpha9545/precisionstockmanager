-- ============================================================================
-- 2026-10-11_TrayAccessByLocation.sql
--
-- WHY (owner decisions 2026-10-10): tray visibility and tray receiving follow the LOCATION.
--   TrayStock.ViewMotherPlant          -> Mother Plant Supervisor: READ-ONLY empty-tray balances and tray
--                                         history of the supervisor's OWN Mother Plant Areas
--                                         (Authorization/TrayAreaScope.cs). No add / adjust / transfer /
--                                         cocopeat / pitmos / administration.
--   TrayStock.ReceiveTransferMainArea  -> Sowing Supervisor: receive / reject tray transfers into MAIN
--                                         OFFICE Areas it is assigned to -- the Main Area tray role (it
--                                         already adds and adjusts trays there). Mother Plant Areas keep
--                                         TrayStock.ReceiveTransfer (Mother Plant Supervisor, 2026-10-10).
--                                         Which permission applies is decided by the destination Area's
--                                         type (Services/TrayTransferRules.ReceivePermissionFor); no
--                                         receiver gets cross-Area access.
-- ADDITIVE ONLY: no grant is removed, no stock or ledger row changes. Every grant added is logged in
-- dbo.RolePermissionChangeLog (created here if missing). Requires 2026-10-10_TrayTransfers.sql.
--
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to the server named with
-- -v TARGET_SERVER=<@@SERVERNAME>. One transaction. Idempotent (re-run = no-op).
-- Undo: 2026-10-11_TrayAccessByLocation_Rollback.sql (refuses while a tray transfer to a Main Office
-- Area is still pending).
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52141, '2026-10-11_TrayAccessByLocation.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52142, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52143, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
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

BEGIN TRANSACTION TrayAccess1011;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';
    IF OBJECT_ID('dbo.TrayTransfers', 'U') IS NULL
        THROW 52144, 'Run 2026-10-10_TrayTransfers.sql first.', 1;
    DECLARE @Missing NVARCHAR(100);
    DECLARE @Roles TABLE (Name NVARCHAR(100));
    INSERT INTO @Roles VALUES (N'Mother Plant Supervisor'), (N'Sowing Supervisor');
    SELECT @Missing = r.Name FROM @Roles r
    WHERE NOT EXISTS (SELECT 1 FROM dbo.Roles x WHERE COALESCE(NULLIF(LTRIM(RTRIM(x.Name)), N''), x.RoleName) = r.Name);
    IF @Missing IS NOT NULL
        THROW 52145, 'A required role (Mother Plant Supervisor / Sowing Supervisor) is missing.', 1;

    PRINT '=== Before: rows that must not change ===';
    SELECT 'RolePermissions' AS T, COUNT(*) AS Rows FROM dbo.RolePermissions
    UNION ALL SELECT 'TrayStock', COUNT(*) FROM dbo.TrayStock
    UNION ALL SELECT 'TrayStockTransactions', COUNT(*) FROM dbo.TrayStockTransactions;

    IF OBJECT_ID('dbo.RolePermissionChangeLog', 'U') IS NULL
    BEGIN
        EXEC (N'CREATE TABLE dbo.RolePermissionChangeLog (
            Id          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_RolePermissionChangeLog PRIMARY KEY,
            ChangedAt   DATETIME2     NOT NULL CONSTRAINT DF_RolePermissionChangeLog_ChangedAt DEFAULT SYSUTCDATETIME(),
            RoleId      INT           NOT NULL,
            RoleName    NVARCHAR(100) NOT NULL,
            PermissionCode NVARCHAR(200) NOT NULL,
            Change      NVARCHAR(10)  NOT NULL CONSTRAINT CK_RolePermissionChangeLog_Change CHECK (Change IN (N''Added'', N''Removed'')),
            Script      NVARCHAR(200) NOT NULL,
            Reason      NVARCHAR(500) NOT NULL);');
        PRINT '=== dbo.RolePermissionChangeLog created ===';
    END

    DECLARE @Perms TABLE (Code NVARCHAR(200) PRIMARY KEY, Description NVARCHAR(400), SortOrder INT);
    INSERT INTO @Perms VALUES
        (N'TrayStock.ViewMotherPlant',         N'View empty-tray balances and tray history of own Mother Plant Areas (read-only)', 1),
        (N'TrayStock.ReceiveTransferMainArea', N'Receive or reject empty trays sent to own Main Office Areas', 2);
    DECLARE @Code NVARCHAR(200), @PermDescription NVARCHAR(400);
    DECLARE perm_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT Code, Description FROM @Perms ORDER BY SortOrder;
    OPEN perm_cursor;
    FETCH NEXT FROM perm_cursor INTO @Code, @PermDescription;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM dbo.Permissions WHERE Code = @Code)
        BEGIN
            IF COLUMNPROPERTY(OBJECT_ID('dbo.Permissions'), 'Id', 'IsIdentity') = 1
                INSERT INTO dbo.Permissions (Code, Description) VALUES (@Code, @PermDescription);
            ELSE
                INSERT INTO dbo.Permissions (Id, Code, Description) SELECT ISNULL(MAX(Id), 0) + 1, @Code, @PermDescription FROM dbo.Permissions;
            PRINT CONCAT('=== Permission ', @Code, ' added ===');
        END
        ELSE PRINT CONCAT('=== Permission ', @Code, ' already exists -- no-op ===');
        FETCH NEXT FROM perm_cursor INTO @Code, @PermDescription;
    END
    CLOSE perm_cursor;
    DEALLOCATE perm_cursor;

    DECLARE @Grants TABLE (RoleName NVARCHAR(100), Code NVARCHAR(200), Reason NVARCHAR(500));
    INSERT INTO @Grants VALUES
        (N'Mother Plant Supervisor', N'TrayStock.ViewMotherPlant', N'Read-only trays of own Mother Plant Areas before transplanting (owner decision 2026-10-10)'),
        (N'Sowing Supervisor', N'TrayStock.ReceiveTransferMainArea', N'Main Area tray role receives transfers into own Main Office Areas (owner decision 2026-10-10)');
    INSERT INTO dbo.RolePermissionChangeLog (RoleId, RoleName, PermissionCode, Change, Script, Reason)
    SELECT r.Id, g.RoleName, g.Code, N'Added', N'2026-10-11_TrayAccessByLocation.sql', g.Reason
    FROM @Grants g
    JOIN dbo.Roles r ON COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = g.RoleName
    JOIN dbo.Permissions p ON p.Code = g.Code
    WHERE NOT EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.RoleId = r.Id AND rp.PermissionId = p.Id);
    INSERT INTO dbo.RolePermissions (RoleId, PermissionId)
    SELECT r.Id, p.Id
    FROM @Grants g
    JOIN dbo.Roles r ON COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = g.RoleName
    JOIN dbo.Permissions p ON p.Code = g.Code
    WHERE NOT EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.RoleId = r.Id AND rp.PermissionId = p.Id);
    PRINT CONCAT('Grants inserted: ', @@ROWCOUNT, ' (expect 2 on first run, 0 on re-run)');

    PRINT '=== After ===';
    SELECT 'RolePermissions' AS T, COUNT(*) AS Rows FROM dbo.RolePermissions
    UNION ALL SELECT 'TrayStock', COUNT(*) FROM dbo.TrayStock
    UNION ALL SELECT 'TrayStockTransactions', COUNT(*) FROM dbo.TrayStockTransactions;
    SELECT COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) AS RoleName, p.Code
    FROM dbo.RolePermissions rp JOIN dbo.Roles r ON r.Id = rp.RoleId JOIN dbo.Permissions p ON p.Id = rp.PermissionId
    WHERE p.Code IN (SELECT Code FROM @Perms) ORDER BY RoleName, p.Code;
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION TrayAccess1011;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION TrayAccess1011;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION TrayAccess1011;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
