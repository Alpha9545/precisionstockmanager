-- ============================================================================
-- 2026-10-16_MotherPlantTransplantWaste_Rollback.sql
--
-- Undoes 2026-10-16_MotherPlantTransplantWaste.sql: drops TR_RsTransplants_NewRow and CK_RsTransplants_WasteOnly,
-- restores the exact earlier CK_RsTransplants_Quantities / CK_RsTransplants_Status, makes TrayStockId NOT NULL
-- again and drops WasteReason.
-- REFUSES (nothing changed) once the new shapes hold business data: any waste-only row (no transplanted plants)
-- or any recorded WasteReason -- dropping them would lose the record. Restore the pre-migration backup instead.
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to -v TARGET_SERVER=<@@SERVERNAME>.
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52451, '2026-10-16_MotherPlantTransplantWaste_Rollback.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52452, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52453, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
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

BEGIN TRANSACTION MpTransplantWaste1016Rollback;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';
    IF COL_LENGTH('dbo.ReadyStockTransplants', 'WasteReason') IS NULL AND OBJECT_ID('dbo.TR_RsTransplants_NewRow', 'TR') IS NULL
        PRINT '=== Not applied -- no-op ===';
    ELSE
    BEGIN
        DECLARE @WasteOnly INT, @WithReason INT;
        EXEC sp_executesql N'SELECT @W = COUNT(CASE WHEN TransplantedQuantity = 0 THEN 1 END),
                                   @R = COUNT(CASE WHEN WasteReason IS NOT NULL THEN 1 END) FROM dbo.ReadyStockTransplants',
             N'@W INT OUTPUT, @R INT OUTPUT', @W = @WasteOnly OUTPUT, @R = @WithReason OUTPUT;
        PRINT CONCAT('=== Waste-only rows: ', @WasteOnly, '; rows with a waste reason: ', @WithReason, ' ===');
        IF @WasteOnly > 0 OR @WithReason > 0
            THROW 52454, 'Transplant waste has been recorded under the 2026-10-16 rules -- rolling back would lose it. Nothing changed; restore the pre-migration backup if a rollback is really needed.', 1;

        IF OBJECT_ID('dbo.TR_RsTransplants_NewRow', 'TR') IS NOT NULL DROP TRIGGER dbo.TR_RsTransplants_NewRow;
        IF OBJECT_ID('dbo.CK_RsTransplants_WasteOnly', 'C') IS NOT NULL ALTER TABLE dbo.ReadyStockTransplants DROP CONSTRAINT CK_RsTransplants_WasteOnly;
        ALTER TABLE dbo.ReadyStockTransplants DROP CONSTRAINT CK_RsTransplants_Quantities;
        EXEC (N'ALTER TABLE dbo.ReadyStockTransplants WITH CHECK ADD CONSTRAINT CK_RsTransplants_Quantities CHECK (
            TransplantedQuantity > 0 AND LostQuantity >= 0 AND SelectedQuantity = TransplantedQuantity + LostQuantity
            AND SelectedQuantity = FLOOR(SelectedQuantity) AND TransplantedQuantity = FLOOR(TransplantedQuantity) AND LostQuantity = FLOOR(LostQuantity));');
        ALTER TABLE dbo.ReadyStockTransplants DROP CONSTRAINT CK_RsTransplants_Status;
        EXEC (N'ALTER TABLE dbo.ReadyStockTransplants WITH CHECK ADD CONSTRAINT CK_RsTransplants_Status CHECK (
            (Status = N''Completed'' AND ReversedAt IS NULL AND ReversedById IS NULL AND ReversalReason IS NULL)
            OR (Status = N''Reversed'' AND (OutputReadyStockId IS NOT NULL OR OutputSeedSowingId IS NOT NULL)
                AND ReversedAt IS NOT NULL AND ReversedById IS NOT NULL AND ReversalReason IS NOT NULL));');
        ALTER TABLE dbo.ReadyStockTransplants ALTER COLUMN TrayStockId INT NOT NULL;
        ALTER TABLE dbo.ReadyStockTransplants DROP COLUMN WasteReason;

        IF (SELECT definition FROM sys.check_constraints WHERE name = N'CK_RsTransplants_Quantities')
           <> N'([TransplantedQuantity]>(0) AND [LostQuantity]>=(0) AND [SelectedQuantity]=([TransplantedQuantity]+[LostQuantity]) AND [SelectedQuantity]=floor([SelectedQuantity]) AND [TransplantedQuantity]=floor([TransplantedQuantity]) AND [LostQuantity]=floor([LostQuantity]))'
           OR (SELECT definition FROM sys.check_constraints WHERE name = N'CK_RsTransplants_Status')
           <> N'([Status]=N''Completed'' AND [ReversedAt] IS NULL AND [ReversedById] IS NULL AND [ReversalReason] IS NULL OR [Status]=N''Reversed'' AND ([OutputReadyStockId] IS NOT NULL OR [OutputSeedSowingId] IS NOT NULL) AND [ReversedAt] IS NOT NULL AND [ReversedById] IS NOT NULL AND [ReversalReason] IS NOT NULL)'
            THROW 52455, 'The restored constraints are not the exact earlier definitions -- rolled back.', 1;
        PRINT '=== Rolled back to the 2026-10-14 transplant shape ===';
    END
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION MpTransplantWaste1016Rollback;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION MpTransplantWaste1016Rollback;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION MpTransplantWaste1016Rollback;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
