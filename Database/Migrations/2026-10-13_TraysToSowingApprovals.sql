-- ============================================================================
-- 2026-10-13_TraysToSowingApprovals.sql
--
-- WHY (owner requirements 2026-10-11): transplanting into cavity trays -- from a Bed Sowing (Maya
-- Shinde) or from Ready Stock received in a Mother Plant Area (its Mother Plant Supervisor) -- makes
-- a GROWING TRAY BATCH that goes through the existing Sowing Approvals; its Ready Date starts at the
-- TRANSPLANT date:  Ready Date = actual transplant date + the variety's configured Ready Days.
--   1. dbo.BedSowings: no Ready Date at bed stage -- ReadyStockDays / ExpectedReadyDate become
--      NULLable (existing rows keep their values; nothing is rewritten). CK_BedSowings_ReadyDate
--      allows both NULL, or the old consistent pair.
--   2. dbo.SeedSowings: SourceType 'Transplant' (a tray batch made from received Ready Stock;
--      no seed lot / cutting pool -- CK_SeedSowings_Source widened).
--   3. dbo.ReadyStockTransplants.OutputSeedSowingId: the tray batch a transplant made (instead of an
--      immediate Ready Stock batch). One output kind per row; old rows unchanged.
--   4. No supervisor is chosen for these batches (BedSowing / Transplant): they are approved by any
--      active user who may approve sowings (ReadyStock.Confirm, or System Administrator) -- for a
--      BedSowing batch never by the person who recorded the transplant (the seed-sowing rule).
--      TR_SeedSowings_SupervisorRole and TR_ReadyConfirmations_AssignedSupervisor are replaced
--      accordingly; Seed and Cutting sowings keep EXACTLY their existing rules.
--   5. Ready Date gate: TR_ReadyConfirmations_BedReadyDate also refuses approving a 'Transplant' tray
--      batch before its Ready Date (52208), as for a 'BedSowing' batch (52178, unchanged). Re-created
--      (DROP + CREATE) and set to fire FIRST again.
-- No stock, ledger, sowing or approval row is changed.
--
-- Requires 2026-10-12_BedSowing.sql. SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_*
-- copy AND to -v TARGET_SERVER=<@@SERVERNAME>. One transaction. Idempotent (re-run = no-op). The two
-- three triggers are replaced only if they are byte-for-byte the expected 2026-10-12 versions (SHA-256).
-- Undo: 2026-10-13_TraysToSowingApprovals_Rollback.sql (refuses once the new shapes are used).
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52201, '2026-10-13_TraysToSowingApprovals.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52202, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52203, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
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

