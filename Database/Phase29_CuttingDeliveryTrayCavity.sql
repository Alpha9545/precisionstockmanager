/*
    Phase 29: Cutting Delivery to Main Office -- tray/cavity production.

    Adds the tray/cavity architecture already used by Direct Sowing /
    Ready Confirmation (Phase 23/25/PhaseB) to the EXISTING Cutting
    transfer workflow (StockType = 'Cutting' rows of dbo.InternalTransfers,
    Phase 15/16 -- GiveToMainOffice -> ConfirmReceipt -> Transplant). No
    new table, no new workflow: the Mother Plant Supervisor now chooses a
    Tray Cavity and the raw cutting quantity when sending (GiveToMainOffice);
    only cuttings that fill COMPLETE trays are actually sent (Quantity),
    exactly like Direct Sowing's QuantitySown -- the rounding remainder
    simply never leaves the source Area's Cutting Stock pool (no separate
    "remaining" field or concept, per the explicit instruction not to add
    one). Main Office's confirmation (ConfirmReceipt) then enters ONLY
    Actual Ready Trays; Actual Seedlings (ConfirmedQuantity) and Wastage
    are always recalculated server-side (DirectSowingRules.
    ComputeTrayApproval) -- never a freely-typed confirmed quantity.

    All six new columns are nullable and used ONLY for StockType='Cutting'
    rows going forward -- every existing row of any StockType (including
    already-completed/rejected/transplanted Cutting rows) is unaffected;
    they simply keep these columns NULL, exactly as before this script.
    No DB trigger enforces the tray arithmetic (Quantity = NumberOfTrays x
    cavity size, ConfirmedQuantity = ActualReadyTrays x cavity size) --
    that is done in the application layer (InternalTransferRepository),
    matching how every other stock workflow in this codebase (PotProduction,
    Dispatch, MainOfficeIssue, ...) is enforced, rather than the extra,
    separately-approved trigger-based hardening Direct Sowing/Ready Stock
    chose (PhaseB_ReadyStockTrays.sql) -- that stricter DB-level option
    remains available later if wanted.

    Safe to run multiple times. Run after Phase15_CuttingStock_
    TransferConfirmation.sql / Phase16_CuttingWorkflow_ModelB.sql;
    independent of every other phase.
*/

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.InternalTransfers') AND name = 'CavityType')
BEGIN
    ALTER TABLE dbo.InternalTransfers ADD CavityType NVARCHAR(20) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.InternalTransfers') AND name = 'CuttingQuantityEntered')
BEGIN
    ALTER TABLE dbo.InternalTransfers ADD CuttingQuantityEntered DECIMAL(18,2) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.InternalTransfers') AND name = 'NumberOfTrays')
BEGIN
    ALTER TABLE dbo.InternalTransfers ADD NumberOfTrays INT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.InternalTransfers') AND name = 'ActualReadyTrays')
BEGIN
    ALTER TABLE dbo.InternalTransfers ADD ActualReadyTrays DECIMAL(18,2) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.InternalTransfers') AND name = 'WastageQuantity')
BEGIN
    ALTER TABLE dbo.InternalTransfers ADD WastageQuantity DECIMAL(18,2) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.InternalTransfers') AND name = 'WastageReason')
BEGIN
    ALTER TABLE dbo.InternalTransfers ADD WastageReason NVARCHAR(50) NULL;
END
GO

-- Closed cavity list -- identical to DirectSowingRules.CavityTypes /
-- CK_SeedSowings_CavityType.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_InternalTransfers_CavityType')
BEGIN
    ALTER TABLE dbo.InternalTransfers WITH NOCHECK
        ADD CONSTRAINT CK_InternalTransfers_CavityType
        CHECK (CavityType IS NULL OR CavityType IN ('9 Cavity', '24 Cavity', '42 Cavity', '102 Cavity', '150 Cavity'));
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_InternalTransfers_NumberOfTrays')
BEGIN
    ALTER TABLE dbo.InternalTransfers WITH NOCHECK
        ADD CONSTRAINT CK_InternalTransfers_NumberOfTrays CHECK (NumberOfTrays IS NULL OR NumberOfTrays >= 1);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_InternalTransfers_CuttingQuantityEntered')
BEGIN
    ALTER TABLE dbo.InternalTransfers WITH NOCHECK
        ADD CONSTRAINT CK_InternalTransfers_CuttingQuantityEntered CHECK (CuttingQuantityEntered IS NULL OR CuttingQuantityEntered > 0);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_InternalTransfers_ActualReadyTrays')
BEGIN
    ALTER TABLE dbo.InternalTransfers WITH NOCHECK
        ADD CONSTRAINT CK_InternalTransfers_ActualReadyTrays CHECK (ActualReadyTrays IS NULL OR ActualReadyTrays >= 0);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_InternalTransfers_WastageQuantity')
BEGIN
    ALTER TABLE dbo.InternalTransfers WITH NOCHECK
        ADD CONSTRAINT CK_InternalTransfers_WastageQuantity CHECK (WastageQuantity IS NULL OR WastageQuantity >= 0);
END
GO

-- Closed wastage-reason list -- identical to DirectSowingRules.WastageReasons
-- / CK_ReadyConfirmations_WastageReason. Reused rather than duplicated
-- with cutting-specific wording, per the instruction to reuse the
-- existing tray architecture where appropriate.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_InternalTransfers_WastageReason')
BEGIN
    ALTER TABLE dbo.InternalTransfers WITH NOCHECK
        ADD CONSTRAINT CK_InternalTransfers_WastageReason
        CHECK (WastageReason IS NULL OR WastageReason IN ('Germination failure', 'Disease', 'Damaged plants', 'Poor growth', 'Other'));
END
GO
