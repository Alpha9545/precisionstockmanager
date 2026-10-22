-- ============================================================================
-- 2026-10-19_Cavity70_Rollback.sql
--
-- Undoes 2026-10-19_Cavity70.sql: every cavity CHECK constraint and the three triggers go back to their exact earlier
-- definitions (the triggers byte-for-byte; verified by SHA-256), and the '70 Cavity' Material consumption rows are removed.
-- REFUSED (nothing changed) while any 70-cavity data exists -- a sowing, tray pool, tray transfer, Ready Stock batch,
-- transplant or bed transplant of 70 Cavity -- because the earlier constraints would reject those rows.
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to -v TARGET_SERVER=<@@SERVERNAME>.
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52591, '2026-10-19_Cavity70_Rollback.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52592, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52593, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION Cavity70_1019_Rollback;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';
    IF NOT (OBJECT_DEFINITION(OBJECT_ID('dbo.CK_TrayStock_TraySize')) LIKE N'%70 Cavity%')
        PRINT '=== Not applied -- no-op ===';
    ELSE
    BEGIN
        IF EXISTS (SELECT 1 FROM dbo.SeedSowings WHERE CavityType = N'70 Cavity')
           OR EXISTS (SELECT 1 FROM dbo.TrayStock WHERE TraySize = N'70 Cavity')
           OR EXISTS (SELECT 1 FROM dbo.TrayTransfers WHERE TraySize = N'70 Cavity')
           OR EXISTS (SELECT 1 FROM dbo.ReadyStock WHERE CavityType = N'70 Cavity')
           OR EXISTS (SELECT 1 FROM dbo.ReadyStockTransplants WHERE TargetCavityType = N'70 Cavity')
           OR EXISTS (SELECT 1 FROM dbo.BedSowingOperations WHERE CavityType = N'70 Cavity')
           OR EXISTS (SELECT 1 FROM dbo.OutletBookingItems WHERE CavityType = N'70 Cavity')
           OR EXISTS (SELECT 1 FROM dbo.OutletSaleItems WHERE CavityType = N'70 Cavity')
           OR EXISTS (SELECT 1 FROM dbo.OutletWastages WHERE CavityType = N'70 Cavity')
            THROW 52594, '70 Cavity data exists (sowings, trays, transfers, Ready Stock or transplants) -- the rollback would invalidate it; nothing changed.', 1;

        DELETE FROM dbo.MaterialConsumptionRules WHERE CavityType = N'70 Cavity';
        ALTER TABLE dbo.SeedSowings DROP CONSTRAINT CK_SeedSowings_CavityType;
        ALTER TABLE dbo.SeedSowings WITH CHECK ADD CONSTRAINT CK_SeedSowings_CavityType CHECK ([CavityType]='150 Cavity' OR [CavityType]='102 Cavity' OR [CavityType]='42 Cavity' OR [CavityType]='24 Cavity' OR [CavityType]='9 Cavity');
        ALTER TABLE dbo.SeedSowings DROP CONSTRAINT CK_SeedSowings_TrayCount;
        ALTER TABLE dbo.SeedSowings WITH CHECK ADD CONSTRAINT CK_SeedSowings_TrayCount CHECK ([NumberOfTrays] IS NOT NULL AND [NumberOfTrays]>=(1) AND [NumberOfTrays]=floor([QuantitySown]/case [CavityType] when N'9 Cavity' then (9) when N'24 Cavity' then (24) when N'42 Cavity' then (42) when N'102 Cavity' then (102) when N'150 Cavity' then (150)  end));
        ALTER TABLE dbo.SeedSowings DROP CONSTRAINT CK_SeedSowings_SeedQuantity;
        ALTER TABLE dbo.SeedSowings WITH CHECK ADD CONSTRAINT CK_SeedSowings_SeedQuantity CHECK ([SeedQuantity] IS NULL OR [SeedQuantity]=floor([SeedQuantity]) AND [NumberOfTrays] IS NOT NULL AND [QuantitySown]=[NumberOfTrays]*case [CavityType] when N'9 Cavity' then (9) when N'24 Cavity' then (24) when N'42 Cavity' then (42) when N'102 Cavity' then (102) when N'150 Cavity' then (150)  end AND [SeedQuantity]>=[QuantitySown] AND ([SeedQuantity]-[QuantitySown])<case [CavityType] when N'9 Cavity' then (9) when N'24 Cavity' then (24) when N'42 Cavity' then (42) when N'102 Cavity' then (102) when N'150 Cavity' then (150)  end);
        ALTER TABLE dbo.TrayStock DROP CONSTRAINT CK_TrayStock_TraySize;
        ALTER TABLE dbo.TrayStock WITH CHECK ADD CONSTRAINT CK_TrayStock_TraySize CHECK ([TraySize]=N'150 Cavity' OR [TraySize]=N'102 Cavity' OR [TraySize]=N'42 Cavity' OR [TraySize]=N'24 Cavity' OR [TraySize]=N'9 Cavity');
        ALTER TABLE dbo.TrayTransfers DROP CONSTRAINT CK_TrayTransfers_Size;
        ALTER TABLE dbo.TrayTransfers WITH CHECK ADD CONSTRAINT CK_TrayTransfers_Size CHECK ([TraySize]=N'150 Cavity' OR [TraySize]=N'102 Cavity' OR [TraySize]=N'42 Cavity' OR [TraySize]=N'24 Cavity' OR [TraySize]=N'9 Cavity');
        ALTER TABLE dbo.MaterialConsumptionRules DROP CONSTRAINT CK_MaterialConsumptionRules_Cavity;
        ALTER TABLE dbo.MaterialConsumptionRules WITH CHECK ADD CONSTRAINT CK_MaterialConsumptionRules_Cavity CHECK ([CavityType]=N'150 Cavity' OR [CavityType]=N'102 Cavity' OR [CavityType]=N'42 Cavity' OR [CavityType]=N'24 Cavity' OR [CavityType]=N'9 Cavity');
        ALTER TABLE dbo.ReadyStock DROP CONSTRAINT CK_ReadyStock_TransplantCavity;
        ALTER TABLE dbo.ReadyStock WITH CHECK ADD CONSTRAINT CK_ReadyStock_TransplantCavity CHECK ([OriginType]<>N'Transplant' OR ([CavityType]=N'150 Cavity' OR [CavityType]=N'102 Cavity' OR [CavityType]=N'42 Cavity' OR [CavityType]=N'24 Cavity' OR [CavityType]=N'9 Cavity'));
        ALTER TABLE dbo.ReadyStockTransplants DROP CONSTRAINT CK_RsTransplants_Cavity;
        ALTER TABLE dbo.ReadyStockTransplants WITH CHECK ADD CONSTRAINT CK_RsTransplants_Cavity CHECK ([TargetCavityType]=N'150 Cavity' OR [TargetCavityType]=N'102 Cavity' OR [TargetCavityType]=N'42 Cavity' OR [TargetCavityType]=N'24 Cavity' OR [TargetCavityType]=N'9 Cavity');
        ALTER TABLE dbo.ReadyStockTransplants DROP CONSTRAINT CK_RsTransplants_Trays;
        ALTER TABLE dbo.ReadyStockTransplants WITH CHECK ADD CONSTRAINT CK_RsTransplants_Trays CHECK ([OutputTrays]=ceiling([TransplantedQuantity]/case [TargetCavityType] when N'9 Cavity' then (9) when N'24 Cavity' then (24) when N'42 Cavity' then (42) when N'102 Cavity' then (102) when N'150 Cavity' then (150)  end));
        ALTER TABLE dbo.BedSowingOperations DROP CONSTRAINT CK_BedSowingOperations_Shape;
        ALTER TABLE dbo.BedSowingOperations WITH CHECK ADD CONSTRAINT CK_BedSowingOperations_Shape CHECK ([OperationType]=N'Transplant' AND ([CavityType]=N'9 Cavity' OR [CavityType]=N'24 Cavity' OR [CavityType]=N'42 Cavity' OR [CavityType]=N'102 Cavity' OR [CavityType]=N'150 Cavity') AND [OutputTrays]>=(1) AND [Quantity]=[OutputTrays]*case [CavityType] when N'9 Cavity' then (9) when N'24 Cavity' then (24) when N'42 Cavity' then (42) when N'102 Cavity' then (102) when N'150 Cavity' then (150)  end AND [TargetAreaId] IS NOT NULL AND [TargetPolyhouseId] IS NOT NULL AND [SeedSowingId] IS NOT NULL OR [OperationType]=N'Loss' AND [CavityType] IS NULL AND [OutputTrays] IS NULL AND [TargetAreaId] IS NULL AND [TargetPolyhouseId] IS NULL AND [SeedSowingId] IS NULL AND len(ltrim(rtrim(isnull([Reason],N''))))>(0));
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
                                                    WHEN N''42 Cavity'' THEN 42 WHEN N''102 Cavity'' THEN 102
                                                    WHEN N''150 Cavity'' THEN 150 END <> 0)
        THROW 50125, ''Ready Stock quantity must be a whole number of trays of the sowing cavity.'', 1;
