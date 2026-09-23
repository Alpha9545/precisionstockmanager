/* ============================================================
   Phase 8: Internal Transfer
   ------------------------------------------------------------
   This phase has TWO parts:

   PART A -- a correction to Phase 7. Phase 7 pooled EmptyPotInventory
   and PottedPlantStock as single totals with no physical location.
   Internal Transfer only makes sense between two locations, so this
   part ADDS an AreaId to both stock tables before Part B introduces
   the transfer mechanism itself. This was flagged and confirmed with
   the business owner before implementing (rather than guessing) --
   see the decision recorded in PROJECT_DOCUMENTATION.md.

   PART B -- dbo.InternalTransfers ("TR-" prefix): moves a quantity of
   EITHER Empty Pot stock OR Potted Plant stock from one Area to
   another, reusing the SAME dedicated ledgers Phase 7 already created
   (dbo.EmptyPotInventoryTransactions / dbo.PottedPlantStockTransactions)
   with the 'Transfer' TransactionType that was reserved for exactly
   this purpose -- no new ledger table needed.

   ADDITIVE ONLY where possible; Part A's ALTERs are all
   additive/backward-compatible (new nullable column, then a widened
   uniqueness rule) and every step is guarded so this script is safe to
   run more than once. No existing data is deleted; pre-existing
   Phase 7 rows keep working with AreaId = NULL ("unassigned/legacy
   location").

   FIX (this patch) -- dependency-ordering bug in PART A:
   dbo.EmptyPotInventory.UQ_EmptyPotInventory_PotSize (a UNIQUE
   CONSTRAINT on PotSize alone) is referenced by
   dbo.PottedPlantStock.FK_PottedPlantStock_PotSize (a FOREIGN KEY on
   PottedPlantStock.PotSize, created by Phase 7). SQL Server will not
   drop a unique constraint while any FK still points at it (Msg 3725 /
   3727: "Could not drop constraint. See previous errors."). The
   previous version of this script tried to drop
   UQ_EmptyPotInventory_PotSize (in what was then PART A1) before
   dropping FK_PottedPlantStock_PotSize (in what was then PART A2),
   i.e. it dropped the referenced constraint before its dependent FK,
   which fails every time. The fix is PART A0 immediately below: drop
   FK_PottedPlantStock_PotSize FIRST -- before anything else in this
   script touches EmptyPotInventory's constraints -- then proceed with
   the rest of PART A exactly as before. The FK is correctly recreated
   later in PART A2, but pointed at EmptyPotInventory(Id) (a real PK)
   rather than at EmptyPotInventory(PotSize), because PotSize alone is
   no longer unique once AreaId is introduced (only the PotSize+AreaId
   pair is) -- a single-column FK cannot reference one column out of a
   two-column unique key, so "recreate the FK correctly" means
   re-pointing it at the surrogate key, which is exactly what PART A2
   already did; only the ordering was broken.

   Run this AFTER Phase7_PotProduction.sql has been applied. Take a
   backup first per your own DB safety rules.
   ============================================================ */

-- ------------------------------------------------------------
-- PART A0) Drop the FK that depends on the unique constraint PART A1
-- is about to widen, BEFORE PART A1 touches that constraint. This is
-- the one step that was missing/mis-ordered and caused Msg 3725/3727.
-- Dropping an FK is a metadata-only operation -- it does not touch or
-- delete any row in either table, and PottedPlantStock.PotSize itself
-- is untouched (it keeps existing as a plain column; only the FK on it
-- is removed here). The FK is recreated, correctly re-targeted, in
-- PART A2 below (FK_PottedPlantStock_EmptyPotInventory).
-- ------------------------------------------------------------
IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_PottedPlantStock_PotSize')
BEGIN
    ALTER TABLE dbo.PottedPlantStock DROP CONSTRAINT FK_PottedPlantStock_PotSize;
END
GO

-- ------------------------------------------------------------
-- PART A1) Add AreaId to dbo.EmptyPotInventory. A Pot Size's stock is
-- now tracked per-Area, so the business key becomes (PotSize, AreaId)
-- instead of PotSize alone. Existing rows (created before this phase)
-- get AreaId = NULL, representing stock whose physical location was
-- never tracked -- they remain valid and distinct from each other
-- because their PotSize values were already unique.
-- ------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.EmptyPotInventory') AND name = 'AreaId')
BEGIN
    ALTER TABLE dbo.EmptyPotInventory ADD AreaId INT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_EmptyPotInventory_Area')
