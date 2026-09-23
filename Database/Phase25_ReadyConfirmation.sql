/* ============================================================
   Phase 25: Ready Confirmation -> Ready Stock (Phase K)
   ------------------------------------------------------------
   INSPECTION SUMMARY (per the standing "inspect before creating"
   rule):

   Confirmed via grep across Database/*.sql, Models/*.cs, Data/*.cs
   that NO existing Ready Stock / Ready Confirmation table, model, or
   repository exists anywhere in this app -- the only pre-existing
   "Ready" artifact is the legacy, untouched Pages/Data/ReadyStock.cshtml
   report (a different pipeline, against dbo.Inventory, already
   established out of scope by Phase J's own inspection). This phase
   therefore designs a wholly new domain rather than extending an
   existing one, exactly as the spec's own instruction anticipated
   ("if no suitable Ready Stock structure exists, design a dedicated
   Ready Stock domain consistent with the existing stock architecture").

   Closest existing precedent (deliberately mirrored, not reinvented):
   Phase 21/Phase G's dbo.PottedPlantBookings.DispatchedQuantity +
   dbo.Dispatches pattern -- a parent record that tracks a running
   "how much of me has been fulfilled" total, fulfilled by one or more
   CHILD event rows (partial fulfillment allowed once
   UQ_Dispatches_Booking was dropped), each independently validated
   against the parent's own remaining quantity under the parent row's
   lock, with DispatchRepository.CancelAsync locking both rows to
   reverse safely. The Phase K spec's own worked examples (500 sown,
   300 then 200 confirmed = fully confirmed *ALLOWED*; 300 then 300
   *REJECTED*, only 200 remains) are structurally identical to this
   precedent, so it is reused rather than inventing a new
   one-confirmation-per-batch model.

   Design (see Decision 22 in PROJECT_DOCUMENTATION.md for the full
   reasoning):

   1) dbo.SeedSowings.ConfirmedReadyQuantity (additive column) -- the
      running total, mirroring PottedPlantBookings.DispatchedQuantity
      exactly (same CHECK shape: >= 0 AND <= the parent's own total
      quantity). Updated ONLY by dbo.ReadyConfirmations'
      Insert/CancelAsync, under a lock taken directly against
      dbo.SeedSowings by ReadyConfirmationRepository (mirrors
      DispatchRepository writing directly to
      dbo.PottedPlantBookings.DispatchedQuantity rather than routing
      through a separate Booking-repository method).

      Deliberately NO new dbo.SeedSowings.Status value (no
      'PartiallyReady'/'ReadyConfirmed'). The spec's own instruction
      ("if the current design already supports a confirmed quantity
      without changing status, prefer that") is followed exactly:
      Status stays 'Sown' -> 'Cancelled' only, completely untouched by
      this script and by every Phase 25 code path. This is a
      deliberate DIVERGENCE from Phase 21/Phase G's own precedent, which
      DID introduce a 'PartiallyDispatched' status on PottedPlantBookings
      -- the difference is that a Booking's Status feeds real downstream
      decisions elsewhere (e.g. what a Dispatch page offers), whereas a
      partially-Ready-Confirmed Sowing is still, in every other respect,
      simply "Sown" -- nothing else in this app branches on a
      Sowing-readiness state, so ConfirmedReadyQuantity vs QuantitySown
      alone is sufficient and a new status would be unused plumbing.

   2) dbo.ReadyStock (NEW table) -- the confirmed-ready stock POOL.
      Grain is ONE ROW PER SeedSowingId (UNIQUE), NOT pooled across
      Sowings by Species+Area+Lot the way dbo.SeedStock/
      dbo.PottedPlantStock/dbo.CuttingStock are. This is a deliberate
      departure from those tables' own pooling precedent, required by
      the spec's own explicit traceability rule: "Ready Stock must
      retain traceability back to the originating Seed Sowing... do
      not lose original batch identity." Pooling multiple Sowings'
      confirmed quantity into one Species+Area row (the SeedStock
      precedent) would destroy exactly the batch identity the spec
      asks to preserve, so the SeedSowing-scoped grain is used instead
      -- BatchNo/CavityType/SowingDate/AreaId/SpeciesId are denormalized
      onto this row directly from the owning Sowing, never re-derived.
      Created only the FIRST time a Sowing is actually Ready-Confirmed
      (via ReadyStockRepository.GetOrCreateLockedAsync) -- never
      eagerly at Sowing time, per the spec's explicit "do not
      automatically create Ready Stock" rule.

   3) dbo.ReadyStockTransactions (NEW table) -- the dedicated ledger
      for dbo.ReadyStock, per this app's own "dedicated ledger per
      stock entity" decision (mirrors dbo.SeedStockTransactions/
      dbo.PottedPlantStockTransactions exactly, including the
      persisted-computed AfterQuantity column and the
      never-negative-after CHECK). TransactionType is 'Confirmed'
      (positive -- a Ready Confirmation was recorded) or
      'ReversalRemoval' (negative -- a Ready Confirmation was
      cancelled). Per this app's Decision 15 naming convention,
      'ReversalReturn' is reserved for crediting stock back to a
      SOURCE pool it was taken FROM (e.g. cancelling a Sowing credits
      seed back to dbo.SeedStock); there is no source pool here --
      only an addition being undone -- so 'ReversalRemoval' is the
      correct existing name, never a new one.

   4) dbo.ReadyConfirmations (NEW table) -- the auditable confirmation
      EVENT/header, one row per explicit "this batch is physically
      ready" action (however many are eventually recorded against the
      same Sowing). Plays the exact role dbo.Dispatches plays against
      dbo.PottedPlantBookings: it both (a) drives dbo.ReadyStock's
      ledger via ReadyStockRepository.RecordTransactionAsync, and (b)
      maintains the parent dbo.SeedSowings.ConfirmedReadyQuantity
      running total. Its own Status ('Confirmed'/'Cancelled') is
      independent of dbo.SeedSowings.Status, exactly like
      dbo.Dispatches.Status is independent of
      dbo.PottedPlantBookings.Status. Cancellation IS supported (every
      other production-style table in this app -- SeedSowing,
      PotProduction, Dispatch -- already offers one, and the pattern is
      directly reusable): ReadyConfirmationRepository.CancelAsync locks
      both this row and the parent Sowing row, reverses via a
      'ReversalRemoval' ledger entry, decrements
      ConfirmedReadyQuantity, and marks this row 'Cancelled'. The row
      is NEVER deleted, and the underlying ReadyStock pool row is never
      deleted either (only its Quantity moves).

      Batch code: uses the EXISTING dbo.BatchNumberSequences mechanism
      (via BatchNumberRepository.GetNextBatchNumberAsync) with prefix
      "RDY" -- confirmed via grep across every existing
      GetNextBatchNumberAsync(conn, tx, "...") call site to be unused
      (AC, BK, CD, CUT, DIS, LAB, LBR, MP, PO, POT, PROP, SI, SOW, TR
      are the prefixes already in use). No MAX(Code)+1 anywhere.

   No modification to any Phase 16/18/19/20/21/22/23/24 object. No new
   dbo.SeedSowings.Status value, no new permission code (this app has
   no page-level permission attributes at all under /Production --
   AreaAccessService, checked at the page level exactly like every
   other Production page, is the sole authorization boundary re-used
   here unchanged), no change to CK_SeedSowings_Status/
   CK_SeedSowings_CavityType/CK_SeedStockTx_Type.

   ADDITIVE ONLY. Every existing dbo.SeedSowings row gets
   ConfirmedReadyQuantity = 0 (its DEFAULT), which is exactly the
   correct historical value for every row that predates this phase --
   none of them has ever been Ready-Confirmed. Guarded so this script
   is safe to run more than once.

   Run this AFTER Phase24_ReadyAlerts.sql has been applied.
   Take a backup first per your own DB safety rules.
   ============================================================ */

