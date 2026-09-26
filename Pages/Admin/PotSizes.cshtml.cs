using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Admin
{
    // Pot Size master: one standard spelling per size ("5 inch"), chosen
    // from a dropdown everywhere else in the application.
    public class PotSizesModel : PageModel
    {
        private readonly PotSizeRepository _potSizeRepo;

        public PotSizesModel(PotSizeRepository potSizeRepo)
        {
            _potSizeRepo = potSizeRepo;
        }

        public List<PotSize> PotSizes { get; set; } = new();

        [BindProperty]
        public string? NewName { get; set; }

        [BindProperty]
        public int NewSortOrder { get; set; }

        public async Task OnGetAsync()
        {
            PotSizes = await _potSizeRepo.GetAllAsync();
        }

        // Live preview of the standard spelling while typing.
        public JsonResult OnGetNormalize(string? name) => new(new { name = PotSizeRules.Normalize(name) });

        public async Task<IActionResult> OnPostAddAsync()
        {
            var (success, message) = await _potSizeRepo.InsertAsync(NewName, NewSortOrder, User.Identity?.Name);
            if (success)
                TempData["Success"] = $"Pot size '{PotSizeRules.Normalize(NewName)}' added.";
            else
                TempData["Error"] = message;
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostToggleAsync(int id, bool isActive)
        {
            var (success, message) = await _potSizeRepo.SetActiveAsync(id, isActive);
            if (!success)
                TempData["Error"] = message;
            return RedirectToPage();
        }
    }
}
