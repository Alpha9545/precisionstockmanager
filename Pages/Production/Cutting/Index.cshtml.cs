using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.Cutting
{
    // Cutting Production register (what was cut, from which Mother Plant, when) with filters (Correction #5).
    //
    // Filters are query-string values (GET, so a filtered view can be bookmarked / refreshed) and are all applied by the
    // DATABASE in CuttingProductionRepository.SearchAsync, together (AND). The page shows the values that were used
    // again after searching. "Clear filters" is a plain link back to the page with no query string, i.e. the default
    // view (the last 30 days, everything else unset).
    //
    // AREA SECURITY: the user's Areas (AreaAccessService: UserRoles.AreaId claims; null = full access) are part of the
    // query itself and of the dropdown-option queries, so records of an Area the user cannot access are never read,
    // whatever the filters say; the previous in-memory FilterByArea is kept as a second guard.
    public class IndexModel : PageModel
    {
        private readonly CuttingProductionRepository _repo;
        private readonly AreaAccessService _areaAccess;

        public IndexModel(CuttingProductionRepository repo, AreaAccessService areaAccess)
        {
            _repo = repo;
            _areaAccess = areaAccess;
        }

        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public List<CuttingProduction> Items { get; set; } = new();

        [BindProperty(SupportsGet = true)] public int? AreaId { get; set; }
        [BindProperty(SupportsGet = true)] public int? MotherPlantId { get; set; }
        [BindProperty(SupportsGet = true)] public int? SpeciesId { get; set; }
        [BindProperty(SupportsGet = true)] public int? PolyhouseId { get; set; }
        [BindProperty(SupportsGet = true)] public int? SupervisorId { get; set; }
        [BindProperty(SupportsGet = true)] public string? Destination { get; set; }
        [BindProperty(SupportsGet = true)] public int? DestinationAreaId { get; set; }
        [BindProperty(SupportsGet = true)] public string? DeliveryStatus { get; set; }

        public CuttingProductionRepository.FilterOptions Options { get; set; } = new();
        public List<string> Notices { get; set; } = new();
        public bool FilterActive { get; set; }

        public async Task OnGetAsync(DateTime? from, DateTime? to)
        {
            var filter = new CuttingProductionFilter
            {
                From = from, To = to, AreaId = AreaId, MotherPlantId = MotherPlantId, SpeciesId = SpeciesId, PolyhouseId = PolyhouseId,
                SupervisorId = SupervisorId, Destination = Destination, DestinationAreaId = DestinationAreaId, DeliveryStatus = DeliveryStatus
            };
            Notices = filter.Normalize();
            // the default period, as before: the 30 days up to today (or up to the chosen To)
            filter.To ??= DateTime.Today;
            filter.From ??= filter.To.Value.AddDays(-30);
            if (filter.From > filter.To) filter.From = filter.To;

            // keep exactly what will be searched on screen
            From = filter.From.Value; To = filter.To.Value;
            AreaId = filter.AreaId; MotherPlantId = filter.MotherPlantId; SpeciesId = filter.SpeciesId; PolyhouseId = filter.PolyhouseId;
            SupervisorId = filter.SupervisorId; Destination = filter.Destination; DestinationAreaId = filter.DestinationAreaId; DeliveryStatus = filter.DeliveryStatus;
            FilterActive = filter.HasNarrowingFilter;

            IReadOnlyCollection<int>? allowedAreas = _areaAccess.HasFullAreaAccess(User) ? null : _areaAccess.GetAccessibleAreaIds(User);
            Options = await _repo.GetFilterOptionsAsync(allowedAreas);
            Items = _areaAccess.FilterByArea(User, await _repo.SearchAsync(filter, allowedAreas), c => (int?)c.AreaId);
        }
    }
}
