-- ============================================================================
-- Phase C: Ready Stock -> Booking -> Reservation -> Batch Allocation -> Dispatch
-- ============================================================================
-- Run AFTER PhaseB_DirectSowing.sql. Take a full backup first.
-- Additive and idempotent: every step is guarded, so the script is safe to
-- run more than once. Nothing is deleted, no existing row is updated, no
-- table or column is dropped or renamed, users/passwords are not touched.
--
-- WHAT IT DOES
--   1. dbo.ReadyStock (Phase B, one row per sowing batch):
--        + ReservedQuantity   plants promised to bookings, not yet dispatched
--        + DispatchedQuantity plants that have left the nursery (cumulative)
--        + PhysicalQuantity   = Quantity - DispatchedQuantity   (computed)
--        + AvailableQuantity  = Quantity - Reserved - Dispatched (computed)
--      Quantity keeps its Phase B meaning: the supervisor-approved Ready
--      quantity. It is NOT reduced by booking, reservation or dispatch, so
--      the approved history of every batch stays intact.
--      CHECK: Reserved >= 0, Dispatched >= 0, Reserved + Dispatched <= Quantity.
--   2. dbo.ReadyStockTransactions (ledger): TransactionType widened with
--      'Reservation', 'ReservationRelease' (BeforeQuantity = ReservedQuantity)
--      and 'Dispatch' (BeforeQuantity = PhysicalQuantity).
--   3. dbo.Bookings (existing seedling bookings): new columns only, all with
--      safe defaults -- existing rows get 0 / NULL and are NOT rewritten.
--        ReservedQuantity, DispatchedQuantity (Ready Stock flow only),
--        FulfilmentSource NULL | 'Legacy' | 'ReadyStock' (cutover support),
--        RevisionNo, ParentBookingId (booking split off by a revision),
--        CancelledById / CancelledDate / CancellationReason,
--        ModifiedDate / ModifiedBy.
--      The existing Status CHECK (Pending / Completed / Cancelled) is NOT
--      touched.
--   4. New dbo.BookingBatchAllocations: booking <-> Ready Stock batch
--      (reservation and allocation, incl. same-species substitution).
--   5. New dbo.BookingRevisions: booking change history.
--   6. New dbo.SeedlingDispatches + dbo.SeedlingDispatchLines: seedling
--      dispatch header and batch lines (the potted-plant dbo.Dispatches table
--      is NOT touched).
--   7. Read-only validation queries.
-- ============================================================================

SET XACT_ABORT ON;
GO

-- ----------------------------------------------------------------------------
-- STEP 1: ReadyStock reservation / dispatch quantities
-- ----------------------------------------------------------------------------
IF COL_LENGTH('dbo.ReadyStock', 'ReservedQuantity') IS NULL
BEGIN
    ALTER TABLE dbo.ReadyStock ADD ReservedQuantity DECIMAL(18,2) NOT NULL
        CONSTRAINT DF_ReadyStock_ReservedQuantity DEFAULT (0);
END
GO
IF COL_LENGTH('dbo.ReadyStock', 'DispatchedQuantity') IS NULL
BEGIN
    ALTER TABLE dbo.ReadyStock ADD DispatchedQuantity DECIMAL(18,2) NOT NULL
        CONSTRAINT DF_ReadyStock_DispatchedQuantity DEFAULT (0);
END
GO
IF COL_LENGTH('dbo.ReadyStock', 'PhysicalQuantity') IS NULL
BEGIN
    ALTER TABLE dbo.ReadyStock ADD PhysicalQuantity AS (Quantity - DispatchedQuantity) PERSISTED;
END
GO
IF COL_LENGTH('dbo.ReadyStock', 'AvailableQuantity') IS NULL
BEGIN
    ALTER TABLE dbo.ReadyStock ADD AvailableQuantity AS (Quantity - ReservedQuantity - DispatchedQuantity) PERSISTED;
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_ReadyStock_ReservedDispatched')
BEGIN
    ALTER TABLE dbo.ReadyStock ADD CONSTRAINT CK_ReadyStock_ReservedDispatched
        CHECK (ReservedQuantity >= 0 AND DispatchedQuantity >= 0 AND ReservedQuantity + DispatchedQuantity <= Quantity);
