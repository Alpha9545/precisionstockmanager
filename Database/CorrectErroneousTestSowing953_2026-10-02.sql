-- ============================================================================
-- CorrectErroneousTestSowing953_2026-10-02.sql
--
-- WHY: dbo.SeedSowings.Id=953 is an erroneous TEST record created during live
-- debugging of the Confirm Cutting Sowing workflow (an early, since-fixed
-- version of the code fell through to a "whole pool available" fallback
-- instead of the specific confirmed delivery's own 950, producing a
-- 25,910-cutting sowing against dbo.CuttingStock.163 that was never a real
-- business event). Full read-only audit already confirmed:
--   - no dbo.ReadyConfirmations row references SeedSowingId=953
--   - no dbo.ReadyStock row references SeedSowingId=953
--   - no dbo.InternalTransfers row has ConsumedBySeedSowingId=953
--   - nothing else in the schema references dbo.SeedSowings at all
-- i.e. 953 is a dead-end node; nothing downstream depends on it.
--
-- WHAT THIS DOES (ledger-safe correction, nothing hard-deleted):
--   1. Marks dbo.SeedSowings.953 Status = 'Cancelled' (the same status/
--      meaning SeedSowingRepository.CancelAsync already uses for a reversed
--      sowing). CavityType/quantities/Area/Polyhouse/Supervisor/CreatedById
--      are left exactly as they were (TR_SeedSowings_ImmutableTrayData does
--      not gate Status, and nothing here needs those fields changed).
--   2. Appends TWO new dbo.CuttingStockTransactions rows crediting
--      dbo.CuttingStock.163 back +25,910 total, using the SAME
--      TransactionType ('ReversalReturn') and ReferenceType ('SeedSowing')
--      SeedSowingRepository.CancelAsync already uses for exactly this
--      purpose -- never deleting or editing the original rows 877/878,
--      preserving the append-only ledger:
--        +25,896.00  reversing transaction 877 (the erroneous 'Sown')
--        +14.00      reversing transaction 878 (the erroneous 'Wastage')
--      Each new row's Remarks explicitly states it is a correction of
--      erroneous test data referencing SeedSowing 953, and each one's
--      BeforeQuantity is captured live (matching CuttingStockRepository.
--      RecordTransactionAsync's own exact bookkeeping), so
--      dbo.CuttingStock.163.PhysicalQuantity ends up net +25,910.00 versus
--      before this script -- i.e. sowing 953's net effect on stock becomes
--      exactly zero once its original -25,910 and this +25,910 are summed.
--
-- NOT touched: dbo.CuttingStockTransactions 877/878 themselves (preserved,
-- append-only), transactions 882-890, SeedSowings.954/955/956, dbo.
-- InternalTransfers.290/291/292/293, dbo.ReadyStock, dbo.ReadyConfirmations,
-- any Mother Plant/Cutting Production/Main Office delivery record, any
-- permission, schema, trigger, or application code.
--
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy. One
-- transaction. Run with sqlcmd -v MODE=DRYRUN (rolls back) or MODE=COMMIT.
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 51401, 'CorrectErroneousTestSowing953_2026-10-02.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 51402, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION CorrectSowing953;

BEGIN TRY

    -- Guard: this script is only valid for exactly the erroneous record it
    -- was written for -- refuse outright if anything about it has changed
    -- since the audit (status already changed, quantities different, or any
    -- reference now exists where the audit found none).
    IF NOT EXISTS (
        SELECT 1 FROM dbo.SeedSowings
        WHERE Id = 953 AND Status = 'Sown' AND SourceCuttingStockId = 163
          AND QuantitySown = 25896.00 AND SeedQuantity = 25910.00 AND WastageQuantity = 0
    )
        THROW 51403, 'SeedSowing 953 no longer matches the audited erroneous record -- aborting rather than guess.', 1;
    IF EXISTS (SELECT 1 FROM dbo.ReadyStock WHERE SeedSowingId = 953)
        THROW 51404, 'ReadyStock now references SeedSowing 953 -- aborting, this is no longer a safe dead-end record.', 1;
    IF EXISTS (SELECT 1 FROM dbo.ReadyConfirmations WHERE SeedSowingId = 953)
        THROW 51405, 'ReadyConfirmations now references SeedSowing 953 -- aborting, this is no longer a safe dead-end record.', 1;
    IF EXISTS (SELECT 1 FROM dbo.InternalTransfers WHERE ConsumedBySeedSowingId = 953)
        THROW 51406, 'An InternalTransfer now references SeedSowing 953 -- aborting, this is no longer a safe dead-end record.', 1;

    PRINT '=== BEFORE: CuttingStock 163 ===';
    SELECT Id, PhysicalQuantity, InTransitQuantity FROM dbo.CuttingStock WHERE Id = 163;

    PRINT '=== BEFORE: SeedSowing 953 ===';
    SELECT Id, Status, QuantitySown, SeedQuantity, WastageQuantity FROM dbo.SeedSowings WHERE Id = 953;

    ----------------------------------------------------------------------
    -- A. Mark SeedSowing 953 Cancelled (no business fields touched)
    ----------------------------------------------------------------------
    UPDATE dbo.SeedSowings
    SET Status = 'Cancelled', ModifiedDate = SYSUTCDATETIME(), ModifiedBy = N'DataCorrection-2026-10-02'
    WHERE Id = 953 AND Status = 'Sown';
    IF @@ROWCOUNT <> 1
        THROW 51407, 'Expected to update exactly 1 row (SeedSowing 953) -- aborting.', 1;

    ----------------------------------------------------------------------
    -- B. Two offsetting correction ledger entries (ReversalReturn), same
    --    bookkeeping as CuttingStockRepository.RecordTransactionAsync: lock
    --    the pool row, capture BeforeQuantity, update PhysicalQuantity,
    --    insert the ledger row -- repeated twice in sequence so the second
    --    entry's BeforeQuantity correctly reflects the first.
    ----------------------------------------------------------------------
    DECLARE @Before1 DECIMAL(18,2), @Before2 DECIMAL(18,2);

    SELECT @Before1 = PhysicalQuantity FROM dbo.CuttingStock WITH (UPDLOCK, HOLDLOCK) WHERE Id = 163;
    UPDATE dbo.CuttingStock SET PhysicalQuantity = @Before1 + 25896.00, ModifiedDate = SYSUTCDATETIME() WHERE Id = 163;
    INSERT INTO dbo.CuttingStockTransactions
        (CuttingStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)
    VALUES
        (163, SYSUTCDATETIME(), N'ReversalReturn', N'SeedSowing', 953, 25896.00, @Before1, NULL,
         N'Correction: reversal of erroneous test data. Offsets transaction Id 877 (incorrect Sown deduction) for Cancelled test SeedSowing 953.',
         SYSUTCDATETIME());

    SELECT @Before2 = PhysicalQuantity FROM dbo.CuttingStock WITH (UPDLOCK, HOLDLOCK) WHERE Id = 163;
    UPDATE dbo.CuttingStock SET PhysicalQuantity = @Before2 + 14.00, ModifiedDate = SYSUTCDATETIME() WHERE Id = 163;
    INSERT INTO dbo.CuttingStockTransactions
        (CuttingStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)
    VALUES
        (163, SYSUTCDATETIME(), N'ReversalReturn', N'SeedSowing', 953, 14.00, @Before2, NULL,
         N'Correction: reversal of erroneous test data. Offsets transaction Id 878 (incorrect Wastage deduction) for Cancelled test SeedSowing 953.',
         SYSUTCDATETIME());

    ----------------------------------------------------------------------
    -- VERIFICATION
    ----------------------------------------------------------------------
    PRINT '=== AFTER: SeedSowing 953 (expect Status = Cancelled, quantities unchanged) ===';
    SELECT Id, Status, QuantitySown, SeedQuantity, WastageQuantity, ModifiedBy FROM dbo.SeedSowings WHERE Id = 953;

    PRINT '=== AFTER: the two new correction transactions ===';
    SELECT Id, CuttingStockId, Quantity, BeforeQuantity, TransactionType, ReferenceType, ReferenceId, Remarks
    FROM dbo.CuttingStockTransactions WHERE ReferenceType = N'SeedSowing' AND ReferenceId = 953 ORDER BY Id;

    PRINT '=== AFTER: CuttingStock 163 (expect PhysicalQuantity = BEFORE + 25910.00) ===';
    SELECT Id, PhysicalQuantity, InTransitQuantity FROM dbo.CuttingStock WHERE Id = 163;

    PRINT '=== Net stock impact of SeedSowing 953 across ALL its transactions (expect exactly 0) ===';
    SELECT SUM(Quantity) AS NetImpactOfSowing953
    FROM dbo.CuttingStockTransactions WHERE ReferenceType = N'SeedSowing' AND ReferenceId = 953;

    PRINT '=== Original transactions 877/878 UNCHANGED (append-only -- expect same Quantity/BeforeQuantity as before) ===';
    SELECT Id, Quantity, BeforeQuantity, TransactionType FROM dbo.CuttingStockTransactions WHERE Id IN (877, 878) ORDER BY Id;

    PRINT '=== Transactions 882-890 UNCHANGED (compare manually to the snapshot) ===';
    SELECT Id, CuttingStockId, Quantity, BeforeQuantity, TransactionType, ReferenceId FROM dbo.CuttingStockTransactions WHERE Id BETWEEN 882 AND 890 ORDER BY Id;

    PRINT '=== SeedSowing 956 UNCHANGED (expect SeedQuantity 950.00, QuantitySown 936.00, WastageQuantity 0.00) ===';
    SELECT Id, SeedQuantity, QuantitySown, WastageQuantity, Status FROM dbo.SeedSowings WHERE Id = 956;

    PRINT '=== InternalTransfer 293 UNCHANGED (expect ConsumedBySeedSowingId = 956) ===';
    SELECT Id, ConsumedBySeedSowingId FROM dbo.InternalTransfers WHERE Id = 293;

    PRINT '=== InternalTransfer 290 UNCHANGED (expect ConsumedBySeedSowingId = NULL, untouched by this correction) ===';
    SELECT Id, ConsumedBySeedSowingId FROM dbo.InternalTransfers WHERE Id = 290;

    PRINT '=== Row counts unchanged elsewhere (compare manually) ===';
    SELECT 'SeedSowings' AS TableName, COUNT(*) AS Rows FROM dbo.SeedSowings
    UNION ALL SELECT 'CuttingStockTransactions', COUNT(*) FROM dbo.CuttingStockTransactions
    UNION ALL SELECT 'InternalTransfers', COUNT(*) FROM dbo.InternalTransfers
    UNION ALL SELECT 'ReadyStock', COUNT(*) FROM dbo.ReadyStock
    UNION ALL SELECT 'ReadyConfirmations', COUNT(*) FROM dbo.ReadyConfirmations;

END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION CorrectSowing953;
    THROW;
END CATCH;

----------------------------------------------------------------------------
-- Approved 2026-10-02: user reviewed the full read-only audit (no
-- ReadyStock/ReadyConfirmations/InternalTransfer references SeedSowing 953)
-- and the dry run (net stock impact of 953 becomes exactly 0, transactions
-- 877/878/882-890 and SeedSowings 954/955/956 and InternalTransfers 290/293
-- unchanged) and explicitly approved committing.
-- Verified backup: PlantsIMS2_Test_PreSowing953Correction_20261002.bak
----------------------------------------------------------------------------
IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION CorrectSowing953;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION CorrectSowing953;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
