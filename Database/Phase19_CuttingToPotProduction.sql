/* ============================================================
   Phase 19: Cutting Stock -> Pot Production (Phase D)
   ------------------------------------------------------------
   Adds a SECOND source for dbo.PotProduction: a Growing Partner's
   dbo.CuttingStock (Phase 15/16), alongside the existing
   dbo.PropagationBatches source (Phase 6/7). The legacy path is
   left completely intact -- every existing row, every existing
   FK/CHECK that still applies to it, and every existing repository
   method that only ever touches it are unaffected.

       Legacy (unchanged):  PropagationBatch -> PotProduction -> PottedPlantStock
       New (this phase):    Growing Partner CuttingStock -> PotProduction -> PottedPlantStock

   Both paths still consume from dbo.EmptyPotInventory and produce
   into dbo.PottedPlantStock through the EXACT SAME mechanism
   (Data/PotProductionRepository.cs) -- Pot Size / Area handling is
   not duplicated. The only structural difference is WHERE the
   planting material comes from.

   ADDITIVE ONLY.
     - No existing table is dropped.
     - Every ADD COLUMN / ADD CONSTRAINT / CREATE FUNCTION is guarded
       so this script is safe to re-run.
     - The two ALTER COLUMN (nullability) statements and the four
       DROP-then-CREATE-then-ADD sequences (for the two
       scalar-function-backed CHECK constraints) are each individually
       idempotent: re-running this script leaves the schema in the
       exact same end state every time, with no data loss -- dropping
       a CHECK constraint or a function does not touch any row's data,
       and every existing PotProduction row (PropagationBatchId NOT
       NULL, SourceCuttingStockId NULL) already satisfies every new
       rule added below, so no existing data is put at risk when the
       new CHECK constraints are (re-)added WITH CHECK.

   Run this AFTER Phase18_MainOfficeGrowingPartnerIssue.sql has been
   applied. Take a backup first per your own DB safety rules.
   ============================================================ */

-- ------------------------------------------------------------
-- 1) Drop the two Phase 7 scalar-function-backed CHECK constraints
--    (and their backing functions) so PropagationBatchId/MotherPlantId
--    can be relaxed to nullable below, and so the functions can be
--    rewritten to bypass a Cutting Stock-sourced row (PropagationBatchId
--    IS NULL) instead of rejecting it. Dropping a CHECK constraint
--    never touches row data.
-- ------------------------------------------------------------
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_PotProduction_MatchesPropagationBatch')
BEGIN
    ALTER TABLE dbo.PotProduction DROP CONSTRAINT CK_PotProduction_MatchesPropagationBatch;
END
GO

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_PotProduction_WithinSurvivedQuantity')
BEGIN
    ALTER TABLE dbo.PotProduction DROP CONSTRAINT CK_PotProduction_WithinSurvivedQuantity;
END
GO

IF EXISTS (SELECT 1 FROM sys.objects WHERE name = 'fn_PotProduction_MatchesPropagationBatch' AND type = 'FN')
BEGIN
    DROP FUNCTION dbo.fn_PotProduction_MatchesPropagationBatch;
END
GO

IF EXISTS (SELECT 1 FROM sys.objects WHERE name = 'fn_PotProduction_WithinSurvivedQuantity' AND type = 'FN')
BEGIN
    DROP FUNCTION dbo.fn_PotProduction_WithinSurvivedQuantity;
END
GO

-- ------------------------------------------------------------
-- 2) Relax PropagationBatchId / MotherPlantId to nullable. A
--    Cutting Stock-sourced row has no Propagation Batch and no Mother
--    Plant lineage at all, so both are left NULL for it (enforced by
--    CK_PotProduction_SourceType below, added after SourceCuttingStockId
--    exists). Every existing row already has both columns populated,
--    so relaxing NOT NULL -> NULL is a pure widening with no data
--    impact.
-- ------------------------------------------------------------
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.PotProduction') AND name = 'PropagationBatchId' AND is_nullable = 0)
BEGIN
    ALTER TABLE dbo.PotProduction ALTER COLUMN PropagationBatchId INT NULL;
END
GO

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.PotProduction') AND name = 'MotherPlantId' AND is_nullable = 0)
BEGIN
    ALTER TABLE dbo.PotProduction ALTER COLUMN MotherPlantId INT NULL;
END
GO

