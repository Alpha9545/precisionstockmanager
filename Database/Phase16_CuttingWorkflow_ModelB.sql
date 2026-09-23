-- ============================================================================
-- Phase 16: Cutting workflow -- Model B (Confirm Receipt / Confirm Transplant
-- split), in-transit stock tracking, and the CuttingTransplants detail table.
-- ============================================================================
-- Supersedes Phase 15's single-step "ConfirmAsync" model for Cutting-type
-- Internal Transfers ONLY. EmptyPot/PottedPlant transfers (Phase 8) and the
-- rest of Phase 15 (dbo.CuttingStock, dbo.CuttingStockTransactions,
-- SourceCuttingStockId/PendingConfirmationAreaId/ConfirmedQuantity/
-- ConfirmedBy/ConfirmedDate/DiscrepancyReason) are all kept exactly as they
-- are -- this script only WIDENS two CHECK constraints, ADDS two new
-- nullable/defaulted columns to dbo.CuttingStock, and ADDS one new table.
--
-- Approved business workflow (Model B):
--   Source Area -> Give Cutting to Main Office -> PendingConfirmation
--     -> Main Office Confirm Receipt -> ConfirmedAwaitingTransplant
--     -> Main Office Confirm Transplant (Destination Polyhouse + Destination
--        Supervisor + Transplant Date required) -> Transplanted
--   Rejection: PendingConfirmation -> Rejected (unchanged from Phase 15).
--
-- The critical rule this script exists to enforce: Confirm Receipt records
-- ConfirmedQuantity/discrepancy and moves NO stock. Confirm Transplant is the
-- ONLY point any ledger row is written, and it is one-sided -- it decreases
-- the SOURCE CuttingStock by ConfirmedQuantity and creates NOTHING at the
-- destination Polyhouse. The destination is where the cutting is consumed
-- into a transplant, not a second raw-cutting storage location.
--
-- In-transit stock tracking (the optional safety item from the review, now
-- required): a ledger-derived AvailableQuantity computed column, consistent
-- with this project's existing pattern of computed/persisted derived
-- quantities (see CuttingStockTransactions.AfterQuantity). InTransitQuantity
-- is a maintained balance column (like PhysicalQuantity itself), touched only
-- by the same locked, transactional repository methods that touch
-- PhysicalQuantity -- never written to directly elsewhere.
--
-- Additive, idempotent, safe to re-run. No DROP TABLE, no DELETE. Run this
-- AFTER Phase15_CuttingStock_TransferConfirmation.sql.
-- ============================================================================


-- ----------------------------------------------------------------------------
-- PART 1: dbo.CuttingStock -- InTransitQuantity + AvailableQuantity
-- ----------------------------------------------------------------------------

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.CuttingStock') AND name = 'InTransitQuantity')
BEGIN
    ALTER TABLE dbo.CuttingStock ADD InTransitQuantity DECIMAL(18,2) NOT NULL CONSTRAINT DF_CuttingStock_InTransitQuantity DEFAULT (0);
END
GO

-- AvailableQuantity is derived, never written directly by application code --
-- PhysicalQuantity represents everything physically sitting at this Area
-- (whether or not some of it is earmarked against an outbound transfer);
-- InTransitQuantity is the portion currently held against an active
-- (PendingConfirmation or ConfirmedAwaitingTransplant) outbound Cutting
-- transfer sourced from this row; AvailableQuantity is what's left that a
-- NEW outbound transfer is allowed to draw against.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.CuttingStock') AND name = 'AvailableQuantity')
BEGIN
    ALTER TABLE dbo.CuttingStock ADD AvailableQuantity AS (PhysicalQuantity - InTransitQuantity) PERSISTED;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CuttingStock_InTransitNotNegative')
BEGIN
    ALTER TABLE dbo.CuttingStock ADD CONSTRAINT CK_CuttingStock_InTransitNotNegative CHECK (InTransitQuantity >= 0);
END
GO

-- Guarantees AvailableQuantity can never go negative -- InTransit can never
-- exceed what is physically on hand.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CuttingStock_InTransitNotExceedPhysical')
BEGIN
    ALTER TABLE dbo.CuttingStock ADD CONSTRAINT CK_CuttingStock_InTransitNotExceedPhysical CHECK (InTransitQuantity <= PhysicalQuantity);
END
GO


-- ----------------------------------------------------------------------------
-- PART 2: dbo.InternalTransfers -- widen Status for the two new steps
-- ----------------------------------------------------------------------------

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_InternalTransfers_Status')
BEGIN
    ALTER TABLE dbo.InternalTransfers DROP CONSTRAINT CK_InternalTransfers_Status;
END
GO
ALTER TABLE dbo.InternalTransfers ADD CONSTRAINT CK_InternalTransfers_Status
    CHECK (Status IN ('Completed', 'Cancelled', 'PendingConfirmation', 'Rejected', 'ConfirmedAwaitingTransplant', 'Transplanted'));
GO


-- ----------------------------------------------------------------------------
-- PART 3: dbo.CuttingStockTransactions -- widen TransactionType
-- ----------------------------------------------------------------------------

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CuttingStockTx_Type')
BEGIN
    ALTER TABLE dbo.CuttingStockTransactions DROP CONSTRAINT CK_CuttingStockTx_Type;
END
GO
ALTER TABLE dbo.CuttingStockTransactions ADD CONSTRAINT CK_CuttingStockTx_Type
    CHECK (TransactionType IN ('Harvest', 'Transfer', 'Potted', 'Adjustment', 'ReversalRemoval', 'Transplanted'));
GO


-- ----------------------------------------------------------------------------
-- PART 4: dbo.CuttingTransplants -- one row per Transplanted Cutting transfer
-- ----------------------------------------------------------------------------
-- 1:1 companion to a dbo.InternalTransfers row once it reaches Transplanted.
-- Kept as its own table (rather than more columns on InternalTransfers)
-- because InternalTransfers already carries a generic SupervisorId /
-- ResponsiblePersonId whose meaning is the SOURCE side; overloading either
-- of those for the DESTINATION (transplant) supervisor would blur two
-- different people together.

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CuttingTransplants' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.CuttingTransplants
    (
        Id                      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CuttingTransplants PRIMARY KEY,
        InternalTransferId      INT           NOT NULL,
        DestinationSupervisorId INT           NOT NULL,
        TransplantDate          DATE          NOT NULL,
        Remarks                 NVARCHAR(500) NULL,
        CreatedDate             DATETIME2     NOT NULL CONSTRAINT DF_CuttingTransplants_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CreatedBy               NVARCHAR(100) NULL,

        CONSTRAINT FK_CuttingTransplants_Transfer FOREIGN KEY (InternalTransferId) REFERENCES dbo.InternalTransfers(Id),
        CONSTRAINT FK_CuttingTransplants_Supervisor FOREIGN KEY (DestinationSupervisorId) REFERENCES dbo.IMSUsers(Id),
        CONSTRAINT UQ_CuttingTransplants_Transfer UNIQUE (InternalTransferId)
    );

    CREATE INDEX IX_CuttingTransplants_Supervisor ON dbo.CuttingTransplants(DestinationSupervisorId);
END
GO

-- ============================================================================
-- End of Phase 16. Every EXISTING EmptyPot/PottedPlant transfer row, every
-- EXISTING CuttingStock/CuttingStockTransactions row, and every EXISTING
-- CuttingDeliveries/ActualCuttings/CuttingPlans row is completely unaffected
-- -- InTransitQuantity/AvailableQuantity default to 0/PhysicalQuantity for
-- every row that already exists, and no existing Status value was removed.
-- ============================================================================
