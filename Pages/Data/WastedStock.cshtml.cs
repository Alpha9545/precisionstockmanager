using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Data
{
    // Phase D: Wastage report over the current workflow (tray production,
    // cutting deliveries, pot production, potted stock). Leftover seeds or
    // cuttings below one complete tray are stock, not wastage, and never
    // appear here. (The former version read the retired dbo.Inventory
    // pipeline.)
    public class WastedStockModel : PageModel
    {
        private readonly WastageRepository _repo;
        private readonly AreaRepository _areaRepo;
        private readonly AreaAccessService _areaAccess;

        public WastedStockModel(WastageRepository repo, AreaRepository areaRepo, AreaAccessService areaAccess)
        {
            _repo = repo;
            _areaRepo = areaRepo;
            _areaAccess = areaAccess;
        }

        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public int? AreaId { get; set; }
        public string? Search { get; set; }
        public string? Type { get; set; }
        public List<Area> Areas { get; set; } = new();
        public List<WastageRepository.WastageRow> Rows { get; set; } = new();
        public List<string> Types { get; set; } = new();

        public async Task OnGetAsync(DateTime? from, DateTime? to, int? areaId, string? search, string? type)
        {
            To = (to ?? DateTime.Today).Date;
            From = (from ?? To.AddDays(-30)).Date;
            if (From > To)
                (From, To) = (To, From);
            AreaId = areaId;
            Search = search;
            Type = type;
            Areas = (await _areaRepo.GetAllAreas()).Where(a => _areaAccess.CanAccessArea(User, a.Id)).OrderBy(a => a.Name).ToList();
            var rows = (await _repo.GetAsync(From, To, areaId, search))
                .Where(r => r.AreaId.HasValue ? _areaAccess.CanAccessArea(User, r.AreaId) : _areaAccess.HasFullAreaAccess(User))
                .ToList();
            Types = rows.Select(r => r.ProductionType).Distinct().OrderBy(t => t).ToList();
            Rows = string.IsNullOrEmpty(type) ? rows : rows.Where(r => r.ProductionType == type).ToList();
        }
    }
}