END
GO

-- ----------------------------------------------------------------------------
-- STEP 2: ReadyStockTransactions -- new ledger types (existing types kept)
-- ----------------------------------------------------------------------------
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_ReadyStockTx_Type'
           AND [definition] NOT LIKE '%ReservationRelease%')
BEGIN
    ALTER TABLE dbo.ReadyStockTransactions DROP CONSTRAINT CK_ReadyStockTx_Type;
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_ReadyStockTx_Type')
BEGIN
    ALTER TABLE dbo.ReadyStockTransactions ADD CONSTRAINT CK_ReadyStockTx_Type
        CHECK (TransactionType IN ('Confirmed', 'ReversalRemoval', 'Reservation', 'ReservationRelease', 'Dispatch'));
END
GO

-- ----------------------------------------------------------------------------
-- STEP 3: Bookings -- new columns (safe defaults; no existing value changed)
-- ----------------------------------------------------------------------------
IF COL_LENGTH('dbo.Bookings', 'ReservedQuantity') IS NULL
BEGIN
    ALTER TABLE dbo.Bookings ADD ReservedQuantity DECIMAL(18,2) NOT NULL
        CONSTRAINT DF_Bookings_ReservedQuantity DEFAULT (0);
END
GO
IF COL_LENGTH('dbo.Bookings', 'DispatchedQuantity') IS NULL
BEGIN
    ALTER TABLE dbo.Bookings ADD DispatchedQuantity DECIMAL(18,2) NOT NULL
        CONSTRAINT DF_Bookings_DispatchedQuantity DEFAULT (0);
END
GO
IF COL_LENGTH('dbo.Bookings', 'FulfilmentSource') IS NULL
BEGIN
    ALTER TABLE dbo.Bookings ADD FulfilmentSource NVARCHAR(20) NULL;
END
GO
IF COL_LENGTH('dbo.Bookings', 'RevisionNo') IS NULL
BEGIN
    ALTER TABLE dbo.Bookings ADD RevisionNo INT NOT NULL
        CONSTRAINT DF_Bookings_RevisionNo DEFAULT (0);
END
GO
IF COL_LENGTH('dbo.Bookings', 'ParentBookingId') IS NULL
BEGIN
    ALTER TABLE dbo.Bookings ADD ParentBookingId INT NULL;
END
GO
IF COL_LENGTH('dbo.Bookings', 'CancelledById') IS NULL
BEGIN
    ALTER TABLE dbo.Bookings ADD CancelledById INT NULL;
END
GO
IF COL_LENGTH('dbo.Bookings', 'CancelledDate') IS NULL
BEGIN
    ALTER TABLE dbo.Bookings ADD CancelledDate DATETIME2 NULL;
END
GO
IF COL_LENGTH('dbo.Bookings', 'CancellationReason') IS NULL
BEGIN
    ALTER TABLE dbo.Bookings ADD CancellationReason NVARCHAR(500) NULL;
END
GO
IF COL_LENGTH('dbo.Bookings', 'ModifiedDate') IS NULL
BEGIN
    ALTER TABLE dbo.Bookings ADD ModifiedDate DATETIME2 NULL;
END
GO
IF COL_LENGTH('dbo.Bookings', 'ModifiedBy') IS NULL
BEGIN
    ALTER TABLE dbo.Bookings ADD ModifiedBy NVARCHAR(100) NULL;
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Bookings_ParentBooking')
BEGIN
    ALTER TABLE dbo.Bookings ADD CONSTRAINT FK_Bookings_ParentBooking FOREIGN KEY (ParentBookingId) REFERENCES dbo.Bookings(Id);
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Bookings_CancelledBy')
BEGIN
    ALTER TABLE dbo.Bookings ADD CONSTRAINT FK_Bookings_CancelledBy FOREIGN KEY (CancelledById) REFERENCES dbo.IMSUsers(Id);
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_Bookings_FulfilmentSource')
BEGIN
    ALTER TABLE dbo.Bookings ADD CONSTRAINT CK_Bookings_FulfilmentSource
        CHECK (FulfilmentSource IS NULL OR FulfilmentSource IN ('Legacy', 'ReadyStock'));
