/* ============================================================
   Phase 22: Main Office -> Polyhouse/Growing Area Seed Issue (Phase H)
   ------------------------------------------------------------
   INSPECTION SUMMARY (recorded here per the standing "explain exactly
   why before introducing a new table" rule):

   The codebase already has THREE seed-adjacent things:
     1) dbo.SeedCuttingBank / dbo.SeedCuttingTx (legacy, no CREATE
        script exists anywhere in this repo -- these are pre-existing
        DB objects, consumed only via opaque stored procedures
        dbo.SeedCutting_Consume/_Unconsume). Inspection of
        Data/SeedBankRepository.cs and Pages/Office/SeedBank.cshtml.cs
        found: no AreaId column at all (keyed only by PlantId+
        SpeciesId -- a single global pool, not per-location); no
        UPDLOCK/HOLDLOCK anywhere -- the UI's own Edit action lets a
        user directly overwrite Quantity/UtilizedQuantity with a plain
        UPDATE and no transaction; Available is not independently
        auditable (no ledger fields matching this app's established
        "TransactionDate/Type/Reference/Before/After" shape); the
        consuming stored procedures are not defined anywhere in this
        repo, so their exact locking/validation behavior cannot even
        be verified. It has no way to represent "stock currently at a
        specific Polyhouse/Growing Area" at all, which is the entire
        point of this phase. Building Main Office -> Polyhouse seed
        issue on top of it would mean either (a) leaving it exactly as
        unsafe as it is today and bolting an Area concept onto it via
        a side table (fragile, still no locking on the base table), or
        (b) reworking it in place, which risks the legacy SeedEntry/
        Sowing/Inventory pipeline this same table already feeds
        (explicitly out of scope -- Phase H must not touch that
        pipeline). CONCLUSION: not usable, and not safely extendable
        without touching legacy behavior this phase is not authorized
        to change. A new, dedicated seed stock table is required.
     2) dbo.SeedSources (Models/SeedSource.cs, Data/SeedSourcesRepository.cs)
        -- a small, genuinely reusable master table (Id, Name,
        IsActive) for "who supplied this seed." REUSED AS-IS below
        (SeedStock.SeedSourceId), not duplicated.
     3) dbo.PlantSpecies -- the existing Plant/Variety master every
        other stock table in this app already keys against
        (PottedPlantStock, CuttingStock, EmptyPotInventory indirectly
        via PotSize). REUSED AS-IS below (SeedStock.SpeciesId), not
        duplicated.

   Also inspected: dbo.InternalTransfers (Phase 8/15/18/20). It
   already carries THREE parallel nullable source FK columns
   (SourceEmptyPotInventoryId / SourcePottedPlantStockId /
   SourceCuttingStockId) discriminated by StockType, so a fourth
   (SourceSeedStockId) would be mechanically additive. It was NOT
   reused for this phase regardless, per the explicit instruction to
   "keep this separate from Phase 18" and because Seeds are a
   genuinely new inventory domain: InternalTransfers' CancelAsync/
   RejectAsync already branch on five StockTypes in one shared method
   body, and adding a sixth, differently-shaped domain (Species+Area+
   BatchNo+SeedSource, vs the PotSize/Species-only shapes every
   existing StockType already assumes) into that already-dense shared
   file raises regression risk for Phase 18/20/F's own logic for no
   real benefit -- a dedicated module (mirroring Phase 16's own
   precedent of a fully separate CuttingStock/CuttingDelivery table
   set rather than extending InternalTransfers) keeps this phase's
   code, and its risk, fully isolated. See PROJECT_DOCUMENTATION.md
   Decision 19 for the full writeup.

   THIS SCRIPT creates, from scratch:
     - dbo.SeedStock               (per Species+Area+Lot pool)
     - dbo.SeedStockTransactions   (its dedicated ledger, same shape as
                                    every other Phase 7+ stock ledger)
     - dbo.SeedIssues              (the Main Office -> Polyhouse issue
                                    header, PendingConfirmation ->
                                    Completed/Rejected, mirroring Phase
                                    18's MainOfficeIssue lifecycle shape
                                    exactly, but as its own table rather
                                    than a StockType on InternalTransfers)

   ADDITIVE ONLY. Does not touch, alter, or drop any existing table.
   Every CREATE is guarded so this script is safe to run more than
   once. Reuses the EXISTING dbo.BatchNumberSequences mechanism (via
   BatchNumberRepository) for the "SI-" prefix -- no MAX(Code)+1
   anywhere.

   Run this AFTER Phase21_OutletSalesBookingDispatch.sql has been
   applied. Take a backup first per your own DB safety rules.
   ============================================================ */

