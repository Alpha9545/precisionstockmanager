using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Data
{
    // Phase H (2026-09-30): "Monthly Wise Sowing" replacement, built entirely
    // on the NEW dbo.SeedSowings table -- never dbo.SeedEntries (the retired
    // legacy pipeline, removed from the app in Phase 1 of the old-system
    // cleanup and never reconnected). Read-only; no OnPost handler writes
    // anything. Area-scoped in SQL (SeedSowingRepository.GetMonthlyPivotAsync),
    // not just in the UI -- a restricted user's request can never return
    // another Area's rows, whatever filter values are posted.
    public class SeedSowingMonthlyReportModel : PageModel
    {
        private readonly SeedSowingRepository _seedSowingRepo;
        private readonly AreaRepository _areaRepo;
        private readonly PlantTypeRepository _plantTypeRepo;
        private readonly SeedlingAreaScope _areaAccessService;

        public SeedSowingMonthlyReportModel(
            SeedSowingRepository seedSowingRepo, AreaRepository areaRepo,
            PlantTypeRepository plantTypeRepo, SeedlingAreaScope areaAccessService)
        {
            _seedSowingRepo = seedSowingRepo;
            _areaRepo = areaRepo;
            _plantTypeRepo = plantTypeRepo;
            _areaAccessService = areaAccessService;
        }

        [BindProperty(SupportsGet = true)]
        public int Month { get; set; }
        [BindProperty(SupportsGet = true)]
        public int Year { get; set; }
        [BindProperty(SupportsGet = true)]
        public int? AreaId { get; set; }
        [BindProperty(SupportsGet = true)]
        public int? PlantTypeId { get; set; }

        public int DaysInMonth { get; private set; }
        public string MonthName { get; private set; } = string.Empty;
        public List<SeedSowingMonthlyPivotGridRow> Rows { get; private set; } = new();
        public decimal[] DailyTotals { get; private set; } = Array.Empty<decimal>();
        public decimal GrandTotal { get; private set; }

        public List<Area> Areas { get; private set; } = new();
        public List<PlantType> PlantTypes { get; private set; } = new();
        public IEnumerable<int> YearOptions => Enumerable.Range(DateTime.Today.Year - 4, 5).Reverse();

        public string? FilterNotice { get; private set; }

        public async Task OnGetAsync()
        {
            if (Month < 1 || Month > 12) Month = DateTime.Today.Month;
            if (Year < 2000 || Year > 2100) Year = DateTime.Today.Year;
            DaysInMonth = System.DateTime.DaysInMonth(Year, Month);
            MonthName = new DateTime(Year, Month, 1).ToString("MMMM yyyy");

            // Defense in depth: a restricted user cannot force another
            // Area's data into view by editing the query string -- the
            // filter is silently dropped (not just hidden from the
            // dropdown) whenever the requested Area is not one they can
            // access. GetMonthlyPivotAsync's own @Allowed IN-list is the
            // real enforcement; this only prevents a confusing "0 rows"
            // result from an Area the user was never offered.
            if (AreaId.HasValue && !_areaAccessService.CanAccessArea(User, AreaId.Value))
            {
                FilterNotice = "The requested Area is not one you have access to; showing your accessible Areas instead.";
                AreaId = null;
            }

            var allAreas = await _areaRepo.GetAllAreas();
            Areas = _areaAccessService.HasFullAreaAccess(User)
                ? allAreas
                : allAreas.Where(a => _areaAccessService.CanAccessArea(User, a.Id)).ToList();
            PlantTypes = await _plantTypeRepo.GetAllPlantTypes();

            var allowedAreaIds = _areaAccessService.GetAccessibleAreaIdsOrNull(User);
            var flatRows = await _seedSowingRepo.GetMonthlyPivotAsync(Month, Year, AreaId, PlantTypeId, allowedAreaIds);
            Rows = SeedSowingMonthlyReportRules.BuildPivot(flatRows, DaysInMonth);

            DailyTotals = new decimal[DaysInMonth];
            foreach (var row in Rows)
                for (var d = 0; d < DaysInMonth; d++)
                    DailyTotals[d] += row.DailyQuantities[d];
            GrandTotal = DailyTotals.Sum();
        }
    }
}
