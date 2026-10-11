namespace PlantStockManager.Models
{
    // Management Dashboard -- the company-wide OVERVIEW at the top of the page
    // (Data/ManagementDashboardRepository.GetOverviewAsync + the Dashboard
    // calculations in Services/ManagementDashboardCalculations.cs).
    //
    // Two kinds of figure, never mixed in one number:
    //   CURRENT -- the state right now (stock balances, open bookings, open tickets);
    //              the period selector never changes these.
    //   PERIOD  -- a sum/count of events whose own date falls inside the selected
    //              DashboardPeriod (India business dates, both ends inclusive).
    //
    // Terminology (as used across the application):
    //   Plant Type -- dbo.PlantTypes (the crop group, e.g. "MERI GOLD", "CHRYSANTHEMUM")
    //   Variety    -- dbo.PlantSpecies (the named variety, e.g. "Lakhani Yellow"),
    //                 always shown by its own name, never only by its Plant Type.
    //
    // Full KPI definitions: docs/MANAGEMENT_DASHBOARD.md.

    // The selected time window for PERIOD figures.
    public sealed class DashboardPeriod
    {
        public const string Today = "today";
        public const string Last7Days = "7d";
        public const string Last30Days = "30d";
        public const string ThisMonth = "month";
        public const string Custom = "custom";
        public const string DefaultKey = Last30Days;

        public static readonly IReadOnlyList<(string Key, string Label)> Presets = new[]
        {
            (Today, "Today"), (Last7Days, "Last 7 days"), (Last30Days, "Last 30 days"), (ThisMonth, "This month"), (Custom, "Custom"),
        };

        public string Key { get; init; } = DefaultKey;
        public DateTime From { get; init; }   // inclusive India date
        public DateTime To { get; init; }     // inclusive India date
        public DateTime ToExclusive => To.AddDays(1);

        public string Label => Key switch
        {
            Today => $"Today ({From:dd MMM})",
            Last7Days => "Last 7 days",
            Last30Days => "Last 30 days",
            ThisMonth => $"This month ({From:MMM yyyy})",
            _ => From == To ? From.ToString("dd MMM yyyy") : $"{From:dd MMM yyyy} – {To:dd MMM yyyy}",
        };

        // key: one of the preset keys; an unknown/blank key with a From or To date (an old
        // bookmarked ?FromDate=..&ToDate=.. link) is a custom range, otherwise the default.
        public static DashboardPeriod Resolve(string? key, DateTime? from, DateTime? to, DateTime today)
        {
            today = today.Date;
            key = key?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(key) || !Presets.Any(p => p.Key == key))
                key = from.HasValue || to.HasValue ? Custom : DefaultKey;

            switch (key)
            {
                case Today: return new DashboardPeriod { Key = key, From = today, To = today };
                case Last7Days: return new DashboardPeriod { Key = key, From = today.AddDays(-6), To = today };
                case Last30Days: return new DashboardPeriod { Key = key, From = today.AddDays(-29), To = today };
                case ThisMonth: return new DashboardPeriod { Key = key, From = new DateTime(today.Year, today.Month, 1), To = today };
            }

            var f = (from ?? to ?? today).Date;
            var t = (to ?? from ?? today).Date;
            if (f > t) (f, t) = (t, f);
            return new DashboardPeriod { Key = Custom, From = f, To = t };
        }
    }

    // One Area x Polyhouse x Variety slice of dbo.ReadyStock (approved seedlings). Every
    // Ready Stock figure on the overview -- totals, Top 3, Plant Type split, Area and
    // Polyhouse tables -- is summed from this ONE row set, so they always reconcile.
    public sealed class ReadyStockSlice
    {
        public int AreaId { get; set; }
        public string AreaName { get; set; } = string.Empty;
        public string? AreaType { get; set; }
        public int? PolyhouseId { get; set; }
        public string? PolyhouseName { get; set; }
        public int? PolyhouseAreaId { get; set; }   // the Area the Polyhouse itself belongs to
        public string? PolyhouseAreaName { get; set; }
        public int SpeciesId { get; set; }
        public string VarietyName { get; set; } = string.Empty;
        public string? PlantTypeName { get; set; }
        public decimal Physical { get; set; }   // SUM(Quantity - DispatchedQuantity)
        public decimal Reserved { get; set; }   // SUM(ReservedQuantity)
        public decimal Available { get; set; }  // SUM(Quantity - ReservedQuantity - DispatchedQuantity)
        public int Batches { get; set; }
    }

    // dbo.PottedPlantStock per Area (it has no Polyhouse column).
    public sealed class PottedStockByArea
    {
        public int? AreaId { get; set; }
        public string AreaName { get; set; } = string.Empty;
        public string? AreaType { get; set; }
        public decimal Physical { get; set; }
        public decimal Reserved { get; set; }
        public decimal InTransit { get; set; }
        public decimal Available => Physical - Reserved;   // = PottedPlantStock.AvailableQuantity
    }

    public sealed class StockTotals
    {
        public decimal Physical { get; set; }
        public decimal Reserved { get; set; }
        public decimal Available { get; set; }
        public decimal InTransit { get; set; }
        public int Batches { get; set; }
    }

    // A quantity per Variety (input to the Top-N ranking).
    public sealed class VarietyQuantity
    {
        public int? SpeciesId { get; set; }
        public string VarietyName { get; set; } = string.Empty;
        public string? PlantTypeName { get; set; }
        public decimal Quantity { get; set; }
    }

    public sealed class RankedVariety
    {
        public int Rank { get; set; }
        public int? SpeciesId { get; set; }
        public string VarietyName { get; set; } = string.Empty;
        public string? PlantTypeName { get; set; }
        public decimal Quantity { get; set; }
        public decimal? PercentOfTotal { get; set; }   // null when the total is zero
    }

    public sealed class PlantTypeStockRow
    {
        public string PlantTypeName { get; set; } = string.Empty;
        public int Varieties { get; set; }
        public decimal Physical { get; set; }
        public decimal Available { get; set; }
        public decimal? PercentOfTotal { get; set; }
    }

    public sealed class PolyhouseStockRow
    {
        public int? PolyhouseId { get; set; }
        public string Name { get; set; } = string.Empty;   // "(No Polyhouse)" for Area-level stock
        public string? BelongsToAreaName { get; set; }      // set only when the Polyhouse belongs to another Area
        public decimal Physical { get; set; }
        public decimal Reserved { get; set; }
        public decimal Available { get; set; }
    }

    // Master-data mismatch: Ready Stock recorded under one Area on a Polyhouse that belongs to
    // another Area. Shown as a warning only; the stock is not moved.
    public sealed class PolyhouseMismatch
    {
        public int RecordedAreaId { get; set; }
        public string RecordedAreaName { get; set; } = string.Empty;
        public int PolyhouseId { get; set; }
        public string PolyhouseName { get; set; } = string.Empty;
        public int PolyhouseAreaId { get; set; }
        public string PolyhouseAreaName { get; set; } = string.Empty;
        public decimal Physical { get; set; }
        public decimal Reserved { get; set; }
        public int Batches { get; set; }
    }

    public sealed class AreaStockRow
    {
        public int? AreaId { get; set; }
        public string AreaName { get; set; } = string.Empty;
        public string? AreaType { get; set; }
        public decimal ReadyPhysical { get; set; }
        public decimal ReadyReserved { get; set; }
        public decimal ReadyAvailable { get; set; }
        public decimal PottedPhysical { get; set; }
        public decimal PottedAvailable { get; set; }
        public List<PolyhouseStockRow> Polyhouses { get; set; } = new();
    }

    // Seedling bookings (dbo.Bookings). A booking ORDER is ISNULL(OrderBookingId, Id); a
    // LINE is one dbo.Bookings row (one variety). Counts of orders never count lines.
    public sealed class SeedlingOrderSummary
    {
        // CURRENT -- every line whose Status is Pending.
        public int OpenOrders { get; set; }
        public int OpenLines { get; set; }
        public decimal OpenBookedQuantity { get; set; }     // SUM(Quantity)
        public decimal OpenDispatchedQuantity { get; set; } // SUM(DispatchedQuantity)
        public decimal OpenRemainingQuantity { get; set; }  // SUM(max(0, Quantity - Dispatched - legacy-fulfilled))
        public decimal OpenReservedQuantity { get; set; }   // SUM(ReservedQuantity) -- held on Ready Stock
        public int OverdueOrders { get; set; }              // open orders with a line past its DeliveryDate and still to dispatch
        public decimal OverdueRemainingQuantity { get; set; }
        public int DueTodayOrders { get; set; }
        public decimal DueTodayRemainingQuantity { get; set; }

        // PERIOD
        public int OrdersReceived { get; set; }             // by BookingDate, every status (Daily Report definition)
        public decimal QuantityReceived { get; set; }
        public int Dispatches { get; set; }                 // dbo.SeedlingDispatches by DispatchDate
        public decimal DispatchedQuantity { get; set; }
    }

    // PERIOD production activity, company-wide.
    public sealed class ProductionActivity
    {
        public int Sowings { get; set; }
        public decimal SownQuantity { get; set; }
        public int CuttingEntries { get; set; }
        public decimal CuttingsProduced { get; set; }
        public decimal PotsProduced { get; set; }
        public decimal ReadyApprovedQuantity { get; set; }
    }

    public sealed class MaintenanceSnapshot
    {
        public int Active { get; set; }
        public int Critical { get; set; }
        public int Urgent { get; set; }
        public int Overdue { get; set; }
        public int AwaitingConfirmation { get; set; }  // Status = Resolved (work done, raiser to confirm)
        public int ClosedInPeriod { get; set; }
    }

    public sealed class StoreLowItem
    {
        public string ItemCode { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public decimal Available { get; set; }
        public decimal Minimum { get; set; }
        public string? Unit { get; set; }
    }

    public sealed class StoreSnapshot
    {
        public int PendingReview { get; set; }
        public int ApprovedAwaitingIssue { get; set; }
        public int LowStockCount { get; set; }
        public List<StoreLowItem> LowStockItems { get; set; } = new();   // first few, for the card
    }

    public sealed class AreaLabourRow
    {
        public string AreaName { get; set; } = string.Empty;
        public int Locations { get; set; }
        public int At9 { get; set; }
        public int At2 { get; set; }
        public int FullDay { get; set; }
        public int HalfDay { get; set; }
        public int IncompleteLocations { get; set; }
    }

    public sealed class LabourSnapshot
    {
        public DateTime Date { get; set; }
        public int Locations { get; set; }
        public int IncompleteLocations { get; set; }
        public int At9 { get; set; }
        public int At2 { get; set; }
        public int FullDay { get; set; }
        public int HalfDay { get; set; }
        public decimal LabourDays { get; set; }
        public int OvertimeEntries { get; set; }
        public decimal OvertimeWorkerHours { get; set; }
        public List<AreaLabourRow> ByArea { get; set; } = new();
    }

    public sealed class RecentPost
    {
        public int Id { get; set; }
        public string? PostNo { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? AreaName { get; set; }
        public string Status { get; set; } = string.Empty;
        public bool IsPermanent { get; set; }
        public DateTime CreatedAtIndia { get; set; }
    }

    public sealed class SuggestionSnapshot
    {
        public int ActiveProblems { get; set; }
        public int ActiveSuggestions { get; set; }
        public int Permanent { get; set; }
        public List<RecentPost> RecentProblems { get; set; } = new();
    }

    public sealed class ActivityItem
    {
        public DateTime WhenIndia { get; set; }
        public string Kind { get; set; } = string.Empty;   // Booking, Dispatch, Ready Stock, Maintenance, Store, Problem, Suggestion
        public string Text { get; set; } = string.Empty;
        public string? Page { get; set; }
        public int? Id { get; set; }                        // route value "id" for Page, when it is a details page
    }

    public sealed class AttentionItem
    {
        public string Text { get; set; } = string.Empty;
        public int Count { get; set; }
        public string Severity { get; set; } = "warning";   // danger | warning | info
        public string Page { get; set; } = string.Empty;
        public Dictionary<string, string> Query { get; set; } = new();
    }

    public sealed class ManagementOverview
    {
        public DashboardPeriod Period { get; set; } = new();
        public DateTime Today { get; set; }

        public List<ReadyStockSlice> ReadyStock { get; set; } = new();
        public List<PottedStockByArea> PottedStock { get; set; } = new();
        public StockTotals ReadyTotals { get; set; } = new();
        public StockTotals PottedTotals { get; set; } = new();

        public List<RankedVariety> TopByReadyStock { get; set; } = new();
        public List<RankedVariety> TopByOpenBooking { get; set; } = new();
        public List<CategoryValue> ReadyStockByVarietyChart { get; set; } = new();
        public List<PlantTypeStockRow> ReadyStockByPlantType { get; set; } = new();
        public List<VarietyQuantity> OpenBookingByVariety { get; set; } = new();
        public List<AreaStockRow> Areas { get; set; } = new();
        public List<PolyhouseMismatch> PolyhouseMismatches { get; set; } = new();

        public SeedlingOrderSummary Seedlings { get; set; } = new();
        public OutletSalesSummary Potted { get; set; } = new();
        public ProductionActivity Production { get; set; } = new();
        public MaintenanceSnapshot Maintenance { get; set; } = new();
        public StoreSnapshot Store { get; set; } = new();
        public LabourSnapshot Labour { get; set; } = new();
        public SuggestionSnapshot Suggestions { get; set; } = new();

        public List<AttentionItem> Attention { get; set; } = new();
        public List<ActivityItem> RecentActivity { get; set; } = new();
    }
}
