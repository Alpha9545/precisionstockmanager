using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.TrayStock
{
    // Main Office Officer gives/allocates trays to a specific Main Office
    // Polyhouse (MainOffice.Confirm -- the same permission ConfirmReceipt
    // already uses for this role). Polyhouse choices are restricted
    // server-side to real, active Polyhouses under a Main Office-type Area
    // (TrayStockRepository.GetMainOfficePolyhousesAsync / re-validated again
    // by AllocateAsync under lock) -- never Outlet, never a growing-site
    // Polyhouse, never unassigned.
    public class AllocateModel : PageModel
    {
        private readonly TrayStockRepository _trayStockRepo;

        public AllocateModel(TrayStockRepository trayStockRepo)
        {
            _trayStockRepo = trayStockRepo;
        }

        [BindProperty] public DateTime Date { get; set; } = DateTime.Today;
        [BindProperty] public int PolyhouseId { get; set; }
        [BindProperty] public string? TraySize { get; set; }
        [BindProperty] public decimal Quantity { get; set; }
        [BindProperty] public string? Remarks { get; set; }

        public List<Polyhouse> Polyhouses { get; set; } = new();
        public IReadOnlyList<string> TraySizes => DirectSowingRules.CavityTypes;

        public async Task OnGetAsync()
        {
            Polyhouses = await _trayStockRepo.GetMainOfficePolyhousesAsync();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            // SECURITY: the posted PolyhouseId/TraySize/Quantity are never
            // trusted as-is -- AllocateAsync re-validates the Polyhouse is a
            // real, active Main Office Polyhouse and the Tray Size is one of
            // the closed set, under its own lock, independent of whatever
            // the dropdown here actually offered.
            var (success, message) = await _trayStockRepo.AllocateAsync(
                PolyhouseId, TraySize ?? string.Empty, Quantity, User.GetUserId(), User.Identity?.Name, Remarks);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to allocate trays.");
                Polyhouses = await _trayStockRepo.GetMainOfficePolyhousesAsync();
                return Page();
            }

            var polyhouse = (await _trayStockRepo.GetMainOfficePolyhousesAsync()).FirstOrDefault(p => p.Id == PolyhouseId);
            TempData["Success"] = $"{Quantity:N0} {TraySize} trays allocated to {polyhouse?.Name ?? "the selected Polyhouse"}.";
            return RedirectToPage("/Production/TrayStock/Index");
        }
    }
}
