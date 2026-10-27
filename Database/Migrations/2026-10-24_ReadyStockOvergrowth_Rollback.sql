-- ============================================================================
-- 2026-10-24_ReadyStockOvergrowth_Rollback.sql   *** PREPARED FOR REVIEW -- NOT APPLIED ***
--
-- Undoes 2026-10-24_ReadyStockOvergrowth.sql:
--   1. removes ReadyStock.Overgrowth from Sowing Supervisor / Mother Plant Supervisor -- only where the latest change for
--      that role + code is the migration's own 'Added' row (logged 'Removed' in dbo.RolePermissionChangeLog);
--   2. deletes the permission code only if the migration created it and no role holds it any more;
--   3. drops TR_ReadyStockOvergrowthLog_AppendOnly, dbo.ReadyStockOvergrowthLog, dbo.SeedlingDispatchLines.IsOvergrowth and
--      the dbo.ReadyStock Overgrowth columns / constraints / index.
--
-- DATA LOSS GUARD: step 3 discards every Overgrowth marker, its history and the dispatch snapshots. If ANY batch is marked,
--   any history row exists or any dispatch line was recorded as Overgrowth, the script refuses (nothing changed) unless run
--   with -v DISCARD_OVERGROWTH=YES -- exactly that, upper case (compared case-sensitively: "yes" / "Yes" are refused, so
--   a casual value can never discard data). MODE is compared case-sensitively too. The counts are printed either way. Stock quantities, bookings and dispatches themselves
--   are never touched.
--
-- Same TARGET SAFEGUARDS and MODE=DRYRUN / COMMIT as the migration:
--   sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN DISCARD_OVERGROWTH=NO TARGET_SERVER="<ServerName>"
--          TARGET_MACHINE="<MachineName>" TARGET_DB="<DatabaseName>" TARGET_DB_CREATED="<DatabaseCreated>"
--          -i 2026-10-24_ReadyStockOvergrowth_Rollback.sql
-- One transaction. Idempotent (re-run = no-op).
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52651, '2026-10-24_ReadyStockOvergrowth_Rollback.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' COLLATE Latin1_General_BIN2 NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52652, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT (exact upper case).', 1;
IF N'$(DISCARD_OVERGROWTH)' COLLATE Latin1_General_BIN2 NOT IN (N'YES', N'NO')
    THROW 52653, 'Run with sqlcmd -v DISCARD_OVERGROWTH=NO (refuse if any Overgrowth data exists) or YES (discard it) -- exact upper case; any other value is refused.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52654, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
IF CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) <> @@SERVERNAME
    THROW 52655, 'SERVERPROPERTY(''ServerName'') does not match @@SERVERNAME (renamed host?) -- refusing to run.', 1;
IF CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) <> N'$(TARGET_MACHINE)'
    THROW 52656, 'TARGET_MACHINE does not match SERVERPROPERTY(''MachineName'') -- refusing to run on an unexpected host.', 1;
IF DB_NAME() <> N'$(TARGET_DB)'
    THROW 52657, 'TARGET_DB does not match DB_NAME() -- refusing to run against an unexpected database.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND CONVERT(CHAR(19), create_date, 126) = N'$(TARGET_DB_CREATED)')
    THROW 52658, 'TARGET_DB_CREATED does not match this database''s create_date -- refusing to run against an unexpected copy.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION RsOvergrowth1024Rollback;

