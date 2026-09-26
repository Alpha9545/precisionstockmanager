-- ============================================================================
-- Phase F: two independent, additive business-rule relaxations requested in
-- the 9-change verification pass. Neither adds a table or column; both only
-- widen existing CHECK constraints (never a narrowing, so every existing row
-- already satisfies the new, looser rule).
-- ============================================================================
-- TARGET: PlantsIMS2_Test ONLY.
--
-- 1) Ready Stock approval may legitimately exceed the sowing's original
--    QuantitySown (e.g. the recorded/estimated count under-stated the actual
--    field result). Application-side (Services/DirectSowingRules.cs,
--    Data/ReadyConfirmationRepository.cs) already computes Wastage = 0 (never
--    negative) whenever the approved quantity is at or above what remained,
--    and still closes the batch (Status = 'Completed'). The database must
--    stop rejecting that overage and stop requiring EXACT (Ready+Wastage =
--    Sown) accounting on close -- it now only requires AT LEAST the sown
--    quantity be accounted for (Ready + Wastage), never less:
--      dbo.SeedSowings.CK_SeedSowings_ConfirmedReadyQuantity -- drops the
--        "<= QuantitySown" upper bound (keeps ">= 0")
--      dbo.SeedSowings.CK_SeedSowings_WastageQuantity -- drops the
--        "(Ready+Wastage) <= QuantitySown" upper bound (keeps "WastageQuantity >= 0")
--      dbo.SeedSowings.CK_SeedSowings_CompletedAccounted -- "=" becomes ">="
--    What is UNCHANGED (still enforced, in the application and in the
--    database): 0/negative/fractional Actual Ready rejected, no double
--    approval (Status must be 'Sown'; RemainingReadyQuantity <= 0 blocks a
--    further approval), only the assigned Sowing Supervisor may approve,
--    WastageQuantity itself can never be negative, a Wastage Reason is still
--    required whenever an UNDER-count still produces wastage > 0.
--
-- 2) The Seed Sowing workflow's sub-tray remainder (SeedQuantity minus the
--    whole-tray QuantitySown, always less than one tray) is no longer left in
--    the source Seed Stock lot -- Data/SeedSowingRepository.cs now wastes it
--    automatically, in the same transaction, with no reason prompt. The
--    ledger needs the type it now writes:
--      dbo.SeedStockTransactions.CK_SeedStockTx_Type -- adds 'Wastage'
--    (Cutting-sourced sowing is untouched: InsertFromCuttingAsync is a
--    separate method and this phase does not touch it.)
--
-- Existing data: no existing row is changed by either constraint edit --
-- only the rule applied to FUTURE writes gets looser, never stricter.
-- ============================================================================

IF DB_NAME() <> N'PlantsIMS2_Test'
    THROW 50299, 'PhaseF_ReadyStockOverageAndSeedWastage.sql may only be run against PlantsIMS2_Test.', 1;
GO

-- ============================================================================
-- 1a. dbo.SeedSowings.ConfirmedReadyQuantity: drop the "<= QuantitySown" cap.
-- ============================================================================
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_SeedSowings_ConfirmedReadyQuantity')
    ALTER TABLE dbo.SeedSowings DROP CONSTRAINT CK_SeedSowings_ConfirmedReadyQuantity;
ALTER TABLE dbo.SeedSowings WITH CHECK ADD CONSTRAINT CK_SeedSowings_ConfirmedReadyQuantity
    CHECK (ConfirmedReadyQuantity >= 0);
GO

-- ============================================================================
-- 1b. dbo.SeedSowings.WastageQuantity: drop the "(Ready+Wastage) <= QuantitySown" cap.
-- ============================================================================
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_SeedSowings_WastageQuantity')
    ALTER TABLE dbo.SeedSowings DROP CONSTRAINT CK_SeedSowings_WastageQuantity;
ALTER TABLE dbo.SeedSowings WITH CHECK ADD CONSTRAINT CK_SeedSowings_WastageQuantity
    CHECK (WastageQuantity >= 0);
GO

-- ============================================================================
-- 1c. dbo.SeedSowings: a Completed batch must account for AT LEAST the sown
--     quantity (never less); it may now account for more.
-- ============================================================================
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_SeedSowings_CompletedAccounted')
    ALTER TABLE dbo.SeedSowings DROP CONSTRAINT CK_SeedSowings_CompletedAccounted;
ALTER TABLE dbo.SeedSowings WITH CHECK ADD CONSTRAINT CK_SeedSowings_CompletedAccounted
    CHECK (Status <> 'Completed' OR (ConfirmedReadyQuantity + WastageQuantity) >= QuantitySown);
GO

-- ============================================================================
-- 2. dbo.SeedStockTransactions.TransactionType gains 'Wastage'.
-- ============================================================================
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_SeedStockTx_Type')
    ALTER TABLE dbo.SeedStockTransactions DROP CONSTRAINT CK_SeedStockTx_Type;
ALTER TABLE dbo.SeedStockTransactions WITH CHECK ADD CONSTRAINT CK_SeedStockTx_Type
    CHECK (TransactionType IN (N'ReversalReturn', N'Sown', N'Transfer', N'StockIn', N'Wastage'));
GO
