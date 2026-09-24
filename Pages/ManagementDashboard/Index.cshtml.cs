using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.ManagementDashboard
{
    // Phase 26 (Phase L): Management Dashboard. A read-only reporting
    // layer over the existing authoritative tables -- see
    // Data/ManagementDashboardRepository.cs for the full per-section
    // query design and PROJECT_DOCUMENTATION.md Decision 23 for the
    // KPI-to-source mapping. Nothing on this page writes anything.
    //
    // AUTHORIZATION (per the Phase L addendum -- Administrator-only,
    // NOT the normal "Management" role, despite the page's own name):
    // [Authorize(Policy = "Admin.ManageAreas")] below is the actual,
    // server-side gate. This mirrors the exact, already-proven Phase 14
    // precedent (Pages/Admin/Roles.cshtml.cs uses "Admin.ManageRoles",
    // Pages/Admin/UserRoles.cshtml.cs uses "Admin.ManageUsers") rather
    // than the standard [Authorize(Roles = "Admin")] attribute, because
    // Pages/Account/Login.cshtml.cs stamps ASP.NET Core's own
    // ClaimTypes.Role with the LEGACY dbo.Designation/DesignationName
    // value, not the new Phase-14 Role system's role name -- an
    // [Authorize(Roles = "Admin")] check here would silently test the
    // wrong claim. The new Role system's role name lives only in the
    // custom "RoleName" claim (AreaAccessService.RoleNameClaimType),
    // which the Permission-code system deliberately does not check --
    // "Admin.ManageAreas" is instead satisfied by a "Permission" claim
    // of that exact value (PermissionAuthorizationPolicyProvider), and
    // per Database/Phase14_RoleFoundation_AreaExtension.sql section 3c,
    // dbo.RolePermissions grants EVERY permission code -- including
    // all three "Admin.*" codes -- to the Admin role alone via an
    // unconditional cross-join, while every other seeded role (including
    // Management) only ever receives "*.View"-suffixed codes (section
    // 3d) or a short explicit list (section 3e), NEVER an "Admin.*"
    // code. So today, only a user holding the Admin role can satisfy
    // this policy -- confirmed by re-reading the full grant script
    // rather than assumed. This required NO new permission code, NO
    // schema change, and NO modification to the existing role/claim
    // system, per the addendum's own "do not modify the existing role
    // system just to add the dashboard" instruction.
    //
    // A baseline "AuthorizeFolder("/ManagementDashboard")" convention
    // was ALSO added in Program.cs (any authenticated user), exactly
    // mirroring how "/Admin" itself is both AuthorizeFolder-protected
    // AND carries its own stricter per-page Policy attributes -- so
    // this page is covered by two independent layers, and menu
    // visibility (see _Layout.cshtml) is never the only protection.
    // Phase A: the server-side rule for this page ("Admin.ManageAreas") is declared
    // centrally in Authorization/FeatureAuthorizationConventions.cs.
    public class IndexModel : PageModel
    {
        private readonly ManagementDashboardRepository _dashboardRepo;
        private readonly AreaRepository _areaRepo;
        private readonly GrowingPartnerRepository _growingPartnerRepo;
        private readonly PlantSpeciesRepository _plantSpeciesRepo;
        private readonly EmptyPotInventoryRepository _emptyPotInventoryRepo;

        public IndexModel(
            ManagementDashboardRepository dashboardRepo,
            AreaRepository areaRepo,
            GrowingPartnerRepository growingPartnerRepo,
            PlantSpeciesRepository plantSpeciesRepo,
            EmptyPotInventoryRepository emptyPotInventoryRepo)
        {
            _dashboardRepo = dashboardRepo;
            _areaRepo = areaRepo;
            _growingPartnerRepo = growingPartnerRepo;
            _plantSpeciesRepo = plantSpeciesRepo;
            _emptyPotInventoryRepo = emptyPotInventoryRepo;
        }

        // Filters -- bound from the query string so the header
        // filter bar's own GET form round-trips naturally, exactly
        // like Pages/Production/ReadyAlerts/Index.cshtml.cs's
        // WindowDays. Administrator has full cross-Area access (per
        // the addendum -- "do NOT apply normal Area restrictions to
        // the Administrator dashboard"), so these are narrowing
        // conveniences for the Admin viewing the dashboard, never an
        // authorization boundary -- there is no other role that can
        // reach this page at all, so there is nothing to bypass by
        // tampering with the query string (Step 14 is still satisfied:
        // every value below is validated against the database inside
        // ManagementDashboardRepository's own parameterized queries,
        // never interpolated, and a non-existent/foreign AreaId simply
        // returns zero rows rather than exposing anything).
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

        public ManagementDashboardViewModel Dashboard { get; set; } = new();

        public async Task OnGetAsync()
        {
            var filters = new ManagementDashboardFilters
            {
                FromDate = FromDate,
                ToDate = ToDate,
                AreaId = AreaId,
                GrowingPartnerId = GrowingPartnerId,
                SpeciesId = SpeciesId,
                PotSize = string.IsNullOrWhiteSpace(PotSize) ? null : PotSize
            };

            // Keep the bound properties normalized to what will actually
            // be used, so the filter bar's own form redisplays exactly
            // what was applied.
            FromDate = filters.FromDate;
            ToDate = filters.ToDate;
            PotSize = filters.PotSize;

            var production = await _dashboardRepo.GetProductionSummaryAsync(filters);
            var readyStock = await _dashboardRepo.GetReadyStockSummaryAsync(filters);
            var growingPartners = await _dashboardRepo.GetGrowingPartnerSummaryAsync(filters);
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
            var growingPartnerOptions = await _growingPartnerRepo.GetAllAsync(activeOnly: true);
            var speciesOptions = await _plantSpeciesRepo.GetAllAsync();
            var emptyPots = await _emptyPotInventoryRepo.GetAllAsync(activeOnly: true);

            Dashboard = new ManagementDashboardViewModel
            {
                Filters = filters,
                Production = production,
                ReadyStock = readyStock,
                GrowingPartners = growingPartners,
                OutletSales = outletSales,
                Seed = seed,
                Cutting = cutting,
                StockHealth = stockHealth,
                SowingVsReadyTrend = sowingVsReadyTrend,
                ReadyStockTrend = new List<TrendPoint>(), // deliberately not a separate chart -- see repository comment
                OutletDispatchTrend = outletDispatchTrend,
                GrowingPartnerProductionChart = growingPartners
                    .Select(g => new CategoryValue { Label = $"{g.GrowingPartnerName} / {g.AreaName}", Value = g.PotTrayProduction })
                    .Where(c => c.Value > 0)
                    .OrderByDescending(c => c.Value)
                    .ToList(),
                Areas = areas,
                GrowingPartnerOptions = growingPartnerOptions,
                SpeciesOptions = speciesOptions,
                PotSizeOptions = emptyPots.Select(e => e.PotSize).Distinct().OrderBy(p => p).ToList()
            };
        }
    }
}
