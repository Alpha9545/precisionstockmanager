using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.TrayStock
{
    // 2026-10-08: Adding trays is part of Add Material (Material Type: Tray), with the same
    // fields and server rules (Pages/Production/MaterialStock/TrayAddForm.cs). This old URL
    // only redirects there and keeps its original permission rule (TrayStock.Enter).
    public class AddModel : PageModel
    {
        public IActionResult OnGet() => RedirectToPage("/Production/MaterialStock/Add", new { type = MaterialStockRules.TraysKind });
    }
}
