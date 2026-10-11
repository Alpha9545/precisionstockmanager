using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.ManagementDashboard
{
    // Phase 26 (Phase L): Management Dashboard. A read-only reporting
    // layer over the existing authoritative tables -- see
    // Data/ManagementDashboardRepository.cs for the full per-section
    // query design, docs/MANAGEMENT_DASHBOARD.md for every KPI definition
    // and PROJECT_DOCUMENTATION.md Decision 23 for the original
    // KPI-to-source mapping. Nothing on this page writes anything.
    //
    // AUTHORIZATION (per the Phase L addendum -- Administrator-only,
    // NOT the normal "Management" role, despite the page's own name):
    // the page requires the "Admin.ManageAreas" permission, declared
    // centrally in Authorization/FeatureAuthorizationConventions.cs and
    // enforced server-side on every request (not just by hiding the menu).
    // dbo.RolePermissions grants no "Admin.*" code to any ordinary role, so
    // in practice only a full-access user (System Administrator) passes --
    // see ClaimsPrincipalSecurityExtensions.HasPermission. The baseline
    // "AuthorizeFolder("/ManagementDashboard")" convention in Program.cs
    // is a second, independent layer.
    //
    // Layout: the company-wide OVERVIEW (current state + the selected
    // period) on top; the original Phase L production detail sections, with
    // their own Area / Variety / Pot Size filters, below.
    public class IndexModel : PageModel
    {
        private readonly ManagementDashboardRepository _dashboardRepo;
        private readonly AreaRepository _areaRepo;
        private readonly PlantSpeciesRepository _plantSpeciesRepo;
        private readonly EmptyPotInventoryRepository _emptyPotInventoryRepo;
        private readonly BookingRepository _bookingRepo;
        private readonly SeedlingFulfilmentRepository _fulfilmentRepo;
        private readonly MaintenanceRepository _maintenanceRepo;
        private readonly DailyLabourAttendanceRepository _labourRepo;
        private readonly LabourOvertimeRepository _overtimeRepo;
        private readonly SuggestionRepository _suggestionRepo;
        private readonly PotBatchRepository _potBatchRepo;
        private readonly TransplantRepository _transplantRepo;

        public IndexModel(
            ManagementDashboardRepository dashboardRepo,
            AreaRepository areaRepo,
            PlantSpeciesRepository plantSpeciesRepo,
            EmptyPotInventoryRepository emptyPotInventoryRepo,
            BookingRepository bookingRepo,
            SeedlingFulfilmentRepository fulfilmentRepo,
            MaintenanceRepository maintenanceRepo,
            DailyLabourAttendanceRepository labourRepo,
            LabourOvertimeRepository overtimeRepo,
            SuggestionRepository suggestionRepo,
            PotBatchRepository potBatchRepo,
            TransplantRepository transplantRepo)
        {
            _dashboardRepo = dashboardRepo;
            _areaRepo = areaRepo;
            _plantSpeciesRepo = plantSpeciesRepo;
            _emptyPotInventoryRepo = emptyPotInventoryRepo;
            _bookingRepo = bookingRepo;
            _fulfilmentRepo = fulfilmentRepo;
            _maintenanceRepo = maintenanceRepo;
            _labourRepo = labourRepo;
            _overtimeRepo = overtimeRepo;
            _suggestionRepo = suggestionRepo;
            _potBatchRepo = potBatchRepo;
            _transplantRepo = transplantRepo;
        }

        public const int TopVarieties = 3;
        public const int ChartVarieties = 10;
        public const int RecentActivityItems = 12;

        // Period for every PERIOD figure (overview and detail alike): a preset
        // (today / 7d / 30d / month) or custom From..To. Current-state figures
        // ignore it. Area / Variety / Pot Size narrow the production detail
        // section only -- they are conveniences for the Administrator, never an
        // authorization boundary (every value is a SQL parameter, and an unknown
        // id simply matches nothing).
        [BindProperty(SupportsGet = true)]
        public string? Range { get; set; }

        [BindProperty(SupportsGet = true)]
        public DateTime? FromDate { get; set; }

        [BindProperty(SupportsGet = true)]
        public DateTime? ToDate { get; set; }

        [BindProperty(SupportsGet = true)]
        public int? AreaId { get; set; }

        [BindProperty(SupportsGet = true)]
        public int? GrowingPartnerId { get; set; }

        [BindProperty(SupportsGet = true)]
        public int? SpeciesId { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? PotSize { get; set; }

        public DashboardPeriod Period { get; set; } = new();
        public ManagementOverview Overview { get; set; } = new();
        // 2026-10-09: Main Area -> Mother Plant transfers and transplanting, today (India) + current in-transit.
        public TransplantDaySummary MotherPlantFlow { get; set; } = new();
        public ManagementDashboardViewModel Dashboard { get; set; } = new();
        public bool DetailFiltered => AreaId.HasValue || SpeciesId.HasValue || PotSize != null;

        public async Task OnGetAsync()
        {
            var today = AppClock.Today;
            Period = DashboardPeriod.Resolve(Range, FromDate, ToDate, today);
            Range = Period.Key;
            FromDate = Period.From;
            ToDate = Period.To;
            PotSize = string.IsNullOrWhiteSpace(PotSize) ? null : PotSize;

            var filters = new ManagementDashboardFilters
            {
                FromDate = Period.From,
                ToDate = Period.To,
                AreaId = AreaId,
                GrowingPartnerId = GrowingPartnerId,
                SpeciesId = SpeciesId,
                PotSize = PotSize
            };

            // ---- Production detail (Phase L sections, own filters) ----
            var production = await _dashboardRepo.GetProductionSummaryAsync(filters);
            var readyStock = await _dashboardRepo.GetReadyStockSummaryAsync(filters, today);
            var outletSales = await _dashboardRepo.GetOutletSalesSummaryAsync(filters);
            // Reuses production.SeedStock (the all-Areas per-Unit Seed
            // Stock balance) instead of a second, identical query -- see
            // GetSeedSummaryAsync's own comment.
            var seed = await _dashboardRepo.GetSeedSummaryAsync(filters, production.SeedStock);
            var cutting = await _dashboardRepo.GetCuttingSummaryAsync(filters);
            var stockHealth = _dashboardRepo.BuildStockHealth(production, cutting, readyStock, seed);
            var sowingVsReadyTrend = await _dashboardRepo.GetSowingVsReadyTrendAsync(filters);
            var outletDispatchTrend = await _dashboardRepo.GetOutletDispatchTrendAsync(filters);

            var areas = await _areaRepo.GetAllAreas();
            var speciesOptions = await _plantSpeciesRepo.GetAllAsync();
            var emptyPots = await _emptyPotInventoryRepo.GetAllAsync(activeOnly: true);

            Dashboard = new ManagementDashboardViewModel
            {
                Filters = filters,
                Production = production,
                ReadyStock = readyStock,
                OutletSales = outletSales,
                Seed = seed,
                Cutting = cutting,
                StockHealth = stockHealth,
                SowingVsReadyTrend = sowingVsReadyTrend,
                ReadyStockTrend = new List<TrendPoint>(), // deliberately not a separate chart -- see repository comment
                OutletDispatchTrend = outletDispatchTrend,
                Areas = areas,
                SpeciesOptions = speciesOptions,
                PotSizeOptions = emptyPots.Select(e => e.PotSize).Distinct().OrderBy(p => p).ToList()
            };

            // ---- Company-wide overview ----
            // The detail results above ARE company-wide when no detail filter is set;
            // only re-query the three summaries the overview needs when one is.
            var companyFilters = new ManagementDashboardFilters { FromDate = Period.From, ToDate = Period.To };
            var companyReady = DetailFiltered ? await _dashboardRepo.GetReadyStockSummaryAsync(companyFilters, today) : readyStock;
            var companyCutting = DetailFiltered ? await _dashboardRepo.GetCuttingSummaryAsync(companyFilters) : cutting;
            var companyOutlet = DetailFiltered ? await _dashboardRepo.GetOutletSalesSummaryAsync(companyFilters) : outletSales;

            var data = await _dashboardRepo.GetOverviewAsync(Period, today);

            // Seedling bookings received / dispatched in the period: the SAME
            // definitions the Daily Report uses (BookingRepository counts unique
            // orders; the Dispatch Register's lines summed per dispatch).
            var (ordersReceived, quantityReceived) = await _bookingRepo.GetBookingCountByBookingDateAsync(Period.From, Period.ToExclusive);
            var dispatchLines = await _fulfilmentRepo.GetDispatchRegisterAsync(Period.From, Period.To, null);
            var (dispatchCount, dispatchQuantity) = DailyReportCalculations.SummarizeDispatchLines(dispatchLines);
            data.Seedlings.OrdersReceived = ordersReceived;
            data.Seedlings.QuantityReceived = quantityReceived;
            data.Seedlings.Dispatches = dispatchCount;
            data.Seedlings.DispatchedQuantity = dispatchQuantity;

            var activeTickets = await _maintenanceRepo.SearchAsync(new MaintenanceTicketFilter { ActiveOnly = true }, null);
            var maintenance = ManagementDashboardCalculations.Maintenance(activeTickets, data.MaintenanceClosedInPeriod, DateTime.UtcNow, today);

            var (labourDays, labourSummary) = await _labourRepo.SearchDaysAsync(new DailyLabourFilter { FromDate = today, ToDate = today }, null);
            var overtime = await _overtimeRepo.SearchAsync(new LabourOvertimeFilter { FromDate = today, ToDate = today }, null);

            var suggestionCounts = await _suggestionRepo.GetCountsAsync(null);
            var potBatchesOverdue = (await _potBatchRepo.GetAllAsync(PotBatchRules.InProduction))
                .Count(b => b.Readiness(today) == PotBatchRules.DueOverdue);

            MotherPlantFlow = await _transplantRepo.GetDaySummaryAsync(today);

            var readyByVariety = ManagementDashboardCalculations.ReadyPhysicalByVariety(data.ReadyStock);
            Overview = new ManagementOverview
            {
                Period = Period,
                Today = today,
                ReadyStock = data.ReadyStock,
                PottedStock = data.PottedStock,
                ReadyTotals = ManagementDashboardCalculations.Totals(data.ReadyStock),
                PottedTotals = ManagementDashboardCalculations.Totals(data.PottedStock),
                TopByReadyStock = ManagementDashboardCalculations.RankTop(readyByVariety, TopVarieties),
                TopByOpenBooking = ManagementDashboardCalculations.RankTop(data.OpenBookingByVariety, TopVarieties),
                ReadyStockByVarietyChart = ManagementDashboardCalculations.TopWithOthers(readyByVariety, ChartVarieties),
                ReadyStockByPlantType = ManagementDashboardCalculations.ByPlantType(data.ReadyStock),
                OpenBookingByVariety = data.OpenBookingByVariety,
                Areas = ManagementDashboardCalculations.BuildAreaRows(data.ReadyStock, data.PottedStock),
                PolyhouseMismatches = ManagementDashboardCalculations.FindPolyhouseMismatches(data.ReadyStock),
                Seedlings = data.Seedlings,
                Potted = companyOutlet,
                Production = data.Production,
                Maintenance = maintenance,
                Store = data.Store,
                Labour = ManagementDashboardCalculations.Labour(today, labourDays, labourSummary, overtime),
                Suggestions = new SuggestionSnapshot
                {
                    ActiveProblems = suggestionCounts.ActiveProblems,
                    ActiveSuggestions = suggestionCounts.ActiveSuggestions,
                    Permanent = suggestionCounts.Permanent,
                    RecentProblems = data.RecentProblems,
                },
                Attention = ManagementDashboardCalculations.BuildAttention(today, maintenance, data.Seedlings,
                    companyReady.OverdueCount, potBatchesOverdue, companyCutting.PendingConfirmationCount, data.Store, suggestionCounts.ActiveProblems),
                RecentActivity = ManagementDashboardRepository.MergeActivity(data.RecentActivity, RecentActivityItems),
            };
        }
    }
}
