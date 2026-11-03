-- ============================================================================
-- 2026-11-03_FieldWorkDays_Rollback.sql          *** PREPARED FOR REVIEW ***
--
-- Undoes 2026-11-03_FieldWorkDays.sql, ONLY while nothing depends on it:
--   * REFUSED when dbo.FieldDays, dbo.FieldPhotos, dbo.FieldDayEvents or dbo.FieldWorkConsents holds any row
--     (field days are work records -- archive / delete them deliberately first; photo FILES on disk are not touched)
--   * REFUSED when any user is assigned to the "Field Employee" or "Field Manager" role (dbo.UserRoles)
-- Then: removes the FieldWork.* and Dashboard.View grants that the migration logged (dbo.RolePermissionChangeLog 'Removed' rows), deletes
--   the two roles and two permission codes ONLY if their description carries the migration's marker, and drops the
--   trigger and the four tables. Nothing else is touched.
--
-- Same target safeguards and operator checklist as the migration (MODE=DRYRUN first; COMMIT only with the owner's
-- explicit approval for that database).
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 54121, '2026-11-03_FieldWorkDays_Rollback.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' COLLATE Latin1_General_BIN2 NOT IN (N'DRYRUN', N'COMMIT')
    THROW 54122, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT (exact upper case).', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 54123, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
IF CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) <> @@SERVERNAME
    THROW 54124, 'SERVERPROPERTY(''ServerName'') does not match @@SERVERNAME (renamed host?) -- refusing to run.', 1;
IF CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) <> N'$(TARGET_MACHINE)'
    THROW 54125, 'TARGET_MACHINE does not match SERVERPROPERTY(''MachineName'') -- refusing to run on an unexpected host.', 1;
IF DB_NAME() <> N'$(TARGET_DB)'
    THROW 54126, 'TARGET_DB does not match DB_NAME() -- refusing to run against an unexpected database.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND CONVERT(CHAR(19), create_date, 126) = N'$(TARGET_DB_CREATED)')
    THROW 54127, 'TARGET_DB_CREATED does not match this database''s create_date -- refusing to run against an unexpected copy.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION FieldWorkDays1103Rollback;

