namespace PlantStockManager.Models
{
    // Phase 26 (Phase L): view models for the read-only Management
    // Dashboard. Every value here is either an aggregate read from an
    // existing authoritative table/ledger (Data/ManagementDashboardRepository.cs)
    // or display metadata about that value -- nothing on this page is a
    // new stock/quantity column, and nothing here is ever written back
    // to the database. See PROJECT_DOCUMENTATION.md Decision 23 for the
    // full per-KPI authoritative-source mapping.
    //
    // Every quantity DTO below states its own "kind" in the comment next
    // to it: **Balance** (current point-in-time stock, ignores the date
    // filter -- there is only ever "now"), or **Movement** (a sum over
    // rows whose own date falls inside the selected [FromDate,
    // ToDateExclusive) range -- Step 5's explicit instruction never to
    // blur the two). Mixing these within one number would misrepresent
    // the business; keeping them as separate, clearly-labeled fields is
    // what lets the Razor page label each card correctly.
    public class ManagementDashboardFilters
    {
        public DateTime? FromDate { get; set; }
        public DateTime? ToDate { get; set; }
        public int? AreaId { get; set; }
        public int? GrowingPartnerId { get; set; }
        public int? SpeciesId { get; set; }
        public string? PotSize { get; set; }
        public string? Status { get; set; }

        // Movement queries need a concrete range even when the user has
        // not picked one -- default to "last 30 days" so the trend
        // charts and activity counts always show something meaningful
        // rather than an empty/all-time scan. Balance queries never use
        // these two.
        public DateTime EffectiveFromDate => (FromDate ?? DateTime.Today.AddDays(-30)).Date;

        // Step 5: range predicates ("Date >= @FromDate AND Date <
        // @ToDateExclusive"), never CAST(DateColumn AS DATE) = @Date --
        // this is the exclusive upper bound every repository method
        // below uses.
        public DateTime EffectiveToDateExclusive => (ToDate ?? DateTime.Today).Date.AddDays(1);
    }

    // A quantity that carries its own unit, for domains where different
    // rows can legitimately use different units (Seed Stock's per-row
    // "Unit" column -- e.g. grams vs. pieces) -- Step 3's "use clearly
    // defined units; do not mix ... without identifying the unit" rule
    // means these are never summed across different Unit values.
    public class UnitQuantity
    {
        public string Unit { get; set; } = string.Empty;
        public decimal Physical { get; set; }
        public decimal InTransit { get; set; }
        public decimal Available { get; set; }
    }

    // Section A: Overall Production Summary. All Balance (current, as
    // of now) except where noted.
    public class ProductionSummary
    {
        public decimal MotherPlantActiveQuantity { get; set; } // Balance -- unit: plants
        public int MotherPlantActiveCount { get; set; } // Balance -- unit: batches

        public decimal CuttingStockPhysical { get; set; } // Balance -- unit: cuttings
        public decimal CuttingStockInTransit { get; set; }
        public decimal CuttingStockAvailable { get; set; }

        public decimal PotProductionQuantityInRange { get; set; } // Movement -- unit: pots produced

        public decimal PottedPlantPhysical { get; set; } // Balance -- unit: potted plants
        public decimal PottedPlantReserved { get; set; }
        public decimal PottedPlantAvailable { get; set; }
        public decimal PottedPlantInTransit { get; set; }

        public decimal ReadyStockQuantity { get; set; } // Balance -- unit: plants confirmed Ready

        public List<UnitQuantity> SeedStock { get; set; } = new(); // Balance -- unit varies per row

        public decimal EmptyPotPhysical { get; set; } // Balance -- unit: pots
    }

    // Section B: Ready Stock / Production Readiness. Sown/Confirmed/
    // Remaining are Balance totals across every still-open Sowing (not
    // date-filtered -- a batch sown 40 days ago that is still only
    // partially confirmed must still count). Overdue/ReadyToday/
    // ReadySoon reuse SeedSowingRepository.ClassifyReadyAlert exactly
    // (Phase 24/J), never a second classification.
    public class ReadyStockSummary
    {
        public decimal SownQuantity { get; set; }
        public decimal ConfirmedReadyQuantity { get; set; }
        public decimal RemainingQuantity { get; set; }

        public int OverdueCount { get; set; }
        public decimal OverdueQuantity { get; set; } // sum of RemainingReadyQuantity for Overdue sowings

        public int ReadyTodayCount { get; set; }
        public decimal ReadyTodayQuantity { get; set; }

        public int ReadySoonCount { get; set; }
        public decimal ReadySoonQuantity { get; set; }

        public decimal ReadyStockQuantity { get; set; } // Balance -- dbo.ReadyStock, confirmed Ready only
    }

    public class GrowingPartnerSummaryRow
    {
        public int? GrowingPartnerId { get; set; }
        public string GrowingPartnerName { get; set; } = string.Empty; // "(Internally Run)" when null
        public int AreaId { get; set; }
        public string AreaName { get; set; } = string.Empty;
        public string? AreaType { get; set; }

        public decimal StarterMaterialReceived { get; set; } // Movement -- Seed Issue Completed, ConfirmedQuantity
        public decimal CuttingReceived { get; set; } // Movement -- Cutting transfer Confirmed/Transplanted
        public decimal PotTrayProduction { get; set; } // Movement -- PotProduction Completed
        public decimal CurrentPottedStock { get; set; } // Balance -- PottedPlantStock.PhysicalQuantity
        public decimal TransfersToOutlet { get; set; } // Movement -- GrowingPartnerToOutlet Completed
        public int PendingConfirmations { get; set; } // Balance -- open items awaiting this Area's confirmation
    }