END');
        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyStock_WholeTrays'))), 2)
           <> '686650B8B56148844FF5BC0271A6CECF9CE66465EA3F4925EBFA198EF3A930AF'
            THROW 52595, 'TR_ReadyStock_WholeTrays was not restored byte-for-byte -- rolling back.', 1;
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
                                                                                      WHEN N''42 Cavity'' THEN 42 WHEN N''102 Cavity'' THEN 102
                                                                                      WHEN N''150 Cavity'' THEN 150 END
                  OR i.WastageQuantity <> (CASE WHEN (sw.QuantitySown - sw.ConfirmedReadyQuantity - sw.WastageQuantity - i.ConfirmedQuantity) > 0
                                                 THEN (sw.QuantitySown - sw.ConfirmedReadyQuantity - sw.WastageQuantity - i.ConfirmedQuantity)
                                                 ELSE 0 END))
        THROW 50124, ''Approval must be whole Actual Ready Trays; seedlings = trays x the sowing cavity; wastage = the sowing''''s still-expected quantity minus seedlings this approval, never negative.'', 1;
END
');
        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_TrayQuantity'))), 2)
           <> '61FC7262D9B3E2E9F58DD9C54A4BA68855BB6D6E68E0C6F311810500F931F1FC'
            THROW 52595, 'TR_ReadyConfirmations_TrayQuantity was not restored byte-for-byte -- rolling back.', 1;
        EXEC (N'
ALTER TRIGGER dbo.TR_RsTransplants_NewRow ON dbo.ReadyStockTransplants
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    -- 2026-10-16: waste (the untransplanted remainder, or a waste-only closure) always has a reason.
    IF EXISTS (SELECT 1 FROM inserted WHERE LostQuantity > 0 AND LEN(LTRIM(RTRIM(ISNULL(WasteReason, N'''')))) = 0)
        THROW 52439, ''Enter the reason for the seedlings recorded as waste.'', 1;
    -- The transplanted seedlings fill COMPLETE trays of the target cavity -- no partly filled tray.
    IF EXISTS (SELECT 1 FROM inserted
               WHERE TransplantedQuantity <> OutputTrays * CASE TargetCavityType WHEN N''9 Cavity'' THEN 9 WHEN N''24 Cavity'' THEN 24
                                                         WHEN N''42 Cavity'' THEN 42 WHEN N''102 Cavity'' THEN 102 WHEN N''150 Cavity'' THEN 150 END)
        THROW 52440, ''The transplanted seedlings must fill complete trays of the chosen cavity.'', 1;
    -- Stock received for POT PRODUCTION (the batch itself or any batch it derives from) is never transplanted into trays.
    DECLARE @PotPurpose INT;
    WITH chain AS (
        SELECT i.Id AS TransplantId, rs.Id, rs.ParentReadyStockId, rs.OriginType, rs.OriginReferenceId, 0 AS Depth
        FROM inserted i JOIN dbo.ReadyStock rs ON rs.Id = i.SourceReadyStockId
        UNION ALL
        SELECT c.TransplantId, p.Id, p.ParentReadyStockId, p.OriginType, p.OriginReferenceId, c.Depth + 1
        FROM chain c JOIN dbo.ReadyStock p ON p.Id = c.ParentReadyStockId
        WHERE c.Depth < 20)
    SELECT @PotPurpose = COUNT(*) FROM chain c
    JOIN dbo.InternalTransfers t ON t.Id = c.OriginReferenceId AND t.StockType = N''MotherPlantTransfer''
    WHERE c.OriginType = N''Transfer'' AND t.TransferPurpose = N''PotProduction'';
    IF @PotPurpose > 0
        THROW 52441, ''This stock was received for Pot Production and cannot be transplanted into trays.'', 1;
END');
        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_RsTransplants_NewRow'))), 2)
           <> 'C674964D06DA36541DB168841947DB7FE5C43556EBB63AC1E663FDD09D7E5B99'
            THROW 52595, 'TR_RsTransplants_NewRow was not restored byte-for-byte -- rolling back.', 1;
        PRINT '=== 70 Cavity removed: constraints, triggers and Material consumption rows are the earlier versions ===';
    END
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION Cavity70_1019_Rollback;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION Cavity70_1019_Rollback;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION Cavity70_1019_Rollback;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
