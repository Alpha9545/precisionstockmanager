using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace PlantStockManager.Pages.Admin
{
    public class SeedsourceModel : PageModel
    {
        private readonly SeedSourcesRepository _seedSourcesRepository;

        public SeedsourceModel(SeedSourcesRepository seedSourcesRepository)
        {
            _seedSourcesRepository = seedSourcesRepository;
        }

        public List<SeedSource> SeedSources { get; set; } = new();

        [BindProperty]
        public string NewSeedSourceName { get; set; }

        [BindProperty]
        public int EditId { get; set; }

        [BindProperty]
        public string EditSeedSourceName { get; set; }

        public async Task OnGetAsync()
        {
            SeedSources = await _seedSourcesRepository.GetAllSeedSources();
        }

        public async Task<IActionResult> OnPostAddAsync()
        {
            if (!string.IsNullOrWhiteSpace(NewSeedSourceName))
            {
                await _seedSourcesRepository.AddSeedSource(NewSeedSourceName);
            }
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostEditAsync()
        {
            if (EditId > 0 && !string.IsNullOrWhiteSpace(EditSeedSourceName))
            {
                await _seedSourcesRepository.UpdateSeedSource(EditId, EditSeedSourceName);
            }
            return RedirectToPage();
        }
    }
}
