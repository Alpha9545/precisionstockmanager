namespace PlantStockManager.Models
{
    // Daily Report (Step 3A): a read-only, company-wide (no Area filter --
    // see Services/DailyReportService.cs for why) snapshot of one calendar
    // day's activity, assembled entirely from existing repositories. This
    // model carries data only -- no formatting, no WhatsApp/message text,
    // no scheduling. dbo.SeedEntries/dbo.Inventory/dbo.InventoryTransactions
    // (the retired legacy pipeline) are never read by anything that
    // produces this model.
    public class DailyReport
    {
        // The calendar day this report covers (date-only; always midnight).
        public DateTime ReportDate { get; set; }

        // ---- 1. Sowing -----------------------------------------------------
        // Total QuantitySown across all non-Cancelled dbo.SeedSowings rows
        // whose SowingDate falls on ReportDate. Source:
        // ManagementDashboardRepository.GetSeedSummaryAsync ->
        // SeedSummary.SeedSownQuantityInRange.
        public decimal SowingQuantity { get; set; }

        // ---- 2. Ready Stock -------------------------------------------------
        // Current (as-of-now, not date-ranged) counts/quantities of Seed
        // Sowing batches by Ready-alert category -- the same classification
        // Pages/Production/ReadyAlerts/Index.cshtml.cs uses. Source:
        // ManagementDashboardRepository.GetReadyStockSummaryAsync.
        public int ReadyStockOverdueCount { get; set; }
        public decimal ReadyStockOverdueQuantity { get; set; }
        public int ReadyStockReadyTodayCount { get; set; }
        public decimal ReadyStockReadyTodayQuantity { get; set; }
        public int ReadyStockReadySoonCount { get; set; }
        public decimal ReadyStockReadySoonQuantity { get; set; }

        // ---- 2B. Ready Stock by Plant Type (Step 8B) -------------------------
        // The Ready Stock BALANCE (dbo.ReadyStock.Quantity - DispatchedQuantity
        // -- the same figure Pages/Production/ReadyStock/Index.cshtml.cs calls
        // "Physical Qty" and sums as its own page total; NOT Overdue/ReadyToday/
        // ReadySoon above, which are upcoming-Sowing alert counts, not actual
        // Ready Stock on hand), broken out for the WhatsApp Daily Report's four
        // named Plant Types. Source: ManagementDashboardRepository.
        // GetReadyStockSummaryAsync -> ReadyStockSummary.ReadyStockQuantityByPlantType,
        // looked up by the exact, live dbo.PlantTypes.Name (verified: "MERI
        // GOLD", "CHRYSANTHEMUM", "ZINNIA", "SEASONAL VARITIES"). 0, never
        // null, when that Plant Type currently has no Ready Stock.
        public decimal ReadyStockMeriGoldQuantity { get; set; }
        public decimal ReadyStockChrysanthemumQuantity { get; set; }
        public decimal ReadyStockZinniaQuantity { get; set; }
        public decimal ReadyStockSeasonalVaritiesQuantity { get; set; }

        // ---- 2C. Ready Stock by Plant Type, WITH names (Step 8D) -------------
        // The exact same 4 entries as the 4 scalar properties just above,
        // but carrying each Plant Type's own dbo.PlantTypes.Name alongside its
        // quantity, in the DATABASE's own PlantTypeId order (MERI GOLD,
        // CHRYSANTHEMUM, ZINNIA, SEASONAL VARITIES) -- a direct, unmodified
        // pass-through of ManagementDashboardRepository.GetReadyStockSummaryAsync's
        // own ReadyStockSummary.ReadyStockQuantityByPlantType (no second query,
        // no hand-typed name anywhere in this class). Exists because the
        // WhatsApp template (Services/DailyReportWhatsAppFormatter.cs) needs
        // to print each Plant Type's NAME as its own variable, not just its
        // quantity -- the 4 scalar properties above remain for any other
        // existing consumer that only ever needed the quantity.
        public List<ReadyStockPlantTypeQuantity> ReadyStockByPlantType { get; set; } = new();

        // ---- 3. Seedling Bookings -------------------------------------------
        // New dbo.Bookings rows whose BookingDate falls on ReportDate --
        // every status (Pending/Confirmed/Cancelled/...) counts as "a
        // booking request came in today." Source:
        // BookingRepository.GetBookingCountByBookingDateAsync (new).
        public int SeedlingBookingsCount { get; set; }
        public decimal SeedlingBookingsQuantity { get; set; }

        // ---- 4. Potted Plant Bookings ---------------------------------------
        // New dbo.PottedPlantBookings rows whose BookingDate falls on
        // ReportDate. Source: ManagementDashboardRepository.
        // GetOutletSalesSummaryAsync -> OutletSalesSummary.TotalBookingsInRange.
        public int PottedPlantBookingsCount { get; set; }

        // ---- 5. Seedling Dispatch -------------------------------------------
        // Count = number of distinct seedling dispatch EVENTS (not line
        // items) whose DispatchDate falls on ReportDate; Quantity = total
        // seedlings across every line of those dispatches. Source:
        // SeedlingFulfilmentRepository.GetDispatchRegisterAsync, summarized
        // by the pure Services/DailyReportCalculations.SummarizeDispatchLines.
        public int SeedlingDispatchCount { get; set; }
        public decimal SeedlingDispatchQuantity { get; set; }

        // ---- 6. Potted Plant Dispatch ---------------------------------------
        // Completed dbo.Dispatches rows whose DispatchDate falls on
        // ReportDate. Source: ManagementDashboardRepository.
        // GetOutletSalesSummaryAsync -> OutletSalesSummary.
        // CompletedDispatchCountInRange/QuantityInRange.
        public int PottedPlantDispatchCount { get; set; }
        public decimal PottedPlantDispatchQuantity { get; set; }

        // ---- 7. Outlet Sales -------------------------------------------------
        // Count of dbo.OutletSales rows whose SaleDate falls on ReportDate.
        // Source: OutletSaleRepository.GetSalesCountByDateRangeAsync (new).
        public int OutletSalesCount { get; set; }

        // ---- 8. Fertilizer Usage ---------------------------------------------
        // Total Quantity across dbo.FertilizerUsage rows whose IssueDate
        // falls on ReportDate (no Fertilizer/Source/Receiver/EnteredBy
        // filter). Source: FertilizerTransactionRepository.SearchAsync
        // (From = To = ReportDate), summed in DailyReportService.
        public decimal FertilizerUsageQuantity { get; set; }

        // ---- 9. Wastage --------------------------------------------------------
        // Total quantity across every ledger Data/WastageRepository.cs
        // already covers (tray production, cutting deliveries, pot
        // production, potted stock) whose date falls on ReportDate. Source:
        // WastageRepository.GetAsync(ReportDate, ReportDate), summed in
        // DailyReportService.
        public decimal WastageQuantity { get; set; }

        // ---- 10. Employee Software Activity (Step 5) ------------------------
        // Active employees only (dbo.IMSUsers.IsActive = 1), exactly as
        // UserLoginHistoryRepository.GetTodaysActivityAsync already scopes
        // it -- no separate filtering here. Source: that one method call,
        // partitioned by DailyReportCalculations.PartitionEmployeeActivity
        // (pure, no new SQL).
        public int TotalActiveEmployees { get; set; }
        public int LoggedInEmployeeCount { get; set; }
        public int NotLoggedInEmployeeCount { get; set; }
        public List<EmployeeLoginActivity> EmployeesLoggedInToday { get; set; } = new();
        public List<EmployeeLoginActivity> EmployeesNotLoggedInToday { get; set; } = new();

        // Step 7 (approved): ActiveUserCount = logged in AND has at least
        // one qualifying recorded business activity (see
        // UserLoginHistoryRepository.GetTodaysActivityAsync for the exact
        // approved source list). NoActivityRecordedCount = LoggedInEmployeeCount
        // - ActiveUserCount (the approved formula, exactly).
        public int ActiveUserCount { get; set; }
        public int NoActivityRecordedCount { get; set; }
    }
}