END
GO
-- Reserved + Dispatched can never exceed the booked quantity. Created only if
-- every existing row already satisfies it (new columns are 0, so any row with
-- Quantity >= 0 does); otherwise it is reported and skipped, never forced.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_Bookings_FulfilmentQuantities')
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.Bookings WHERE ReservedQuantity + DispatchedQuantity > Quantity OR ReservedQuantity < 0 OR DispatchedQuantity < 0)
        PRINT 'WARNING: CK_Bookings_FulfilmentQuantities NOT created -- existing rows violate it (see validation queries).';
    ELSE
        ALTER TABLE dbo.Bookings ADD CONSTRAINT CK_Bookings_FulfilmentQuantities
            CHECK (ReservedQuantity >= 0 AND DispatchedQuantity >= 0 AND ReservedQuantity + DispatchedQuantity <= Quantity);
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Bookings_ParentBookingId' AND object_id = OBJECT_ID('dbo.Bookings'))
BEGIN
    -- Plain (not filtered) index: a filtered index on this existing, busy table
    -- would require QUOTED_IDENTIFIER/ANSI_NULLS ON for every writer.
    CREATE INDEX IX_Bookings_ParentBookingId ON dbo.Bookings(ParentBookingId);
END
GO

-- ----------------------------------------------------------------------------
-- STEP 4: BookingBatchAllocations (reservation / allocation per batch)
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.BookingBatchAllocations', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.BookingBatchAllocations
    (
        Id                  INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_BookingBatchAllocations PRIMARY KEY,
        BookingId           INT            NOT NULL,
        ReadyStockId        INT            NOT NULL,
        BookedSpeciesId     INT            NULL,      -- variety on the booking
        ActualSpeciesId     INT            NOT NULL,  -- variety of the batch
        IsSubstitution      BIT            NOT NULL CONSTRAINT DF_BookingBatchAllocations_IsSubstitution DEFAULT (0),
        SubstitutionReason  NVARCHAR(500)  NULL,
        Quantity            DECIMAL(18,2)  NOT NULL,  -- total ever allocated on this line
        DispatchedQuantity  DECIMAL(18,2)  NOT NULL CONSTRAINT DF_BookingBatchAllocations_Dispatched DEFAULT (0),
        ReleasedQuantity    DECIMAL(18,2)  NOT NULL CONSTRAINT DF_BookingBatchAllocations_Released DEFAULT (0),
        OpenQuantity AS (Quantity - DispatchedQuantity - ReleasedQuantity) PERSISTED,
        Status              NVARCHAR(20)   NOT NULL CONSTRAINT DF_BookingBatchAllocations_Status DEFAULT ('Active'),
        CreatedById         INT            NULL,
        CreatedBy           NVARCHAR(100)  NULL,
        CreatedDate         DATETIME2      NOT NULL CONSTRAINT DF_BookingBatchAllocations_CreatedDate DEFAULT (SYSUTCDATETIME()),
        ModifiedBy          NVARCHAR(100)  NULL,
        ModifiedDate        DATETIME2      NULL,

        CONSTRAINT FK_BookingBatchAllocations_Booking       FOREIGN KEY (BookingId)       REFERENCES dbo.Bookings(Id),
        CONSTRAINT FK_BookingBatchAllocations_ReadyStock    FOREIGN KEY (ReadyStockId)    REFERENCES dbo.ReadyStock(Id),
        CONSTRAINT FK_BookingBatchAllocations_BookedSpecies FOREIGN KEY (BookedSpeciesId) REFERENCES dbo.PlantSpecies(Id),
        CONSTRAINT FK_BookingBatchAllocations_ActualSpecies FOREIGN KEY (ActualSpeciesId) REFERENCES dbo.PlantSpecies(Id),
        CONSTRAINT FK_BookingBatchAllocations_CreatedBy     FOREIGN KEY (CreatedById)     REFERENCES dbo.IMSUsers(Id),
        CONSTRAINT CK_BookingBatchAllocations_Quantity CHECK (Quantity > 0 AND DispatchedQuantity >= 0 AND ReleasedQuantity >= 0
                                                              AND DispatchedQuantity + ReleasedQuantity <= Quantity),
        CONSTRAINT CK_BookingBatchAllocations_Status CHECK (Status IN ('Active', 'Closed')),
        CONSTRAINT CK_BookingBatchAllocations_Closed CHECK (Status = 'Active' OR DispatchedQuantity + ReleasedQuantity = Quantity),
        CONSTRAINT CK_BookingBatchAllocations_Substitution CHECK (IsSubstitution = 0 OR LEN(LTRIM(RTRIM(ISNULL(SubstitutionReason, '')))) > 0)
    );

    CREATE INDEX IX_BookingBatchAllocations_BookingId ON dbo.BookingBatchAllocations(BookingId);
    CREATE INDEX IX_BookingBatchAllocations_ReadyStockId ON dbo.BookingBatchAllocations(ReadyStockId);
    -- One active line per booking + batch (no duplicate allocations).
    CREATE UNIQUE INDEX UX_BookingBatchAllocations_Active ON dbo.BookingBatchAllocations(BookingId, ReadyStockId) WHERE Status = 'Active';
