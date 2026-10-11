using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.Transplanting
{
    // Transplanting history (Transplant.View): every operation in the user's Areas (target OR
    // source), filtered in SQL. Dates are the India business dates the operations were recorded for.
    public class HistoryModel : PageModel
    {
        private readonly TransplantRepository _repo;
        private readonly AreaRepository _areaRepo;
        private readonly PolyhouseRepository _polyhouseRepo;
        private readonly PlantSpeciesRepository _speciesRepo;
        private readonly AreaAccessService _areaAccess;   // Mother Plant Area work: always Area-scoped (no Development switch), as the Mother Plant module

        public HistoryModel(TransplantRepository repo, AreaRepository areaRepo, PolyhouseRepository polyhouseRepo,
            PlantSpeciesRepository speciesRepo, AreaAccessService areaAccess)
        {
            _repo = repo;
            _areaRepo = areaRepo;
            _polyhouseRepo = polyhouseRepo;
            _speciesRepo = speciesRepo;
            _areaAccess = areaAccess;
        }

        [BindProperty(SupportsGet = true)] public TransplantFilter Filter { get; set; } = new();

        public List<ReadyStockTransplant> Rows { get; private set; } = new();
        public List<Area> Areas { get; private set; } = new();
        public List<Polyhouse> Polyhouses { get; private set; } = new();
        public List<(int Id, string Name)> Varieties { get; private set; } = new();
        public const int MaxRows = 1000;

        public async Task OnGetAsync()
        {
            if (Filter.From.HasValue && Filter.To.HasValue && Filter.From > Filter.To) (Filter.From, Filter.To) = (Filter.To, Filter.From);
            if (!DirectSowingRules.IsValidCavityType(Filter.CavityType)) Filter.CavityType = null;
            if (Filter.Status is not (TransplantRules.StatusCompleted or TransplantRules.StatusReversed)) Filter.Status = null;

            Rows = await _repo.ListAsync(Filter, (_areaAccess.HasFullAreaAccess(User) ? null : _areaAccess.GetAccessibleAreaIds(User)), MaxRows);
            Areas = (await _areaRepo.GetAllAreas())
                .Where(a => a.AreaType == TransplantRules.MotherPlantAreaType && _areaAccess.CanAccessArea(User, a.Id))
                .OrderBy(a => a.Name).ToList();
            var areaIds = Areas.Select(a => a.Id).ToHashSet();
            Polyhouses = (await _polyhouseRepo.GetAllPolyhouses())
                .Where(p => p.AreaId.HasValue && areaIds.Contains(p.AreaId.Value)).OrderBy(p => p.Name).ToList();
            Varieties = (await _speciesRepo.GetAllAsync())
                .Select(s => (s.Id, (s.Name ?? "").Trim())).OrderBy(s => s.Item2).ToList();
        }
    }
}