-- ------------------------------------------------------------
-- 1) Seed Stock (per Species + Area + Lot/Batch pool) + its ledger
-- ------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'SeedStock' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.SeedStock
    (
        Id                INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SeedStock PRIMARY KEY,
        SpeciesId         INT            NOT NULL,
        AreaId            INT            NOT NULL,

        -- Optional per spec item 13 ("seed lot/batch... if existing
        -- architecture supports it") -- NOT NULL with an empty-string
        -- default (rather than nullable) specifically so a real UNIQUE
        -- constraint can be placed on (SpeciesId, AreaId, BatchNo)
        -- without hitting SQL Server's "at most one NULL" behavior on
        -- unique indexes -- a brand-new table has no legacy data to
        -- accommodate, so this is a clean, deliberate choice, not
        -- something forced onto existing rows (contrast with Phase 8's
        -- EmptyPotInventory.AreaId, which HAD to stay nullable for
        -- pre-existing legacy rows).
        BatchNo           NVARCHAR(50)   NOT NULL CONSTRAINT DF_SeedStock_BatchNo DEFAULT (''),

        -- Supplier/source, if known -- reuses the EXISTING dbo.SeedSources
        -- master (Phase-agnostic legacy table), never duplicated.
        SeedSourceId      INT            NULL,

        Unit              NVARCHAR(20)   NOT NULL CONSTRAINT DF_SeedStock_Unit DEFAULT ('pcs'),

        PhysicalQuantity  DECIMAL(18,2)  NOT NULL CONSTRAINT DF_SeedStock_PhysicalQuantity DEFAULT (0),

        -- Mirrors CuttingStock/PottedPlantStock's InTransitQuantity
        -- (Phase 16 / Phase 18): raised at Issue time (nothing else
        -- moves yet), released at Confirm/Reject time. There is no
        -- "ReservedQuantity" concept here at all -- unlike
        -- PottedPlantStock, nothing else (no Booking-equivalent) ever
        -- competes for Seed Stock in this phase, so Available is
        -- simply Physical - InTransit, safely PERSISTED (PottedPlantStock
        -- could not do this because ITS AvailableQuantity was already
        -- defined, pre-Phase-18, as Physical - Reserved only, and
        -- redefining it would have changed Booking/Dispatch's existing
        -- meaning -- no such prior definition exists here to protect).
        InTransitQuantity DECIMAL(18,2)  NOT NULL CONSTRAINT DF_SeedStock_InTransitQuantity DEFAULT (0),
        AvailableQuantity AS (PhysicalQuantity - InTransitQuantity) PERSISTED,

        CreatedDate       DATETIME2      NOT NULL CONSTRAINT DF_SeedStock_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CreatedBy         NVARCHAR(100)  NULL,
        ModifiedDate      DATETIME2      NULL,
        ModifiedBy        NVARCHAR(100)  NULL,

        CONSTRAINT UQ_SeedStock_SpeciesAreaBatch UNIQUE (SpeciesId, AreaId, BatchNo),
        CONSTRAINT FK_SeedStock_Species    FOREIGN KEY (SpeciesId)    REFERENCES dbo.PlantSpecies(Id),
        CONSTRAINT FK_SeedStock_Area        FOREIGN KEY (AreaId)       REFERENCES dbo.Area(Id),
        CONSTRAINT FK_SeedStock_SeedSource FOREIGN KEY (SeedSourceId) REFERENCES dbo.SeedSources(Id),

        CONSTRAINT CK_SeedStock_Physical  CHECK (PhysicalQuantity >= 0),
        CONSTRAINT CK_SeedStock_InTransit CHECK (InTransitQuantity >= 0),
        CONSTRAINT CK_SeedStock_InTransitWithinPhysical CHECK (InTransitQuantity <= PhysicalQuantity)
    );

    CREATE INDEX IX_SeedStock_SpeciesId ON dbo.SeedStock(SpeciesId);
    CREATE INDEX IX_SeedStock_AreaId    ON dbo.SeedStock(AreaId);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'SeedStockTransactions' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.SeedStockTransactions
    (
        Id              INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SeedStockTransactions PRIMARY KEY,
        SeedStockId     INT            NOT NULL,
        TransactionDate DATETIME2      NOT NULL CONSTRAINT DF_SeedStockTx_TransactionDate DEFAULT (SYSUTCDATETIME()),

        -- 'StockIn'  -- Main Office (or any Area) receives seed directly
        --              (Pages/Production/SeedStock/AddStock).
        -- 'Transfer' -- the confirmed movement of a Seed Issue: a
        --              negative entry against the source pool and a
        --              positive entry against the destination pool,
        --              written together in ONE transaction -- reusing
        --              the exact 'Transfer' TransactionType name every
        --              other confirmed Area-to-Area stock movement in
        --              this app already uses (EmptyPot/PottedPlant/
        --              Cutting/MainOfficeIssue/GrowingPartnerToOutlet),
        --              per the explicit "reuse existing transaction
        --              types, do not blindly invent Issue/Receipt
        --              names" instruction. No ledger row is ever
        --              written for the InTransit rise/fall itself
        --              (mirrors ReserveInTransitAsync/ReleaseInTransitAsync
        --              elsewhere: nothing has actually moved yet).
        TransactionType NVARCHAR(30)   NOT NULL,
        ReferenceType   NVARCHAR(30)   NULL,        -- e.g. 'SeedIssue'
        ReferenceId     INT            NULL,
        Quantity        DECIMAL(18,2)  NOT NULL,     -- signed delta
        BeforeQuantity  DECIMAL(18,2)  NOT NULL,
        AfterQuantity   AS (BeforeQuantity + Quantity) PERSISTED,
        UserId          INT            NULL,
        Remarks         NVARCHAR(500)  NULL,
        CreatedAt       DATETIME2      NOT NULL CONSTRAINT DF_SeedStockTx_CreatedAt DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT FK_SeedStockTx_SeedStock FOREIGN KEY (SeedStockId) REFERENCES dbo.SeedStock(Id),
        CONSTRAINT FK_SeedStockTx_User      FOREIGN KEY (UserId)      REFERENCES dbo.IMSUsers(Id),
        CONSTRAINT CK_SeedStockTx_Type CHECK (TransactionType IN ('StockIn', 'Transfer'))
    );

    CREATE INDEX IX_SeedStockTransactions_SeedStockId ON dbo.SeedStockTransactions(SeedStockId);
END
GO

-- ------------------------------------------------------------
-- 2) Seed Issues (Main Office -> Polyhouse/Growing Area header)
-- ------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'SeedIssues' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.SeedIssues
    (
        Id                 INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SeedIssues PRIMARY KEY,
        IssueCode          NVARCHAR(20)   NOT NULL,

        SourceSeedStockId  INT            NOT NULL,

        -- Denormalized from the locked SourceSeedStock row at Insert
        -- time, exactly like every other transfer/issue/booking header
        -- in this app (Booking.SpeciesId/PotSize/AreaId, InternalTransfers.
        -- SourceAreaId, etc.) -- never trusted from the caller.
        SpeciesId          INT            NOT NULL,
        SourceAreaId       INT            NOT NULL,

        DestinationAreaId  INT            NOT NULL,

        IssuedQuantity     DECIMAL(18,2)  NOT NULL,
        IssueDate          DATETIME2      NOT NULL CONSTRAINT DF_SeedIssues_IssueDate DEFAULT (SYSUTCDATETIME()),

        -- PendingConfirmation -> Completed | Rejected. Deliberately the
        -- SAME two-outcome, single-confirmation shape as Phase 18's
        -- MainOfficeIssue -- no ConfirmedAwaitingTransplant/Transplanted
        -- stage, per the explicit "no multi-stage Cutting-style workflow
        -- for Seed Issue" instruction.
        Status             NVARCHAR(30)   NOT NULL CONSTRAINT DF_SeedIssues_Status DEFAULT ('PendingConfirmation'),

        ConfirmedQuantity  DECIMAL(18,2)  NULL,
        ConfirmedBy        INT            NULL,
        ConfirmedDate      DATETIME2      NULL,

        -- Reused for BOTH the short-receipt discrepancy reason AND the
        -- rejection reason -- mirrors dbo.InternalTransfers' own
        -- DiscrepancyReason column, which RejectAsync there already
        -- writes the rejection reason into rather than a second column.
        DiscrepancyReason  NVARCHAR(500)  NULL,

        ResponsiblePersonId INT           NULL,
        SupervisorId        INT           NULL,
        Remarks             NVARCHAR(500) NULL,

        CreatedDate        DATETIME2      NOT NULL CONSTRAINT DF_SeedIssues_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CreatedBy          NVARCHAR(100)  NULL,
        ModifiedDate       DATETIME2      NULL,
        ModifiedBy         NVARCHAR(100)  NULL,

        CONSTRAINT UQ_SeedIssues_Code UNIQUE (IssueCode),
        CONSTRAINT FK_SeedIssues_SourceSeedStock  FOREIGN KEY (SourceSeedStockId) REFERENCES dbo.SeedStock(Id),
        CONSTRAINT FK_SeedIssues_Species          FOREIGN KEY (SpeciesId)         REFERENCES dbo.PlantSpecies(Id),
        CONSTRAINT FK_SeedIssues_SourceArea        FOREIGN KEY (SourceAreaId)      REFERENCES dbo.Area(Id),
        CONSTRAINT FK_SeedIssues_DestinationArea   FOREIGN KEY (DestinationAreaId) REFERENCES dbo.Area(Id),
        CONSTRAINT FK_SeedIssues_ResponsiblePerson FOREIGN KEY (ResponsiblePersonId) REFERENCES dbo.IMSUsers(Id),
        CONSTRAINT FK_SeedIssues_Supervisor        FOREIGN KEY (SupervisorId)         REFERENCES dbo.IMSUsers(Id),
        CONSTRAINT FK_SeedIssues_ConfirmedBy        FOREIGN KEY (ConfirmedBy)          REFERENCES dbo.IMSUsers(Id),

        CONSTRAINT CK_SeedIssues_Status CHECK (Status IN ('PendingConfirmation', 'Completed', 'Rejected')),
        CONSTRAINT CK_SeedIssues_IssuedQuantity CHECK (IssuedQuantity > 0),
        CONSTRAINT CK_SeedIssues_ConfirmedQuantity CHECK (ConfirmedQuantity IS NULL OR (ConfirmedQuantity >= 0 AND ConfirmedQuantity <= IssuedQuantity)),
        CONSTRAINT CK_SeedIssues_DifferentAreas CHECK (SourceAreaId <> DestinationAreaId)
    );

    CREATE INDEX IX_SeedIssues_Status ON dbo.SeedIssues(Status);
    CREATE INDEX IX_SeedIssues_SourceAreaId ON dbo.SeedIssues(SourceAreaId);
    CREATE INDEX IX_SeedIssues_DestinationAreaId ON dbo.SeedIssues(DestinationAreaId);
END
GO
