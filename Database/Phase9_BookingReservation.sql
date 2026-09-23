/* ============================================================
   Phase 9: Booking Reservation / Available
   ------------------------------------------------------------
   A NEW, separate reservation system for dbo.PottedPlantStock
   (Phase 7) -- decided (with the business owner, see
   PROJECT_DOCUMENTATION.md) over extending the EXISTING
   dbo.Bookings table after inspecting it: dbo.Bookings,
   Data/BookingRepository.cs, and every page under Pages/Bookings are
   entirely wired to the OLD dbo.Inventory (seed-derived raw stock) -- creating a
   booking there writes a header row only, and FulfillBooking
   directly decrements Inventory.RemainingQuantity at fulfillment
   time, with no Reserved/Available concept at all. That existing
   module is left COMPLETELY UNTOUCHED by this phase, per "never
   break existing functionality."

   dbo.PottedPlantBookings ("BK-" prefix) reserves quantity against
   one specific PottedPlantStock row (Species + Pot Size + Area).
   Creating a booking increases PottedPlantStock.ReservedQuantity;
   it NEVER touches PhysicalQuantity -- per the stock rule "Booking
   reserves but never physically reduces stock; Dispatch completion
   reduces physical stock." Reuses the SAME dedicated ledger Phase 7
   already created (dbo.PottedPlantStockTransactions) via its
   already-reserved 'Reservation' / 'ReservationRelease'
   TransactionTypes -- no new ledger table needed, consistent with
   the "one dedicated ledger per stock entity" decision. For those
   two TransactionTypes, the ledger's BeforeQuantity/AfterQuantity
   represent ReservedQuantity at that moment (not PhysicalQuantity,
   which is what every other TransactionType on this ledger tracks)
   -- distinguished by TransactionType when reading the ledger.

   Status is intentionally left as only ('Pending', 'Cancelled') for
   now -- Phase 10 (Dispatch) does not exist yet, so a 'Dispatched'
   status is not added here; Phase 10's own migration will widen
   this CHECK constraint additively when it is built, exactly like
   Phase 8 widened dbo.EmptyPotInventoryTransactions' CHECK
   constraint for 'Transfer'.

   ADDITIVE ONLY.
     - Does not touch, alter, or drop dbo.Bookings, dbo.Inventory,
       or any other existing table.
     - Every CREATE is guarded with an existence check, so this
       script is safe to run more than once.
     - Reuses the EXISTING dbo.BatchNumberSequences table for the
       "BK-" prefix, and the EXISTING dbo.States / dbo.Districts
       lookup tables (same ones Data/BookingRepository.cs already
       reads) for the optional delivery-address fields.

   Run this AFTER Phase8_InternalTransfer.sql has been applied. Take
   a backup first per your own DB safety rules.
   ============================================================ */

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PottedPlantBookings' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.PottedPlantBookings
    (
        Id                  INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PottedPlantBookings PRIMARY KEY,
        BookingCode         NVARCHAR(20)   NOT NULL,

        -- The specific Species+PotSize+Area pool reserved against.
        -- SpeciesId/PotSize/AreaId are denormalized copies of the
        -- referenced PottedPlantStock row (kept here so reports don't
        -- always need to join back), always derived server-side from
        -- the selected pool -- CK_PottedPlantBookings_MatchesStock
        -- below guarantees it at the database level too.
        PottedPlantStockId  INT            NOT NULL,
        SpeciesId           INT            NOT NULL,
        PotSize             NVARCHAR(50)   NOT NULL,
        AreaId              INT            NULL,

        Quantity            DECIMAL(18,2)  NOT NULL,

        CustomerName        NVARCHAR(200)  NOT NULL,
        Address             NVARCHAR(500)  NULL,
        Contact             NVARCHAR(50)   NULL,
        StateId             INT            NULL,
        DistrictId          INT            NULL,

        BookingDate         DATETIME2      NOT NULL CONSTRAINT DF_PottedPlantBookings_BookingDate DEFAULT (SYSUTCDATETIME()),
        DeliveryDate        DATE           NULL,
        ActualDeliveryDate  DATETIME2      NULL,

        -- 'Pending' (reserved, awaiting Dispatch) | 'Cancelled'.
        -- See header note re: Phase 10 widening this for 'Dispatched'.
        Status              NVARCHAR(30)   NOT NULL CONSTRAINT DF_PottedPlantBookings_Status DEFAULT ('Pending'),

        AdvanceTaken        BIT            NOT NULL CONSTRAINT DF_PottedPlantBookings_AdvanceTaken DEFAULT (0),
        AdvanceTakenAmount  DECIMAL(18,2)  NULL,
        AdvanceTakenDetails NVARCHAR(500)  NULL,

        BookedById          INT            NULL,   -- sales person, FK IMSUsers
        BookedByOther       NVARCHAR(200)  NULL,   -- free-text alternative, mirrors dbo.Bookings' pattern

        Remarks             NVARCHAR(500)  NULL,
        CreatedDate         DATETIME2      NOT NULL CONSTRAINT DF_PottedPlantBookings_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CreatedBy           NVARCHAR(100)  NULL,
        ModifiedDate        DATETIME2      NULL,
        ModifiedBy          NVARCHAR(100)  NULL,

        CONSTRAINT UQ_PottedPlantBookings_Code UNIQUE (BookingCode),
        CONSTRAINT FK_PottedPlantBookings_Stock    FOREIGN KEY (PottedPlantStockId) REFERENCES dbo.PottedPlantStock(Id),
        CONSTRAINT FK_PottedPlantBookings_Species  FOREIGN KEY (SpeciesId)          REFERENCES dbo.PlantSpecies(Id),
        CONSTRAINT FK_PottedPlantBookings_Area     FOREIGN KEY (AreaId)             REFERENCES dbo.Area(Id),
        CONSTRAINT FK_PottedPlantBookings_State    FOREIGN KEY (StateId)            REFERENCES dbo.States(StateId),
        CONSTRAINT FK_PottedPlantBookings_District FOREIGN KEY (DistrictId)         REFERENCES dbo.Districts(DistrictId),
        CONSTRAINT FK_PottedPlantBookings_BookedBy FOREIGN KEY (BookedById)         REFERENCES dbo.IMSUsers(Id),

        CONSTRAINT CK_PottedPlantBookings_Status CHECK (Status IN ('Pending', 'Cancelled')),
        CONSTRAINT CK_PottedPlantBookings_Quantity CHECK (Quantity > 0),
        CONSTRAINT CK_PottedPlantBookings_AdvanceAmount CHECK (AdvanceTakenAmount IS NULL OR AdvanceTakenAmount >= 0)
    );

    CREATE INDEX IX_PottedPlantBookings_Stock   ON dbo.PottedPlantBookings(PottedPlantStockId);
    CREATE INDEX IX_PottedPlantBookings_Status  ON dbo.PottedPlantBookings(Status);
    CREATE INDEX IX_PottedPlantBookings_Species ON dbo.PottedPlantBookings(SpeciesId);
