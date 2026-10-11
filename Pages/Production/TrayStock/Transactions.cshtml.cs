using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.TrayStock
{
    // 2026-10-08: Tray Stock history is part of Reports -> Material History (Trays). This old
    // URL only redirects there, keeping its filters and its original permission rule.
    public class TransactionsModel : PageModel
    {
        public IActionResult OnGet(int? areaId, int? polyhouseId, string? traySize)
            => RedirectToPage("/Production/MaterialStock/History", new
            {
                Material = MaterialStockRules.TraysKind, AreaId = areaId, PolyhouseId = polyhouseId,
                Cavity = DirectSowingRules.IsValidCavityType(traySize) ? traySize : null,
            });
    }
}
