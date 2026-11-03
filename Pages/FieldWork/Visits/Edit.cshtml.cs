using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Models;
using PlantStockManager.Services.FieldWork;

namespace PlantStockManager.Pages.FieldWork.Visits
{
    // Manager correction of a saved visit: discussed, notes and items only. The check-in (time, GPS, customer, Field Day,
    // photos, status before) never changes; the old values are logged in the customer's change history.
    public class EditModel : PageModel
    {
        private readonly FieldVisitService _visits;

        public EditModel(FieldVisitService visits) => _visits = visits;

        [BindProperty(SupportsGet = true)] public int Id { get; set; }
        [BindProperty] public string? Discussed { get; set; }
        [BindProperty] public string? Notes { get; set; }
        [BindProperty] public List<string?> ItemText { get; set; } = new();
        [BindProperty] public List<string?> ItemQty { get; set; } = new();

        public FieldVisit Visit { get; private set; } = new();
        public FieldCustomer Customer { get; private set; } = new();
        public List<string> Errors { get; } = new();

        private async Task<IActionResult?> LoadAsync(bool fill)
        {
            if (!await _visits.SchemaReadyAsync())
                return NotFound();
            var found = await _visits.GetVisibleAsync(User, Id);
            if (found == null)
                return NotFound();
            (Visit, Customer) = found.Value;
            if (fill)
            {
                Discussed = Visit.Discussed;
                Notes = Visit.Notes;
                ItemText = Visit.Items.Select(i => (string?)i.ItemText).ToList();
                ItemQty = Visit.Items.Select(i => i.Quantity?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).ToList();
            }
            return null;
        }

        public async Task<IActionResult> OnGetAsync() => await LoadAsync(true) ?? Page();

        public async Task<IActionResult> OnPostAsync()
        {
            var denied = await LoadAsync(false);
            if (denied != null)
                return denied;
            var result = await _visits.EditAsync(User, Id, Discussed, Notes, ItemText, ItemQty);
            if (result.Ok)
            {
                TempData["FieldWorkMessage"] = "Visit corrected (old text kept in the change history).";
                return RedirectToPage("/FieldWork/Customers/Details", null, new { id = Customer.Id }, $"visit-{Id}");
            }
            Errors.AddRange(result.Errors);
            return Page();
        }
    }
}