END
GO

-- Denormalization-consistency backstop: SpeciesId/PotSize/AreaId on a
-- booking must always match the PottedPlantStock row it reserves
-- against, mirroring the fn_Xyz_MatchesParent pattern used at every
-- other stage of this chain since Phase 3/4.
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE name = 'fn_PottedPlantBookings_MatchesStock' AND type = 'FN')
BEGIN
    EXEC('
    CREATE FUNCTION dbo.fn_PottedPlantBookings_MatchesStock(@PottedPlantStockId INT, @SpeciesId INT, @PotSize NVARCHAR(50), @AreaId INT)
    RETURNS BIT
    AS
    BEGIN
        DECLARE @Result BIT = 0;
        IF EXISTS (
            SELECT 1 FROM dbo.PottedPlantStock
            WHERE Id = @PottedPlantStockId
              AND SpeciesId = @SpeciesId
              AND PotSize = @PotSize
              AND (AreaId = @AreaId OR (AreaId IS NULL AND @AreaId IS NULL))
        )
            SET @Result = 1;
        RETURN @Result;
    END');
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_PottedPlantBookings_MatchesStock')
BEGIN
    ALTER TABLE dbo.PottedPlantBookings
        ADD CONSTRAINT CK_PottedPlantBookings_MatchesStock
        CHECK (dbo.fn_PottedPlantBookings_MatchesStock(PottedPlantStockId, SpeciesId, PotSize, AreaId) = 1);
END
GO
