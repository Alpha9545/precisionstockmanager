using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Services.FieldWork;

namespace PlantStockManager.Pages.FieldWork.Customers
{
    // Edit the profile fields (assigned employee or manager). Status, assignment and archive are manager actions on
    // Details; GPS and the creation record never change. Every change is logged (dbo.FieldCustomerEvents 'Updated').
    public class EditModel : PageModel
    {
        private readonly FieldCustomerService _service;

        public EditModel(FieldCustomerService service) => _service = service;

        [BindProperty(SupportsGet = true)] public int Id { get; set; }
        [BindProperty] public string? Name { get; set; }
        [BindProperty] public string? ContactPerson { get; set; }
        [BindProperty] public string? Phone { get; set; }
        [BindProperty] public string? AlternatePhone { get; set; }
        [BindProperty] public string? CustomerType { get; set; }
        [BindProperty] public string? VillageCity { get; set; }
        [BindProperty] public string? District { get; set; }
        [BindProperty] public string? Address { get; set; }
        [BindProperty] public bool ConfirmDuplicate { get; set; }

        public bool SchemaReady { get; private set; } = true;
        public string CustomerName { get; private set; } = "";
        public List<string> Errors { get; } = new();
        public bool HasDuplicates { get; private set; }
        public List<string> DistrictSuggestions { get; private set; } = new();

        private async Task<IActionResult?> LoadAsync(bool fillFields)
        {
            SchemaReady = await _service.SchemaReadyAsync();
            if (!SchemaReady)
                return Page();
            var c = await _service.GetVisibleAsync(User, Id);
            if (c == null || !FieldCustomerService.MayEdit(User, c))
                return NotFound();
            CustomerName = c.Name;
            DistrictSuggestions = await _service.AllDistrictsAsync();
            if (fillFields)
                (Name, ContactPerson, Phone, AlternatePhone, CustomerType, VillageCity, District, Address)
                    = (c.Name, c.ContactPerson, c.Phone, c.AlternatePhone, c.CustomerType, c.VillageCity, c.District, c.Address);
            return null;
        }

        public async Task<IActionResult> OnGetAsync() => await LoadAsync(true) ?? Page();

        public async Task<IActionResult> OnGetCheckPhoneAsync(string? phone, string? alt)
        {
            if (!await _service.SchemaReadyAsync() || await _service.GetVisibleAsync(User, Id) == null)
                return NotFound();
            var matches = await _service.FindDuplicatesAsync(phone, alt, Id);
            return new JsonResult(new { warning = matches.Count > 0 ? FieldCustomerRules.DuplicateWarning(matches) : null });
        }

        public async Task<IActionResult> OnPostAsync()
        {
            var denied = await LoadAsync(false);
            if (denied != null)
                return denied;
            var result = await _service.UpdateProfileAsync(User, Id,
                new FieldCustomerRules.CustomerInput(Name, ContactPerson, Phone, AlternatePhone, CustomerType, VillageCity, District, Address),
                ConfirmDuplicate);
            if (result.Ok)
            {
                TempData["FieldWorkMessage"] = "Customer updated.";
                return RedirectToPage("/FieldWork/Customers/Details", new { id = Id });
            }
            Errors.AddRange(result.Errors);
            HasDuplicates = result.NeedsDuplicateConfirmation;
            return Page();
        }
    }
}
