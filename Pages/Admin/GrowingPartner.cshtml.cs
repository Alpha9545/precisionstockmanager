using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Admin
{
    public class GrowingPartnerModel : PageModel
    {
        private readonly GrowingPartnerRepository _growingPartnerRepo;

        public GrowingPartnerModel(GrowingPartnerRepository growingPartnerRepo)
        {
            _growingPartnerRepo = growingPartnerRepo;
        }

        public List<GrowingPartner> GrowingPartners { get; set; } = new();

        [BindProperty]
        public GrowingPartner NewGrowingPartner { get; set; } = new();

        [BindProperty]
        public GrowingPartner EditGrowingPartner { get; set; } = new();

        public async Task OnGetAsync()
        {
            GrowingPartners = await _growingPartnerRepo.GetAllAsync();
        }

        public async Task<IActionResult> OnPostAddAsync()
        {
            if (string.IsNullOrWhiteSpace(NewGrowingPartner.Name))
                ModelState.AddModelError("NewGrowingPartner.Name", "Growing Partner Name is required.");

            if (!ModelState.IsValid)
            {
                GrowingPartners = await _growingPartnerRepo.GetAllAsync();
                return Page();
            }

            NewGrowingPartner.CreatedBy = User.Identity?.Name ?? "System";
            var (success, message, _) = await _growingPartnerRepo.InsertAsync(NewGrowingPartner);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to add Growing Partner.");
                GrowingPartners = await _growingPartnerRepo.GetAllAsync();
                return Page();
            }

            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostEditAsync()
        {
            if (EditGrowingPartner.Id <= 0)
                ModelState.AddModelError("EditGrowingPartner.Id", "Invalid Growing Partner.");
            if (string.IsNullOrWhiteSpace(EditGrowingPartner.Name))
                ModelState.AddModelError("EditGrowingPartner.Name", "Growing Partner Name is required.");

            if (!ModelState.IsValid)
            {
                GrowingPartners = await _growingPartnerRepo.GetAllAsync();
                return Page();
            }

            EditGrowingPartner.ModifiedBy = User.Identity?.Name ?? "System";
            var (success, message) = await _growingPartnerRepo.UpdateAsync(EditGrowingPartner);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to update Growing Partner.");
                GrowingPartners = await _growingPartnerRepo.GetAllAsync();
                return Page();
            }

            return RedirectToPage();
        }
    }
}