BEGIN TRY
    PRINT '=== Server / machine / database: ' + @@SERVERNAME + ' / ' + CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128))
        + ' / ' + DB_NAME() + ' -- MODE=$(MODE), DISCARD_OVERGROWTH=$(DISCARD_OVERGROWTH) ===';

    -- ---- data-loss guard -------------------------------------------------------------------------------------------
    DECLARE @Marked INT = 0, @LogRows INT = 0, @OgLines INT = 0;
    IF COL_LENGTH('dbo.ReadyStock', 'IsOvergrowth') IS NOT NULL
        EXEC sp_executesql N'SELECT @n = COUNT(*) FROM dbo.ReadyStock WITH (UPDLOCK, HOLDLOCK) WHERE IsOvergrowth = 1', N'@n INT OUTPUT', @n = @Marked OUTPUT;
    IF OBJECT_ID('dbo.ReadyStockOvergrowthLog', 'U') IS NOT NULL
        EXEC sp_executesql N'SELECT @n = COUNT(*) FROM dbo.ReadyStockOvergrowthLog WITH (UPDLOCK, HOLDLOCK)', N'@n INT OUTPUT', @n = @LogRows OUTPUT;
    IF COL_LENGTH('dbo.SeedlingDispatchLines', 'IsOvergrowth') IS NOT NULL
        EXEC sp_executesql N'SELECT @n = COUNT(*) FROM dbo.SeedlingDispatchLines WITH (UPDLOCK, HOLDLOCK) WHERE IsOvergrowth = 1', N'@n INT OUTPUT', @n = @OgLines OUTPUT;
    PRINT '=== Overgrowth data that would be discarded: marked batches ' + CAST(@Marked AS NVARCHAR(20))
        + ', history rows ' + CAST(@LogRows AS NVARCHAR(20)) + ', dispatch lines recorded as Overgrowth ' + CAST(@OgLines AS NVARCHAR(20)) + ' ===';
    IF (@Marked + @LogRows + @OgLines) > 0 AND N'$(DISCARD_OVERGROWTH)' COLLATE Latin1_General_BIN2 <> N'YES'
        THROW 52659, 'Overgrowth data exists -- refusing to discard it. Re-run with -v DISCARD_OVERGROWTH=YES only if that is intended. Nothing changed.', 1;

    -- ---- 1 + 2. permission ---------------------------------------------------------------------------------------------
    DECLARE @Code NVARCHAR(100) = N'ReadyStock.Overgrowth';
    DECLARE @Script NVARCHAR(200) = N'2026-10-24_ReadyStockOvergrowth.sql';
    DECLARE @RollbackScript NVARCHAR(200) = N'2026-10-24_ReadyStockOvergrowth_Rollback.sql';
    DECLARE @OwnedDescription NVARCHAR(200) =
        N'Mark / unmark Ready Stock batches as Overgrowth (own Area) [created by 2026-10-24_ReadyStockOvergrowth.sql]';
    DECLARE @Perm INT = (SELECT Id FROM dbo.Permissions WITH (UPDLOCK, HOLDLOCK) WHERE Code = @Code);

    IF @Perm IS NULL
        PRINT '=== Permission ReadyStock.Overgrowth does not exist -- no grant or code to remove ===';
    ELSE
    BEGIN
        DECLARE @PermText NVARCHAR(20) = CAST(@Perm AS NVARCHAR(20));
        DECLARE @Roles TABLE (RoleName NVARCHAR(100) PRIMARY KEY, RoleId INT NULL);
        INSERT INTO @Roles (RoleName) VALUES (N'Sowing Supervisor'), (N'Mother Plant Supervisor');
        UPDATE x SET RoleId = r.Id FROM @Roles x JOIN dbo.Roles r ON COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = x.RoleName;

        DECLARE @RoleName NVARCHAR(100), @Role INT, @LastChange NVARCHAR(10), @LastScript NVARCHAR(200);
        DECLARE revoke_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT RoleName, RoleId FROM @Roles WHERE RoleId IS NOT NULL ORDER BY RoleName;
        OPEN revoke_cursor;
        FETCH NEXT FROM revoke_cursor INTO @RoleName, @Role;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            IF EXISTS (SELECT 1 FROM dbo.RolePermissions WITH (UPDLOCK, HOLDLOCK) WHERE RoleId = @Role AND PermissionId = @Perm)
            BEGIN
                SET @LastChange = NULL; SET @LastScript = NULL;
                SELECT TOP 1 @LastChange = Change, @LastScript = Script
                FROM dbo.RolePermissionChangeLog WHERE RoleId = @Role AND PermissionCode = @Code ORDER BY Id DESC;
                IF ISNULL(@LastChange, N'') <> N'Added' OR ISNULL(@LastScript, N'') <> @Script
                    THROW 52660, 'A ReadyStock.Overgrowth grant was not (last) added by 2026-10-24_ReadyStockOvergrowth.sql -- nothing changed.', 1;
                INSERT INTO dbo.RolePermissionChangeLog (RoleId, RoleName, PermissionCode, Change, Script, Reason)
                VALUES (@Role, @RoleName, @Code, N'Removed', @RollbackScript, N'Rollback of 2026-10-24_ReadyStockOvergrowth.sql [PermissionId=' + @PermText + N']');
                DELETE FROM dbo.RolePermissions WHERE RoleId = @Role AND PermissionId = @Perm;
                PRINT '=== ReadyStock.Overgrowth removed from ' + @RoleName + ' ===';
            END
            ELSE
                PRINT '=== ' + @RoleName + ': not granted -- nothing to remove ===';
            FETCH NEXT FROM revoke_cursor INTO @RoleName, @Role;
        END
        CLOSE revoke_cursor;
        DEALLOCATE revoke_cursor;

        DECLARE @CreatedByMigration BIT = CASE WHEN
                EXISTS (SELECT 1 FROM dbo.Permissions WHERE Id = @Perm AND Code = @Code AND Description = @OwnedDescription)
            AND EXISTS (SELECT 1 FROM dbo.RolePermissionChangeLog
                        WHERE PermissionCode = @Code AND Change = N'Added' AND Script = @Script
                          AND Reason LIKE N'%[[]code created by this script; PermissionId=' + @PermText + N']')
            THEN 1 ELSE 0 END;
        IF @CreatedByMigration = 0
            PRINT '=== Permission code KEPT: it was not created by the migration ===';
        ELSE IF EXISTS (SELECT 1 FROM dbo.RolePermissions WITH (UPDLOCK, HOLDLOCK) WHERE PermissionId = @Perm)
            PRINT '=== Permission code KEPT: a role still holds ReadyStock.Overgrowth ===';
        ELSE
        BEGIN
            DELETE FROM dbo.Permissions WHERE Id = @Perm AND Code = @Code AND Description = @OwnedDescription;
            IF @@ROWCOUNT <> 1
                THROW 52661, 'The migration-owned permission code could not be deleted exactly once -- nothing changed.', 1;
            PRINT '=== Permission ReadyStock.Overgrowth (created by the migration) deleted ===';
        END
    END

    -- ---- 3. schema -----------------------------------------------------------------------------------------------------
    IF OBJECT_ID('dbo.TR_ReadyStockOvergrowthLog_AppendOnly', 'TR') IS NOT NULL
        EXEC (N'DROP TRIGGER dbo.TR_ReadyStockOvergrowthLog_AppendOnly;');
    IF OBJECT_ID('dbo.ReadyStockOvergrowthLog', 'U') IS NOT NULL
    BEGIN
        EXEC (N'DROP TABLE dbo.ReadyStockOvergrowthLog;');
        PRINT '=== dbo.ReadyStockOvergrowthLog dropped ===';
    END
    IF COL_LENGTH('dbo.SeedlingDispatchLines', 'IsOvergrowth') IS NOT NULL
    BEGIN
        EXEC (N'ALTER TABLE dbo.SeedlingDispatchLines DROP CONSTRAINT DF_SeedlingDispatchLines_IsOvergrowth;');
        EXEC (N'ALTER TABLE dbo.SeedlingDispatchLines DROP COLUMN IsOvergrowth;');
        PRINT '=== dbo.SeedlingDispatchLines.IsOvergrowth dropped ===';
    END
    IF COL_LENGTH('dbo.ReadyStock', 'IsOvergrowth') IS NOT NULL
    BEGIN
        IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.ReadyStock') AND name = N'IX_ReadyStock_Overgrowth')
            EXEC (N'DROP INDEX IX_ReadyStock_Overgrowth ON dbo.ReadyStock;');
        EXEC (N'ALTER TABLE dbo.ReadyStock DROP CONSTRAINT CK_ReadyStock_Overgrowth, FK_ReadyStock_OvergrowthMarkedBy, DF_ReadyStock_IsOvergrowth;');
        EXEC (N'ALTER TABLE dbo.ReadyStock DROP COLUMN IsOvergrowth, OvergrowthMarkedAt, OvergrowthMarkedById, OvergrowthReason;');
        PRINT '=== dbo.ReadyStock Overgrowth columns dropped ===';
    END
    ELSE
        PRINT '=== dbo.ReadyStock has no Overgrowth columns -- no-op ===';
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION RsOvergrowth1024Rollback;
    THROW;
END CATCH;

IF N'$(MODE)' COLLATE Latin1_General_BIN2 = N'COMMIT'
BEGIN
    COMMIT TRANSACTION RsOvergrowth1024Rollback;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION RsOvergrowth1024Rollback;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
