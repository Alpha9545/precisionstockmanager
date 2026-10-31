-- ============================================================================
-- 2026-10-31_CuttingPartialTraySowing_Rollback.sql          *** PREPARED FOR REVIEW -- NOT APPLIED ***
--
-- Undoes 2026-10-31_CuttingPartialTraySowing.sql: CK_SeedSowings_TrayCount, CK_SeedSowings_SeedQuantity,
-- TR_ReadyConfirmations_TrayQuantity and TR_ReadyStock_WholeTrays go back to their exact earlier definitions (the ones
-- 2026-10-19_Cavity70.sql leaves), each verified by SHA-256 before the transaction can commit.
-- REFUSED (nothing changed) while any partial-tray data exists -- a cutting sowing recorded as one partial tray, or a
-- Ready Stock sowing batch that is not whole trays -- because the earlier rules would reject those rows. Such a sowing
-- would first have to be cancelled through the application (approval cancel, then sowing cancel); nothing here deletes
-- or edits data.
-- Same TARGET safeguards and operator checklist as the forward script (MODE / TARGET_SERVER / TARGET_MACHINE /
-- TARGET_DB / TARGET_DB_CREATED). Run DRYRUN first; COMMIT only with the owner's explicit approval for that database.
-- One transaction. Idempotent (no-op when the forward script is not applied).
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 53721, '2026-10-31_CuttingPartialTraySowing_Rollback.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' COLLATE Latin1_General_BIN2 NOT IN (N'DRYRUN', N'COMMIT')
    THROW 53722, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT (exact upper case).', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 53723, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
IF CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) <> @@SERVERNAME
    THROW 53724, 'SERVERPROPERTY(''ServerName'') does not match @@SERVERNAME (renamed host?) -- refusing to run.', 1;
IF CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) <> N'$(TARGET_MACHINE)'
    THROW 53725, 'TARGET_MACHINE does not match SERVERPROPERTY(''MachineName'') -- refusing to run on an unexpected host.', 1;
IF DB_NAME() <> N'$(TARGET_DB)'
    THROW 53726, 'TARGET_DB does not match DB_NAME() -- refusing to run against an unexpected database.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND CONVERT(CHAR(19), create_date, 126) = N'$(TARGET_DB_CREATED)')
    THROW 53727, 'TARGET_DB_CREATED does not match this database''s create_date -- refusing to run against an unexpected copy.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION CuttingPartialTray1031_Rollback;

