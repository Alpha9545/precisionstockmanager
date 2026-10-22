-- ============================================================================
-- 2026-10-19_Cavity70.sql
--
-- WHY (owner requirement 2026-10-12, "Add 70-cavity tray option"): a 70-cavity tray (one full tray = 70 plants) joins
--   the closed cavity list 9 / 24 / 42 / 102 / 150. The application list is DirectSowingRules.CavityTypes; the database
--   repeats that closed list (and the cavity -> plants-per-tray mapping) in 10 CHECK constraints and 3 triggers, which
--   would refuse every 70-cavity row. Without this migration the application never offers 70 Cavity
--   (WorkflowCorrectionsSchema.Cavity70ProbeSql) and refuses to stock 70-cavity trays.
--
-- WHAT (one transaction; nothing else changes -- the five existing cavities keep exactly their rules):
--   CHECK constraints dropped and re-added WITH CHECK, each with '70 Cavity' / 70 added:
--     SeedSowings          CK_SeedSowings_CavityType, CK_SeedSowings_TrayCount, CK_SeedSowings_SeedQuantity
--     TrayStock            CK_TrayStock_TraySize          TrayTransfers  CK_TrayTransfers_Size
--     ReadyStock           CK_ReadyStock_TransplantCavity
--     ReadyStockTransplants CK_RsTransplants_Cavity, CK_RsTransplants_Trays
--     BedSowingOperations  CK_BedSowingOperations_Shape   MaterialConsumptionRules CK_MaterialConsumptionRules_Cavity
--   Triggers (replaced only if byte-for-byte the expected current version, SHA-256), WHEN N'70 Cavity' THEN 70 added:
--     TR_ReadyStock_WholeTrays, TR_ReadyConfirmations_TrayQuantity, TR_RsTransplants_NewRow
--   Reference data: dbo.MaterialConsumptionRules gets one ACTIVE '70 Cavity' row per material type that already has
--     cavity rules (Cocopeat, Pitmos) at 1.5 kg / tray -- the value every existing cavity uses (2026-10-08_MaterialStock.sql
--     seeded 1.5 for all). Change it in Material Stock > Settings if 70-cavity trays take a different amount.
--
-- NOT changed: no sowing, tray, stock, approval, booking, role or permission row; Outlet*_StockType constraints do not
--   list cavities and are untouched.
-- Requires: 2026-10-08_MaterialStock.sql, 2026-10-09_ReadyStockTransplanting.sql, 2026-10-10_TrayTransfers.sql,
--   2026-10-12_BedSowing.sql, 2026-10-16_MotherPlantTransplantWaste.sql (the trigger versions this replaces).
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to -v TARGET_SERVER=<@@SERVERNAME>.
-- One transaction. Idempotent (re-run = no-op). Undo: 2026-10-19_Cavity70_Rollback.sql (refused once 70-cavity data exists).
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52581, '2026-10-19_Cavity70.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52582, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52583, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION Cavity70_1019;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';
    IF OBJECT_ID('dbo.MaterialConsumptionRules', 'U') IS NULL OR OBJECT_ID('dbo.ReadyStockTransplants', 'U') IS NULL
       OR OBJECT_ID('dbo.TrayTransfers', 'U') IS NULL OR OBJECT_ID('dbo.BedSowingOperations', 'U') IS NULL
       OR COL_LENGTH('dbo.ReadyStockTransplants', 'WasteReason') IS NULL
        THROW 52584, 'Run 2026-10-08_MaterialStock, 2026-10-09_ReadyStockTransplanting, 2026-10-10_TrayTransfers, 2026-10-12_BedSowing and 2026-10-16_MotherPlantTransplantWaste first.', 1;

    IF OBJECT_DEFINITION(OBJECT_ID('dbo.CK_TrayStock_TraySize')) LIKE N'%70 Cavity%'
        PRINT '=== Already applied -- no-op ===';
    ELSE
    BEGIN
        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyStock_WholeTrays'))), 2)
           <> '686650B8B56148844FF5BC0271A6CECF9CE66465EA3F4925EBFA198EF3A930AF'
            THROW 52585, 'TR_ReadyStock_WholeTrays is not the expected version -- schema drift, nothing changed.', 1;
        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_TrayQuantity'))), 2)
           <> '61FC7262D9B3E2E9F58DD9C54A4BA68855BB6D6E68E0C6F311810500F931F1FC'
            THROW 52585, 'TR_ReadyConfirmations_TrayQuantity is not the expected version -- schema drift, nothing changed.', 1;
        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_RsTransplants_NewRow'))), 2)
           <> 'C674964D06DA36541DB168841947DB7FE5C43556EBB63AC1E663FDD09D7E5B99'
            THROW 52585, 'TR_RsTransplants_NewRow is not the expected version -- schema drift, nothing changed.', 1;
        -- every existing row already satisfies the widened constraints (the list only grows)
        IF OBJECT_DEFINITION(OBJECT_ID('dbo.CK_SeedSowings_CavityType')) <> N'([CavityType]=''150 Cavity'' OR [CavityType]=''102 Cavity'' OR [CavityType]=''42 Cavity'' OR [CavityType]=''24 Cavity'' OR [CavityType]=''9 Cavity'')'
            THROW 52586, 'CK_SeedSowings_CavityType is not the expected definition -- schema drift, nothing changed.', 1;
        ALTER TABLE dbo.SeedSowings DROP CONSTRAINT CK_SeedSowings_CavityType;
        ALTER TABLE dbo.SeedSowings WITH CHECK ADD CONSTRAINT CK_SeedSowings_CavityType CHECK ([CavityType]='150 Cavity' OR [CavityType]='102 Cavity' OR [CavityType]='42 Cavity' OR [CavityType]='70 Cavity' OR [CavityType]='24 Cavity' OR [CavityType]='9 Cavity');
        IF OBJECT_DEFINITION(OBJECT_ID('dbo.CK_SeedSowings_TrayCount')) <> N'([NumberOfTrays] IS NOT NULL AND [NumberOfTrays]>=(1) AND [NumberOfTrays]=floor([QuantitySown]/case [CavityType] when N''9 Cavity'' then (9) when N''24 Cavity'' then (24) when N''42 Cavity'' then (42) when N''102 Cavity'' then (102) when N''150 Cavity'' then (150)  end))'
            THROW 52586, 'CK_SeedSowings_TrayCount is not the expected definition -- schema drift, nothing changed.', 1;
        ALTER TABLE dbo.SeedSowings DROP CONSTRAINT CK_SeedSowings_TrayCount;
        ALTER TABLE dbo.SeedSowings WITH CHECK ADD CONSTRAINT CK_SeedSowings_TrayCount CHECK ([NumberOfTrays] IS NOT NULL AND [NumberOfTrays]>=(1) AND [NumberOfTrays]=floor([QuantitySown]/case [CavityType] when N'9 Cavity' then (9) when N'24 Cavity' then (24) when N'42 Cavity' then (42) when N'70 Cavity' then (70) when N'102 Cavity' then (102) when N'150 Cavity' then (150)  end));
        IF OBJECT_DEFINITION(OBJECT_ID('dbo.CK_SeedSowings_SeedQuantity')) <> N'([SeedQuantity] IS NULL OR [SeedQuantity]=floor([SeedQuantity]) AND [NumberOfTrays] IS NOT NULL AND [QuantitySown]=[NumberOfTrays]*case [CavityType] when N''9 Cavity'' then (9) when N''24 Cavity'' then (24) when N''42 Cavity'' then (42) when N''102 Cavity'' then (102) when N''150 Cavity'' then (150)  end AND [SeedQuantity]>=[QuantitySown] AND ([SeedQuantity]-[QuantitySown])<case [CavityType] when N''9 Cavity'' then (9) when N''24 Cavity'' then (24) when N''42 Cavity'' then (42) when N''102 Cavity'' then (102) when N''150 Cavity'' then (150)  end)'
            THROW 52586, 'CK_SeedSowings_SeedQuantity is not the expected definition -- schema drift, nothing changed.', 1;
        ALTER TABLE dbo.SeedSowings DROP CONSTRAINT CK_SeedSowings_SeedQuantity;
        ALTER TABLE dbo.SeedSowings WITH CHECK ADD CONSTRAINT CK_SeedSowings_SeedQuantity CHECK ([SeedQuantity] IS NULL OR [SeedQuantity]=floor([SeedQuantity]) AND [NumberOfTrays] IS NOT NULL AND [QuantitySown]=[NumberOfTrays]*case [CavityType] when N'9 Cavity' then (9) when N'24 Cavity' then (24) when N'42 Cavity' then (42) when N'70 Cavity' then (70) when N'102 Cavity' then (102) when N'150 Cavity' then (150)  end AND [SeedQuantity]>=[QuantitySown] AND ([SeedQuantity]-[QuantitySown])<case [CavityType] when N'9 Cavity' then (9) when N'24 Cavity' then (24) when N'42 Cavity' then (42) when N'70 Cavity' then (70) when N'102 Cavity' then (102) when N'150 Cavity' then (150)  end);
        IF OBJECT_DEFINITION(OBJECT_ID('dbo.CK_TrayStock_TraySize')) <> N'([TraySize]=N''150 Cavity'' OR [TraySize]=N''102 Cavity'' OR [TraySize]=N''42 Cavity'' OR [TraySize]=N''24 Cavity'' OR [TraySize]=N''9 Cavity'')'
            THROW 52586, 'CK_TrayStock_TraySize is not the expected definition -- schema drift, nothing changed.', 1;
        ALTER TABLE dbo.TrayStock DROP CONSTRAINT CK_TrayStock_TraySize;
        ALTER TABLE dbo.TrayStock WITH CHECK ADD CONSTRAINT CK_TrayStock_TraySize CHECK ([TraySize]=N'150 Cavity' OR [TraySize]=N'102 Cavity' OR [TraySize]=N'42 Cavity' OR [TraySize]=N'70 Cavity' OR [TraySize]=N'24 Cavity' OR [TraySize]=N'9 Cavity');
        IF OBJECT_DEFINITION(OBJECT_ID('dbo.CK_TrayTransfers_Size')) <> N'([TraySize]=N''150 Cavity'' OR [TraySize]=N''102 Cavity'' OR [TraySize]=N''42 Cavity'' OR [TraySize]=N''24 Cavity'' OR [TraySize]=N''9 Cavity'')'
            THROW 52586, 'CK_TrayTransfers_Size is not the expected definition -- schema drift, nothing changed.', 1;
        ALTER TABLE dbo.TrayTransfers DROP CONSTRAINT CK_TrayTransfers_Size;
        ALTER TABLE dbo.TrayTransfers WITH CHECK ADD CONSTRAINT CK_TrayTransfers_Size CHECK ([TraySize]=N'150 Cavity' OR [TraySize]=N'102 Cavity' OR [TraySize]=N'42 Cavity' OR [TraySize]=N'70 Cavity' OR [TraySize]=N'24 Cavity' OR [TraySize]=N'9 Cavity');
        IF OBJECT_DEFINITION(OBJECT_ID('dbo.CK_MaterialConsumptionRules_Cavity')) <> N'([CavityType]=N''150 Cavity'' OR [CavityType]=N''102 Cavity'' OR [CavityType]=N''42 Cavity'' OR [CavityType]=N''24 Cavity'' OR [CavityType]=N''9 Cavity'')'
            THROW 52586, 'CK_MaterialConsumptionRules_Cavity is not the expected definition -- schema drift, nothing changed.', 1;
        ALTER TABLE dbo.MaterialConsumptionRules DROP CONSTRAINT CK_MaterialConsumptionRules_Cavity;
        ALTER TABLE dbo.MaterialConsumptionRules WITH CHECK ADD CONSTRAINT CK_MaterialConsumptionRules_Cavity CHECK ([CavityType]=N'150 Cavity' OR [CavityType]=N'102 Cavity' OR [CavityType]=N'42 Cavity' OR [CavityType]=N'70 Cavity' OR [CavityType]=N'24 Cavity' OR [CavityType]=N'9 Cavity');
        IF OBJECT_DEFINITION(OBJECT_ID('dbo.CK_ReadyStock_TransplantCavity')) <> N'([OriginType]<>N''Transplant'' OR ([CavityType]=N''150 Cavity'' OR [CavityType]=N''102 Cavity'' OR [CavityType]=N''42 Cavity'' OR [CavityType]=N''24 Cavity'' OR [CavityType]=N''9 Cavity''))'
            THROW 52586, 'CK_ReadyStock_TransplantCavity is not the expected definition -- schema drift, nothing changed.', 1;
        ALTER TABLE dbo.ReadyStock DROP CONSTRAINT CK_ReadyStock_TransplantCavity;
        ALTER TABLE dbo.ReadyStock WITH CHECK ADD CONSTRAINT CK_ReadyStock_TransplantCavity CHECK ([OriginType]<>N'Transplant' OR ([CavityType]=N'150 Cavity' OR [CavityType]=N'102 Cavity' OR [CavityType]=N'42 Cavity' OR [CavityType]=N'70 Cavity' OR [CavityType]=N'24 Cavity' OR [CavityType]=N'9 Cavity'));
        IF OBJECT_DEFINITION(OBJECT_ID('dbo.CK_RsTransplants_Cavity')) <> N'([TargetCavityType]=N''150 Cavity'' OR [TargetCavityType]=N''102 Cavity'' OR [TargetCavityType]=N''42 Cavity'' OR [TargetCavityType]=N''24 Cavity'' OR [TargetCavityType]=N''9 Cavity'')'
            THROW 52586, 'CK_RsTransplants_Cavity is not the expected definition -- schema drift, nothing changed.', 1;
        ALTER TABLE dbo.ReadyStockTransplants DROP CONSTRAINT CK_RsTransplants_Cavity;
        ALTER TABLE dbo.ReadyStockTransplants WITH CHECK ADD CONSTRAINT CK_RsTransplants_Cavity CHECK ([TargetCavityType]=N'150 Cavity' OR [TargetCavityType]=N'102 Cavity' OR [TargetCavityType]=N'42 Cavity' OR [TargetCavityType]=N'70 Cavity' OR [TargetCavityType]=N'24 Cavity' OR [TargetCavityType]=N'9 Cavity');
        IF OBJECT_DEFINITION(OBJECT_ID('dbo.CK_RsTransplants_Trays')) <> N'([OutputTrays]=ceiling([TransplantedQuantity]/case [TargetCavityType] when N''9 Cavity'' then (9) when N''24 Cavity'' then (24) when N''42 Cavity'' then (42) when N''102 Cavity'' then (102) when N''150 Cavity'' then (150)  end))'
            THROW 52586, 'CK_RsTransplants_Trays is not the expected definition -- schema drift, nothing changed.', 1;
        ALTER TABLE dbo.ReadyStockTransplants DROP CONSTRAINT CK_RsTransplants_Trays;
        ALTER TABLE dbo.ReadyStockTransplants WITH CHECK ADD CONSTRAINT CK_RsTransplants_Trays CHECK ([OutputTrays]=ceiling([TransplantedQuantity]/case [TargetCavityType] when N'9 Cavity' then (9) when N'24 Cavity' then (24) when N'42 Cavity' then (42) when N'70 Cavity' then (70) when N'102 Cavity' then (102) when N'150 Cavity' then (150)  end));
        IF OBJECT_DEFINITION(OBJECT_ID('dbo.CK_BedSowingOperations_Shape')) <> N'([OperationType]=N''Transplant'' AND ([CavityType]=N''9 Cavity'' OR [CavityType]=N''24 Cavity'' OR [CavityType]=N''42 Cavity'' OR [CavityType]=N''102 Cavity'' OR [CavityType]=N''150 Cavity'') AND [OutputTrays]>=(1) AND [Quantity]=[OutputTrays]*case [CavityType] when N''9 Cavity'' then (9) when N''24 Cavity'' then (24) when N''42 Cavity'' then (42) when N''102 Cavity'' then (102) when N''150 Cavity'' then (150)  end AND [TargetAreaId] IS NOT NULL AND [TargetPolyhouseId] IS NOT NULL AND [SeedSowingId] IS NOT NULL OR [OperationType]=N''Loss'' AND [CavityType] IS NULL AND [OutputTrays] IS NULL AND [TargetAreaId] IS NULL AND [TargetPolyhouseId] IS NULL AND [SeedSowingId] IS NULL AND len(ltrim(rtrim(isnull([Reason],N''''))))>(0))'
            THROW 52586, 'CK_BedSowingOperations_Shape is not the expected definition -- schema drift, nothing changed.', 1;
        ALTER TABLE dbo.BedSowingOperations DROP CONSTRAINT CK_BedSowingOperations_Shape;
        ALTER TABLE dbo.BedSowingOperations WITH CHECK ADD CONSTRAINT CK_BedSowingOperations_Shape CHECK ([OperationType]=N'Transplant' AND ([CavityType]=N'9 Cavity' OR [CavityType]=N'24 Cavity' OR [CavityType]=N'42 Cavity' OR [CavityType]=N'70 Cavity' OR [CavityType]=N'102 Cavity' OR [CavityType]=N'150 Cavity') AND [OutputTrays]>=(1) AND [Quantity]=[OutputTrays]*case [CavityType] when N'9 Cavity' then (9) when N'24 Cavity' then (24) when N'42 Cavity' then (42) when N'70 Cavity' then (70) when N'102 Cavity' then (102) when N'150 Cavity' then (150)  end AND [TargetAreaId] IS NOT NULL AND [TargetPolyhouseId] IS NOT NULL AND [SeedSowingId] IS NOT NULL OR [OperationType]=N'Loss' AND [CavityType] IS NULL AND [OutputTrays] IS NULL AND [TargetAreaId] IS NULL AND [TargetPolyhouseId] IS NULL AND [SeedSowingId] IS NULL AND len(ltrim(rtrim(isnull([Reason],N''))))>(0));
        PRINT '=== 10 cavity CHECK constraints now accept 70 Cavity ===';
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
                                                         WHEN N''42 Cavity'' THEN 42 WHEN N''70 Cavity'' THEN 70 WHEN N''102 Cavity'' THEN 102 WHEN N''150 Cavity'' THEN 150 END)
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
        PRINT '=== TR_ReadyStock_WholeTrays, TR_ReadyConfirmations_TrayQuantity, TR_RsTransplants_NewRow: 70 Cavity = 70 plants per tray ===';

        INSERT INTO dbo.MaterialConsumptionRules (MaterialTypeId, CavityType, QuantityPerTray)
        SELECT m.MaterialTypeId, N'70 Cavity', 1.5
        FROM (SELECT DISTINCT MaterialTypeId FROM dbo.MaterialConsumptionRules WHERE IsActive = 1) m
        WHERE NOT EXISTS (SELECT 1 FROM dbo.MaterialConsumptionRules x WHERE x.MaterialTypeId = m.MaterialTypeId AND x.CavityType = N'70 Cavity');
        PRINT '=== Material consumption rules for 70 Cavity (kg per tray) ===';
        SELECT t.Name AS Material, c.CavityType, c.QuantityPerTray FROM dbo.MaterialConsumptionRules c
        JOIN dbo.MaterialTypes t ON t.Id = c.MaterialTypeId WHERE c.CavityType = N'70 Cavity';

        IF NOT (OBJECT_DEFINITION(OBJECT_ID('dbo.CK_TrayStock_TraySize')) LIKE N'%70 Cavity%' AND OBJECT_DEFINITION(OBJECT_ID('dbo.CK_SeedSowings_CavityType')) LIKE N'%70 Cavity%' AND OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyStock_WholeTrays')) LIKE N'%70 Cavity%' AND OBJECT_DEFINITION(OBJECT_ID('dbo.TR_RsTransplants_NewRow')) LIKE N'%70 Cavity%')
            THROW 52587, 'Post-check failed: the application probe (WorkflowCorrectionsSchema.Cavity70ProbeSql) would not see 70 Cavity.', 1;
    END
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION Cavity70_1019;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION Cavity70_1019;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION Cavity70_1019;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
