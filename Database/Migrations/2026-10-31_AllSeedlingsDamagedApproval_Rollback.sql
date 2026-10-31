-- ============================================================================
-- 2026-10-31_AllSeedlingsDamagedApproval_Rollback.sql          *** PREPARED FOR REVIEW -- NOT APPLIED ***
--
-- Undoes 2026-10-31_AllSeedlingsDamagedApproval.sql: CK_ReadyConfirmations_ActualTrays and
-- TR_ReadyConfirmations_TrayQuantity go back to their exact earlier definitions (the ones
-- 2026-10-31_CuttingPartialTraySowing.sql leaves), each verified before the transaction can commit (the CHECK by SHA-256;
-- the trigger by SHA-256 or by its whitespace-insensitive SHA-256, so a copy of this file with other line endings works).
-- REFUSED (nothing changed) while any approval with 0 Actual Ready Trays exists -- Confirmed OR Cancelled -- because the
-- earlier CHECK would reject those rows. Nothing here deletes or edits data.
-- Same TARGET safeguards and operator checklist as the forward script (MODE / TARGET_SERVER / TARGET_MACHINE /
-- TARGET_DB / TARGET_DB_CREATED). Run DRYRUN first; COMMIT only with the owner's explicit approval for that database.
-- One transaction. Idempotent (no-op when the forward script is not applied).
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 53761, '2026-10-31_AllSeedlingsDamagedApproval_Rollback.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' COLLATE Latin1_General_BIN2 NOT IN (N'DRYRUN', N'COMMIT')
    THROW 53762, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT (exact upper case).', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 53763, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
IF CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) <> @@SERVERNAME
    THROW 53764, 'SERVERPROPERTY(''ServerName'') does not match @@SERVERNAME (renamed host?) -- refusing to run.', 1;
IF CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) <> N'$(TARGET_MACHINE)'
    THROW 53765, 'TARGET_MACHINE does not match SERVERPROPERTY(''MachineName'') -- refusing to run on an unexpected host.', 1;
IF DB_NAME() <> N'$(TARGET_DB)'
    THROW 53766, 'TARGET_DB does not match DB_NAME() -- refusing to run against an unexpected database.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND CONVERT(CHAR(19), create_date, 126) = N'$(TARGET_DB_CREATED)')
    THROW 53767, 'TARGET_DB_CREATED does not match this database''s create_date -- refusing to run against an unexpected copy.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION AllSeedlingsDamaged1031_Rollback;

BEGIN TRY
    DECLARE @Created CHAR(19) = (SELECT CONVERT(CHAR(19), create_date, 126) FROM sys.databases WHERE database_id = DB_ID());
    PRINT '=== Server / machine / database / created: ' + @@SERVERNAME + ' / ' + CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128))
        + ' / ' + DB_NAME() + ' / ' + @Created + ' -- MODE=$(MODE) ===';
    IF NOT (OBJECT_DEFINITION(OBJECT_ID('dbo.CK_ReadyConfirmations_ActualTrays')) LIKE N'%Damaged plants%'
            OR OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_TrayQuantity')) LIKE N'%AllSeedlingsDamaged%')
        PRINT '=== Not applied -- no-op ===';
    ELSE
    BEGIN
        PRINT '=== 0-tray (all seedlings damaged) approvals (must be none) ===';
        SELECT Id, ConfirmationCode, SeedSowingId, Status, WastageQuantity, WastageReason FROM dbo.ReadyConfirmations WHERE ActualTrayQuantity = 0;
        IF EXISTS (SELECT 1 FROM dbo.ReadyConfirmations WHERE ActualTrayQuantity = 0)
            THROW 53768, 'An approval with 0 Actual Ready Trays (all seedlings damaged) exists -- the earlier CHECK would reject it. Nothing changed.', 1;

        ALTER TABLE dbo.ReadyConfirmations DROP CONSTRAINT CK_ReadyConfirmations_ActualTrays;
        ALTER TABLE dbo.ReadyConfirmations WITH CHECK ADD CONSTRAINT CK_ReadyConfirmations_ActualTrays CHECK ([ActualTrayQuantity] IS NULL OR [ActualTrayQuantity]>=(1));
        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.CK_ReadyConfirmations_ActualTrays'))), 2)
           <> '53A66B1472CC90F89F50B4BF677349E85E4B3FC45F5F05BD364C3CCBBA81F171'
            THROW 53770, 'CK_ReadyConfirmations_ActualTrays was not restored exactly -- rolling back.', 1;

        -- The trigger: the exact text of 2026-10-31_CuttingPartialTraySowing.sql (verified below).
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
                  OR (p.IsPartialTray = 0 AND i.ConfirmedQuantity <> i.ActualTrayQuantity * c.Cavity)
                  OR (p.IsPartialTray = 1 AND (i.ActualTrayQuantity <> 1
                                              OR i.ConfirmedQuantity <> sw.QuantitySown - sw.ConfirmedReadyQuantity - sw.WastageQuantity))
                  OR i.WastageQuantity <> (CASE WHEN (sw.QuantitySown - sw.ConfirmedReadyQuantity - sw.WastageQuantity - i.ConfirmedQuantity) > 0
                                                 THEN (sw.QuantitySown - sw.ConfirmedReadyQuantity - sw.WastageQuantity - i.ConfirmedQuantity)
                                                 ELSE 0 END))
        THROW 50124, ''Approval must be whole Actual Ready Trays; seedlings = trays x the sowing cavity; wastage = the sowing''''s still-expected quantity minus seedlings this approval, never negative.'', 1;
END
');
        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_TrayQuantity'))), 2)
           <> '4A8A7A62296838F15C07FEC482EC0C55EF72E214540B31FDBAB682BBB56E57AD'
           AND CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', REPLACE(REPLACE(REPLACE(REPLACE(SUBSTRING(OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_TrayQuantity')), CHARINDEX(N'ON dbo.ReadyConfirmations', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_TrayQuantity'))), 8000), CHAR(13), N''), CHAR(10), N''), CHAR(9), N''), N' ', N'')), 2)
           <> 'CCC79E1900BD7D01551C64D02B21225689C63A32A049818CE49B313D7E23EAE8'
            THROW 53770, 'TR_ReadyConfirmations_TrayQuantity was not restored exactly -- rolling back.', 1;
        PRINT '=== Rolled back: the 2 objects are their earlier definitions again ===';
    END
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION AllSeedlingsDamaged1031_Rollback;
    THROW;
END CATCH;

IF N'$(MODE)' COLLATE Latin1_General_BIN2 = N'COMMIT'
BEGIN
    COMMIT TRANSACTION AllSeedlingsDamaged1031_Rollback;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION AllSeedlingsDamaged1031_Rollback;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
