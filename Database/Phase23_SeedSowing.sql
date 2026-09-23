/* ============================================================
   Phase 23: Seed Stock -> Sowing (Phase I)
   ------------------------------------------------------------
   INSPECTION SUMMARY (per the standing "inspect before creating"
   rule):

   Sowing is a single-actor PRODUCTION event, not a two-party
   transfer -- structurally it is the seed-domain equivalent of
   Phase 7's dbo.PotProduction (which consumes dbo.EmptyPotInventory
   and produces dbo.PottedPlantStock in one step, no confirm/reject
   workflow) rather than Phase 22's dbo.SeedIssues (which moves seed
   between two Areas and needs a PendingConfirmation stage). A
   Polyhouse/Growing Area Supervisor sows their OWN already-received
   Seed Stock -- there is no second party to confirm receipt from.

   Legacy name collision check: this app already has an unrelated
   legacy "Sowing" concept (Pages/SeedEntry/Sowing.cshtml,
   Models/SowingRecord.cs, dbo... behind the old Seed/Inventory
   pipeline) that Phase H's own inspection already found unsafe to
   touch/reuse and explicitly out of scope. To avoid any naming
   confusion with that pre-existing, untouched module, every new
   object here is prefixed "SeedSowing" (table, model, repository,
   pages), matching Phase 22's own "SeedStock"/"SeedIssues" naming --
   never the bare "Sowing" name the legacy pipeline already owns.

   Traceable inputs: this table ONLY consumes dbo.SeedStock (Phase
   22/H) -- it does not read or write dbo.SeedCuttingBank/SeedCuttingTx,
   dbo.SeedEntries, or any other legacy seed table.

   Destination/location rule: SourceSeedStockId's own AreaId must
   resolve, server-side, to an ACTIVE Area whose AreaType is 'Kunjir'
   or 'Kiran' -- the exact same evidence-based rule Phase H's
   post-review correction already established for where a Seed Issue
   may be received (see PROJECT_DOCUMENTATION.md Decision 19's
   correction paragraph: these are the only two AreaTypes, in the
   app's one closed 5-value AreaType enum, that represent general
   growing/production Areas). Reused here rather than re-litigated,
   since Sowing only ever happens at the same kind of location a Seed
   Issue can be received at.

   Cavity type: POST-REVIEW CORRECTION. Originally left as free text
   (mirroring Decision 16's PotSize precedent), on the assumption no
   fixed cavity list had been confirmed by the business. The business
   owner has since confirmed the nursery's sowing cavity types ARE a
   closed, already-known set: '9 Cavity', '24 Cavity', '42 Cavity',
   '102 Cavity', '150 Cavity'. Reporting by cavity type (a Phase J/K
   concern) would otherwise silently split identical stock across
   '102 Cavity' / '102-cell' / '102 cell' / '102c' typed by different
   users -- exactly the failure mode Decision 8's own AreaType enum
   (CK_Area_AreaType) was designed to prevent for Area classification.
   Correction: CavityType is now locked to that exact 5-value list via
   CK_SeedSowings_CavityType, mirroring CK_Area_AreaType's own
   "NVARCHAR column + CHECK IN (...)" shape -- NOT a new lookup master
   table (no appropriate existing master covers this, and one row per
   value five values is not worth a generic lookup framework) and NOT
   a numeric/enum column (kept as the exact display string so no
   separate code<->label mapping is needed anywhere). This script was
   corrected IN PLACE, not via a new Phase24 migration, because it has
   never been executed against any real database (this sandbox has no
   live SQL Server access at all -- see PROJECT_DOCUMENTATION.md's
   Build Verification section) -- there is no historical row anywhere
   that this edit could invalidate.

   Expected Ready Date: a plain, OPTIONAL, manually-entered DATE.
   Deliberately NOT auto-computed from any CavityType/Species
   duration table -- building that calculation now would mean
   building the "ReadyStockDays" concept Phase H's own spec
   explicitly named as OUT OF SCOPE until Phase J/K. The column
   exists purely so Phase J (Ready Alerts) has something to query
   later -- per the "make Phase I possible" compatibility note --
   without this phase computing or acting on it itself.

   Ledger: NO new dedicated ledger table. Sowing only consumes
   dbo.SeedStock, so it reuses dbo.SeedStockTransactions exactly as
   Phase 19/D reused dbo.CuttingStockTransactions for cutting
   consumption by Pot Production (Decision 15) -- widens
   CK_SeedStockTx_Type to add 'Sown' (the consuming entry) and
   'ReversalReturn' (credit back on a cancelled Sowing, reusing the
   EXACT name Decision 15 already established for "credit stock back
   on reversal" across this app, never a new name).

   Reversal: CancelAsync is included (every other production-style
   table in this app -- PotProduction, Cutting transplant -- offers
   one), crediting the consumed seed back to its source pool under
   lock and marking the SeedSowings row 'Cancelled'. The row is never
   deleted.

   ADDITIVE ONLY. Does not touch, alter, or drop any existing table
   other than widening dbo.SeedStockTransactions' own type CHECK.
   Every CREATE/ALTER is guarded so this script is safe to run more
   than once. Reuses the EXISTING dbo.BatchNumberSequences mechanism
   (via BatchNumberRepository) for the "SOW-" prefix -- no
   MAX(Code)+1 anywhere, and no collision with any prefix already in
   use (AC, BK, CD, CUT, DIS, LAB, LBR, MP, PO, POT, PROP, SI, TR).

   Run this AFTER Phase22_MainOfficeSeedIssue.sql has been applied.
   Take a backup first per your own DB safety rules.
   ============================================================ */