BEGIN TRANSACTION TrayApprovals1013;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';
    IF OBJECT_ID('dbo.BedSowings', 'U') IS NULL OR OBJECT_ID('dbo.TR_BedSowingOperations_Transplanter', 'TR') IS NULL
        THROW 52204, 'Run 2026-10-12_BedSowing.sql first.', 1;
    IF OBJECT_ID('dbo.ReadyStockTransplants', 'U') IS NULL
        THROW 52205, 'Run 2026-10-09_ReadyStockTransplanting.sql first.', 1;

    DECLARE @Applied BIT = CASE WHEN COL_LENGTH('dbo.ReadyStockTransplants', 'OutputSeedSowingId') IS NULL THEN 0 ELSE 1 END;
    IF @Applied = 0
    BEGIN
        -- The two triggers must be exactly the versions this script replaces (schema-drift guard).
        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_SeedSowings_SupervisorRole'))), 2)
               <> '7F27B6BAE19A2FC9476E4C42500F84F5CF428D42D40EC966C13B91FA118E994A'
           OR CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_AssignedSupervisor'))), 2)
               <> '45BB189258FE38301D126860B6482081FE53219CC6741D1B39D0E31E934145EF'
           OR CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_BedReadyDate'))), 2)
               <> '393EB10870B6B3EFBE18E56D71258793F0BED489C8FBA8F9E25C7D757BA93B65'
            THROW 52206, 'TR_SeedSowings_SupervisorRole / TR_ReadyConfirmations_AssignedSupervisor / TR_ReadyConfirmations_BedReadyDate are not the expected versions -- schema drift, nothing changed.', 1;
        IF (SELECT definition FROM sys.check_constraints WHERE name = N'CK_SeedSowings_Source')
           <> N'([SourceType]=N''Seed'' AND [SourceSeedStockId] IS NOT NULL AND [SourceCuttingStockId] IS NULL OR [SourceType]=N''Cutting'' AND [SourceCuttingStockId] IS NOT NULL AND [SourceSeedStockId] IS NULL OR [SourceType]=N''BedSowing'' AND [SourceSeedStockId] IS NULL AND [SourceCuttingStockId] IS NULL)'
            THROW 52207, 'CK_SeedSowings_Source is not the expected 2026-10-12 definition -- schema drift, nothing changed.', 1;
    END

    PRINT '=== Before: rows that must not change ===';
    SELECT 'SeedSowings' AS T, COUNT(*) AS Rows FROM dbo.SeedSowings
    UNION ALL SELECT 'ReadyStock', COUNT(*) FROM dbo.ReadyStock
    UNION ALL SELECT 'ReadyStockTransplants', COUNT(*) FROM dbo.ReadyStockTransplants
    UNION ALL SELECT 'ReadyConfirmations', COUNT(*) FROM dbo.ReadyConfirmations
    UNION ALL SELECT 'BedSowings', COUNT(*) FROM dbo.BedSowings;

    IF @Applied = 0
    BEGIN
        -- 1. Bed Sowing: no Ready Date at bed stage.
        ALTER TABLE dbo.BedSowings DROP CONSTRAINT CK_BedSowings_ReadyDate;
        EXEC (N'ALTER TABLE dbo.BedSowings ALTER COLUMN ReadyStockDays INT NULL;');
        EXEC (N'ALTER TABLE dbo.BedSowings ALTER COLUMN ExpectedReadyDate DATE NULL;');
        EXEC (N'ALTER TABLE dbo.BedSowings WITH CHECK ADD CONSTRAINT CK_BedSowings_ReadyDate CHECK (
               (ReadyStockDays IS NULL AND ExpectedReadyDate IS NULL)
            OR (ReadyStockDays > 0 AND ExpectedReadyDate = DATEADD(DAY, ReadyStockDays, SowingDate)));');
        PRINT '=== dbo.BedSowings: Ready Days / Ready Date optional (set only on the tray batches, from the transplant date) ===';

        -- 2. SeedSowings: SourceType ''Transplant''.
        ALTER TABLE dbo.SeedSowings DROP CONSTRAINT CK_SeedSowings_Source;
        EXEC (N'ALTER TABLE dbo.SeedSowings WITH CHECK ADD CONSTRAINT CK_SeedSowings_Source CHECK (
               ([SourceType]=N''Seed'' AND [SourceSeedStockId] IS NOT NULL AND [SourceCuttingStockId] IS NULL)
            OR ([SourceType]=N''Cutting'' AND [SourceCuttingStockId] IS NOT NULL AND [SourceSeedStockId] IS NULL)
            OR ([SourceType]=N''BedSowing'' AND [SourceSeedStockId] IS NULL AND [SourceCuttingStockId] IS NULL)
            OR ([SourceType]=N''Transplant'' AND [SourceSeedStockId] IS NULL AND [SourceCuttingStockId] IS NULL));');
        PRINT '=== CK_SeedSowings_Source widened to SourceType Transplant ===';

        -- 3. ReadyStockTransplants: the output may be a tray batch.
        EXEC (N'ALTER TABLE dbo.ReadyStockTransplants ADD OutputSeedSowingId INT NULL CONSTRAINT FK_RsTransplants_OutputSowing REFERENCES dbo.SeedSowings (Id);');
        EXEC (N'CREATE UNIQUE INDEX UX_RsTransplants_OutputSowing ON dbo.ReadyStockTransplants (OutputSeedSowingId) WHERE OutputSeedSowingId IS NOT NULL;');
        EXEC (N'ALTER TABLE dbo.ReadyStockTransplants WITH CHECK ADD CONSTRAINT CK_RsTransplants_OneOutput CHECK (OutputReadyStockId IS NULL OR OutputSeedSowingId IS NULL);');
        ALTER TABLE dbo.ReadyStockTransplants DROP CONSTRAINT CK_RsTransplants_Status;
        EXEC (N'ALTER TABLE dbo.ReadyStockTransplants WITH CHECK ADD CONSTRAINT CK_RsTransplants_Status CHECK (
               ([Status]=N''Completed'' AND [ReversedAt] IS NULL AND [ReversedById] IS NULL AND [ReversalReason] IS NULL)
            OR ([Status]=N''Reversed'' AND ([OutputReadyStockId] IS NOT NULL OR [OutputSeedSowingId] IS NOT NULL)
                AND [ReversedAt] IS NOT NULL AND [ReversedById] IS NOT NULL AND [ReversalReason] IS NOT NULL));');
        PRINT '=== dbo.ReadyStockTransplants.OutputSeedSowingId added ===';

        -- 4. Supervisor / approver rules for the two new tray-batch origins.
        EXEC (N'ALTER TRIGGER dbo.TR_SeedSowings_SupervisorRole
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
END');
        EXEC (N'ALTER TRIGGER dbo.TR_ReadyConfirmations_AssignedSupervisor
ON dbo.ReadyConfirmations
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    -- SEED: only the assigned supervisor, never the recorder.
    -- CUTTING: the assigned supervisor, or any active user who may approve sowings
    -- (ReadyStock.Confirm through any role, or the full-access System Administrator role).
    -- 2026-10-13 BEDSOWING / TRANSPLANT (no supervisor assigned): any active user who may approve
    -- sowings; a BedSowing batch never by the user who recorded the transplant.
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
               WHERE i.ApprovedById IS NULL
                  OR (sw.SourceType NOT IN (N''Cutting'', N''BedSowing'', N''Transplant'')
                      AND (sw.SupervisorId IS NULL
                           OR i.ApprovedById <> sw.SupervisorId
                           OR (sw.CreatedById IS NOT NULL AND i.ApprovedById = sw.CreatedById)))
                  OR (sw.SourceType = N''Cutting''
                      AND (sw.SupervisorId IS NULL
                           OR (i.ApprovedById <> sw.SupervisorId AND m.MayApprove = 0)))
                  OR (sw.SourceType IN (N''BedSowing'', N''Transplant'') AND m.MayApprove = 0)
                  OR (sw.SourceType = N''BedSowing'' AND sw.CreatedById IS NOT NULL AND i.ApprovedById = sw.CreatedById))
        THROW 50111, ''Only the assigned supervisor (or, for a cutting sowing, a user who may approve sowings) can approve it; a seed sowing never by the user who recorded it.'', 1;
END');
        PRINT '=== TR_SeedSowings_SupervisorRole / TR_ReadyConfirmations_AssignedSupervisor: BedSowing + Transplant batches approved by any approver ===';

        -- 5. Ready Date gate for Transplant tray batches too (fires first, as before).
        DROP TRIGGER dbo.TR_ReadyConfirmations_BedReadyDate;
        EXEC (N'CREATE TRIGGER dbo.TR_ReadyConfirmations_BedReadyDate ON dbo.ReadyConfirmations
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    -- India business date (IST, +05:30, no daylight saving) -- the same day AppClock.Today gives.
    IF EXISTS (SELECT 1 FROM inserted i JOIN dbo.SeedSowings sw ON sw.Id = i.SeedSowingId
               WHERE sw.SourceType = N''BedSowing''
                 AND (sw.ExpectedReadyDate IS NULL OR CAST(SWITCHOFFSET(SYSDATETIMEOFFSET(), ''+05:30'') AS date) < sw.ExpectedReadyDate))
        THROW 52178, ''A tray batch from a Bed Sowing cannot be approved before its Ready Date.'', 1;
    -- 2026-10-13: likewise a tray batch transplanted from received Ready Stock (Ready Date = transplant
    -- date + Ready Days). Direct Seed Sowing and Cutting Sowing are not affected.
    IF EXISTS (SELECT 1 FROM inserted i JOIN dbo.SeedSowings sw ON sw.Id = i.SeedSowingId
               WHERE sw.SourceType = N''Transplant''
                 AND (sw.ExpectedReadyDate IS NULL OR CAST(SWITCHOFFSET(SYSDATETIMEOFFSET(), ''+05:30'') AS date) < sw.ExpectedReadyDate))
        THROW 52208, ''A tray batch transplanted from Ready Stock cannot be approved before its Ready Date.'', 1;
END');
        EXEC sp_settriggerorder @triggername = N'dbo.TR_ReadyConfirmations_BedReadyDate', @order = N'First', @stmttype = N'INSERT';
        PRINT '=== TR_ReadyConfirmations_BedReadyDate: BedSowing + Transplant tray batches approved only from their Ready Date (fires first) ===';
    END
    ELSE PRINT '=== Already applied -- no-op ===';

    PRINT '=== After ===';
    SELECT 'SeedSowings' AS T, COUNT(*) AS Rows FROM dbo.SeedSowings
    UNION ALL SELECT 'ReadyStock', COUNT(*) FROM dbo.ReadyStock
    UNION ALL SELECT 'ReadyStockTransplants', COUNT(*) FROM dbo.ReadyStockTransplants
    UNION ALL SELECT 'ReadyConfirmations', COUNT(*) FROM dbo.ReadyConfirmations
    UNION ALL SELECT 'BedSowings', COUNT(*) FROM dbo.BedSowings;
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION TrayApprovals1013;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION TrayApprovals1013;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION TrayApprovals1013;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
