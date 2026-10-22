-- ============================================================================
-- 2026-10-21_MotherPlantDirectSowing.sql
--
-- WHY (owner requirement 2026-10-13, "Direct Sowing for Mother Plant Areas, with Area Supervisor approval"):
--   Mother Plant & Cutting > Direct Sowing records a Direct (seed) Sowing INTO a Mother Plant Area: seed from a Main Office
--   lot (as Seed Production Direct Sowing), empty trays from the Area's own tray pool, no Polyhouse, and the batch waits in
--   Sowing Approvals until an Area Supervisor of THAT Area (or a System Administrator) approves it -- the user who recorded
--   it may approve it, like a Mother Plant transplant batch. The existing database rules refuse exactly that:
--     TR_SeedSowings_SupervisorRole            a seed sowing must be assigned to an active Sowing Supervisor (50203) --
--                                              Mother Plant Areas are run by Mother Plant Supervisors;
--     TR_ReadyConfirmations_AssignedSupervisor a seed sowing is approved only by its assigned supervisor, never by its
--                                              recorder (50111).
--   The batch must also be told apart from a Seed Production Direct Sowing for good (not by the Area's current type).
--
-- WHAT (one transaction; nothing else is touched):
--   dbo.SeedSowings.IsMotherPlantDirectSowing BIT NOT NULL DEFAULT 0 (DF_SeedSowings_IsMotherPlantDirectSowing) -- every
--     existing row stays 0, so every existing sowing keeps exactly its current rules.
--   CK_SeedSowings_MotherPlantDirectSowing: when 1 -> SourceType 'Seed', no supervisor, no Polyhouse, no growing material,
--     recorder known.
--   TR_SeedSowings_MotherPlantDirectSowing (new): when 1 -> an active Mother Plant Area; flag and Area fixed afterwards;
--     a seed sowing in a Mother Plant Area is always flagged.
--   TR_SeedSowings_SupervisorRole (replaced only if byte-for-byte the current version, SHA-256): the Sowing Supervisor rule
--     skips flagged rows. Every other line unchanged.
--   TR_ReadyConfirmations_AssignedSupervisor (same guard): a flagged batch is approved only by an Area Supervisor of its
--     own Area (ReadyStock.Confirm through a role assigned to that Area) or a System Administrator (52711); the
--     assigned-supervisor rule skips flagged rows. Bed Sowing / Cutting / Transplant / Seed Production rules unchanged.
--
-- NOT changed: no existing row, stock, ledger, role, permission, Ready Date rule (TR_ReadyConfirmations_BedReadyDate) or
--   tray / material rule. Ready Date of the new batches = Sowing Date + the variety's growing days (Direct Sowing rule).
-- Requires: 2026-10-14_MotherPlantAreaTraysAndTransferPurpose.sql (Mother Plant Area tray pools) and
--   2026-10-17_TransplantApprovalAreaScope.sql (the trigger version replaced here).
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to -v TARGET_SERVER=<@@SERVERNAME>.
-- Idempotent (re-run = no-op). Undo: 2026-10-21_MotherPlantDirectSowing_Rollback.sql (refused once such a sowing exists).
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- Probe used by the application (Data/WorkflowCorrectionsSchema.MotherPlantDirectSowingProbeSql):
--   COL_LENGTH('dbo.SeedSowings', 'IsMotherPlantDirectSowing') IS NOT NULL AND OBJECT_ID('dbo.TR_SeedSowings_MotherPlantDirectSowing', 'TR') IS NOT NULL AND OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_AssignedSupervisor')) LIKE N'%MotherPlantDirectSowing%' AND OBJECT_DEFINITION(OBJECT_ID('dbo.TR_SeedSowings_SupervisorRole')) LIKE N'%MotherPlantDirectSowing%'
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52701, '2026-10-21_MotherPlantDirectSowing.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52702, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52703, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION MpDirectSowing1021;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';
    IF NOT (COL_LENGTH('dbo.InternalTransfers', 'TransferPurpose') IS NOT NULL AND OBJECT_ID('dbo.TR_TrayStock_PoolLocation', 'TR') IS NOT NULL)
        THROW 52704, 'Run 2026-10-14_MotherPlantAreaTraysAndTransferPurpose.sql first (Mother Plant Area tray pools).', 1;
    IF OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_AssignedSupervisor')) NOT LIKE N'%TransplantApproverInBatchArea%'
        THROW 52705, 'Run 2026-10-17_TransplantApprovalAreaScope.sql first (the Area Supervisor rule this extends).', 1;

    IF COL_LENGTH('dbo.SeedSowings', 'IsMotherPlantDirectSowing') IS NOT NULL AND OBJECT_ID('dbo.TR_SeedSowings_MotherPlantDirectSowing', 'TR') IS NOT NULL AND OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_AssignedSupervisor')) LIKE N'%MotherPlantDirectSowing%' AND OBJECT_DEFINITION(OBJECT_ID('dbo.TR_SeedSowings_SupervisorRole')) LIKE N'%MotherPlantDirectSowing%'
        PRINT '=== Already applied -- no-op ===';
    ELSE
    BEGIN
        IF COL_LENGTH('dbo.SeedSowings', 'IsMotherPlantDirectSowing') IS NOT NULL
           OR OBJECT_ID('dbo.TR_SeedSowings_MotherPlantDirectSowing', 'TR') IS NOT NULL
            THROW 52706, 'Partly applied (column or trigger already present) -- schema drift, nothing changed.', 1;
        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_SeedSowings_SupervisorRole'))), 2)
           <> '2A7E4E589631092B4205D5DC712B8801ACBF23B416D04CAA74AEAD78553B0591'
            THROW 52707, 'TR_SeedSowings_SupervisorRole is not the expected version -- schema drift, nothing changed.', 1;
        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_AssignedSupervisor'))), 2)
           <> '2E3FB87CFA3D67B254A435AFE6AE58E5D900E4CFD59961425B7A91784773D3C4'
            THROW 52708, 'TR_ReadyConfirmations_AssignedSupervisor is not the expected version -- schema drift, nothing changed.', 1;

        DECLARE @sql NVARCHAR(MAX);

        -- 1) The workflow identity of the batch (0 for every existing row).
        EXEC (N'ALTER TABLE dbo.SeedSowings ADD IsMotherPlantDirectSowing BIT NOT NULL
                CONSTRAINT DF_SeedSowings_IsMotherPlantDirectSowing DEFAULT (0)');
        EXEC (N'ALTER TABLE dbo.SeedSowings WITH CHECK ADD CONSTRAINT CK_SeedSowings_MotherPlantDirectSowing CHECK (IsMotherPlantDirectSowing = 0 OR (SourceType = N''Seed'' AND SupervisorId IS NULL AND PolyhouseId IS NULL AND GrowingMaterialTypeId IS NULL AND CreatedById IS NOT NULL))');

        -- 2) The new trigger.
        SET @sql = N'CREATE TRIGGER dbo.TR_SeedSowings_MotherPlantDirectSowing ON dbo.SeedSowings
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    -- 2026-10-21 (MotherPlantDirectSowing): Mother Plant & Cutting > Direct Sowing. A Direct (seed) Sowing recorded in a
    -- Mother Plant Area carries IsMotherPlantDirectSowing = 1: no Polyhouse, no assigned supervisor, no growing material
    -- (CK_SeedSowings_MotherPlantDirectSowing); approved only by an Area Supervisor of its Area or a System Administrator
    -- (TR_ReadyConfirmations_AssignedSupervisor).
    IF EXISTS (SELECT 1 FROM deleted)
    BEGIN
        -- The workflow and the Area of such a batch are fixed once it is recorded.
        IF UPDATE(IsMotherPlantDirectSowing) AND EXISTS (
            SELECT 1 FROM inserted i JOIN deleted d ON d.Id = i.Id WHERE i.IsMotherPlantDirectSowing <> d.IsMotherPlantDirectSowing)
            THROW 52712, ''Whether a sowing is a Mother Plant Area Direct Sowing cannot be changed after it is recorded.'', 1;
        IF UPDATE(AreaId) AND EXISTS (
            SELECT 1 FROM inserted i JOIN deleted d ON d.Id = i.Id WHERE i.IsMotherPlantDirectSowing = 1 AND i.AreaId <> d.AreaId)
            THROW 52713, ''The Area of a Mother Plant Area Direct Sowing cannot be changed after it is recorded.'', 1;
        RETURN;
    END
    -- A new one is recorded in an ACTIVE Mother Plant Area...
    IF EXISTS (SELECT 1 FROM inserted i LEFT JOIN dbo.Area a ON a.Id = i.AreaId
               WHERE i.IsMotherPlantDirectSowing = 1
                 AND (a.Id IS NULL OR ISNULL(a.IsActive, 0) = 0 OR ISNULL(a.AreaType, N'''') <> N''MotherPlant''))
        THROW 52714, ''A Mother Plant Area Direct Sowing must be recorded in an active Mother Plant Area.'', 1;
    -- ...and a seed sowing in a Mother Plant Area is always one (Seed Production Direct Sowing is Main Areas only).
    IF EXISTS (SELECT 1 FROM inserted i JOIN dbo.Area a ON a.Id = i.AreaId
               WHERE i.SourceType = N''Seed'' AND i.IsMotherPlantDirectSowing = 0 AND a.AreaType = N''MotherPlant'')
        THROW 52715, ''A seed sowing in a Mother Plant Area is recorded through Mother Plant & Cutting > Direct Sowing.'', 1;
END';
        SET @sql = REPLACE(@sql, NCHAR(13) + NCHAR(10), NCHAR(10));
        EXEC (@sql);

        -- 3) TR_SeedSowings_SupervisorRole: the Sowing Supervisor rule skips flagged rows.
        DROP TRIGGER dbo.TR_SeedSowings_SupervisorRole;
        SET @sql = N'CREATE TRIGGER dbo.TR_SeedSowings_SupervisorRole
ON dbo.SeedSowings
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    -- SEED sowing (unchanged, INSERT only): assigned to an active Sowing Supervisor
    -- who is not the person recording it (the assigned supervisor is the only approver).
    -- 2026-10-21 (MotherPlantDirectSowing): a Direct Sowing recorded in a Mother Plant Area has NO assigned supervisor --
    -- an Area Supervisor of its Area approves it (TR_ReadyConfirmations_AssignedSupervisor, TR_SeedSowings_MotherPlantDirectSowing).
    IF NOT EXISTS (SELECT 1 FROM deleted)
       AND EXISTS (SELECT 1
               FROM inserted i
               WHERE i.SourceType NOT IN (N''Cutting'', N''BedSowing'', N''Transplant'')
                 AND i.IsMotherPlantDirectSowing = 0
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

        -- 4) TR_ReadyConfirmations_AssignedSupervisor: Area Supervisor (or System Administrator) for flagged batches.
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
    -- 2026-10-21 (MotherPlantDirectSowing): a Direct Sowing batch of a Mother Plant Area -- only an Area Supervisor of the
    -- batch''s OWN Area (an active user holding ReadyStock.Confirm through a role assigned to that Area) or a System
    -- Administrator, the same rule as a Transplant batch; the user who recorded it may approve it.
    IF EXISTS (SELECT 1
               FROM inserted i
               INNER JOIN dbo.SeedSowings sw ON sw.Id = i.SeedSowingId
               WHERE sw.IsMotherPlantDirectSowing = 1
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
                                                          AND p.Code = N''ReadyStock.Confirm'')))))
        THROW 52711, ''A Direct Sowing batch of a Mother Plant Area is approved only by an Area Supervisor of that Area or a System Administrator.'', 1;
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
                      AND sw.IsMotherPlantDirectSowing = 0
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

        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_SeedSowings_SupervisorRole'))), 2) <> 'C0033E31F5C75753E28C8F9BFF5E0839E1EB8A6B3DB1EDE84F7A5F98213AB57D'
           OR CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_AssignedSupervisor'))), 2) <> '3DCC5F011A768E2529BE2B1421F2BB57A3EAFB71B599EBBA560EEA0625169D8F'
            THROW 52709, 'Post-check failed: a replaced trigger is not the expected new version.', 1;
        IF NOT (COL_LENGTH('dbo.SeedSowings', 'IsMotherPlantDirectSowing') IS NOT NULL AND OBJECT_ID('dbo.TR_SeedSowings_MotherPlantDirectSowing', 'TR') IS NOT NULL AND OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_AssignedSupervisor')) LIKE N'%MotherPlantDirectSowing%' AND OBJECT_DEFINITION(OBJECT_ID('dbo.TR_SeedSowings_SupervisorRole')) LIKE N'%MotherPlantDirectSowing%')
            THROW 52710, 'Post-check failed: the application probe would not see the change.', 1;
        PRINT '=== Mother Plant Area Direct Sowing: column, CHECK, TR_SeedSowings_MotherPlantDirectSowing; SupervisorRole and AssignedSupervisor extended ===';
    END
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION MpDirectSowing1021;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION MpDirectSowing1021;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION MpDirectSowing1021;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