BEGIN TRY
    DECLARE @Created CHAR(19) = (SELECT CONVERT(CHAR(19), create_date, 126) FROM sys.databases WHERE database_id = DB_ID());
    PRINT '=== Server / machine / database / created: ' + @@SERVERNAME + ' / ' + CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128))
        + ' / ' + DB_NAME() + ' / ' + @Created + ' -- MODE=$(MODE) ===';

    DECLARE @Script NVARCHAR(200) = N'2026-11-03_FieldWorkDays.sql';
    DECLARE @RollbackScript NVARCHAR(200) = N'2026-11-03_FieldWorkDays_Rollback.sql';
    DECLARE @Marker NVARCHAR(100) = N' [created by 2026-11-03_FieldWorkDays.sql]';
    DECLARE @Rows INT = 0;

    IF OBJECT_ID('dbo.FieldDayEvents', 'U') IS NOT NULL SET @Rows += (SELECT COUNT(*) FROM dbo.FieldDayEvents WITH (TABLOCKX, HOLDLOCK));
    IF OBJECT_ID('dbo.FieldDays', 'U') IS NOT NULL SET @Rows += (SELECT COUNT(*) FROM dbo.FieldDays WITH (TABLOCKX, HOLDLOCK));
    IF OBJECT_ID('dbo.FieldPhotos', 'U') IS NOT NULL SET @Rows += (SELECT COUNT(*) FROM dbo.FieldPhotos WITH (TABLOCKX, HOLDLOCK));
    IF OBJECT_ID('dbo.FieldWorkConsents', 'U') IS NOT NULL SET @Rows += (SELECT COUNT(*) FROM dbo.FieldWorkConsents WITH (TABLOCKX, HOLDLOCK));
    IF @Rows > 0
        THROW 54128, 'Field Work tables hold data (field days / photos / events / consents) -- refusing to drop them. Nothing changed.', 1;

    -- every grant the migration made (FieldWork.* and Dashboard.View on each new role)
    DECLARE @Roles TABLE (RoleName NVARCHAR(100), Code NVARCHAR(100), RoleId INT NULL, PRIMARY KEY (RoleName, Code));
    INSERT INTO @Roles (RoleName, Code) VALUES (N'Field Employee', N'FieldWork.Day'), (N'Field Manager', N'FieldWork.Manage'),
                                               (N'Field Employee', N'Dashboard.View'), (N'Field Manager', N'Dashboard.View');
    UPDATE x SET RoleId = r.Id FROM @Roles x JOIN dbo.Roles r ON COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = x.RoleName;
    IF EXISTS (SELECT 1 FROM dbo.UserRoles ur WITH (UPDLOCK, HOLDLOCK) JOIN @Roles x ON x.RoleId = ur.RoleId)
        THROW 54129, 'A user is still assigned to Field Employee / Field Manager -- remove those assignments first. Nothing changed.', 1;

    -- grants logged by the migration
    DECLARE @RoleName NVARCHAR(100), @Code NVARCHAR(100), @Role INT, @Perm INT, @LastChange NVARCHAR(10), @LastScript NVARCHAR(200);
    DECLARE revoke_cursor CURSOR LOCAL FAST_FORWARD FOR
        SELECT x.RoleName, x.Code, x.RoleId, p.Id FROM @Roles x JOIN dbo.Permissions p ON p.Code = x.Code WHERE x.RoleId IS NOT NULL;
    OPEN revoke_cursor;
    FETCH NEXT FROM revoke_cursor INTO @RoleName, @Code, @Role, @Perm;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        IF EXISTS (SELECT 1 FROM dbo.RolePermissions WITH (UPDLOCK, HOLDLOCK) WHERE RoleId = @Role AND PermissionId = @Perm)
        BEGIN
            SELECT @LastChange = NULL, @LastScript = NULL;
            SELECT TOP 1 @LastChange = Change, @LastScript = Script
            FROM dbo.RolePermissionChangeLog WHERE RoleId = @Role AND PermissionCode = @Code ORDER BY Id DESC;
            IF ISNULL(@LastChange, N'') <> N'Added' OR ISNULL(@LastScript, N'') <> @Script
                THROW 54130, 'A Field Work grant was not (last) added by 2026-11-03_FieldWorkDays.sql -- nothing changed.', 1;
            INSERT INTO dbo.RolePermissionChangeLog (RoleId, RoleName, PermissionCode, Change, Script, Reason)
            VALUES (@Role, @RoleName, @Code, N'Removed', @RollbackScript, N'Rollback of 2026-11-03_FieldWorkDays.sql [PermissionId=' + CAST(@Perm AS NVARCHAR(20)) + N']');
            DELETE FROM dbo.RolePermissions WHERE RoleId = @Role AND PermissionId = @Perm;
            PRINT '=== ' + @Code + ' removed from ' + @RoleName + ' ===';
        END
        FETCH NEXT FROM revoke_cursor INTO @RoleName, @Code, @Role, @Perm;
    END
    CLOSE revoke_cursor;
    DEALLOCATE revoke_cursor;

    -- roles created by the migration, when no permission is left on them
    DELETE r FROM dbo.Roles r JOIN @Roles x ON x.RoleId = r.Id
    WHERE RIGHT(r.Description, LEN(@Marker)) = @Marker
      AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.RoleId = r.Id)
      AND NOT EXISTS (SELECT 1 FROM dbo.UserRoles ur WHERE ur.RoleId = r.Id);
    PRINT CONCAT('Roles deleted: ', @@ROWCOUNT, ' (2 when the migration created them)');

    -- permission codes created by the migration, when no role holds them
    DELETE p FROM dbo.Permissions p
    WHERE p.Code IN (N'FieldWork.Day', N'FieldWork.Manage') AND RIGHT(p.Description, LEN(@Marker)) = @Marker
      AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.PermissionId = p.Id);
    PRINT CONCAT('Permissions deleted: ', @@ROWCOUNT, ' (2 when the migration created them)');

    IF OBJECT_ID('dbo.TR_FieldDays_Guard', 'TR') IS NOT NULL DROP TRIGGER dbo.TR_FieldDays_Guard;
    IF OBJECT_ID('dbo.FieldDayEvents', 'U') IS NOT NULL DROP TABLE dbo.FieldDayEvents;
    IF OBJECT_ID('dbo.FieldDays', 'U') IS NOT NULL DROP TABLE dbo.FieldDays;
    IF OBJECT_ID('dbo.FieldPhotos', 'U') IS NOT NULL DROP TABLE dbo.FieldPhotos;
    IF OBJECT_ID('dbo.FieldWorkConsents', 'U') IS NOT NULL DROP TABLE dbo.FieldWorkConsents;
    PRINT '=== Field Work tables and trigger dropped ===';

    SELECT name, type_desc FROM sys.objects
    WHERE name IN (N'FieldPhotos', N'FieldDays', N'FieldDayEvents', N'FieldWorkConsents', N'TR_FieldDays_Guard');
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION FieldWorkDays1103Rollback;
    THROW;
END CATCH;

IF N'$(MODE)' COLLATE Latin1_General_BIN2 = N'COMMIT'
BEGIN
    COMMIT TRANSACTION FieldWorkDays1103Rollback;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION FieldWorkDays1103Rollback;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