-- ------------------------------------------------------------
-- 3) New columns: the alternate source (a specific CuttingStock row)
--    and how many cuttings it cost to produce this row's Quantity of
--    potted plants. Quantity itself keeps its existing, unchanged
--    meaning ("pots consumed = potted plants produced") for BOTH
--    paths -- no redefinition, per the explicit instruction not to
--    repurpose an existing field's meaning. Production loss for a
--    Cutting-sourced row is a DERIVED value (CuttingQuantityConsumed -
--    Quantity), computed in the application layer
--    (Models/PotProduction.cs's Loss property) rather than stored as
--    a third column, since it adds no information the other two
--    columns don't already carry.
-- ------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.PotProduction') AND name = 'SourceCuttingStockId')
BEGIN
    ALTER TABLE dbo.PotProduction ADD SourceCuttingStockId INT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.PotProduction') AND name = 'CuttingQuantityConsumed')
BEGIN
    ALTER TABLE dbo.PotProduction ADD CuttingQuantityConsumed DECIMAL(18,2) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_PotProduction_CuttingStock')
BEGIN
    ALTER TABLE dbo.PotProduction
        ADD CONSTRAINT FK_PotProduction_CuttingStock FOREIGN KEY (SourceCuttingStockId) REFERENCES dbo.CuttingStock(Id);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PotProduction_SourceCuttingStockId')
BEGIN
    CREATE INDEX IX_PotProduction_SourceCuttingStockId ON dbo.PotProduction(SourceCuttingStockId);
END
GO

-- ------------------------------------------------------------
-- 4) Source-consistency CHECK constraints.
--
--    CK_PotProduction_SourceType: exactly one of PropagationBatchId /
--    SourceCuttingStockId is populated, never both, never neither --
--    and MotherPlantId's presence exactly follows PropagationBatchId
--    (a Cutting-sourced row has no Mother Plant lineage; a
--    Batch-sourced row always has, unchanged from today).
--
--    CK_PotProduction_CuttingQuantityConsumed: CuttingQuantityConsumed
--    is populated if and only if SourceCuttingStockId is, and -- the
--    stock rule applied to this stage -- it can never be LESS than
--    Quantity (you cannot produce more potted plants than the
--    cuttings you consumed; the difference is potting loss).
--
--    Every existing row (PropagationBatchId NOT NULL, MotherPlantId
--    NOT NULL, SourceCuttingStockId NULL, CuttingQuantityConsumed
--    NULL) already satisfies both, so adding these WITH CHECK (the
--    default) is safe against current data.
-- ------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_PotProduction_SourceType')
BEGIN
    ALTER TABLE dbo.PotProduction
        ADD CONSTRAINT CK_PotProduction_SourceType
        CHECK (
            (PropagationBatchId IS NOT NULL AND SourceCuttingStockId IS NULL AND MotherPlantId IS NOT NULL)
            OR
            (PropagationBatchId IS NULL AND SourceCuttingStockId IS NOT NULL AND MotherPlantId IS NULL)
        );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_PotProduction_CuttingQuantityConsumed')
BEGIN
    ALTER TABLE dbo.PotProduction
        ADD CONSTRAINT CK_PotProduction_CuttingQuantityConsumed
        CHECK (
            (SourceCuttingStockId IS NULL AND CuttingQuantityConsumed IS NULL)
            OR
            (SourceCuttingStockId IS NOT NULL AND CuttingQuantityConsumed IS NOT NULL AND CuttingQuantityConsumed >= Quantity)
        );
END
GO

