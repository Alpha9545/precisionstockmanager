-- ============================================================================
-- 2026-10-16_MotherPlantTransplantWaste.sql
--
-- WHY (owner requirements 2026-10-12): Transplant into Trays in a Mother Plant Area works like Main Office
-- bed transplanting -- the user enters the seedlings to PROCESS, the complete trays of the chosen cavity are
-- transplanted and the untransplanted remainder is recorded as WASTE (with a reason) in the same save;
-- fewer seedlings than one tray may be closed as waste only (explicitly confirmed, with a reason). And a batch
-- received for POT PRODUCTION is never transplanted into trays -- now refused by the database too.
--
-- WHAT (dbo.ReadyStockTransplants):
--   1. WasteReason NVARCHAR(500) NULL -- why the remainder / the discarded seedlings were wasted.
--      Existing rows keep NULL (their losses were recorded before a reason was asked for).
--   2. TrayStockId becomes NULLable -- a waste-only closure takes no trays. FK_RsTransplants_TrayStock unchanged.
--   3. CK_RsTransplants_Quantities: TransplantedQuantity may be 0 when LostQuantity > 0 (waste-only); every other
--      rule unchanged (whole numbers, Selected = Transplanted + Lost). Replaced only if it is exactly the
--      2026-10-09 definition (schema-drift guard).
--   4. CK_RsTransplants_Status: a waste-only row (no output) may be Reversed too; otherwise unchanged (guarded).
--   5. CK_RsTransplants_WasteOnly (new): a row either transplants (Transplanted > 0, a tray pool) or is waste-only
--      (Transplanted = 0, 0 trays, no tray pool, no output batch). Every existing row satisfies it (checked first).
--   6. TR_RsTransplants_NewRow (new, AFTER INSERT): a new row with waste has a reason; the transplanted
--      seedlings fill complete trays exactly (Transplanted = OutputTrays x cavity); and the source batch has no
--      Pot Production transfer anywhere in its lineage (the batch itself or any parent).
--
-- NOT changed: no transplant, Ready Stock, tray, sowing or ledger row; no permission.
-- Requires: 2026-10-14_MotherPlantAreaTraysAndTransferPurpose.sql (and so 2026-10-09 and 2026-10-13).
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to -v TARGET_SERVER=<@@SERVERNAME>.
-- One transaction. Idempotent (re-run = no-op). Undo: 2026-10-16_MotherPlantTransplantWaste_Rollback.sql
-- (refuses once a waste reason or a waste-only row has been recorded).
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52431, '2026-10-16_MotherPlantTransplantWaste.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52432, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52433, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
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