BEGIN
    ALTER TABLE dbo.EmptyPotInventory
        ADD CONSTRAINT FK_EmptyPotInventory_Area FOREIGN KEY (AreaId) REFERENCES dbo.Area(Id);
END
GO

-- Safe to drop now: PART A0 above already removed the one FK
-- (FK_PottedPlantStock_PotSize) that depended on this constraint.
IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'UQ_EmptyPotInventory_PotSize')
BEGIN
    ALTER TABLE dbo.EmptyPotInventory DROP CONSTRAINT UQ_EmptyPotInventory_PotSize;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UQ_EmptyPotInventory_PotSize_AreaId')
BEGIN
    -- A plain UNIQUE CONSTRAINT would reject more than one NULL AreaId
    -- combination sharing... actually SQL Server unique INDEXES have
    -- the same single-NULL-combination behavior, but each pre-existing
    -- PotSize is still distinct from every other, so this is safe for
    -- the current data. Implemented as a unique index (equivalent to a
    -- unique constraint) so it can be added without a constraint-name
    -- clash if the table already has one from a partial run.
    CREATE UNIQUE INDEX UQ_EmptyPotInventory_PotSize_AreaId ON dbo.EmptyPotInventory(PotSize, AreaId);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_EmptyPotInventory_AreaId')
BEGIN
    CREATE INDEX IX_EmptyPotInventory_AreaId ON dbo.EmptyPotInventory(AreaId);
END
GO

-- Phase 7's dbo.EmptyPotInventoryTransactions CHECK constraint only
-- allowed ('StockIn', 'Consumption', 'Adjustment', 'ReversalReturn') --
-- it never anticipated 'Transfer', unlike PottedPlantStockTransactions'
-- equivalent constraint (which already reserved 'Transfer' for this
-- phase). Widen it now so InternalTransferRepository can write signed
-- 'Transfer' ledger entries against Empty Pot stock too. A cancelled
-- transfer is reversed with a second, equal-and-opposite 'Transfer'
-- entry rather than a separate reversal-specific type, so no other
-- value needs to be added here.
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_EmptyPotInvTx_Type')
BEGIN
    ALTER TABLE dbo.EmptyPotInventoryTransactions DROP CONSTRAINT CK_EmptyPotInvTx_Type;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_EmptyPotInvTx_Type')
BEGIN
    ALTER TABLE dbo.EmptyPotInventoryTransactions
        ADD CONSTRAINT CK_EmptyPotInvTx_Type CHECK (TransactionType IN ('StockIn', 'Consumption', 'Adjustment', 'ReversalReturn', 'Transfer'));
END
GO