-- ------------------------------------------------------------
-- 5) Recreate the two Phase 7 defense-in-depth functions, now
--    bypassing (returning 1 / pass) whenever PropagationBatchId IS
--    NULL -- i.e. whenever the row is Cutting Stock-sourced, per
--    CK_PotProduction_SourceType above. The real consistency/quantity
--    checks for a Cutting-sourced row are performed in the application
--    layer (Data/PotProductionRepository.cs.InsertFromCuttingStockAsync,
--    under a row lock on the source CuttingStock), mirroring exactly
--    how every earlier phase in this chain (Phase 15/16/18) already
--    does its authoritative validation in the repository and treats
--    the DB CHECK as a backstop.
--
--    For every EXISTING (Batch-sourced) row, both functions behave
--    IDENTICALLY to their original Phase 7 definitions -- the only
--    change is the new leading bypass for PropagationBatchId IS NULL,
--    which no existing row can ever hit.
-- ------------------------------------------------------------
EXEC('
CREATE FUNCTION dbo.fn_PotProduction_MatchesPropagationBatch(@PropagationBatchId INT, @MotherPlantId INT, @SpeciesId INT)
RETURNS BIT
AS
BEGIN
    -- Phase 19: a Cutting Stock-sourced row has no Propagation Batch
    -- to match against -- nothing to validate here for it.
    IF @PropagationBatchId IS NULL RETURN 1;

    DECLARE @Result BIT = 0;
    IF EXISTS (
        SELECT 1 FROM dbo.PropagationBatches
        WHERE Id = @PropagationBatchId
          AND MotherPlantId = @MotherPlantId
          AND SpeciesId = @SpeciesId
    )
        SET @Result = 1;
    RETURN @Result;
END');
GO

EXEC('
CREATE FUNCTION dbo.fn_PotProduction_WithinSurvivedQuantity(@Id INT, @PropagationBatchId INT, @Quantity DECIMAL(18,2))
RETURNS BIT
AS
BEGIN
    -- Phase 19: a Cutting Stock-sourced row draws against CuttingStock
    -- (validated in the application layer against AvailableQuantity),
    -- not against any Propagation Batch''s SurvivedQuantity -- nothing
    -- to validate here for it.
    IF @PropagationBatchId IS NULL RETURN 1;

    DECLARE @Result BIT = 0;
    DECLARE @SurvivedQuantity DECIMAL(18,2);
    DECLARE @OtherRowsTotal DECIMAL(18,2);

    SELECT @SurvivedQuantity = SurvivedQuantity FROM dbo.PropagationBatches WHERE Id = @PropagationBatchId;

    SELECT @OtherRowsTotal = ISNULL(SUM(Quantity), 0)
    FROM dbo.PotProduction
    WHERE PropagationBatchId = @PropagationBatchId AND Id <> @Id AND Status <> ''Cancelled'';

    IF @SurvivedQuantity IS NOT NULL AND (@OtherRowsTotal + @Quantity) <= @SurvivedQuantity
        SET @Result = 1;

    RETURN @Result;
END');
GO

ALTER TABLE dbo.PotProduction
    ADD CONSTRAINT CK_PotProduction_MatchesPropagationBatch
    CHECK (dbo.fn_PotProduction_MatchesPropagationBatch(PropagationBatchId, MotherPlantId, SpeciesId) = 1);
GO

ALTER TABLE dbo.PotProduction
    ADD CONSTRAINT CK_PotProduction_WithinSurvivedQuantity
    CHECK (dbo.fn_PotProduction_WithinSurvivedQuantity(Id, PropagationBatchId, Quantity) = 1);
GO

-- ------------------------------------------------------------
-- 6) Cutting ledger: reuse the Phase 15 'Potted' type (reserved,
--    unused until now, for exactly this consumption) for the source
--    side -- no widening needed for that direction.
--
--    Reversal (Cancel) needs to CREDIT cuttings back, which is a
--    different semantic from every existing CuttingStockTransactions
--    type: 'Harvest'/'Transfer' credit for other reasons, 'Potted' only
--    ever debits, and 'ReversalRemoval' (already on this table) is, by
--    its established meaning on every OTHER ledger in this app
--    (PottedPlantStockTransactions), strictly a NEGATIVE/removing
--    entry -- using it here for a positive credit-back would silently
--    contradict that convention. dbo.EmptyPotInventoryTransactions
--    already has exactly this "credit stock back on reversal" concept
--    under the name 'ReversalReturn' (Phase 8), so CuttingStockTransactions
--    gains the SAME name for the SAME purpose, rather than inventing a
--    new one -- the one narrow, justified widening this phase makes to
--    an existing ledger's type list.
-- ------------------------------------------------------------
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CuttingStockTx_Type')
BEGIN
    ALTER TABLE dbo.CuttingStockTransactions DROP CONSTRAINT CK_CuttingStockTx_Type;
END
GO

ALTER TABLE dbo.CuttingStockTransactions ADD CONSTRAINT CK_CuttingStockTx_Type
    CHECK (TransactionType IN ('Harvest', 'Transfer', 'Potted', 'Adjustment', 'ReversalRemoval', 'Transplanted', 'ReversalReturn'));
GO

-- No widening needed on dbo.PottedPlantStockTransactions -- 'Production'
-- (destination credit) and 'ReversalRemoval' (reversal) already exist
-- and are reused as-is for the Cutting-sourced path, identically to the
-- legacy path.

-- No new Permission code needed -- PotProduction.View / PotProduction.Enter
-- (Phase 14) already govern "view/enter pot production records"
-- regardless of source type.
