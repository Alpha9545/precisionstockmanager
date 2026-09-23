/* ============================================================
   Phase 10: Dispatch
   ------------------------------------------------------------
   Completes a Phase 9 Booking (dbo.PottedPlantBookings) by
   physically reducing dbo.PottedPlantStock, per the stock rule
   "Booking reserves but never physically reduces stock; Dispatch
   completion reduces physical stock."

   dbo.Dispatches ("DIS-" prefix) is 1:1 with a Booking for this
   phase (UQ_Dispatches_Booking) -- a Booking is dispatched in full,
   in one step, matching how the existing legacy FulfillBooking flow
   also completes a whole booking at once. A future phase could add
   partial/split dispatch as its own additive migration; nothing
   here forecloses that.

   Dispatching writes TWO ledger entries against the SAME dedicated
   ledger Phase 7 already created (dbo.PottedPlantStockTransactions),
   continuing the convention set in Phase 9's migration:
     - a 'Dispatch' entry, whose Before/AfterQuantity represent
       PhysicalQuantity (physical stock actually leaves) -- 'Dispatch'
       was already reserved in Phase 7's CK_PottedStockTx_Type.
     - a 'ReservationRelease' entry, whose Before/AfterQuantity
       represent ReservedQuantity (the reservation is now fulfilled,
       so it comes off Reserved same as a cancellation would, just
       for a different reason) -- reuses the exact same TransactionType
       Phase 9 uses for a cancelled booking, since both mean "this
       reservation no longer exists," just with 'Dispatch' on the
       Physical-side entry distinguishing why.
   No new ledger table, per the "one dedicated ledger per stock
   entity" decision.

   Also widens dbo.PottedPlantBookings' Status CHECK constraint to
   add 'Dispatched' -- flagged as deferred in Phase 9's own migration
   comment, exactly like Phase 8 widened
   dbo.EmptyPotInventoryTransactions' CHECK constraint for 'Transfer'.

   ADDITIVE ONLY. Run this AFTER Phase9_BookingReservation.sql has
   been applied. Take a backup first per your own DB safety rules.
   ============================================================ */

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_PottedPlantBookings_Status')
BEGIN
    ALTER TABLE dbo.PottedPlantBookings DROP CONSTRAINT CK_PottedPlantBookings_Status;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_PottedPlantBookings_Status')
BEGIN
    ALTER TABLE dbo.PottedPlantBookings
        ADD CONSTRAINT CK_PottedPlantBookings_Status CHECK (Status IN ('Pending', 'Dispatched', 'Cancelled'));
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Dispatches' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.Dispatches
    (
        Id                  INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Dispatches PRIMARY KEY,
        DispatchCode        NVARCHAR(20)   NOT NULL,

        -- One Dispatch per Booking (UQ below) -- see header note.
        PottedPlantBookingId INT           NOT NULL,

        -- Denormalized copies of the Booking's own pool reference, so
        -- reports/traceability queries don't always need to join back
        -- through PottedPlantBookings -- always derived server-side
        -- from the referenced Booking, and CK_Dispatches_MatchesBooking
        -- below guarantees it (and the Quantity match) at the database
        -- level too.
        PottedPlantStockId  INT            NOT NULL,
        SpeciesId           INT            NOT NULL,
        PotSize             NVARCHAR(50)   NOT NULL,
        AreaId              INT            NULL,

        Quantity            DECIMAL(18,2)  NOT NULL,

        DispatchDate        DATETIME2      NOT NULL CONSTRAINT DF_Dispatches_DispatchDate DEFAULT (SYSUTCDATETIME()),
        Status              NVARCHAR(30)   NOT NULL CONSTRAINT DF_Dispatches_Status DEFAULT ('Completed'),

        ResponsiblePersonId INT            NULL,   -- delivery person
        SupervisorId        INT            NULL,

        Remarks             NVARCHAR(500)  NULL,
        CreatedDate         DATETIME2      NOT NULL CONSTRAINT DF_Dispatches_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CreatedBy           NVARCHAR(100)  NULL,
        ModifiedDate        DATETIME2      NULL,
        ModifiedBy          NVARCHAR(100)  NULL,

        CONSTRAINT UQ_Dispatches_Code    UNIQUE (DispatchCode),
        CONSTRAINT UQ_Dispatches_Booking UNIQUE (PottedPlantBookingId),
        CONSTRAINT FK_Dispatches_Booking     FOREIGN KEY (PottedPlantBookingId) REFERENCES dbo.PottedPlantBookings(Id),
        CONSTRAINT FK_Dispatches_Stock       FOREIGN KEY (PottedPlantStockId)   REFERENCES dbo.PottedPlantStock(Id),
        CONSTRAINT FK_Dispatches_Species     FOREIGN KEY (SpeciesId)            REFERENCES dbo.PlantSpecies(Id),
        CONSTRAINT FK_Dispatches_Area        FOREIGN KEY (AreaId)               REFERENCES dbo.Area(Id),
        CONSTRAINT FK_Dispatches_Responsible FOREIGN KEY (ResponsiblePersonId)  REFERENCES dbo.IMSUsers(Id),
        CONSTRAINT FK_Dispatches_Supervisor  FOREIGN KEY (SupervisorId)         REFERENCES dbo.IMSUsers(Id),

        CONSTRAINT CK_Dispatches_Status   CHECK (Status IN ('Completed', 'Cancelled')),
        CONSTRAINT CK_Dispatches_Quantity CHECK (Quantity > 0)
    );

    CREATE INDEX IX_Dispatches_Booking ON dbo.Dispatches(PottedPlantBookingId);
    CREATE INDEX IX_Dispatches_Stock   ON dbo.Dispatches(PottedPlantStockId);
    CREATE INDEX IX_Dispatches_Status  ON dbo.Dispatches(Status);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE name = 'fn_Dispatches_MatchesBooking' AND type = 'FN')
BEGIN
    EXEC('
    CREATE FUNCTION dbo.fn_Dispatches_MatchesBooking(@PottedPlantBookingId INT, @PottedPlantStockId INT, @SpeciesId INT, @PotSize NVARCHAR(50), @AreaId INT, @Quantity DECIMAL(18,2))
    RETURNS BIT
    AS
    BEGIN
        DECLARE @Result BIT = 0;
        IF EXISTS (
            SELECT 1 FROM dbo.PottedPlantBookings
            WHERE Id = @PottedPlantBookingId
              AND PottedPlantStockId = @PottedPlantStockId
              AND SpeciesId = @SpeciesId
              AND PotSize = @PotSize
              AND (AreaId = @AreaId OR (AreaId IS NULL AND @AreaId IS NULL))
              AND Quantity = @Quantity
        )
            SET @Result = 1;
        RETURN @Result;
    END');
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_Dispatches_MatchesBooking')
BEGIN
    ALTER TABLE dbo.Dispatches
        ADD CONSTRAINT CK_Dispatches_MatchesBooking
        CHECK (dbo.fn_Dispatches_MatchesBooking(PottedPlantBookingId, PottedPlantStockId, SpeciesId, PotSize, AreaId, Quantity) = 1);
END
GO
