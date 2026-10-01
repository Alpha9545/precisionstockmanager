using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Services
{
    // Daily Report (Steps 3A + 5) -- DATA ONLY. Assembles one DailyReport by
    // calling existing, already-reviewed repository methods; adds no new
    // SQL of its own beyond the two small aggregates Step 3A added
    // (BookingRepository.GetBookingCountByBookingDateAsync,
    // OutletSaleRepository.GetSalesCountByDateRangeAsync) plus Step 5's
    // reuse of UserLoginHistoryRepository.GetTodaysActivityAsync (no new
    // SQL there either). Never touches
    // dbo.SeedEntries/dbo.Inventory/dbo.InventoryTransactions (the retired
    // legacy pipeline) -- none of the repositories called below reference
    // them.
    //
    // NO WhatsApp formatting/sending, NO scheduler/background service.
    // Nothing here writes to the database -- every call below is a read.
    //
    // Area scope: every figure is company-WIDE (no Area filter passed to
    // any repository call). This is intentional, not a bypass of the
    // existing per-user Area-security system (SeedlingAreaScope/
    // AreaAccessService, both completely untouched by this class): a daily
    // management summary is inherently a whole-business view for the 3
    // managing persons, the same "unscoped aggregate" mode
    // ManagementDashboardRepository already operates in today for its own
    // (Admin-only) page. This class introduces no new way for an ordinary
    // logged-in user to see data outside their Area -- it is not reachable
    // from any page or user-facing endpoint in this step.
    public class DailyReportService
    {
        private readonly ManagementDashboardRepository _dashboardRepo;
        private readonly BookingRepository _bookingRepo;
        private readonly SeedlingFulfilmentRepository _fulfilmentRepo;
        private readonly OutletSaleRepository _outletSaleRepo;
        private readonly FertilizerTransactionRepository _fertilizerRepo;
        private readonly WastageRepository _wastageRepo;
        private readonly UserLoginHistoryRepository _loginHistoryRepo;

        public DailyReportService(
            ManagementDashboardRepository dashboardRepo,
            BookingRepository bookingRepo,
            SeedlingFulfilmentRepository fulfilmentRepo,
            OutletSaleRepository outletSaleRepo,
            FertilizerTransactionRepository fertilizerRepo,
            WastageRepository wastageRepo,
            UserLoginHistoryRepository loginHistoryRepo)
        {
            _dashboardRepo = dashboardRepo;
            _bookingRepo = bookingRepo;
            _fulfilmentRepo = fulfilmentRepo;
            _outletSaleRepo = outletSaleRepo;
            _fertilizerRepo = fertilizerRepo;
            _wastageRepo = wastageRepo;
            _loginHistoryRepo = loginHistoryRepo;
        }

        public Task<DailyReport> BuildTodayReportAsync() => BuildReportForDateAsync(DateTime.Today);

        // Date-parameterized (not hardcoded to DateTime.Today) so this is
        // usable for a backfill/resend later, and so it can be exercised in
        // a test against a known, fixed date.
        public async Task<DailyReport> BuildReportForDateAsync(DateTime date)
        {
            var day = date.Date;
            var nextDay = day.AddDays(1);
            var report = new DailyReport { ReportDate = day };

            // ---- 1 & 2: Sowing + Ready Stock -- both come off
            // ManagementDashboardRepository, whose own date filter
            // (EffectiveFromDate/EffectiveToDateExclusive) is driven by
            // FromDate/ToDate on ManagementDashboardFilters.
            var filters = new ManagementDashboardFilters { FromDate = day, ToDate = day };

            // GetSeedSummaryAsync's second parameter (allAreasSeedStock) is
            // only used to populate SeedSummary.SeedStockRemaining, which
            // this report does not use -- passing an empty list avoids
            // re-issuing GetProductionSummaryAsync's own SeedStock query
            // just to throw its result away. SeedSownQuantityInRange itself
            // comes from a separate, independent query inside this same
            // method call (dbo.SeedSowings only).
            var seedSummary = await _dashboardRepo.GetSeedSummaryAsync(filters, new List<UnitQuantity>());
            report.SowingQuantity = seedSummary.SeedSownQuantityInRange;

            var readyStock = await _dashboardRepo.GetReadyStockSummaryAsync(filters);
            report.ReadyStockOverdueCount = readyStock.OverdueCount;
            report.ReadyStockOverdueQuantity = readyStock.OverdueQuantity;
            report.ReadyStockReadyTodayCount = readyStock.ReadyTodayCount;
            report.ReadyStockReadyTodayQuantity = readyStock.ReadyTodayQuantity;
            report.ReadyStockReadySoonCount = readyStock.ReadySoonCount;
            report.ReadyStockReadySoonQuantity = readyStock.ReadySoonQuantity;

            // ---- 2B/2C: Ready Stock by Plant Type -- same readyStock result
            // above, no second query. ReadyStockByPlantType is a direct,
            // unmodified pass-through (the WhatsApp formatter's source for
            // both each Plant Type's NAME and its quantity -- no name is
            // hand-typed in the formatter). The 4 scalar properties below are
            // kept for any other existing consumer that only needs the
            // quantity; a Plant Type with zero Ready Stock is still present
            // as its own entry (see GetReadyStockByPlantTypeAsync), and the
            // struct record's own default (0) covers a lookup finding
            // nothing, so this never throws if a name is ever renamed.
            report.ReadyStockByPlantType = readyStock.ReadyStockQuantityByPlantType;
            report.ReadyStockMeriGoldQuantity = readyStock.ReadyStockQuantityByPlantType.FirstOrDefault(p => p.PlantTypeName == "MERI GOLD").Quantity;
            report.ReadyStockChrysanthemumQuantity = readyStock.ReadyStockQuantityByPlantType.FirstOrDefault(p => p.PlantTypeName == "CHRYSANTHEMUM").Quantity;
            report.ReadyStockZinniaQuantity = readyStock.ReadyStockQuantityByPlantType.FirstOrDefault(p => p.PlantTypeName == "ZINNIA").Quantity;
            report.ReadyStockSeasonalVaritiesQuantity = readyStock.ReadyStockQuantityByPlantType.FirstOrDefault(p => p.PlantTypeName == "SEASONAL VARITIES").Quantity;

            // ---- 3: Seedling Bookings (by BookingDate, not DeliveryDate)
            var (seedlingBookingsCount, seedlingBookingsQuantity) =
                await _bookingRepo.GetBookingCountByBookingDateAsync(day, nextDay);
            report.SeedlingBookingsCount = seedlingBookingsCount;
            report.SeedlingBookingsQuantity = seedlingBookingsQuantity;

            // ---- 4 & 6: Potted Plant Bookings + Potted Plant Dispatch --
            // both already computed by GetOutletSalesSummaryAsync.
            var outletSummary = await _dashboardRepo.GetOutletSalesSummaryAsync(filters);
            report.PottedPlantBookingsCount = outletSummary.TotalBookingsInRange;
            report.PottedPlantDispatchCount = outletSummary.CompletedDispatchCountInRange;
            report.PottedPlantDispatchQuantity = outletSummary.CompletedDispatchQuantityInRange;

            // ---- 5: Seedling Dispatch
            var dispatchLines = await _fulfilmentRepo.GetDispatchRegisterAsync(day, day, null);
            var (seedlingDispatchCount, seedlingDispatchQuantity) = DailyReportCalculations.SummarizeDispatchLines(dispatchLines);
            report.SeedlingDispatchCount = seedlingDispatchCount;
            report.SeedlingDispatchQuantity = seedlingDispatchQuantity;

            // ---- 7: Outlet Sales
            report.OutletSalesCount = await _outletSaleRepo.GetSalesCountByDateRangeAsync(day, nextDay);

            // ---- 8: Fertilizer Usage
            var fertilizerFilter = new FertilizerTransactionFilter { From = day, To = day };
            fertilizerFilter.Normalize();
            var fertilizerRows = await _fertilizerRepo.SearchAsync(fertilizerFilter);
            report.FertilizerUsageQuantity = fertilizerRows.Sum(r => r.Quantity);

            // ---- 9: Wastage
            var wastageRows = await _wastageRepo.GetAsync(day, day);
            report.WastageQuantity = wastageRows.Sum(r => r.Quantity);

            // ---- 10: Employee Software Activity (Steps 5 + 7)
            var employeeActivity = await _loginHistoryRepo.GetTodaysActivityAsync(day);
            var (loggedIn, notLoggedIn) = DailyReportCalculations.PartitionEmployeeActivity(employeeActivity);
            report.TotalActiveEmployees = employeeActivity.Count;
            report.EmployeesLoggedInToday = loggedIn;
            report.EmployeesNotLoggedInToday = notLoggedIn;
            report.LoggedInEmployeeCount = loggedIn.Count;
            report.NotLoggedInEmployeeCount = notLoggedIn.Count;

            var (activeUserCount, noActivityRecordedCount) = DailyReportCalculations.SummarizeActiveUsers(loggedIn);
            report.ActiveUserCount = activeUserCount;
            report.NoActivityRecordedCount = noActivityRecordedCount;

            return report;
        }
    }
}
