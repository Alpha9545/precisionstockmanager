-- ============================================================================
-- 2026-10-23_ReadyStockDispatchToCustomer_Rollback.sql
--
-- Undoes 2026-10-23_ReadyStockDispatchToCustomer.sql -- ONLY what that script owns:
--   1) the Office Coordinator grant of ReadyStock.DispatchToCustomer, removed only when the LATEST change-log row for that
--      role + code is that script's 'Added' row (a grant added or re-added by anyone else is refused: THROW, nothing
--      changed). The removal is logged as a new 'Removed' row naming the Permission Id;
--   2) the permission code itself, deleted only when ALL of these hold:
--        - the migration CREATED it: its Description is exactly the migration's owned description AND that script's
--          'Added' row for this role + code says "[code created by this script; PermissionId=<this Id>]";
--        - the migration's grant is gone (step 1 removed it, or it was already removed), and
--        - no role at all references the code any more (dbo.RolePermissions).
--      A code that pre-existed the migration ("[code pre-existed; ...]", or any other Description) is never deleted.
--   No change-log row is ever updated or deleted; no other role, grant or permission is touched. Dispatches already
--   recorded stay (bookings, dispatches and ledger rows are never removed). No schema change.
--
-- TARGET SAFEGUARDS: identical to the migration -- DB_NAME() allow-list, MODE, and ALL of TARGET_SERVER (= @@SERVERNAME),
--   SERVERPROPERTY('ServerName') = @@SERVERNAME, TARGET_MACHINE (= SERVERPROPERTY('MachineName')), TARGET_DB (= DB_NAME())
--   and TARGET_DB_CREATED (= this database's sys.databases.create_date, yyyy-mm-ddThh:mi:ss). Every variable is required.
--
-- OPERATOR CHECKLIST (as for the migration):
--   Before DRYRUN: run the identity query from the migration header on the intended server + database, confirm the host
--     independently (the machine you are logged on to), take a COPY_ONLY backup, then
--       sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER="<ServerName>" TARGET_MACHINE="<MachineName>"
--              TARGET_DB="<DatabaseName>" TARGET_DB_CREATED="<DatabaseCreated>" -i 2026-10-23_ReadyStockDispatchToCustomer_Rollback.sql
--     and read: the identity line; whether the grant is removed; whether the code is deleted or KEPT and why.
--   Before COMMIT: same server + database + four values as the reviewed DRYRUN, the backup exists, approval given.
--
-- One transaction. Safely repeatable: a second run finds nothing of the migration left and changes nothing.
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52621, '2026-10-23_ReadyStockDispatchToCustomer_Rollback.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52622, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52623, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
IF CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) <> @@SERVERNAME
    THROW 52627, 'SERVERPROPERTY(''ServerName'') does not match @@SERVERNAME (renamed host?) -- refusing to run.', 1;
IF CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) <> N'$(TARGET_MACHINE)'
    THROW 52628, 'TARGET_MACHINE does not match SERVERPROPERTY(''MachineName'') -- refusing to run on an unexpected host.', 1;
IF DB_NAME() <> N'$(TARGET_DB)'
    THROW 52629, 'TARGET_DB does not match DB_NAME() -- refusing to run against an unexpected database.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND CONVERT(CHAR(19), create_date, 126) = N'$(TARGET_DB_CREATED)')
    THROW 52630, 'TARGET_DB_CREATED does not match this database''s create_date -- refusing to run against an unexpected copy.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION RsDispatchCustomer1023Rollback;