BEGIN TRANSACTION MpTransplantWaste1016;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';
    IF COL_LENGTH('dbo.InternalTransfers', 'TransferPurpose') IS NULL OR OBJECT_ID('dbo.TR_TrayStock_PoolLocation', 'TR') IS NULL
        THROW 52434, 'Run 2026-10-14_MotherPlantAreaTraysAndTransferPurpose.sql first.', 1;

    DECLARE @Applied BIT = CASE WHEN COL_LENGTH('dbo.ReadyStockTransplants', 'WasteReason') IS NOT NULL
                                 AND OBJECT_ID('dbo.TR_RsTransplants_NewRow', 'TR') IS NOT NULL THEN 1 ELSE 0 END;
    IF @Applied = 0
    BEGIN
        -- Schema-drift guards: exactly the constraints this script replaces, nothing of it present yet.
        IF (SELECT definition FROM sys.check_constraints WHERE name = N'CK_RsTransplants_Quantities')
           <> N'([TransplantedQuantity]>(0) AND [LostQuantity]>=(0) AND [SelectedQuantity]=([TransplantedQuantity]+[LostQuantity]) AND [SelectedQuantity]=floor([SelectedQuantity]) AND [TransplantedQuantity]=floor([TransplantedQuantity]) AND [LostQuantity]=floor([LostQuantity]))'
            THROW 52435, 'CK_RsTransplants_Quantities is not the expected 2026-10-09 definition -- schema drift, nothing changed.', 1;
        IF (SELECT definition FROM sys.check_constraints WHERE name = N'CK_RsTransplants_Status')
           <> N'([Status]=N''Completed'' AND [ReversedAt] IS NULL AND [ReversedById] IS NULL AND [ReversalReason] IS NULL OR [Status]=N''Reversed'' AND ([OutputReadyStockId] IS NOT NULL OR [OutputSeedSowingId] IS NOT NULL) AND [ReversedAt] IS NOT NULL AND [ReversedById] IS NOT NULL AND [ReversalReason] IS NOT NULL)'
            THROW 52436, 'CK_RsTransplants_Status is not the expected 2026-10-13 definition -- schema drift, nothing changed.', 1;
        IF COL_LENGTH('dbo.ReadyStockTransplants', 'WasteReason') IS NOT NULL OR OBJECT_ID('dbo.TR_RsTransplants_NewRow', 'TR') IS NOT NULL
           OR OBJECT_ID('dbo.CK_RsTransplants_WasteOnly', 'C') IS NOT NULL
            THROW 52437, 'Part of this migration already exists while it is not complete -- inspect the database, nothing changed.', 1;
        -- Data check for the new constraint: every existing row transplants and has its tray pool.
        IF EXISTS (SELECT 1 FROM dbo.ReadyStockTransplants WHERE TransplantedQuantity <= 0 OR TrayStockId IS NULL)
            THROW 52438, 'An existing transplant row has no transplanted plants or no tray pool -- inspect it first, nothing changed.', 1;
    END

    PRINT '=== Before: rows that must not change ===';
    SELECT COUNT(*) AS Transplants, SUM(SelectedQuantity) AS Selected, SUM(TransplantedQuantity) AS Transplanted,
           SUM(LostQuantity) AS Lost, SUM(OutputTrays) AS Trays
    FROM dbo.ReadyStockTransplants;

    IF @Applied = 0
    BEGIN
        ------------------------------------------------------------------
        -- 1. + 2. columns
        ------------------------------------------------------------------
        ALTER TABLE dbo.ReadyStockTransplants ADD WasteReason NVARCHAR(500) NULL;
        ALTER TABLE dbo.ReadyStockTransplants ALTER COLUMN TrayStockId INT NULL;
        PRINT '=== 1. WasteReason added; 2. TrayStockId NULLable ===';

        ------------------------------------------------------------------
        -- 3. + 4. + 5. constraints
        ------------------------------------------------------------------
        ALTER TABLE dbo.ReadyStockTransplants DROP CONSTRAINT CK_RsTransplants_Quantities;
        EXEC (N'ALTER TABLE dbo.ReadyStockTransplants WITH CHECK ADD CONSTRAINT CK_RsTransplants_Quantities CHECK (
            TransplantedQuantity >= 0 AND LostQuantity >= 0 AND (TransplantedQuantity > 0 OR LostQuantity > 0)
            AND SelectedQuantity = TransplantedQuantity + LostQuantity
            AND SelectedQuantity = FLOOR(SelectedQuantity) AND TransplantedQuantity = FLOOR(TransplantedQuantity) AND LostQuantity = FLOOR(LostQuantity));');
        ALTER TABLE dbo.ReadyStockTransplants DROP CONSTRAINT CK_RsTransplants_Status;
        EXEC (N'ALTER TABLE dbo.ReadyStockTransplants WITH CHECK ADD CONSTRAINT CK_RsTransplants_Status CHECK (
            (Status = N''Completed'' AND ReversedAt IS NULL AND ReversedById IS NULL AND ReversalReason IS NULL)
            OR (Status = N''Reversed'' AND (OutputReadyStockId IS NOT NULL OR OutputSeedSowingId IS NOT NULL OR TransplantedQuantity = 0)
                AND ReversedAt IS NOT NULL AND ReversedById IS NOT NULL AND ReversalReason IS NOT NULL));');
        EXEC (N'ALTER TABLE dbo.ReadyStockTransplants WITH CHECK ADD CONSTRAINT CK_RsTransplants_WasteOnly CHECK (
            (TransplantedQuantity > 0 AND TrayStockId IS NOT NULL)
            OR (TransplantedQuantity = 0 AND OutputTrays = 0 AND TrayStockId IS NULL AND OutputReadyStockId IS NULL AND OutputSeedSowingId IS NULL));');
        PRINT '=== 3. CK_RsTransplants_Quantities, 4. CK_RsTransplants_Status, 5. CK_RsTransplants_WasteOnly ===';

        ------------------------------------------------------------------
        -- 6. new-row rules
        ------------------------------------------------------------------
        EXEC (N'
CREATE TRIGGER dbo.TR_RsTransplants_NewRow ON dbo.ReadyStockTransplants
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
        PRINT '=== 6. TR_RsTransplants_NewRow ===';
    END
    ELSE PRINT '=== Already applied -- no-op ===';

    PRINT '=== After ===';
    SELECT COUNT(*) AS Transplants, SUM(SelectedQuantity) AS Selected, SUM(TransplantedQuantity) AS Transplanted,
           SUM(LostQuantity) AS Lost, SUM(OutputTrays) AS Trays
    FROM dbo.ReadyStockTransplants;
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION MpTransplantWaste1016;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION MpTransplantWaste1016;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION MpTransplantWaste1016;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
