using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.TrayStock
{
    // Tray Stock ledger/history -- same read-only, filterable shape as
    // EmptyPotInventory's own transaction history page.
    public class TransactionsModel : PageModel
    {
        private readonly TrayStockRepository _trayStockRepo;

        public TransactionsModel(TrayStockRepository trayStockRepo)
        {
            _trayStockRepo = trayStockRepo;
        }

        public List<TrayStockTransaction> Transactions { get; set; } = new();
        public List<Polyhouse> Polyhouses { get; set; } = new();
        public int? PolyhouseId { get; set; }
        public string? TraySize { get; set; }
        public IReadOnlyList<string> TraySizes => DirectSowingRules.CavityTypes;

        public async Task OnGetAsync(int? polyhouseId, string? traySize)
        {
            PolyhouseId = polyhouseId;
            TraySize = traySize;
            Polyhouses = await _trayStockRepo.GetMainOfficePolyhousesAsync();
            Transactions = await _trayStockRepo.GetTransactionsAsync(polyhouseId: polyhouseId, traySize: traySize);
        }
    }
}