-- ------------------------------------------------------------
-- 1) Widen dbo.SeedStockTransactions' type CHECK (additive only)
-- ------------------------------------------------------------
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_SeedStockTx_Type')
BEGIN
    ALTER TABLE dbo.SeedStockTransactions DROP CONSTRAINT CK_SeedStockTx_Type;
END
GO

ALTER TABLE dbo.SeedStockTransactions ADD CONSTRAINT CK_SeedStockTx_Type
    CHECK (TransactionType IN ('StockIn', 'Transfer', 'Sown', 'ReversalReturn'));
GO

-- ------------------------------------------------------------
-- 2) Seed Sowing (the Phase I production event)
-- ------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'SeedSowings' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.SeedSowings
    (
        Id                 INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SeedSowings PRIMARY KEY,
        SowingCode         NVARCHAR(20)   NOT NULL,

        SourceSeedStockId  INT            NOT NULL,

        -- Denormalized from the locked SourceSeedStock row at Insert
        -- time -- never trusted from the caller, exactly like every
        -- other production/transfer/issue header in this app.
        SpeciesId          INT            NOT NULL,
        AreaId             INT            NOT NULL,
        BatchNo            NVARCHAR(50)   NOT NULL CONSTRAINT DF_SeedSowings_BatchNo DEFAULT (''),
        SeedSourceId       INT            NULL,

        -- POST-REVIEW CORRECTION: locked to the business's exact 5-value
        -- cavity list via CK_SeedSowings_CavityType below (mirrors
        -- CK_Area_AreaType's own shape) -- no longer free text.
        -- NumberOfTrays is purely informational; nothing in this phase
        -- validates it against QuantitySown/CavityType.
        CavityType         NVARCHAR(30)   NOT NULL,
        NumberOfTrays      INT            NULL,

        -- Quantity of SEED consumed, in the same Unit as the source
        -- SeedStock pool. This is what decrements SeedStock.PhysicalQuantity.
        QuantitySown       DECIMAL(18,2)  NOT NULL,

        SowingDate         DATETIME2      NOT NULL CONSTRAINT DF_SeedSowings_SowingDate DEFAULT (SYSUTCDATETIME()),

        -- Optional, manually-entered estimate -- see header comment:
        -- deliberately not auto-computed. Exists for Phase J to read
        -- later, not for this phase to act on.
        ExpectedReadyDate  DATE           NULL,

        -- 'Sown' -> 'Cancelled' only. No 'Ready'/'Dispatched' values --
        -- those belong to Phase J/K, not built here. The CHECK is
        -- still guarded/re-creatable so those phases can widen it
        -- later without a redesign, exactly like every other
        -- lifecycle CHECK in this app.
        Status             NVARCHAR(30)   NOT NULL CONSTRAINT DF_SeedSowings_Status DEFAULT ('Sown'),

        ResponsiblePersonId INT           NULL,
        SupervisorId        INT           NULL,
        Remarks             NVARCHAR(500) NULL,

        CreatedDate        DATETIME2      NOT NULL CONSTRAINT DF_SeedSowings_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CreatedBy          NVARCHAR(100)  NULL,
        ModifiedDate       DATETIME2      NULL,
        ModifiedBy         NVARCHAR(100)  NULL,

        CONSTRAINT UQ_SeedSowings_Code UNIQUE (SowingCode),
        CONSTRAINT FK_SeedSowings_SourceSeedStock FOREIGN KEY (SourceSeedStockId) REFERENCES dbo.SeedStock(Id),
        CONSTRAINT FK_SeedSowings_Species          FOREIGN KEY (SpeciesId)         REFERENCES dbo.PlantSpecies(Id),
        CONSTRAINT FK_SeedSowings_Area              FOREIGN KEY (AreaId)            REFERENCES dbo.Area(Id),
        CONSTRAINT FK_SeedSowings_SeedSource        FOREIGN KEY (SeedSourceId)      REFERENCES dbo.SeedSources(Id),
        CONSTRAINT FK_SeedSowings_ResponsiblePerson FOREIGN KEY (ResponsiblePersonId) REFERENCES dbo.IMSUsers(Id),
        CONSTRAINT FK_SeedSowings_Supervisor        FOREIGN KEY (SupervisorId)         REFERENCES dbo.IMSUsers(Id),

        CONSTRAINT CK_SeedSowings_Status CHECK (Status IN ('Sown', 'Cancelled')),
        CONSTRAINT CK_SeedSowings_QuantitySown CHECK (QuantitySown > 0),
        CONSTRAINT CK_SeedSowings_NumberOfTrays CHECK (NumberOfTrays IS NULL OR NumberOfTrays > 0),

        -- POST-REVIEW CORRECTION: the exact, closed set of cavity types
        -- confirmed by the business owner. Exact-string match (no
        -- trimming/case-folding at the DB level) -- the application
        -- layer (SeedSowingRepository.InsertAsync, and the Create page's
        -- dropdown) is what guarantees only these five strings, spelled
        -- exactly this way, are ever offered or submitted, so a variant
        -- like '102-cell'/'102 cell'/'102c'/'102 cavity' never reaches
        -- this far -- this CHECK is the last-resort backstop against a
        -- hand-crafted POST bypassing the application layer entirely.
        CONSTRAINT CK_SeedSowings_CavityType CHECK (CavityType IN ('9 Cavity', '24 Cavity', '42 Cavity', '102 Cavity', '150 Cavity'))
    );

    CREATE INDEX IX_SeedSowings_AreaId ON dbo.SeedSowings(AreaId);
    CREATE INDEX IX_SeedSowings_SourceSeedStockId ON dbo.SeedSowings(SourceSeedStockId);
    CREATE INDEX IX_SeedSowings_Status ON dbo.SeedSowings(Status);
END
GO