-- ------------------------------------------------------------
-- 1) dbo.SeedSowings.ConfirmedReadyQuantity (running total, mirrors
--    PottedPlantBookings.DispatchedQuantity from Phase 21/Phase G).
-- ------------------------------------------------------------
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.SeedSowings') AND name = 'ConfirmedReadyQuantity'
)
BEGIN
    ALTER TABLE dbo.SeedSowings ADD ConfirmedReadyQuantity DECIMAL(18,2) NOT NULL
        CONSTRAINT DF_SeedSowings_ConfirmedReadyQuantity DEFAULT (0);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_SeedSowings_ConfirmedReadyQuantity')
BEGIN
    ALTER TABLE dbo.SeedSowings ADD CONSTRAINT CK_SeedSowings_ConfirmedReadyQuantity
        CHECK (ConfirmedReadyQuantity >= 0 AND ConfirmedReadyQuantity <= QuantitySown);
END
GO

-- ------------------------------------------------------------
-- 2) dbo.ReadyStock -- the confirmed-ready stock pool, one row per
--    SeedSowingId (see header comment for why this grain, not a
--    Species+Area+Lot pool, is required).
-- ------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ReadyStock' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.ReadyStock
    (
        Id                    INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReadyStock PRIMARY KEY,

        -- One-to-one with the originating Sowing.
        SeedSowingId          INT            NOT NULL,

        -- Denormalized from the owning Sowing at the moment this row
        -- is first created -- never re-derived later, same discipline
        -- every other traceability field in this app already uses.
        SpeciesId             INT            NOT NULL,
        AreaId                INT            NOT NULL,
        BatchNo               NVARCHAR(50)   NOT NULL CONSTRAINT DF_ReadyStock_BatchNo DEFAULT (''),
        CavityType            NVARCHAR(30)   NOT NULL,
        SowingDate            DATETIME2      NOT NULL,

        -- Running balance of confirmed-ready stock not yet consumed by
        -- any later phase (nothing in this app consumes it yet).
        -- Raised only by RecordTransactionAsync('Confirmed', +qty),
        -- lowered only by RecordTransactionAsync('ReversalRemoval', -qty).
        Quantity              DECIMAL(18,2)  NOT NULL CONSTRAINT DF_ReadyStock_Quantity DEFAULT (0),

        FirstConfirmationDate DATETIME2      NULL,

        CreatedDate           DATETIME2      NOT NULL CONSTRAINT DF_ReadyStock_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CreatedBy             NVARCHAR(100)  NULL,
        ModifiedDate          DATETIME2      NULL,
        ModifiedBy            NVARCHAR(100)  NULL,

        CONSTRAINT UQ_ReadyStock_SeedSowing UNIQUE (SeedSowingId),
        CONSTRAINT FK_ReadyStock_SeedSowing FOREIGN KEY (SeedSowingId) REFERENCES dbo.SeedSowings(Id),
        CONSTRAINT FK_ReadyStock_Species    FOREIGN KEY (SpeciesId)    REFERENCES dbo.PlantSpecies(Id),
        CONSTRAINT FK_ReadyStock_Area       FOREIGN KEY (AreaId)       REFERENCES dbo.Area(Id),
        CONSTRAINT CK_ReadyStock_Quantity   CHECK (Quantity >= 0)
    );

    CREATE INDEX IX_ReadyStock_AreaId ON dbo.ReadyStock(AreaId);
    CREATE INDEX IX_ReadyStock_SpeciesId ON dbo.ReadyStock(SpeciesId);
