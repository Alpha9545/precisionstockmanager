using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.TrayStock
{
    // Tray Stock ledger/history -- read-only, filterable by Area, Polyhouse and
    // Cavity, limited in SQL to the Areas this user may access (a requested
    // areaId outside them returns nothing). Includes every historical row, so
    // the 2026-10-04 migrations and old records stay understandable.
    public class TransactionsModel : PageModel
    {
        private readonly TrayStockRepository _trayStockRepo;
        private readonly AreaRepository _areaRepo;
        private readonly PolyhouseRepository _polyhouseRepo;
        private readonly AreaAccessService _areaAccess;

        public TransactionsModel(TrayStockRepository trayStockRepo, AreaRepository areaRepo, PolyhouseRepository polyhouseRepo, AreaAccessService areaAccess)
        {
            _trayStockRepo = trayStockRepo;
            _areaRepo = areaRepo;
            _polyhouseRepo = polyhouseRepo;
            _areaAccess = areaAccess;
        }

        public List<TrayStockTransaction> Transactions { get; set; } = new();
        public List<Area> Areas { get; set; } = new();
        public List<Polyhouse> Polyhouses { get; set; } = new();
        public int? AreaId { get; set; }
        public int? PolyhouseId { get; set; }
        public string? TraySize { get; set; }
        public IReadOnlyList<string> TraySizes => DirectSowingRules.CavityTypes;

        public async Task OnGetAsync(int? areaId, int? polyhouseId, string? traySize)
        {
            AreaId = areaId;
            PolyhouseId = polyhouseId;
            TraySize = DirectSowingRules.IsValidCavityType(traySize) ? traySize : null;
            IReadOnlyCollection<int>? allowed = _areaAccess.HasFullAreaAccess(User) ? null : _areaAccess.GetAccessibleAreaIds(User);
            Areas = (await _areaRepo.GetAllAreas())
                .Where(a => allowed == null || allowed.Contains(a.Id))
                .OrderBy(a => a.Name)
                .ToList();
            Polyhouses = AreaId.HasValue && Areas.Any(a => a.Id == AreaId)
                ? (await _polyhouseRepo.GetByAreaIdAsync(AreaId.Value)).OrderBy(p => p.Name).ToList()
                : new List<Polyhouse>();
            Transactions = await _trayStockRepo.GetTransactionsAsync(areaId, polyhouseId, TraySize, allowed);
        }
    }
}