END
GO

-- ----------------------------------------------------------------------------
-- STEP 5: BookingRevisions (history -- the booking row keeps only the latest)
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.BookingRevisions', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.BookingRevisions
    (
        Id                   INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_BookingRevisions PRIMARY KEY,
        BookingId            INT            NOT NULL,
        RevisionNo           INT            NOT NULL,
        PreviousPlantId      INT            NULL,
        NewPlantId           INT            NULL,
        PreviousSpeciesId    INT            NULL,
        NewSpeciesId         INT            NULL,
        PreviousQuantity     INT            NOT NULL,
        NewQuantity          INT            NOT NULL,
        PreviousDeliveryDate DATE           NULL,
        NewDeliveryDate      DATE           NULL,
        ReleasedQuantity     DECIMAL(18,2)  NOT NULL CONSTRAINT DF_BookingRevisions_Released DEFAULT (0),
        SplitBookingId       INT            NULL,     -- booking created for an added variety
        OtherChanges         NVARCHAR(1000) NULL,
        Reason               NVARCHAR(500)  NOT NULL,
        ChangedById          INT            NULL,
        ChangedBy            NVARCHAR(100)  NULL,
        ChangedDate          DATETIME2      NOT NULL CONSTRAINT DF_BookingRevisions_ChangedDate DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT FK_BookingRevisions_Booking      FOREIGN KEY (BookingId)      REFERENCES dbo.Bookings(Id),
        CONSTRAINT FK_BookingRevisions_SplitBooking FOREIGN KEY (SplitBookingId) REFERENCES dbo.Bookings(Id),
        CONSTRAINT FK_BookingRevisions_ChangedBy    FOREIGN KEY (ChangedById)    REFERENCES dbo.IMSUsers(Id),
        CONSTRAINT UQ_BookingRevisions_BookingRevision UNIQUE (BookingId, RevisionNo),
        CONSTRAINT CK_BookingRevisions_Quantity CHECK (NewQuantity > 0 AND ReleasedQuantity >= 0),
        CONSTRAINT CK_BookingRevisions_Reason CHECK (LEN(LTRIM(RTRIM(Reason))) > 0)
    );
END
GO