END
GO

-- ------------------------------------------------------------
-- 3) dbo.ReadyStockTransactions -- the dedicated ledger for
--    dbo.ReadyStock, same shape as every other Phase 7+ stock ledger.
-- ------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ReadyStockTransactions' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.ReadyStockTransactions
    (
        Id              INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReadyStockTransactions PRIMARY KEY,
        ReadyStockId    INT            NOT NULL,
        TransactionDate DATETIME2      NOT NULL CONSTRAINT DF_ReadyStockTx_TransactionDate DEFAULT (SYSUTCDATETIME()),

        -- 'Confirmed'       -- a Ready Confirmation was recorded (positive).
        -- 'ReversalRemoval' -- a Ready Confirmation was cancelled (negative).
        -- Decision 15 naming convention: 'ReversalReturn' is reserved
        -- for crediting a SOURCE pool back; there is none here.
        TransactionType NVARCHAR(30)   NOT NULL,
        ReferenceType   NVARCHAR(30)   NULL,        -- 'ReadyConfirmation'
        ReferenceId     INT            NULL,        -- ReadyConfirmations.Id
        Quantity        DECIMAL(18,2)  NOT NULL,    -- SIGNED delta applied to Quantity
        BeforeQuantity  DECIMAL(18,2)  NOT NULL,    -- Quantity before this transaction
        AfterQuantity AS (BeforeQuantity + Quantity) PERSISTED,
        UserId          INT            NULL,
        Remarks         NVARCHAR(500)  NULL,
        CreatedAt       DATETIME2      NOT NULL CONSTRAINT DF_ReadyStockTx_CreatedAt DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT FK_ReadyStockTx_ReadyStock FOREIGN KEY (ReadyStockId) REFERENCES dbo.ReadyStock(Id),
        CONSTRAINT FK_ReadyStockTx_User       FOREIGN KEY (UserId)       REFERENCES dbo.IMSUsers(Id),
        CONSTRAINT CK_ReadyStockTx_Type CHECK (TransactionType IN ('Confirmed', 'ReversalRemoval')),
        CONSTRAINT CK_ReadyStockTx_NeverNegativeAfter CHECK (BeforeQuantity + Quantity >= 0)
    );

    CREATE INDEX IX_ReadyStockTransactions_ReadyStockId ON dbo.ReadyStockTransactions(ReadyStockId);
