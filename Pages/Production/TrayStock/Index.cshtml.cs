using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.TrayStock
{
    // 2026-10-08: Tray Stock is shown inside Material Stock (Trays). This old URL is kept so
    // bookmarks and links still work; it only redirects, keeping its filters, and keeps its
    // original permission rule (FeatureAuthorizationConventions).
    public class IndexModel : PageModel
    {
        public IActionResult OnGet(int? areaId, int? polyhouseId, string? traySize)
            => RedirectToPage("/Production/MaterialStock/Index", new
            {
                Material = MaterialStockRules.TraysKind, AreaId = areaId, PolyhouseId = polyhouseId,
                Cavity = DirectSowingRules.IsValidCavityType(traySize) ? traySize : null,
            });
    }
}