-- ------------------------------------------------------------
-- PART A2) Add AreaId + EmptyPotInventoryId to dbo.PottedPlantStock.
-- PotSize alone can no longer be assumed unique on EmptyPotInventory
-- (it is now unique per PotSize+AreaId), so the FK that used to point
-- at EmptyPotInventory(PotSize) is replaced with a proper FK to
-- EmptyPotInventory(Id) -- more correct anyway, since it is a genuine
-- surrogate-key relationship rather than a string match. PotSize stays
-- as a plain (non-FK'd) denormalized display column, same pattern used
-- for MotherPlantCode/SpeciesName elsewhere in this project.
-- ------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.PottedPlantStock') AND name = 'AreaId')
BEGIN
    ALTER TABLE dbo.PottedPlantStock ADD AreaId INT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.PottedPlantStock') AND name = 'EmptyPotInventoryId')
BEGIN
    ALTER TABLE dbo.PottedPlantStock ADD EmptyPotInventoryId INT NULL;
END
GO

-- Backfill EmptyPotInventoryId for any pre-existing rows by matching
-- PotSize against the (now AreaId IS NULL) legacy EmptyPotInventory row
-- of the same PotSize -- guaranteed to exist because Phase 7's FK
-- required it before this migration ever ran.
UPDATE pps
SET pps.EmptyPotInventoryId = epi.Id
FROM dbo.PottedPlantStock pps
INNER JOIN dbo.EmptyPotInventory epi ON epi.PotSize = pps.PotSize AND epi.AreaId IS NULL
WHERE pps.EmptyPotInventoryId IS NULL;
GO

-- FK_PottedPlantStock_PotSize was already dropped in PART A0 above
-- (before PART A1 touched UQ_EmptyPotInventory_PotSize) -- nothing to
-- drop here anymore. Re-checking it here too, so this step stays a
-- correct, idempotent no-op even if this script is ever run against a
-- database where PART A0 was skipped for some other reason.
IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_PottedPlantStock_PotSize')
BEGIN
    ALTER TABLE dbo.PottedPlantStock DROP CONSTRAINT FK_PottedPlantStock_PotSize;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_PottedPlantStock_EmptyPotInventory')
BEGIN
    ALTER TABLE dbo.PottedPlantStock
        ADD CONSTRAINT FK_PottedPlantStock_EmptyPotInventory FOREIGN KEY (EmptyPotInventoryId) REFERENCES dbo.EmptyPotInventory(Id);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_PottedPlantStock_Area')
BEGIN
    ALTER TABLE dbo.PottedPlantStock
        ADD CONSTRAINT FK_PottedPlantStock_Area FOREIGN KEY (AreaId) REFERENCES dbo.Area(Id);
END
GO

IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'UQ_PottedPlantStock_SpeciesPotSize')
BEGIN
    ALTER TABLE dbo.PottedPlantStock DROP CONSTRAINT UQ_PottedPlantStock_SpeciesPotSize;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UQ_PottedPlantStock_SpeciesPotSizeArea')
BEGIN
    CREATE UNIQUE INDEX UQ_PottedPlantStock_SpeciesPotSizeArea ON dbo.PottedPlantStock(SpeciesId, PotSize, AreaId);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PottedPlantStock_AreaId')
BEGIN
    CREATE INDEX IX_PottedPlantStock_AreaId ON dbo.PottedPlantStock(AreaId);
END
GO

-- ------------------------------------------------------------
-- PART A3) Add AreaId + EmptyPotInventoryId to dbo.PotProduction, so
-- every NEW Pot Production run records which Area it drew empty pots
-- from / produced potted stock into. Pre-existing rows get AreaId =
-- NULL (their stock effects already landed in the "unassigned" pools
-- created before this phase).
-- ------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.PotProduction') AND name = 'AreaId')
BEGIN
    ALTER TABLE dbo.PotProduction ADD AreaId INT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.PotProduction') AND name = 'EmptyPotInventoryId')
BEGIN
    ALTER TABLE dbo.PotProduction ADD EmptyPotInventoryId INT NULL;
END
GO

UPDATE pp
SET pp.EmptyPotInventoryId = epi.Id
FROM dbo.PotProduction pp
INNER JOIN dbo.EmptyPotInventory epi ON epi.PotSize = pp.PotSize AND epi.AreaId IS NULL
WHERE pp.EmptyPotInventoryId IS NULL;
GO

IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_PotProduction_PotSize')
BEGIN
    ALTER TABLE dbo.PotProduction DROP CONSTRAINT FK_PotProduction_PotSize;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_PotProduction_EmptyPotInventory')
BEGIN
    ALTER TABLE dbo.PotProduction
        ADD CONSTRAINT FK_PotProduction_EmptyPotInventory FOREIGN KEY (EmptyPotInventoryId) REFERENCES dbo.EmptyPotInventory(Id);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_PotProduction_Area')
BEGIN
    ALTER TABLE dbo.PotProduction
        ADD CONSTRAINT FK_PotProduction_Area FOREIGN KEY (AreaId) REFERENCES dbo.Area(Id);
END
GO

