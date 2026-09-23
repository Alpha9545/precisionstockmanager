using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Admin
{
    public class VendorModel : PageModel
    {
        private readonly VendorRepository _vendorRepo;

        public VendorModel(VendorRepository vendorRepo)
        {
            _vendorRepo = vendorRepo;
        }

        public List<Vendor> Vendors { get; set; } = new();

        [BindProperty]
        public Vendor NewVendor { get; set; } = new();

        [BindProperty]
        public Vendor EditVendor { get; set; } = new();

        public async Task OnGetAsync()
        {
            Vendors = await _vendorRepo.GetAllAsync();
        }

        public async Task<IActionResult> OnPostAddAsync()
        {
            if (string.IsNullOrWhiteSpace(NewVendor.Name))
                ModelState.AddModelError("NewVendor.Name", "Vendor Name is required.");

            if (!ModelState.IsValid)
            {
                Vendors = await _vendorRepo.GetAllAsync();
                return Page();
            }

            NewVendor.CreatedBy = User.Identity?.Name ?? "System";
            var (success, message, _) = await _vendorRepo.InsertAsync(NewVendor);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to add Vendor.");
                Vendors = await _vendorRepo.GetAllAsync();
                return Page();
            }

            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostEditAsync()
        {
            if (EditVendor.Id <= 0)
                ModelState.AddModelError("EditVendor.Id", "Invalid Vendor.");
            if (string.IsNullOrWhiteSpace(EditVendor.Name))
                ModelState.AddModelError("EditVendor.Name", "Vendor Name is required.");

            if (!ModelState.IsValid)
            {
                Vendors = await _vendorRepo.GetAllAsync();
                return Page();
            }

            EditVendor.ModifiedBy = User.Identity?.Name ?? "System";
            var (success, message) = await _vendorRepo.UpdateAsync(EditVendor);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to update Vendor.");
                Vendors = await _vendorRepo.GetAllAsync();
                return Page();
            }

            return RedirectToPage();
        }
    }
}