-- ----------------------------------------------------------------------------
-- STEP 6: SeedlingDispatches + SeedlingDispatchLines
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.SeedlingDispatches', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.SeedlingDispatches
    (
        Id                  INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SeedlingDispatches PRIMARY KEY,
        DispatchCode        NVARCHAR(20)   NOT NULL,
        BookingId           INT            NOT NULL,
        DispatchDate        DATE           NOT NULL,
        CustomerName        NVARCHAR(200)  NULL,      -- snapshot at dispatch time
        TotalQuantity       DECIMAL(18,2)  NOT NULL,
        Status              NVARCHAR(20)   NOT NULL CONSTRAINT DF_SeedlingDispatches_Status DEFAULT ('Completed'),
        ResponsiblePersonId INT            NULL,
        Remarks             NVARCHAR(500)  NULL,
        CreatedById         INT            NULL,
        CreatedBy           NVARCHAR(100)  NULL,
        CreatedDate         DATETIME2      NOT NULL CONSTRAINT DF_SeedlingDispatches_CreatedDate DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT UQ_SeedlingDispatches_Code UNIQUE (DispatchCode),
        CONSTRAINT FK_SeedlingDispatches_Booking     FOREIGN KEY (BookingId)           REFERENCES dbo.Bookings(Id),
        CONSTRAINT FK_SeedlingDispatches_Responsible FOREIGN KEY (ResponsiblePersonId) REFERENCES dbo.IMSUsers(Id),
        CONSTRAINT FK_SeedlingDispatches_CreatedBy   FOREIGN KEY (CreatedById)         REFERENCES dbo.IMSUsers(Id),
        CONSTRAINT CK_SeedlingDispatches_Quantity CHECK (TotalQuantity > 0),
        CONSTRAINT CK_SeedlingDispatches_Status CHECK (Status IN ('Completed', 'Cancelled'))
    );

    CREATE INDEX IX_SeedlingDispatches_BookingId ON dbo.SeedlingDispatches(BookingId);
    CREATE INDEX IX_SeedlingDispatches_DispatchDate ON dbo.SeedlingDispatches(DispatchDate);
