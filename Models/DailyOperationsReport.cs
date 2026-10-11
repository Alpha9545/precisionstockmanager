namespace PlantStockManager.Models
{
    // Daily Operations Report (Management Dashboard, System Administrator only).
    // Read-only, company-wide, for ANY India (Asia/Kolkata) calendar date -- see
    // docs/DAILY_OPERATIONS_REPORT.md for every figure's source, date rule and
    // historical limitation. Nothing here is a snapshot table: every figure is
    // recomputed from the operational tables/ledgers on each request.

    // ---- Raw, per-India-day figures for a date RANGE, exactly as
    // Data/DailyOperationsReportRepository.cs reads them in ONE batched
    // command. Services/DailyOperationsReportRules.Build turns it into one
    // DailyOperationsReport per day (pure, no database).
    public sealed record DayFigure(DateTime Date, string? Key, int Count, decimal Quantity, int Lines = 0);

    // 2026-10-10: which balance a Ready Stock ledger row moves (docs/STOCK_DEFINITIONS.md).
    public static class StockKind
    {
        public const char Owned = 'O';      // plants owned: every row except the two hold kinds below
        public const char Reserved = 'R';   // 'Reservation' / 'ReservationRelease'
        public const char InTransit = 'T';  // 'TransferHold' / 'TransferHoldRelease'
    }

    // Date null = before the range (opening).
    public sealed record PlantTypeMovement(int PlantTypeId, DateTime? Date, decimal Quantity, char Kind = StockKind.Owned);

    // Owned / reserved / in-transit plants of one Plant Type (current table or a reconstructed date).
    public sealed record ReadyStockBalance(int PlantTypeId, decimal Owned, decimal Reserved, decimal InTransit);

    // A legacy ledger movement whose business date differs from the day it was recorded (before
    // business dates were stored on the ledger, 2026-10-10). Reported, never changed.
    public sealed record RecordingLag(string What, DateTime BusinessDate, DateTime RecordedDate, decimal Plants);

    // 2026-10-11: Ready Stock ledger rows dated BEFORE their batch first held stock (e.g. a dispatch
    // or transfer dated before the batch was approved Ready). On the India days BusinessDate ..
    // BatchStartDate - 1 that Plant Type's history cannot be reliably reconstructed: flagged, never moved.
    public sealed record ReadyStockDatingIssue(int PlantTypeId, DateTime BusinessDate, DateTime BatchStartDate, decimal Plants);

    public sealed record EmployeeLogin(int UserId, DateTime Date, DateTime FirstLoginUtc, int Logins);

    public sealed record EmployeeActivityDay(int UserId, DateTime Date);

    public sealed record NamedId(int Id, string Name);

    // The first India date for which each source holds any record. A date
    // before it cannot be reported (the module may not have been in use yet);
    // null = the source holds no records at all.
    public sealed class DailyOperationsCoverage
    {
        public DateTime? SowingFrom { get; set; }
        public DateTime? SeedlingBookingsFrom { get; set; }
        public DateTime? PottedBookingsFrom { get; set; }
        public DateTime? SeedlingDispatchFrom { get; set; }
        public DateTime? PottedDispatchFrom { get; set; }
        public DateTime? OutletSalesFrom { get; set; }
        public DateTime? FertilizerFrom { get; set; }
        public DateTime? WastageFrom { get; set; }
        public DateTime? ReadyStockFrom { get; set; }
        public DateTime? LoginTrackingStartUtc { get; set; }

        // Ready Stock batches whose current physical balance (Quantity -
        // DispatchedQuantity) differs from the sum of their physical ledger
        // movements. 0 = the ledger fully explains every balance, so the
        // as-of-date reconstruction is exact.
        public int ReadyStockUnreconciledBatches { get; set; }
    }

    public sealed class DailyOperationsSourceData
    {
        public DateTime From { get; set; }   // India dates, inclusive
        public DateTime To { get; set; }

        public List<DayFigure> Sowing { get; } = new();            // Key = SourceType (Seed / Cutting)
        public List<DayFigure> SeedlingBookings { get; } = new();  // Count = orders, Lines = booking lines
        public List<DayFigure> PottedBookings { get; } = new();
        public List<DayFigure> SeedlingDispatches { get; } = new(); // Count = dispatch events
        public List<DayFigure> PottedDispatches { get; } = new();
        public List<DayFigure> OutletSales { get; } = new();
        public List<DayFigure> Fertilizer { get; } = new();        // Key = unit name
        public List<DayFigure> Wastage { get; } = new();           // plants / cuttings / pots
        public List<DayFigure> SeedLotWastage { get; } = new();    // Key = seed stock unit (Seeds, pcs ...)

        public List<NamedId> PlantTypes { get; } = new();
        public List<PlantTypeMovement> ReadyStockMovements { get; } = new();
        public List<ReadyStockBalance> ReadyStockCurrent { get; } = new();
        public List<RecordingLag> RecordingLags { get; } = new();
        public List<ReadyStockDatingIssue> ReadyStockDatingIssues { get; } = new();

        public List<NamedId> Employees { get; } = new();
        public List<EmployeeLogin> Logins { get; } = new();
        public List<EmployeeActivityDay> Activity { get; } = new();

        public DailyOperationsCoverage Coverage { get; set; } = new();
    }

    // ---- One day's report -------------------------------------------------

    // A figure that is either established by the records, or not available
    // (with the reason). "0" is only ever shown for an Available figure.
    public sealed class ReportFigure
    {
        public bool Available { get; init; }
        public string? UnavailableReason { get; init; }
        public int Count { get; init; }
        public decimal Quantity { get; init; }
        public int Lines { get; init; }

        public static ReportFigure NotAvailable(string reason) => new() { Available = false, UnavailableReason = reason };
    }

    // Ready Stock of one Plant Type (docs/STOCK_DEFINITIONS.md):
    //   Owned     = approved - dispatched (plants the company still has, wherever they are)
    //   Physical  = Owned - InTransit      (at the Plant Type's locations)
    //   Available = Physical - Reserved    (free for booking / dispatch -- the primary figure)
    public sealed record ReadyStockLine(string PlantTypeName, decimal Owned, decimal Reserved = 0, decimal InTransit = 0)
    {
        public decimal Physical => Owned - InTransit;
        public decimal Available => Physical - Reserved;
    }

    public sealed record UnitQuantityLine(string Unit, decimal Quantity);

    public sealed class DailyOperationsReport
    {
        public DateTime ReportDate { get; set; }
        public DateTime TodayIndia { get; set; }
        public DateTime GeneratedAtIndia { get; set; }
        public bool IsToday => ReportDate.Date == TodayIndia.Date;

        // ---- Daily Operations ----
        public ReportFigure Sowing { get; set; } = new();
        public decimal SeedSowingQuantity { get; set; }
        public decimal CuttingSowingQuantity { get; set; }

        public bool ReadyStockAvailable { get; set; }
        public string? ReadyStockUnavailableReason { get; set; }
        public string ReadyStockBasis { get; set; } = string.Empty;
        public List<ReadyStockLine> ReadyStock { get; set; } = new();   // non-zero Plant Types, most available first
        public decimal ReadyStockAvailableTotal => ReadyStock.Sum(r => r.Available);
        public decimal ReadyStockPhysicalTotal => ReadyStock.Sum(r => r.Physical);
        public decimal ReadyStockReservedTotal => ReadyStock.Sum(r => r.Reserved);
        public decimal ReadyStockInTransitTotal => ReadyStock.Sum(r => r.InTransit);
        public decimal ReadyStockOwnedTotal => ReadyStock.Sum(r => r.Owned);

        public ReportFigure SeedlingBookings { get; set; } = new();
        public ReportFigure PottedBookings { get; set; } = new();
        public ReportFigure SeedlingDispatch { get; set; } = new();
        public ReportFigure PottedDispatch { get; set; } = new();
        public ReportFigure OutletSales { get; set; } = new();

        public ReportFigure Fertilizer { get; set; } = new();           // Count = issue entries
        public List<UnitQuantityLine> FertilizerByUnit { get; set; } = new();

        public ReportFigure Wastage { get; set; } = new();              // plants / cuttings / pots
        public List<UnitQuantityLine> SeedLotWastage { get; set; } = new();

        // ---- Software Activity ----
        public int TotalEmployees { get; set; }
        public bool LoginDataAvailable { get; set; }
        public string? LoginUnavailableReason { get; set; }
        public string? LoginPartialDayNote { get; set; }
        public int LoginEvents { get; set; }
        public List<string> LoggedIn { get; set; } = new();
        public List<string> ActiveUsers { get; set; } = new();
        public List<string> NoActivityRecorded { get; set; } = new();
        public List<string> NotLoggedIn { get; set; } = new();
        public List<string> ActivityWithoutLogin { get; set; } = new();  // subset of NotLoggedIn

        public List<string> Notes { get; set; } = new();
    }
}