-- ------------------------------------------------------------
-- PART B) dbo.InternalTransfers -- moves quantity of ONE stock kind
-- (Empty Pot or Potted Plant) from a source Area's pool to a
-- destination Area's pool. Never touches the two pools' quantities
-- directly: Data/InternalTransferRepository.cs calls the SAME
-- RecordTransactionAsync methods Phase 7 already built (decrement the
-- source with a 'Transfer' ledger entry, increment/create-then-
-- increment the destination with another 'Transfer' ledger entry),
-- inside one database transaction, so a Transfer is exactly two
-- ledger rows plus this header row -- consistent with "never modify
-- stock without a corresponding transaction/history record."
-- ------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'InternalTransfers' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.InternalTransfers
    (
        Id                INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_InternalTransfers PRIMARY KEY,
        TransferCode      NVARCHAR(20)   NOT NULL,

        -- 'EmptyPot' | 'PottedPlant' -- which stock kind this transfer moves.
        StockType         NVARCHAR(20)   NOT NULL,

        -- For StockType = 'EmptyPot': identifies the Pot Size pool.
        -- For StockType = 'PottedPlant': identifies the Species+PotSize pool.
        -- Exactly one of these two is populated, matching StockType
        -- (enforced by CK_InternalTransfers_SourceMatchesStockType
        -- below).
        SourceEmptyPotInventoryId  INT NULL,
        SourcePottedPlantStockId   INT NULL,
        SourceAreaId               INT NOT NULL,

        -- The destination pool is resolved (get-or-create) by the
        -- repository at the SAME PotSize (and SpeciesId, for Potted
        -- Plant) as the source, in the destination Area -- so only the
        -- destination Area needs to be captured here; which exact
        -- destination row it landed in is visible via the ledger's
        -- ReferenceId on both stock tables.
        DestinationAreaId         INT NOT NULL,

        Quantity          DECIMAL(18,2)  NOT NULL,
        Status            NVARCHAR(30)   NOT NULL CONSTRAINT DF_InternalTransfers_Status DEFAULT ('Completed'),
        ResponsiblePersonId INT          NULL,
        SupervisorId        INT          NULL,
        Remarks           NVARCHAR(500)  NULL,
        CreatedDate       DATETIME2      NOT NULL CONSTRAINT DF_InternalTransfers_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CreatedBy         NVARCHAR(100)  NULL,
        ModifiedDate      DATETIME2      NULL,
        ModifiedBy        NVARCHAR(100)  NULL,

        CONSTRAINT UQ_InternalTransfers_Code UNIQUE (TransferCode),
        CONSTRAINT FK_InternalTransfers_SourceEmptyPot  FOREIGN KEY (SourceEmptyPotInventoryId) REFERENCES dbo.EmptyPotInventory(Id),
        CONSTRAINT FK_InternalTransfers_SourcePotted     FOREIGN KEY (SourcePottedPlantStockId)  REFERENCES dbo.PottedPlantStock(Id),
        CONSTRAINT FK_InternalTransfers_SourceArea        FOREIGN KEY (SourceAreaId)              REFERENCES dbo.Area(Id),
        CONSTRAINT FK_InternalTransfers_DestinationArea   FOREIGN KEY (DestinationAreaId)         REFERENCES dbo.Area(Id),
        CONSTRAINT FK_InternalTransfers_Responsible        FOREIGN KEY (ResponsiblePersonId)      REFERENCES dbo.IMSUsers(Id),
        CONSTRAINT FK_InternalTransfers_Supervisor         FOREIGN KEY (SupervisorId)             REFERENCES dbo.IMSUsers(Id),

        CONSTRAINT CK_InternalTransfers_StockType CHECK (StockType IN ('EmptyPot', 'PottedPlant')),
        CONSTRAINT CK_InternalTransfers_Status    CHECK (Status IN ('Completed', 'Cancelled')),
        CONSTRAINT CK_InternalTransfers_Quantity  CHECK (Quantity > 0),
        CONSTRAINT CK_InternalTransfers_DifferentAreas CHECK (SourceAreaId <> DestinationAreaId),
        CONSTRAINT CK_InternalTransfers_SourceMatchesStockType CHECK (
            (StockType = 'EmptyPot'     AND SourceEmptyPotInventoryId IS NOT NULL AND SourcePottedPlantStockId IS NULL) OR
            (StockType = 'PottedPlant'  AND SourcePottedPlantStockId  IS NOT NULL AND SourceEmptyPotInventoryId IS NULL)
        )
    );

    CREATE INDEX IX_InternalTransfers_SourceEmptyPot ON dbo.InternalTransfers(SourceEmptyPotInventoryId);
    CREATE INDEX IX_InternalTransfers_SourcePotted    ON dbo.InternalTransfers(SourcePottedPlantStockId);
    CREATE INDEX IX_InternalTransfers_Status          ON dbo.InternalTransfers(Status);
END
GO