BEGIN TRY
    DECLARE @Created CHAR(19) = (SELECT CONVERT(CHAR(19), create_date, 126) FROM sys.databases WHERE database_id = DB_ID());
    PRINT '=== Server / machine / database / created: ' + @@SERVERNAME + ' / ' + CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128))
        + ' / ' + DB_NAME() + ' / ' + @Created + ' -- MODE=$(MODE) ===';
    IF OBJECT_ID('dbo.RolePermissionChangeLog', 'U') IS NULL
        THROW 52625, 'dbo.RolePermissionChangeLog is missing -- nothing changed.', 1;

    DECLARE @RoleName NVARCHAR(100) = N'Office Coordinator';
    DECLARE @Code NVARCHAR(100) = N'ReadyStock.DispatchToCustomer';
    DECLARE @Script NVARCHAR(200) = N'2026-10-23_ReadyStockDispatchToCustomer.sql';
    DECLARE @RollbackScript NVARCHAR(200) = N'2026-10-23_ReadyStockDispatchToCustomer_Rollback.sql';
    DECLARE @OwnedDescription NVARCHAR(200) =
        N'Dispatch whole Ready Stock trays to a customer (own Main Office / Mother Plant Area) [created by 2026-10-23_ReadyStockDispatchToCustomer.sql]';
    DECLARE @Role INT = (SELECT Id FROM dbo.Roles WHERE COALESCE(NULLIF(LTRIM(RTRIM(Name)), N''), RoleName) = @RoleName);
    DECLARE @Perm INT = (SELECT Id FROM dbo.Permissions WITH (UPDLOCK, HOLDLOCK) WHERE Code = @Code);

    IF @Perm IS NULL
        PRINT '=== Permission does not exist -- nothing of the migration is left; no-op ===';
    ELSE
    BEGIN
        DECLARE @PermText NVARCHAR(20) = CAST(@Perm AS NVARCHAR(20));

        -- 1) the grant: only when the latest change for this role + code is the migration's 'Added'
        IF @Role IS NOT NULL AND EXISTS (SELECT 1 FROM dbo.RolePermissions WITH (UPDLOCK, HOLDLOCK) WHERE RoleId = @Role AND PermissionId = @Perm)
        BEGIN
            DECLARE @LastChange NVARCHAR(10), @LastScript NVARCHAR(200);
            SELECT TOP 1 @LastChange = Change, @LastScript = Script
            FROM dbo.RolePermissionChangeLog WHERE RoleId = @Role AND PermissionCode = @Code ORDER BY Id DESC;
            IF ISNULL(@LastChange, N'') <> N'Added' OR ISNULL(@LastScript, N'') <> @Script
                THROW 52624, 'The Office Coordinator grant was not (last) added by 2026-10-23_ReadyStockDispatchToCustomer.sql -- nothing changed.', 1;
            INSERT INTO dbo.RolePermissionChangeLog (RoleId, RoleName, PermissionCode, Change, Script, Reason)
            VALUES (@Role, @RoleName, @Code, N'Removed', @RollbackScript,
                    N'Rollback of 2026-10-23_ReadyStockDispatchToCustomer.sql [PermissionId=' + @PermText + N']');
            DELETE FROM dbo.RolePermissions WHERE RoleId = @Role AND PermissionId = @Perm;
            PRINT '=== ReadyStock.DispatchToCustomer removed from Office Coordinator ===';
        END
        ELSE
            PRINT '=== Not granted to Office Coordinator -- no grant to remove ===';

        -- 2) the code: only when the migration created it, its grant is gone and no role references it
        DECLARE @CreatedByMigration BIT = CASE WHEN
                EXISTS (SELECT 1 FROM dbo.Permissions WHERE Id = @Perm AND Code = @Code AND Description = @OwnedDescription)
            AND EXISTS (SELECT 1 FROM dbo.RolePermissionChangeLog
                        WHERE RoleId = @Role AND PermissionCode = @Code AND Change = N'Added' AND Script = @Script
                          AND Reason LIKE N'%[[]code created by this script; PermissionId=' + @PermText + N']')
            THEN 1 ELSE 0 END;
        DECLARE @GrantGone BIT = CASE WHEN NOT EXISTS (SELECT 1 FROM dbo.RolePermissions WHERE RoleId = @Role AND PermissionId = @Perm) THEN 1 ELSE 0 END;
        DECLARE @Referenced BIT = CASE WHEN EXISTS (SELECT 1 FROM dbo.RolePermissions WITH (UPDLOCK, HOLDLOCK) WHERE PermissionId = @Perm) THEN 1 ELSE 0 END;

        IF @CreatedByMigration = 0
            PRINT '=== Permission code KEPT: it was not created by the migration (pre-existing code) ===';
        ELSE IF @GrantGone = 0
            PRINT '=== Permission code KEPT: the Office Coordinator grant is still present ===';
        ELSE IF @Referenced = 1
            PRINT '=== Permission code KEPT: another role still holds ReadyStock.DispatchToCustomer ===';
        ELSE
        BEGIN
            DELETE FROM dbo.Permissions WHERE Id = @Perm AND Code = @Code AND Description = @OwnedDescription;
            IF @@ROWCOUNT <> 1
                THROW 52626, 'The migration-owned permission code could not be deleted exactly once -- nothing changed.', 1;
            PRINT '=== Permission ReadyStock.DispatchToCustomer (created by the migration) deleted ===';
        END
    END

    SELECT p.Id AS PermissionId, p.Code, p.Description,
           (SELECT COUNT(*) FROM dbo.RolePermissions rp WHERE rp.PermissionId = p.Id) AS RolesHoldingIt
    FROM dbo.Permissions p WHERE p.Code = N'ReadyStock.DispatchToCustomer';
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION RsDispatchCustomer1023Rollback;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION RsDispatchCustomer1023Rollback;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION RsDispatchCustomer1023Rollback;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
