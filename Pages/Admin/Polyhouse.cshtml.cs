using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Admin
{
    //[Authorize(Policy = "Admin")]

    public class PolyhouseModel : PageModel
    {
        private readonly PolyhouseRepository _polyhouseRepo;

        public PolyhouseModel(PolyhouseRepository polyhouseRepo)
        {
            _polyhouseRepo = polyhouseRepo;
        }

        public List<Polyhouse> Polyhouses { get; set; } = new();

        [BindProperty]
        public string NewPolyhouseName { get; set; }

        [BindProperty]
        public int EditId { get; set; }

        [BindProperty]
        public string EditPolyhouseName { get; set; }

        public async Task OnGetAsync()
        {
            Polyhouses = await _polyhouseRepo.GetAllPolyhouses();
        }

        public async Task<IActionResult> OnPostAddAsync()
        {
            if (!string.IsNullOrWhiteSpace(NewPolyhouseName))
            {
                await _polyhouseRepo.AddPolyhouse(NewPolyhouseName);
            }
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostEditAsync()
        {
            if (EditId > 0 && !string.IsNullOrWhiteSpace(EditPolyhouseName))
            {
                await _polyhouseRepo.UpdatePolyhouse(EditId, EditPolyhouseName);
            }
            return RedirectToPage();
        }
    }
}
