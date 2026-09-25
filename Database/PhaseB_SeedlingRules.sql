-- ============================================================================
-- Phase B (add-on): seedling workflow rules
-- ============================================================================
-- Run IMMEDIATELY AFTER PhaseB_DirectSowing.sql (it needs the
-- SeedSowings.WastageQuantity / ReadyConfirmations.WastageQuantity columns).
-- Additive and idempotent: every object is created only if it does not exist
-- yet. No row and no existing column/constraint is changed.
--
--   1. SeedSowings.CreatedById (nullable, FK -> IMSUsers): the user who
--      recorded the sowing. Used by the self-approval rule (nobody approves
--      a sowing they recorded -- DirectSowingRules.IsOwnSowing).
--   2. Whole plants only: Seed Stock -> Direct Sowing -> Supervisor
--      Approval -> Ready Stock quantities are whole numbers. A CHECK
--      constraint cannot be added while an existing row breaks it (SQL
--      Server validates existing rows), so this can never hide fractional data.
-- ============================================================================

SET XACT_ABORT ON;
GO

-- 1. Who recorded the sowing (self-approval rule).
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SeedSowings') AND name = 'CreatedById')
    ALTER TABLE dbo.SeedSowings ADD CreatedById INT NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_SeedSowings_CreatedBy')
    ALTER TABLE dbo.SeedSowings ADD CONSTRAINT FK_SeedSowings_CreatedBy FOREIGN KEY (CreatedById) REFERENCES dbo.IMSUsers(Id);
GO

-- 2. Whole plants only.

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_SeedStock_WholeQuantities')
    ALTER TABLE dbo.SeedStock ADD CONSTRAINT CK_SeedStock_WholeQuantities
        CHECK (PhysicalQuantity = FLOOR(PhysicalQuantity) AND InTransitQuantity = FLOOR(InTransitQuantity));
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_SeedStockTx_WholeQuantities')
    ALTER TABLE dbo.SeedStockTransactions ADD CONSTRAINT CK_SeedStockTx_WholeQuantities
        CHECK (Quantity = FLOOR(Quantity) AND BeforeQuantity = FLOOR(BeforeQuantity));
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_SeedSowings_WholeQuantities')
    ALTER TABLE dbo.SeedSowings ADD CONSTRAINT CK_SeedSowings_WholeQuantities
        CHECK (QuantitySown = FLOOR(QuantitySown)
               AND ConfirmedReadyQuantity = FLOOR(ConfirmedReadyQuantity)
               AND WastageQuantity = FLOOR(WastageQuantity));
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_ReadyConfirmations_WholeQuantities')
    ALTER TABLE dbo.ReadyConfirmations ADD CONSTRAINT CK_ReadyConfirmations_WholeQuantities
        CHECK (ConfirmedQuantity = FLOOR(ConfirmedQuantity) AND WastageQuantity = FLOOR(WastageQuantity));
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_ReadyStock_WholeQuantities')
    ALTER TABLE dbo.ReadyStock ADD CONSTRAINT CK_ReadyStock_WholeQuantities
        CHECK (Quantity = FLOOR(Quantity)
               AND ReservedQuantity = FLOOR(ReservedQuantity)
               AND DispatchedQuantity = FLOOR(DispatchedQuantity));
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_ReadyStockTx_WholeQuantities')
    ALTER TABLE dbo.ReadyStockTransactions ADD CONSTRAINT CK_ReadyStockTx_WholeQuantities
        CHECK (Quantity = FLOOR(Quantity) AND BeforeQuantity = FLOOR(BeforeQuantity));
GO
