-- ============================================================================
-- 2026-10-31_AllSeedlingsDamagedApproval.sql          *** PREPARED FOR REVIEW -- NOT APPLIED ***
--
-- WHY (owner requirement 2026-10-31): Supervisor Approval option "All Seedlings Damaged -- Approve with 0 Ready
--   Seedlings". When every seedling a sowing still expects was damaged, the sowing must be approved and closed with 0 ready
--   seedlings. The existing approval model already records it (ConfirmedQuantity 0, WastageQuantity = the still-expected
--   quantity, WastageReason 'Damaged plants', CK_ReadyConfirmations_Quantity allows 0 + wastage > 0), EXCEPT that two
--   objects refuse 0 Actual Ready Trays:
--     CK_ReadyConfirmations_ActualTrays     ActualTrayQuantity IS NULL OR >= 1
--     TR_ReadyConfirmations_TrayQuantity    ActualTrayQuantity NULL refused; a partial-tray cutting sowing needs exactly 1
--
-- WHAT (only these two objects; each accepts exactly one new shape and keeps every existing rule):
--   1. CK_ReadyConfirmations_ActualTrays   + OR (ActualTrayQuantity = 0 AND ConfirmedQuantity = 0 AND WastageReason = 'Damaged plants')
--   2. TR_ReadyConfirmations_TrayQuantity  + 0 trays => 0 seedlings and reason 'Damaged plants'; the existing wastage rule
--      then makes Wastage = the sowing's still-expected quantity. Every approval of 1 tray or more: unchanged.
--   0 Actual Ready Trays therefore always means "all seedlings damaged" and is stored by nothing else.
--
-- NOT changed: any data row, any other table, constraint, trigger, index or permission. Ready Stock: the batch row the
--   approval references holds 0 (no ledger row); TR_ReadyStock_WholeTrays already accepts 0.
--
-- APPLICATION WITHOUT THIS SCRIPT: WorkflowCorrectionsSchema.AllSeedlingsDamagedAsync is false, so the option is not offered
--   and the server refuses it (fail closed). Ordinary approvals are unchanged either way.
--
-- TARGET SAFEGUARDS (as 2026-10-31_CuttingPartialTraySowing.sql): the local AND the live database can both be named
--   PlantsIMS2_Test. The first batch refuses to continue unless ALL of these hold (nothing is read or written before them):
--     1. DB_NAME() is PlantsIMS2_Test or PlantsIMS2_Scratch_*      2. MODE is DRYRUN or COMMIT (exact upper case)
--     3. @@SERVERNAME = $(TARGET_SERVER)                            4. SERVERPROPERTY('ServerName') = @@SERVERNAME
--     5. SERVERPROPERTY('MachineName') = $(TARGET_MACHINE)          6. DB_NAME() = $(TARGET_DB)
--     7. sys.databases.create_date of THIS database = $(TARGET_DB_CREATED)   (yyyy-mm-ddThh:mi:ss)
--   Then both objects must be EXACTLY the expected earlier version, else nothing changes ("schema drift"): the CHECK by the
--   SHA-256 of its definition; the trigger by the SHA-256 of its definition OR of its text from "ON dbo.ReadyConfirmations"
--   with all whitespace removed (a copy that differs only in line endings / indentation is the same logic). Those versions
--   are the ones 2026-10-31_CuttingPartialTraySowing.sql leaves.
--
-- OPERATOR CHECKLIST
--   a) read-only identity query, keep its output:
--        SELECT @@SERVERNAME AS ServerName, CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) AS ServerProperty,
--               CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) AS MachineName, DB_NAME() AS DatabaseName,
--               CONVERT(CHAR(19), d.create_date, 126) AS DatabaseCreated
--        FROM sys.databases d WHERE d.database_id = DB_ID();
--   b) confirm independently that this machine IS the intended one; c) take a COPY_ONLY backup;
--   d) sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN 'TARGET_SERVER="<ServerName>"' 'TARGET_MACHINE="<MachineName>"'
--             'TARGET_DB="<DatabaseName>"' 'TARGET_DB_CREATED="<DatabaseCreated>"' -i 2026-10-31_AllSeedlingsDamagedApproval.sql
--      (from PowerShell, single-quote each -v argument as shown, or the quotes are lost)
--   It ends "DRY RUN -- rolled back". Then MODE=COMMIT -- only with the owner's explicit approval for that database.
--
-- Requires: 2026-10-31_CuttingPartialTraySowing.sql (run it first). One transaction. Idempotent.
-- Undo: 2026-10-31_AllSeedlingsDamagedApproval_Rollback.sql (refused once a 0-tray approval exists).
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 53741, '2026-10-31_AllSeedlingsDamagedApproval.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' COLLATE Latin1_General_BIN2 NOT IN (N'DRYRUN', N'COMMIT')
    THROW 53742, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT (exact upper case).', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 53743, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
