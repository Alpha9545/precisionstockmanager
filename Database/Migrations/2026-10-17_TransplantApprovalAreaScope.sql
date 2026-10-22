-- ============================================================================
-- 2026-10-17_TransplantApprovalAreaScope.sql
--
-- WHY (owner requirement 2026-10-12, Mother Plant Area transplanting -> Area Ready Stock): a tray batch transplanted in a
--   Mother Plant Area (SeedSowings.SourceType 'Transplant') becomes that Area's Ready Stock when the AREA SUPERVISOR
--   approves it in Sowing Approvals. Until now ANY active user holding ReadyStock.Confirm (in any Area) could approve it.
--
-- WHAT: TR_ReadyConfirmations_AssignedSupervisor (replaced only if it is byte-for-byte the 2026-10-16 version, SHA-256):
--   Transplant -> the approver is active AND (holds ReadyStock.Confirm through a role assigned to the batch's own Area,
--                 or holds the System Administrator role). Seed, Cutting and Bed Sowing rules are unchanged.
--   The Ready Date rule of a Transplant batch (TR_ReadyConfirmations_BedReadyDate) is unchanged.
--
-- NOT changed: no sowing, approval, stock, role or permission row.
-- Requires: 2026-10-16_BedSowingApprover.sql.
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to -v TARGET_SERVER=<@@SERVERNAME>.
-- One transaction. Idempotent (re-run = no-op). Undo: 2026-10-17_TransplantApprovalAreaScope_Rollback.sql.
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52521, '2026-10-17_TransplantApprovalAreaScope.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52522, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52523, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION TransplantScope1017;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';
    IF OBJECT_ID('dbo.BedSowingApprover', 'U') IS NULL
        THROW 52524, 'Run 2026-10-16_BedSowingApprover.sql first.', 1;

    IF OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_AssignedSupervisor')) LIKE N'%TransplantApproverInBatchArea%'
        PRINT '=== Already applied -- no-op ===';
    ELSE
    BEGIN
        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_AssignedSupervisor'))), 2)
           <> '0C58DC69469C32ED9E67F557EA0BCB9690E9F4C09741CC080E02D4017AA6149D'
            THROW 52525, 'TR_ReadyConfirmations_AssignedSupervisor is not the expected 2026-10-16 version -- schema drift, nothing changed.', 1;

        PRINT '=== Transplant tray batches waiting for approval, with their Area Supervisors (who may approve after this change) ===';
        SELECT sw.Id, sw.SowingCode, a.Name AS Area,
               (SELECT STRING_AGG(x.Name, N', ') FROM (SELECT DISTINCT u.Name FROM dbo.UserRoles ur
                    JOIN dbo.IMSUsers u ON u.Id = ur.UserId AND ISNULL(u.IsActive, 0) = 1
                    JOIN dbo.RolePermissions rp ON rp.RoleId = ur.RoleId
                    JOIN dbo.Permissions p ON p.Id = rp.PermissionId AND p.Code = N'ReadyStock.Confirm'
                    WHERE ur.AreaId = sw.AreaId) x) AS AreaSupervisors
        FROM dbo.SeedSowings sw JOIN dbo.Area a ON a.Id = sw.AreaId
        WHERE sw.SourceType = N'Transplant' AND sw.Status = N'Sown';

        EXEC (N'ALTER TRIGGER dbo.TR_ReadyConfirmations_AssignedSupervisor
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
END');
        PRINT '=== TR_ReadyConfirmations_AssignedSupervisor: a Transplant batch only by its Area Supervisor (or a System Administrator) ===';
    END
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION TransplantScope1017;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION TransplantScope1017;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION TransplantScope1017;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
