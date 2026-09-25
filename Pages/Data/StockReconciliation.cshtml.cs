using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Data
{
    // Phase 8 (Stock History and Wastage Integration): "Opening + IN - OUT
    // - Wastage = Closing" checked for every stock pool across all five
    // modern stock types, purely from each one's own existing ledger --
    // no new business rule, a pure data-integrity report. A discrepancy
    // here would mean a stock row's PhysicalQuantity/Quantity column and
    // its own ledger have drifted apart (e.g. a manual DB edit bypassing
    // the repository layer) -- under normal operation every
    // RecordTransactionAsync-style method updates both together in the
    // same transaction, so this should read 100% balanced.
    public class StockReconciliationModel : PageModel
    {
        private readonly StockLedgerRepository _stockLedgerRepository;

        public StockReconciliationModel(StockLedgerRepository stockLedgerRepository)
        {
            _stockLedgerRepository = stockLedgerRepository;
        }

        public List<StockReconciliationRow> Rows { get; set; } = new();
        public int DiscrepancyCount => Rows.Count(r => !r.IsBalanced);

        public async Task OnGetAsync()
        {
            Rows = await _stockLedgerRepository.GetReconciliationAsync();
        }
    }
}
