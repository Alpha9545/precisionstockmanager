using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Fertilizer
{
    // Fertilizer Stock (purchase batches). Correction I3: Insert/Update/Delete now go through
    // FertilizerStockRepository, which re-applies its own rule under a database row lock at the moment it writes --
    // never trusting the state the page showed when the form was opened. A batch may be edited or deleted only
    // while nothing has been issued from it (FertilizerStockRules.EditAllowed / DeletionRules
    // .FertilizerStockDependencies); a stale form, a crafted POST or a concurrent Issue all land on the same check,
    // so none of them can slip past it. Page permission (Fertilizer.View / Fertilizer.Enter) is unchanged.
    public class StockModel : PageModel
    {
        private readonly FertilizerStockRepository _repo;
        public StockModel(FertilizerStockRepository repo) => _repo = repo;

        // List
        public List<FertilizerStockRow> Stocks { get; set; } = new();

        // Dropdowns
        public List<DropdownItem> Fertilizers { get; set; } = new();
        public List<DropdownItem> Units { get; set; } = new();
        public List<DropdownItem> Sources { get; set; } = new();

        [BindProperty]
        public FertilizerStock Stock { get; set; } = new();

        public async Task OnGetAsync()
        {
            await LoadAllAsync();
        }

        // INSERT + UPDATE
        public async Task<IActionResult> OnPostSaveAsync()
        {
            var result = Stock.StockId == 0
                ? await _repo.InsertAsync(Stock)
                : await _repo.UpdateAsync(Stock);

            if (!result.Success)
            {
                ModelState.AddModelError(string.Empty, result.Message ?? "Failed to save this stock entry.");
                await LoadAllAsync();
                return Page();
            }
            TempData["Success"] = Stock.StockId == 0 ? "Fertilizer stock added." : "Fertilizer stock updated.";
            return RedirectToPage();
        }

        // EDIT (loads the form; does not write anything)
        public async Task<IActionResult> OnPostEditAsync(int id)
        {
            var stock = await _repo.GetByIdAsync(id);
            if (stock == null)
            {
                ModelState.AddModelError("", FertilizerStockRules.NotFoundMessage);
                await LoadAllAsync();
                return Page();
            }
            if (!FertilizerStockRules.EditAllowed(stock.Quantity, stock.LatestAvailableQuantity))
            {
                ModelState.AddModelError("", FertilizerStockRules.AlreadyUsedMessage);
                await LoadAllAsync();
                return Page();
            }
            Stock = stock;
            await LoadAllAsync();
            return Page();
        }

        // DELETE
        public async Task<IActionResult> OnPostDeleteAsync(int id)
        {
            var result = await _repo.DeleteAsync(id);
            TempData[result.Succeeded ? "Success" : "Error"] = result.Message;
            return RedirectToPage();
        }

        private async Task LoadAllAsync()
        {
            Stocks = await _repo.GetAllAsync();
            Fertilizers = await _repo.GetFertilizersAsync();
            Units = await _repo.GetUnitsAsync();
            Sources = await _repo.GetSourcesAsync();
        }
    }
}


public class DropdownItem
{
    public int Id { get; set; }
    public string Name { get; set; }
}
