-- ============================================================================
-- 2026-10-21_MotherPlantDirectSowing_Rollback.sql
--
-- Undoes 2026-10-21_MotherPlantDirectSowing.sql: drops TR_SeedSowings_MotherPlantDirectSowing, CK_SeedSowings_MotherPlantDirectSowing and the
-- column SeedSowings.IsMotherPlantDirectSowing, and puts TR_SeedSowings_SupervisorRole and
-- TR_ReadyConfirmations_AssignedSupervisor back to their exact earlier versions (SHA-256 checked after the restore).
-- REFUSED (nothing changed) once any Mother Plant Area Direct Sowing exists: dropping the column would erase which batches
-- they are, and the restored rules could never approve them (no assigned supervisor). Cancelled ones count too.
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to -v TARGET_SERVER=<@@SERVERNAME>.
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52721, '2026-10-21_MotherPlantDirectSowing_Rollback.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52722, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52723, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION MpDirectSowing1021Rollback;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';
    IF COL_LENGTH('dbo.SeedSowings', 'IsMotherPlantDirectSowing') IS NULL
       AND OBJECT_ID('dbo.TR_SeedSowings_MotherPlantDirectSowing', 'TR') IS NULL
        PRINT '=== Not applied -- no-op ===';
    ELSE
    BEGIN
        DECLARE @sql NVARCHAR(MAX), @n INT;
        IF COL_LENGTH('dbo.SeedSowings', 'IsMotherPlantDirectSowing') IS NOT NULL
        BEGIN
            EXEC sp_executesql N'SELECT @n = COUNT(*) FROM dbo.SeedSowings WHERE IsMotherPlantDirectSowing = 1', N'@n INT OUTPUT', @n = @n OUTPUT;
            IF @n > 0
                THROW 52724, 'Mother Plant Area Direct Sowings exist -- the rollback would orphan them. Nothing changed.', 1;
        END

        IF OBJECT_ID('dbo.TR_SeedSowings_MotherPlantDirectSowing', 'TR') IS NOT NULL
            DROP TRIGGER dbo.TR_SeedSowings_MotherPlantDirectSowing;

        DROP TRIGGER dbo.TR_SeedSowings_SupervisorRole;
        SET @sql = N'CREATE TRIGGER dbo.TR_SeedSowings_SupervisorRole
