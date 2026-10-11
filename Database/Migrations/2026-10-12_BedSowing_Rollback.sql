-- ============================================================================
-- 2026-10-12_BedSowing_Rollback.sql -- undoes 2026-10-12_BedSowing.sql.
--
-- REFUSES once any bed sowing exists (dbo.BedSowings has rows, or a dbo.SeedSowings row has
-- SourceType 'BedSowing'): removing the tables would orphan seed, tray, material and Ready Stock
-- history. Restores CK_SeedSowings_Source to its exact earlier definition, removes the role
-- assignment, the grant (logged in dbo.RolePermissionChangeLog), the role and the permission.
-- Also removes the review additions: the guard triggers (the two on dbo.BedSowingOperations go with
-- that table), dbo.BedSowingAuditLog and
-- dbo.MaterialTransactions.TransactionDate (refused while any material movement carries a business date).
--
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to the server named with
-- -v TARGET_SERVER=<@@SERVERNAME>. One transaction. Idempotent (re-run = no-op).
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52181, '2026-10-12_BedSowing_Rollback.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52182, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52183, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
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

BEGIN TRANSACTION BedSowingRb1012;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';

    IF OBJECT_ID('dbo.BedSowings', 'U') IS NOT NULL
    BEGIN
        DECLARE @Beds INT;
        EXEC sp_executesql N'SELECT @n = COUNT(*) FROM dbo.BedSowings', N'@n INT OUTPUT', @n = @Beds OUTPUT;
        IF @Beds > 0
            THROW 52184, 'Bed sowings exist -- rollback refused (it would orphan seed, tray, material and Ready Stock history).', 1;
    END
    IF EXISTS (SELECT 1 FROM dbo.SeedSowings WHERE SourceType = N'BedSowing')
        THROW 52185, 'Tray batches from bed sowings exist -- rollback refused.', 1;
    IF COL_LENGTH('dbo.MaterialTransactions', 'TransactionDate') IS NOT NULL
    BEGIN
        DECLARE @Dated INT;
        EXEC sp_executesql N'SELECT @n = COUNT(*) FROM dbo.MaterialTransactions WHERE TransactionDate IS NOT NULL', N'@n INT OUTPUT', @n = @Dated OUTPUT;
        IF @Dated > 0
            THROW 52186, 'Material movements carry business dates -- rollback refused (they would be lost).', 1;
    END

    -- The guards first, so the role / permission removal below is not refused.
    IF OBJECT_ID('dbo.TR_UserRoles_BedTransplanter', 'TR') IS NOT NULL DROP TRIGGER dbo.TR_UserRoles_BedTransplanter;
    IF OBJECT_ID('dbo.TR_RolePermissions_BedTransplant', 'TR') IS NOT NULL DROP TRIGGER dbo.TR_RolePermissions_BedTransplant;
    IF OBJECT_ID('dbo.TR_ReadyConfirmations_BedReadyDate', 'TR') IS NOT NULL DROP TRIGGER dbo.TR_ReadyConfirmations_BedReadyDate;

    -- Role assignment, grant (logged), role, permission.
    DECLARE @RoleName NVARCHAR(100) = N'Bed Sowing Transplanter';
    DECLARE @RoleId INT = (SELECT Id FROM dbo.Roles WHERE COALESCE(NULLIF(LTRIM(RTRIM(Name)), N''), RoleName) = @RoleName);
    DECLARE @PermissionId INT = (SELECT Id FROM dbo.Permissions WHERE Code = N'BedSowing.Transplant');
    IF @RoleId IS NOT NULL
    BEGIN
        DELETE FROM dbo.UserRoles WHERE RoleId = @RoleId;
        PRINT CONCAT('User role assignments removed: ', @@ROWCOUNT);
    END
    IF @PermissionId IS NOT NULL
    BEGIN
        IF OBJECT_ID('dbo.RolePermissionChangeLog', 'U') IS NOT NULL
            INSERT INTO dbo.RolePermissionChangeLog (RoleId, RoleName, PermissionCode, Change, Script, Reason)
            SELECT rp.RoleId, COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName), N'BedSowing.Transplant', N'Removed',
                   N'2026-10-12_BedSowing_Rollback.sql', N'Rollback of 2026-10-12_BedSowing.sql'
            FROM dbo.RolePermissions rp JOIN dbo.Roles r ON r.Id = rp.RoleId WHERE rp.PermissionId = @PermissionId;
        DELETE FROM dbo.RolePermissions WHERE PermissionId = @PermissionId;
        PRINT CONCAT('Grants removed: ', @@ROWCOUNT);
        DELETE FROM dbo.Permissions WHERE Id = @PermissionId;
    END
    IF @RoleId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions WHERE RoleId = @RoleId)
        DELETE FROM dbo.Roles WHERE Id = @RoleId;

    -- dbo.SeedSowings back to Seed / Cutting only.
    IF OBJECT_ID('dbo.TR_SeedSowings_ImmutableBedLink', 'TR') IS NOT NULL
        DROP TRIGGER dbo.TR_SeedSowings_ImmutableBedLink;
    IF COL_LENGTH('dbo.SeedSowings', 'BedSowingId') IS NOT NULL
    BEGIN
        IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.SeedSowings') AND name = N'IX_SeedSowings_BedSowingId')
            DROP INDEX IX_SeedSowings_BedSowingId ON dbo.SeedSowings;
        IF OBJECT_ID('dbo.CK_SeedSowings_BedLink', 'C') IS NOT NULL
            ALTER TABLE dbo.SeedSowings DROP CONSTRAINT CK_SeedSowings_BedLink;
        IF OBJECT_ID('dbo.FK_SeedSowings_BedSowing', 'F') IS NOT NULL
            ALTER TABLE dbo.SeedSowings DROP CONSTRAINT FK_SeedSowings_BedSowing;
        ALTER TABLE dbo.SeedSowings DROP CONSTRAINT CK_SeedSowings_Source;
        EXEC (N'ALTER TABLE dbo.SeedSowings WITH CHECK ADD CONSTRAINT CK_SeedSowings_Source CHECK (
            [SourceType]=N''Seed'' AND [SourceSeedStockId] IS NOT NULL AND [SourceCuttingStockId] IS NULL
            OR [SourceType]=N''Cutting'' AND [SourceCuttingStockId] IS NOT NULL AND [SourceSeedStockId] IS NULL);');
        EXEC (N'ALTER TABLE dbo.SeedSowings DROP COLUMN BedSowingId;');
        PRINT '=== dbo.SeedSowings.BedSowingId removed; CK_SeedSowings_Source restored ===';
    END

    IF OBJECT_ID('dbo.BedSowingOperations', 'U') IS NOT NULL DROP TABLE dbo.BedSowingOperations;   -- drops its trigger
    IF OBJECT_ID('dbo.BedSowings', 'U') IS NOT NULL DROP TABLE dbo.BedSowings;                     -- drops its trigger
    IF OBJECT_ID('dbo.BedSowingLocations', 'U') IS NOT NULL DROP TABLE dbo.BedSowingLocations;
    IF OBJECT_ID('dbo.BedSowingAuditLog', 'U') IS NOT NULL DROP TABLE dbo.BedSowingAuditLog;                     -- drops its trigger
    IF COL_LENGTH('dbo.MaterialTransactions', 'TransactionDate') IS NOT NULL
        EXEC (N'ALTER TABLE dbo.MaterialTransactions DROP COLUMN TransactionDate;');
    DELETE FROM dbo.BatchNumberSequences WHERE Prefix IN (N'BS', N'BT') AND LastNumber = 0;
    PRINT '=== Bed Sowing tables removed ===';

    SELECT definition AS CK_SeedSowings_Source FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID('dbo.SeedSowings') AND name = N'CK_SeedSowings_Source';
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION BedSowingRb1012;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION BedSowingRb1012;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION BedSowingRb1012;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