BEGIN TRY
    DECLARE @Created CHAR(19) = (SELECT CONVERT(CHAR(19), create_date, 126) FROM sys.databases WHERE database_id = DB_ID());
    PRINT '=== Server / machine / database / created: ' + @@SERVERNAME + ' / ' + CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128))
        + ' / ' + DB_NAME() + ' / ' + @Created + ' -- MODE=$(MODE) ===';
    IF NOT (OBJECT_DEFINITION(OBJECT_ID('dbo.CK_SeedSowings_TrayCount')) LIKE N'%Cutting%'
            OR OBJECT_DEFINITION(OBJECT_ID('dbo.CK_SeedSowings_SeedQuantity')) LIKE N'%Cutting%'
            OR OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_TrayQuantity')) LIKE N'%CuttingPartialTray%'
            OR OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyStock_WholeTrays')) LIKE N'%CuttingPartialTray%')
        PRINT '=== Not applied -- no-op ===';
    ELSE
    BEGIN
        PRINT '=== Partial-tray data (must be none) ===';
        SELECT Id, SowingCode, SourceType, CavityType, NumberOfTrays, QuantitySown, SeedQuantity, Status FROM dbo.SeedSowings
        WHERE NumberOfTrays IS NOT NULL AND QuantitySown <> NumberOfTrays * case CavityType when N'9 Cavity' then (9) when N'24 Cavity' then (24) when N'42 Cavity' then (42) when N'70 Cavity' then (70) when N'102 Cavity' then (102) when N'150 Cavity' then (150)  end;
        IF EXISTS (SELECT 1 FROM dbo.SeedSowings
                   WHERE NumberOfTrays IS NOT NULL AND QuantitySown <> NumberOfTrays * case CavityType when N'9 Cavity' then (9) when N'24 Cavity' then (24) when N'42 Cavity' then (42) when N'70 Cavity' then (70) when N'102 Cavity' then (102) when N'150 Cavity' then (150)  end)
            THROW 53728, 'A cutting sowing is recorded as one partial tray -- the earlier constraints would reject it. Nothing changed.', 1;
        IF EXISTS (SELECT 1 FROM dbo.ReadyStock
                   WHERE OriginType = N'Sowing' AND Quantity % case CavityType when N'9 Cavity' then (9) when N'24 Cavity' then (24) when N'42 Cavity' then (42) when N'70 Cavity' then (70) when N'102 Cavity' then (102) when N'150 Cavity' then (150)  end <> 0)
            THROW 53729, 'A Ready Stock sowing batch is not whole trays (a partial-tray batch) -- the earlier trigger would reject it. Nothing changed.', 1;

        ALTER TABLE dbo.SeedSowings DROP CONSTRAINT CK_SeedSowings_TrayCount;
        ALTER TABLE dbo.SeedSowings WITH CHECK ADD CONSTRAINT CK_SeedSowings_TrayCount CHECK ([NumberOfTrays] IS NOT NULL AND [NumberOfTrays]>=(1) AND [NumberOfTrays]=floor([QuantitySown]/case [CavityType] when N'9 Cavity' then (9) when N'24 Cavity' then (24) when N'42 Cavity' then (42) when N'70 Cavity' then (70) when N'102 Cavity' then (102) when N'150 Cavity' then (150)  end));
        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.CK_SeedSowings_TrayCount'))), 2)
           <> '1811E48C16A70593D52EC1C0D3B671753FD6BDBC4AAE124872C95AA51AA6BCD5'
            THROW 53730, 'CK_SeedSowings_TrayCount was not restored exactly -- rolling back.', 1;
        ALTER TABLE dbo.SeedSowings DROP CONSTRAINT CK_SeedSowings_SeedQuantity;
        ALTER TABLE dbo.SeedSowings WITH CHECK ADD CONSTRAINT CK_SeedSowings_SeedQuantity CHECK ([SeedQuantity] IS NULL OR [SeedQuantity]=floor([SeedQuantity]) AND [NumberOfTrays] IS NOT NULL AND [QuantitySown]=[NumberOfTrays]*case [CavityType] when N'9 Cavity' then (9) when N'24 Cavity' then (24) when N'42 Cavity' then (42) when N'70 Cavity' then (70) when N'102 Cavity' then (102) when N'150 Cavity' then (150)  end AND [SeedQuantity]>=[QuantitySown] AND ([SeedQuantity]-[QuantitySown])<case [CavityType] when N'9 Cavity' then (9) when N'24 Cavity' then (24) when N'42 Cavity' then (42) when N'70 Cavity' then (70) when N'102 Cavity' then (102) when N'150 Cavity' then (150)  end);
        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.CK_SeedSowings_SeedQuantity'))), 2)
           <> 'CDBDD099D3151355B1DB726EBADCCC29B224F0E76FC1924B820528C88A610AF6'
            THROW 53730, 'CK_SeedSowings_SeedQuantity was not restored exactly -- rolling back.', 1;

        -- The two triggers: the exact text of 2026-10-19_Cavity70.sql (byte-for-byte; verified below).
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
    IF EXISTS (SELECT 1
               FROM inserted i
               INNER JOIN dbo.SeedSowings sw ON sw.Id = i.SeedSowingId
               WHERE i.ActualTrayQuantity IS NULL
                  OR sw.NumberOfTrays IS NULL
                  OR i.ConfirmedQuantity <> i.ActualTrayQuantity * CASE sw.CavityType WHEN N''9 Cavity'' THEN 9 WHEN N''24 Cavity'' THEN 24
                                                                                      WHEN N''42 Cavity'' THEN 42 WHEN N''70 Cavity'' THEN 70 WHEN N''102 Cavity'' THEN 102
                                                                                      WHEN N''150 Cavity'' THEN 150 END
                  OR i.WastageQuantity <> (CASE WHEN (sw.QuantitySown - sw.ConfirmedReadyQuantity - sw.WastageQuantity - i.ConfirmedQuantity) > 0
                                                 THEN (sw.QuantitySown - sw.ConfirmedReadyQuantity - sw.WastageQuantity - i.ConfirmedQuantity)
                                                 ELSE 0 END))
        THROW 50124, ''Approval must be whole Actual Ready Trays; seedlings = trays x the sowing cavity; wastage = the sowing''''s still-expected quantity minus seedlings this approval, never negative.'', 1;
END
');
        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_TrayQuantity'))), 2)
           <> 'C6FA08B87C055F0D9E9B5F136D47E67001D4D32E0A7426104B59EC896CC995EC'
           AND CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', REPLACE(REPLACE(REPLACE(REPLACE(SUBSTRING(OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_TrayQuantity')), CHARINDEX(N'ON dbo.ReadyConfirmations', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_TrayQuantity'))), 8000), CHAR(13), N''), CHAR(10), N''), CHAR(9), N''), N' ', N'')), 2)
           <> '36E0381FE2FF925C6678310BF34AA100548C45FC1B7EA4BBACDF4C4B9E6E898A'
            THROW 53730, 'TR_ReadyConfirmations_TrayQuantity was not restored byte-for-byte -- rolling back.', 1;
        EXEC (N'
CREATE OR ALTER TRIGGER dbo.TR_ReadyStock_WholeTrays
ON dbo.ReadyStock
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT UPDATE(Quantity) RETURN;
    -- Only a NEW or CHANGED Quantity of a SOWING batch is tested. Received (''Transfer'') and
    -- transplanted (''Transplant'') batches keep exact plant counts (partial last tray allowed).
    IF EXISTS (SELECT 1
               FROM inserted i
               LEFT JOIN deleted d ON d.Id = i.Id
               WHERE i.OriginType = N''Sowing''
                 AND (d.Id IS NULL OR d.Quantity <> i.Quantity)
                 AND i.Quantity % CASE i.CavityType WHEN N''9 Cavity'' THEN 9 WHEN N''24 Cavity'' THEN 24
                                                    WHEN N''42 Cavity'' THEN 42 WHEN N''70 Cavity'' THEN 70 WHEN N''102 Cavity'' THEN 102
                                                    WHEN N''150 Cavity'' THEN 150 END <> 0)
        THROW 50125, ''Ready Stock quantity must be a whole number of trays of the sowing cavity.'', 1;
END');
        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyStock_WholeTrays'))), 2)
           <> '1C81A4A2C6C0B91D4AA193080498CF0C137C3F9EA687EB7161032B8255BBD5B8'
           AND CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', REPLACE(REPLACE(REPLACE(REPLACE(SUBSTRING(OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyStock_WholeTrays')), CHARINDEX(N'ON dbo.ReadyStock', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyStock_WholeTrays'))), 8000), CHAR(13), N''), CHAR(10), N''), CHAR(9), N''), N' ', N'')), 2)
           <> '605D421A435C63EC476F6A350595BEA633CEBF378422798C81590BFE790CD506'
            THROW 53730, 'TR_ReadyStock_WholeTrays was not restored byte-for-byte -- rolling back.', 1;
        PRINT '=== Rolled back: the 4 objects are their earlier definitions again ===';
    END
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION CuttingPartialTray1031_Rollback;
    THROW;
END CATCH;

IF N'$(MODE)' COLLATE Latin1_General_BIN2 = N'COMMIT'
BEGIN
    COMMIT TRANSACTION CuttingPartialTray1031_Rollback;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION CuttingPartialTray1031_Rollback;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