IF CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) <> @@SERVERNAME
    THROW 53744, 'SERVERPROPERTY(''ServerName'') does not match @@SERVERNAME (renamed host?) -- refusing to run.', 1;
IF CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) <> N'$(TARGET_MACHINE)'
    THROW 53745, 'TARGET_MACHINE does not match SERVERPROPERTY(''MachineName'') -- refusing to run on an unexpected host.', 1;
IF DB_NAME() <> N'$(TARGET_DB)'
    THROW 53746, 'TARGET_DB does not match DB_NAME() -- refusing to run against an unexpected database.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND CONVERT(CHAR(19), create_date, 126) = N'$(TARGET_DB_CREATED)')
    THROW 53747, 'TARGET_DB_CREATED does not match this database''s create_date -- refusing to run against an unexpected copy.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION AllSeedlingsDamaged1031;

BEGIN TRY
    DECLARE @Created CHAR(19) = (SELECT CONVERT(CHAR(19), create_date, 126) FROM sys.databases WHERE database_id = DB_ID());
    PRINT '=== Server / machine / database / created: ' + @@SERVERNAME + ' / ' + CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128))
        + ' / ' + DB_NAME() + ' / ' + @Created + ' -- MODE=$(MODE) ===';

    IF OBJECT_DEFINITION(OBJECT_ID('dbo.CK_ReadyConfirmations_ActualTrays')) LIKE N'%Damaged plants%'
       AND OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_TrayQuantity')) LIKE N'%AllSeedlingsDamaged%'
        PRINT '=== Already applied -- no-op ===';
    ELSE
    BEGIN
        IF NOT (OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_TrayQuantity')) LIKE N'%CuttingPartialTray%')
            THROW 53748, 'Run 2026-10-31_CuttingPartialTraySowing.sql first.', 1;
        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.CK_ReadyConfirmations_ActualTrays'))), 2)
           <> '53A66B1472CC90F89F50B4BF677349E85E4B3FC45F5F05BD364C3CCBBA81F171'
            THROW 53749, 'CK_ReadyConfirmations_ActualTrays is not the expected version -- schema drift, nothing changed.', 1;
        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_TrayQuantity'))), 2)
           <> '4A8A7A62296838F15C07FEC482EC0C55EF72E214540B31FDBAB682BBB56E57AD'
           AND CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', REPLACE(REPLACE(REPLACE(REPLACE(SUBSTRING(OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_TrayQuantity')), CHARINDEX(N'ON dbo.ReadyConfirmations', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_TrayQuantity'))), 8000), CHAR(13), N''), CHAR(10), N''), CHAR(9), N''), N' ', N'')), 2)
           <> 'CCC79E1900BD7D01551C64D02B21225689C63A32A049818CE49B313D7E23EAE8'
            THROW 53749, 'TR_ReadyConfirmations_TrayQuantity is not the expected version -- schema drift, nothing changed.', 1;

        PRINT '=== Before: approvals (every one already satisfies the widened rules -- they only add a shape) ===';
        SELECT COUNT(*) AS Approvals, SUM(CASE WHEN ActualTrayQuantity = 0 THEN 1 ELSE 0 END) AS ZeroTrayApprovals FROM dbo.ReadyConfirmations;

        -- 1: the ActualTrays CHECK (re-validated WITH CHECK against every existing row)
        ALTER TABLE dbo.ReadyConfirmations DROP CONSTRAINT CK_ReadyConfirmations_ActualTrays;
        ALTER TABLE dbo.ReadyConfirmations WITH CHECK ADD CONSTRAINT CK_ReadyConfirmations_ActualTrays CHECK (
            [ActualTrayQuantity] IS NULL OR [ActualTrayQuantity]>=(1)
            OR ([ActualTrayQuantity]=(0) AND [ConfirmedQuantity]=(0) AND [WastageReason] IS NOT NULL AND [WastageReason]=N'Damaged plants'));

        -- 2: the approval trigger -- 0 trays = all seedlings damaged
        EXEC (N'
CREATE OR ALTER TRIGGER dbo.TR_ReadyConfirmations_TrayQuantity
ON dbo.ReadyConfirmations
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    -- Seedlings come only from complete trays of the sowing''s own cavity;
    -- wastage is whatever of the sowing''s still-expected quantity did not
    -- become a ready seedling THIS approval (never negative -- an overage
    -- against the original sowing quantity is a legitimate outcome and
    -- produces zero wastage, matching DirectSowingRules.ComputeApproval).
    -- 2026-10-31 CuttingPartialTray: a cutting sowing recorded as ONE partial tray (NumberOfTrays 1, QuantitySown under
    -- one tray) is approved as exactly 1 tray holding the sowing''s own still-expected cuttings -- never 1 x cavity.
    -- 2026-10-31 AllSeedlingsDamaged: 0 Actual Ready Trays records that EVERY seedling the sowing still expects was damaged:
    -- 0 seedlings and the reason ''Damaged plants''; the wastage rule below then makes Wastage = that still-expected quantity.
    IF EXISTS (SELECT 1
               FROM inserted i
               INNER JOIN dbo.SeedSowings sw ON sw.Id = i.SeedSowingId
               CROSS APPLY (SELECT CASE sw.CavityType WHEN N''9 Cavity'' THEN 9 WHEN N''24 Cavity'' THEN 24
                                                      WHEN N''42 Cavity'' THEN 42 WHEN N''70 Cavity'' THEN 70 WHEN N''102 Cavity'' THEN 102
                                                      WHEN N''150 Cavity'' THEN 150 END AS Cavity) c
               CROSS APPLY (SELECT CASE WHEN sw.SourceType = N''Cutting'' AND sw.NumberOfTrays = 1
                                         AND sw.QuantitySown > 0 AND sw.QuantitySown < c.Cavity THEN 1 ELSE 0 END AS IsPartialTray) p
               WHERE i.ActualTrayQuantity IS NULL
                  OR sw.NumberOfTrays IS NULL
                  OR (i.ActualTrayQuantity = 0 AND (i.ConfirmedQuantity <> 0 OR ISNULL(i.WastageReason, N'''') <> N''Damaged plants''))
                  OR (i.ActualTrayQuantity > 0 AND p.IsPartialTray = 0 AND i.ConfirmedQuantity <> i.ActualTrayQuantity * c.Cavity)
                  OR (i.ActualTrayQuantity > 0 AND p.IsPartialTray = 1 AND (i.ActualTrayQuantity <> 1
                                              OR i.ConfirmedQuantity <> sw.QuantitySown - sw.ConfirmedReadyQuantity - sw.WastageQuantity))
                  OR i.WastageQuantity <> (CASE WHEN (sw.QuantitySown - sw.ConfirmedReadyQuantity - sw.WastageQuantity - i.ConfirmedQuantity) > 0
                                                 THEN (sw.QuantitySown - sw.ConfirmedReadyQuantity - sw.WastageQuantity - i.ConfirmedQuantity)
                                                 ELSE 0 END))
        THROW 50124, ''Approval must be whole Actual Ready Trays; seedlings = trays x the sowing cavity; wastage = the sowing''''s still-expected quantity minus seedlings this approval, never negative.'', 1;
END
');

        IF NOT (OBJECT_DEFINITION(OBJECT_ID('dbo.CK_ReadyConfirmations_ActualTrays')) LIKE N'%Damaged plants%'
                AND OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_TrayQuantity')) LIKE N'%AllSeedlingsDamaged%'
                AND OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_TrayQuantity')) LIKE N'%CuttingPartialTray%')
            THROW 53750, 'The application probe (WorkflowCorrectionsSchema.AllSeedlingsDamagedProbeSql) would not see this change -- rolling back.', 1;
        IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_ReadyConfirmations_ActualTrays' AND (is_not_trusted = 1 OR is_disabled = 1))
            THROW 53751, 'The widened CHECK constraint is not trusted -- rolling back.', 1;
        PRINT '=== Applied: 1 CHECK constraint widened, 1 trigger altered ===';
        SELECT name, CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(object_id)), 2) AS DefinitionSha256
        FROM sys.objects WHERE name IN (N'CK_ReadyConfirmations_ActualTrays', N'TR_ReadyConfirmations_TrayQuantity') ORDER BY name;
    END
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION AllSeedlingsDamaged1031;
    THROW;
END CATCH;

IF N'$(MODE)' COLLATE Latin1_General_BIN2 = N'COMMIT'
BEGIN
    COMMIT TRANSACTION AllSeedlingsDamaged1031;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION AllSeedlingsDamaged1031;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