END
GO

-- ------------------------------------------------------------
-- 4) dbo.ReadyConfirmations -- the auditable confirmation event/header.
-- ------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ReadyConfirmations' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.ReadyConfirmations
    (
        Id                  INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReadyConfirmations PRIMARY KEY,
        ConfirmationCode    NVARCHAR(20)   NOT NULL,

        SeedSowingId        INT            NOT NULL,
        ReadyStockId         INT            NOT NULL,

        -- The caller-supplied (possibly partial) quantity confirmed
        -- ready NOW. Validated, under lock, against the parent
        -- Sowing's own remaining quantity in
        -- ReadyConfirmationRepository.ConfirmAsync.
        ConfirmedQuantity    DECIMAL(18,2)  NOT NULL,

        ConfirmationDate     DATETIME2      NOT NULL CONSTRAINT DF_ReadyConfirmations_ConfirmationDate DEFAULT (SYSUTCDATETIME()),

        -- 'Confirmed' -> 'Cancelled' only. Independent of
        -- dbo.SeedSowings.Status (see header comment).
        Status               NVARCHAR(30)   NOT NULL CONSTRAINT DF_ReadyConfirmations_Status DEFAULT ('Confirmed'),

        ResponsiblePersonId  INT            NULL,
        SupervisorId         INT            NULL,
        Remarks              NVARCHAR(500)  NULL,

        CreatedDate          DATETIME2      NOT NULL CONSTRAINT DF_ReadyConfirmations_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CreatedBy            NVARCHAR(100)  NULL,
        ModifiedDate         DATETIME2      NULL,
        ModifiedBy           NVARCHAR(100)  NULL,

        CONSTRAINT UQ_ReadyConfirmations_Code UNIQUE (ConfirmationCode),
        CONSTRAINT FK_ReadyConfirmations_SeedSowing FOREIGN KEY (SeedSowingId) REFERENCES dbo.SeedSowings(Id),
        CONSTRAINT FK_ReadyConfirmations_ReadyStock FOREIGN KEY (ReadyStockId) REFERENCES dbo.ReadyStock(Id),
        CONSTRAINT FK_ReadyConfirmations_ResponsiblePerson FOREIGN KEY (ResponsiblePersonId) REFERENCES dbo.IMSUsers(Id),
        CONSTRAINT FK_ReadyConfirmations_Supervisor        FOREIGN KEY (SupervisorId)         REFERENCES dbo.IMSUsers(Id),

        CONSTRAINT CK_ReadyConfirmations_Status CHECK (Status IN ('Confirmed', 'Cancelled')),
        CONSTRAINT CK_ReadyConfirmations_Quantity CHECK (ConfirmedQuantity > 0)
    );

    CREATE INDEX IX_ReadyConfirmations_SeedSowingId ON dbo.ReadyConfirmations(SeedSowingId);
    CREATE INDEX IX_ReadyConfirmations_Status ON dbo.ReadyConfirmations(Status);
END
GO