    public class OutletSalesSummary
    {
        public decimal OutletStockPhysical { get; set; } // Balance -- PottedPlantStock at AreaType='Outlet'
        public decimal OutletStockAvailable { get; set; }

        public int TotalBookingsInRange { get; set; } // Movement -- by BookingDate
        public int PendingBookings { get; set; } // Balance -- current open Status
        public int PartiallyDispatchedBookings { get; set; }
        public int DispatchedBookings { get; set; }
        public int CancelledBookings { get; set; }
        public decimal RemainingBookingQuantity { get; set; } // Balance -- Pending+PartiallyDispatched remaining

        public int CompletedDispatchCountInRange { get; set; } // Movement -- by DispatchDate
        public decimal CompletedDispatchQuantityInRange { get; set; }
    }

    public class SeedSummary
    {
        public List<UnitQuantity> MainOfficeSeedStock { get; set; } = new(); // Balance
        public List<UnitQuantity> SeedStockRemaining { get; set; } = new(); // Balance -- all Areas (Main Office + growing Areas)

        public decimal SeedIssuedQuantityInRange { get; set; } // Movement -- Completed, by IssueDate
        public decimal SeedReceivedQuantityInRange { get; set; } // Movement -- Completed, ConfirmedQuantity, by ConfirmedDate
        public decimal SeedSownQuantityInRange { get; set; } // Movement -- by SowingDate

        public int PendingSeedReceipts { get; set; } // Balance -- Status = 'PendingConfirmation'
        public int SowingActivityCountInRange { get; set; } // Movement -- count of Sowing events by SowingDate
    }

    public class CuttingSummary
    {
        public decimal CuttingPhysical { get; set; } // Balance
        public decimal CuttingInTransit { get; set; } // Balance
        public decimal CuttingAvailable { get; set; } // Balance

        public int PendingConfirmationCount { get; set; } // Balance -- InternalTransfers StockType='Cutting'
        public decimal PendingConfirmationQuantity { get; set; }

        public int ConfirmedAwaitingTransplantCount { get; set; } // Balance
        public decimal ConfirmedAwaitingTransplantQuantity { get; set; }

        public int TransplantedCountInRange { get; set; } // Movement -- by TransplantDate/ConfirmedDate
        public decimal TransplantedQuantityInRange { get; set; }

        public List<CuttingActivityItem> RecentActivity { get; set; } = new();
    }

    public class CuttingActivityItem
    {
        public DateTime TransactionDate { get; set; }
        public string TransactionType { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public string? AreaName { get; set; }
        public string? SpeciesName { get; set; }
    }

    // Section G: Inventory/Stock Health -- one row per stock domain, with
    // an explicit null (rendered "N/A") wherever that domain genuinely
    // has no such concept, rather than a fabricated 0/blank value.
    public class StockHealthRow
    {
        public string Domain { get; set; } = string.Empty;
        public string Unit { get; set; } = string.Empty;
        public decimal? Physical { get; set; }
        public decimal? Reserved { get; set; }
        public decimal? Available { get; set; }
        public decimal? InTransit { get; set; }
    }

    // A single point in a time-bucketed trend chart. Series lets one
    // chart carry more than one line/bar group (e.g. Sown vs. Confirmed
    // Ready) while keeping every point's own bucket/date explicit.
    public class TrendPoint
    {
        public DateTime BucketDate { get; set; }
        public string Series { get; set; } = string.Empty;
        public decimal Value { get; set; }
    }

    // One bar in the Growing Partner production chart -- reuses the
    // same aggregate the Section C table already computed, never a
    // second query for the same number (Step 6's "no duplicate queries
    // for the same metric").
    public class CategoryValue
    {
        public string Label { get; set; } = string.Empty;
        public decimal Value { get; set; }
    }

    // The full payload the Index page needs, assembled by
    // ManagementDashboardRepository across one call per section (never
    // one call per card) plus the filter dropdown option lists.
    public class ManagementDashboardViewModel
    {
        public ManagementDashboardFilters Filters { get; set; } = new();

        public ProductionSummary Production { get; set; } = new();
        public ReadyStockSummary ReadyStock { get; set; } = new();
        public List<GrowingPartnerSummaryRow> GrowingPartners { get; set; } = new();
        public OutletSalesSummary OutletSales { get; set; } = new();
        public SeedSummary Seed { get; set; } = new();
        public CuttingSummary Cutting { get; set; } = new();
        public List<StockHealthRow> StockHealth { get; set; } = new();

        public List<TrendPoint> SowingVsReadyTrend { get; set; } = new();
        public List<TrendPoint> ReadyStockTrend { get; set; } = new();
        public List<TrendPoint> OutletDispatchTrend { get; set; } = new();
        public List<CategoryValue> GrowingPartnerProductionChart { get; set; } = new();

        // Filter dropdown option lists -- populated by the PageModel
        // from the existing master repositories (AreaRepository,
        // GrowingPartnerRepository, PlantSpeciesRepository), never
        // duplicated here.
        public List<Area> Areas { get; set; } = new();
        public List<GrowingPartner> GrowingPartnerOptions { get; set; } = new();
        public List<PlantSpecies> SpeciesOptions { get; set; } = new();
        public List<string> PotSizeOptions { get; set; } = new();
    }
}