END
GO
IF OBJECT_ID('dbo.SeedlingDispatchLines', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.SeedlingDispatchLines
    (
        Id                       INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SeedlingDispatchLines PRIMARY KEY,
        SeedlingDispatchId       INT            NOT NULL,
        BookingBatchAllocationId INT            NOT NULL,
        ReadyStockId             INT            NOT NULL,
        BookedSpeciesId          INT            NULL,
        ActualSpeciesId          INT            NOT NULL,
        IsSubstitution           BIT            NOT NULL CONSTRAINT DF_SeedlingDispatchLines_IsSubstitution DEFAULT (0),
        SubstitutionReason       NVARCHAR(500)  NULL,
        Quantity                 DECIMAL(18,2)  NOT NULL,

        CONSTRAINT FK_SeedlingDispatchLines_Dispatch      FOREIGN KEY (SeedlingDispatchId)       REFERENCES dbo.SeedlingDispatches(Id),
        CONSTRAINT FK_SeedlingDispatchLines_Allocation    FOREIGN KEY (BookingBatchAllocationId) REFERENCES dbo.BookingBatchAllocations(Id),
        CONSTRAINT FK_SeedlingDispatchLines_ReadyStock    FOREIGN KEY (ReadyStockId)             REFERENCES dbo.ReadyStock(Id),
        CONSTRAINT FK_SeedlingDispatchLines_BookedSpecies FOREIGN KEY (BookedSpeciesId)          REFERENCES dbo.PlantSpecies(Id),
        CONSTRAINT FK_SeedlingDispatchLines_ActualSpecies FOREIGN KEY (ActualSpeciesId)          REFERENCES dbo.PlantSpecies(Id),
        CONSTRAINT CK_SeedlingDispatchLines_Quantity CHECK (Quantity > 0)
    );

    CREATE INDEX IX_SeedlingDispatchLines_DispatchId ON dbo.SeedlingDispatchLines(SeedlingDispatchId);
    CREATE INDEX IX_SeedlingDispatchLines_AllocationId ON dbo.SeedlingDispatchLines(BookingBatchAllocationId);
    CREATE INDEX IX_SeedlingDispatchLines_ReadyStockId ON dbo.SeedlingDispatchLines(ReadyStockId);
END
GO

-- ----------------------------------------------------------------------------
-- STEP 7: VALIDATION (read-only)
-- ----------------------------------------------------------------------------
-- Existing bookings (unchanged). Legacy-fulfilled = has 'Allocation' rows in
-- dbo.InventoryTransactions (the old fulfil page).
SELECT b.Status, COUNT(*) AS Bookings, SUM(CAST(b.Quantity AS BIGINT)) AS BookedQuantity,
       SUM(CASE WHEN EXISTS (SELECT 1 FROM dbo.InventoryTransactions it
                             WHERE it.BookingId = b.Id AND it.TransactionType = 'Allocation') THEN 1 ELSE 0 END) AS LegacyFulfilled
FROM dbo.Bookings b
GROUP BY b.Status;

-- New booking columns initialised safely (expect 0 rows).
SELECT COUNT(*) AS BookingsWithUnexpectedPhaseCValues
FROM dbo.Bookings
WHERE ReservedQuantity < 0 OR DispatchedQuantity < 0 OR ReservedQuantity + DispatchedQuantity > Quantity;

-- Legacy seedling Inventory still available (old pipeline, unchanged).
SELECT COUNT(*) AS LegacyInventoryRowsAvailable, ISNULL(SUM(CAST(RemainingQuantity AS BIGINT)), 0) AS LegacyPlantsAvailable
FROM dbo.Inventory WHERE IsUtilized = 'N';

-- Ready Stock (expect ReservedMismatch = 0 and OverReserved = 0).
SELECT COUNT(*) AS ReadyStockBatches,
       ISNULL(SUM(rs.Quantity), 0) AS ApprovedReady,
       ISNULL(SUM(rs.PhysicalQuantity), 0) AS Physical,
       ISNULL(SUM(rs.ReservedQuantity), 0) AS Reserved,
       ISNULL(SUM(rs.DispatchedQuantity), 0) AS Dispatched,
       ISNULL(SUM(rs.AvailableQuantity), 0) AS Available,
       SUM(CASE WHEN rs.ReservedQuantity <> ISNULL(a.OpenQty, 0) THEN 1 ELSE 0 END) AS ReservedMismatch,
       SUM(CASE WHEN rs.ReservedQuantity + rs.DispatchedQuantity > rs.Quantity THEN 1 ELSE 0 END) AS OverReserved
FROM dbo.ReadyStock rs
LEFT JOIN (SELECT ReadyStockId, SUM(OpenQuantity) AS OpenQty FROM dbo.BookingBatchAllocations
           WHERE Status = 'Active' GROUP BY ReadyStockId) a ON a.ReadyStockId = rs.Id;

-- Booking totals equal their allocation lines (expect 0 rows).
SELECT b.Id AS BookingId, b.ReservedQuantity, b.DispatchedQuantity, x.OpenQty, x.DispatchedQty
FROM dbo.Bookings b
JOIN (SELECT BookingId, SUM(CASE WHEN Status = 'Active' THEN OpenQuantity ELSE 0 END) AS OpenQty,
             SUM(DispatchedQuantity) AS DispatchedQty
      FROM dbo.BookingBatchAllocations GROUP BY BookingId) x ON x.BookingId = b.Id
WHERE b.ReservedQuantity <> x.OpenQty OR b.DispatchedQuantity <> x.DispatchedQty;

-- New objects present (expect 4).
SELECT COUNT(*) AS PhaseCTables
FROM sys.tables
WHERE name IN ('BookingBatchAllocations', 'BookingRevisions', 'SeedlingDispatches', 'SeedlingDispatchLines');

-- Potted-plant booking system untouched (row counts only).
SELECT (SELECT COUNT(*) FROM dbo.PottedPlantBookings) AS PottedPlantBookings,
       (SELECT COUNT(*) FROM dbo.Dispatches) AS PottedPlantDispatches;
GO
