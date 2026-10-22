-- ============================================================================
-- 2026-10-17_TransplantApprovalAreaScope_Rollback.sql
--
-- Undoes 2026-10-17_TransplantApprovalAreaScope.sql: TR_ReadyConfirmations_AssignedSupervisor goes back to the exact
-- 2026-10-16 version (a Transplant batch by any active ReadyStock.Confirm holder / System Administrator; SHA-256 checked).
-- Approvals already recorded stay valid and unchanged.
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to -v TARGET_SERVER=<@@SERVERNAME>.
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52531, '2026-10-17_TransplantApprovalAreaScope_Rollback.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52532, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52533, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION TransplantScope1017Rollback;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';
    IF OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_AssignedSupervisor')) NOT LIKE N'%TransplantApproverInBatchArea%'
        PRINT '=== Not applied -- no-op ===';
    ELSE
    BEGIN
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
                  OR (sw.SourceType = N''Transplant'' AND m.MayApprove = 0)))
        THROW 50111, ''Only the assigned supervisor (or, for a cutting sowing, a user who may approve sowings) can approve it; a seed sowing never by the user who recorded it.'', 1;
END');
        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_AssignedSupervisor'))), 2)
           <> '0C58DC69469C32ED9E67F557EA0BCB9690E9F4C09741CC080E02D4017AA6149D'
            THROW 52534, 'The restored trigger is not byte-for-byte the 2026-10-16 version -- rolled back.', 1;
        PRINT '=== Rolled back to the 2026-10-16 approval rule ===';
    END
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION TransplantScope1017Rollback;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION TransplantScope1017Rollback;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION TransplantScope1017Rollback;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
