using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Data
{
    // Phase D: Stock History over the current stock ledgers (Seed, Cutting,
    // Ready Seedling, Empty Pot, Potted Plant). Per stock item:
    //   Opening + Incoming - Outgoing = Balance
    // with the individual movements of the period underneath. (The former
    // version read the retired dbo.Inventory pipeline.)
    public class StockHistoryModel : PageModel
    {
        private readonly StockHistoryRepository _repo;
        private readonly AreaRepository _areaRepo;
        private readonly AreaAccessService _areaAccess;

        public StockHistoryModel(StockHistoryRepository repo, AreaRepository areaRepo, AreaAccessService areaAccess)
        {
            _repo = repo;
            _areaRepo = areaRepo;
            _areaAccess = areaAccess;
        }

        public string StockType { get; set; } = StockHistoryRepository.Seed;
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public int? AreaId { get; set; }
        public string? Search { get; set; }
        public List<Area> Areas { get; set; } = new();

        public List<(StockHistoryRules.Summary Summary, string Item, string? AreaName)> Rows { get; set; } = new();
        public List<StockHistoryRepository.Movement> PeriodMovements { get; set; } = new();

        public async Task OnGetAsync(string? stockType, DateTime? from, DateTime? to, int? areaId, string? search)
        {
            StockType = stockType != null && StockHistoryRepository.StockTypes.ContainsKey(stockType) ? stockType : StockHistoryRepository.Seed;
            To = (to ?? DateTime.Today).Date;
            From = (from ?? To.AddDays(-30)).Date;
            if (From > To)
                (From, To) = (To, From);
            AreaId = areaId;
            Search = search;
            Areas = (await _areaRepo.GetAllAreas()).Where(a => _areaAccess.CanAccessArea(User, a.Id)).OrderBy(a => a.Name).ToList();

            var movements = await _repo.GetMovementsAsync(StockType, To, areaId, search);
            // Only Areas this user may see (unassigned legacy rows: cross-Area users only).
            movements = movements.Where(m => m.AreaId.HasValue ? _areaAccess.CanAccessArea(User, m.AreaId) : _areaAccess.HasFullAreaAccess(User)).ToList();

            var labels = movements.GroupBy(m => m.ItemKey).ToDictionary(g => g.Key, g => (g.First().Item, g.First().AreaName));
            Rows = StockHistoryRules.Summarize(movements.Select(m => new StockHistoryRules.Movement(m.ItemKey, m.Date, m.Quantity)), From, To)
                .Where(s => s.Opening != 0 || s.Incoming != 0 || s.Outgoing != 0)
                .Select(s => (s, labels[s.ItemKey].Item, labels[s.ItemKey].AreaName))
                .OrderBy(r => r.Item2).ThenBy(r => r.AreaName)
                .ToList();
            PeriodMovements = movements.Where(m => m.Date >= From).OrderByDescending(m => m.Date).ToList();
        }
    }
}