ON dbo.SeedSowings
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    -- SEED sowing (unchanged, INSERT only): assigned to an active Sowing Supervisor
    -- who is not the person recording it (the assigned supervisor is the only approver).
    IF NOT EXISTS (SELECT 1 FROM deleted)
       AND EXISTS (SELECT 1
               FROM inserted i
               WHERE i.SourceType NOT IN (N''Cutting'', N''BedSowing'', N''Transplant'')
                 AND (i.SupervisorId IS NULL
                  OR (i.CreatedById IS NOT NULL AND i.SupervisorId = i.CreatedById)
                  OR NOT EXISTS (SELECT 1
                                 FROM dbo.UserRoles ur
                                 INNER JOIN dbo.Roles r ON r.Id = ur.RoleId
                                 INNER JOIN dbo.IMSUsers u ON u.Id = ur.UserId
                                 WHERE ur.UserId = i.SupervisorId
                                   AND ISNULL(u.IsActive, 0) = 1
                                   AND COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''''), r.RoleName) = N''Sowing Supervisor'')))
        THROW 50203, ''A sowing must be assigned to an active Sowing Supervisor other than the person recording it.'', 1;
    -- 2026-10-13: a tray batch transplanted from a Bed Sowing or from received Ready Stock is
    -- approved by any user who may approve sowings -- no supervisor is assigned to it.
    IF NOT EXISTS (SELECT 1 FROM deleted)
       AND EXISTS (SELECT 1 FROM inserted i WHERE i.SourceType IN (N''BedSowing'', N''Transplant'') AND i.SupervisorId IS NOT NULL)
        THROW 50205, ''A tray batch transplanted from a Bed Sowing or from Ready Stock is not assigned to a supervisor.'', 1;
    -- CUTTING TRAY sowing: recorded WITHOUT a supervisor (pending destination) is allowed.
    -- When it GETS one (insert, or NULL -> value at confirmation) the supervisor is an
    -- active user assigned to the sowing''s Area (any role) who holds the approval permission.
    IF EXISTS (SELECT 1
               FROM inserted i
               LEFT JOIN deleted d ON d.Id = i.Id
               WHERE i.SourceType = N''Cutting''
                 AND i.SupervisorId IS NOT NULL
                 AND (d.Id IS NULL OR ISNULL(d.SupervisorId, -1) <> i.SupervisorId)
                 AND (NOT EXISTS (SELECT 1
                                 FROM dbo.UserRoles ur
                                 INNER JOIN dbo.IMSUsers u ON u.Id = ur.UserId
                                 WHERE ur.UserId = i.SupervisorId
                                   AND ur.AreaId = i.AreaId
                                   AND ISNULL(u.IsActive, 0) = 1)
                  OR NOT EXISTS (SELECT 1
                                 FROM dbo.UserRoles ur2
                                 INNER JOIN dbo.RolePermissions rp ON rp.RoleId = ur2.RoleId
                                 INNER JOIN dbo.Permissions p ON p.Id = rp.PermissionId
                                 WHERE ur2.UserId = i.SupervisorId
                                   AND p.Code = N''ReadyStock.Confirm'')))
        THROW 50203, ''A cutting tray sowing must be assigned to an active user of the sowing''''s Area who can approve sowings.'', 1;
    -- ...and its destination is a real, active Polyhouse of that Area (never Outlet).
    IF EXISTS (SELECT 1
               FROM inserted i
               LEFT JOIN deleted d ON d.Id = i.Id
               WHERE i.SourceType = N''Cutting''
                 AND i.SupervisorId IS NOT NULL
                 AND (d.Id IS NULL OR ISNULL(d.SupervisorId, -1) <> i.SupervisorId)
                 AND NOT EXISTS (SELECT 1
                                 FROM dbo.Polyhouses ph
                                 INNER JOIN dbo.Area a ON a.Id = ph.AreaId
                                 WHERE ph.Id = i.PolyhouseId
                                   AND ph.AreaId = i.AreaId
                                   AND a.IsActive = 1
                                   AND (a.AreaType IS NULL OR a.AreaType <> N''Outlet'')))
        THROW 50204, ''A cutting tray sowing must be sown at an active Polyhouse of its Area (not Outlet).'', 1;
END';
        SET @sql = REPLACE(@sql, NCHAR(13) + NCHAR(10), NCHAR(10));
        EXEC (@sql);

        DROP TRIGGER dbo.TR_ReadyConfirmations_AssignedSupervisor;
        SET @sql = N'CREATE TRIGGER dbo.TR_ReadyConfirmations_AssignedSupervisor
ON dbo.ReadyConfirmations
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    -- SEED: only the assigned supervisor, never the recorder.
    -- CUTTING: the assigned supervisor, or any active user who may approve sowings
    -- (ReadyStock.Confirm through any role, or the full-access System Administrator role).
    -- 2026-10-13 TRANSPLANT (no supervisor assigned): any active user who may approve sowings.
    -- 2026-10-17 TRANSPLANT (TransplantApproverInBatchArea): only an Area Supervisor of the batch''s OWN Area -- an active
    -- user holding ReadyStock.Confirm through a role assigned to that Area -- or a System Administrator.
    -- 2026-10-16 BEDSOWING: ONLY the active user named in dbo.BedSowingApprover (Maya Shinde) -- not every
    -- approver, not every administrator; she may approve a batch she transplanted herself (Bed Sowing only).
    IF EXISTS (SELECT 1
               FROM inserted i
               INNER JOIN dbo.SeedSowings sw ON sw.Id = i.SeedSowingId
               WHERE sw.SourceType = N''BedSowing''
                 AND (i.ApprovedById IS NULL
                      OR NOT EXISTS (SELECT 1 FROM dbo.BedSowingApprover ba
                                     INNER JOIN dbo.IMSUsers u ON u.Id = ba.UserId AND ISNULL(u.IsActive, 0) = 1
                                     WHERE ba.Id = 1 AND ba.UserId = i.ApprovedById)))
        THROW 52411, ''Only the designated Bed Sowing approver can approve a tray batch from a Bed Sowing.'', 1;
    IF EXISTS (SELECT 1
               FROM inserted i
               INNER JOIN dbo.SeedSowings sw ON sw.Id = i.SeedSowingId
               OUTER APPLY (SELECT CASE WHEN EXISTS (
                                SELECT 1
                                FROM dbo.IMSUsers u
                                INNER JOIN dbo.UserRoles ur ON ur.UserId = u.Id
                                INNER JOIN dbo.Roles r ON r.Id = ur.RoleId
                                WHERE u.Id = i.ApprovedById
                                  AND ISNULL(u.IsActive, 0) = 1
                                  AND (COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''''), r.RoleName) = N''System Administrator''
                                       OR EXISTS (SELECT 1
                                                  FROM dbo.RolePermissions rp
                                                  INNER JOIN dbo.Permissions p ON p.Id = rp.PermissionId
                                                  WHERE rp.RoleId = ur.RoleId
                                                    AND p.Code = N''ReadyStock.Confirm''))) THEN 1 ELSE 0 END AS MayApprove) m
               WHERE sw.SourceType <> N''BedSowing''
                 AND (i.ApprovedById IS NULL
                  OR (sw.SourceType NOT IN (N''Cutting'', N''Transplant'')
                      AND (sw.SupervisorId IS NULL
                           OR i.ApprovedById <> sw.SupervisorId
                           OR (sw.CreatedById IS NOT NULL AND i.ApprovedById = sw.CreatedById)))
                  OR (sw.SourceType = N''Cutting''
                      AND (sw.SupervisorId IS NULL
                           OR (i.ApprovedById <> sw.SupervisorId AND m.MayApprove = 0)))
                  OR (sw.SourceType = N''Transplant''
                      AND NOT EXISTS (SELECT 1
                                      FROM dbo.IMSUsers u
                                      INNER JOIN dbo.UserRoles ur ON ur.UserId = u.Id
                                      INNER JOIN dbo.Roles r ON r.Id = ur.RoleId
                                      WHERE u.Id = i.ApprovedById
                                        AND ISNULL(u.IsActive, 0) = 1
                                        AND (COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''''), r.RoleName) = N''System Administrator''
                                             OR (ur.AreaId = sw.AreaId
                                                 AND EXISTS (SELECT 1
                                                             FROM dbo.RolePermissions rp
                                                             INNER JOIN dbo.Permissions p ON p.Id = rp.PermissionId
                                                             WHERE rp.RoleId = ur.RoleId
                                                               AND p.Code = N''ReadyStock.Confirm'')))))))
        THROW 50111, ''Only the assigned supervisor (or, for a cutting sowing, a user who may approve sowings) can approve it; a seed sowing never by the user who recorded it.'', 1;
END';
        SET @sql = REPLACE(@sql, NCHAR(13) + NCHAR(10), NCHAR(10));
        EXEC (@sql);

        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_SeedSowings_SupervisorRole'))), 2) <> '2A7E4E589631092B4205D5DC712B8801ACBF23B416D04CAA74AEAD78553B0591'
           OR CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_AssignedSupervisor'))), 2) <> '2E3FB87CFA3D67B254A435AFE6AE58E5D900E4CFD59961425B7A91784773D3C4'
            THROW 52725, 'A trigger was not restored byte-for-byte -- rolling back.', 1;

        IF OBJECT_ID('dbo.CK_SeedSowings_MotherPlantDirectSowing', 'C') IS NOT NULL
            ALTER TABLE dbo.SeedSowings DROP CONSTRAINT CK_SeedSowings_MotherPlantDirectSowing;
        IF OBJECT_ID('dbo.DF_SeedSowings_IsMotherPlantDirectSowing', 'D') IS NOT NULL
            ALTER TABLE dbo.SeedSowings DROP CONSTRAINT DF_SeedSowings_IsMotherPlantDirectSowing;
        IF COL_LENGTH('dbo.SeedSowings', 'IsMotherPlantDirectSowing') IS NOT NULL
            EXEC (N'ALTER TABLE dbo.SeedSowings DROP COLUMN IsMotherPlantDirectSowing');
        PRINT '=== Restored: no Mother Plant Area Direct Sowing schema; both triggers back to their earlier versions ===';
    END
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION MpDirectSowing1021Rollback;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION MpDirectSowing1021Rollback;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION MpDirectSowing1021Rollback;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
