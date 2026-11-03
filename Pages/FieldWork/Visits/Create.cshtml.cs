using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Services.FieldWork;

namespace PlantStockManager.Pages.FieldWork.Visits
{
    // 2026-11-03: visits are logged on the one-step New Visit form. This old address (bookmarks, older links) checks that the
    // user may log a visit for the customer -- 404 otherwise, as before -- and opens New Visit with the customer preselected.
    public class CreateModel : PageModel
    {
        private readonly FieldCustomerService _customers;

        public CreateModel(FieldCustomerService customers) => _customers = customers;

        [BindProperty(SupportsGet = true)] public int CustomerId { get; set; }

        private async Task<IActionResult> RedirectAsync()
        {
            if (!await _customers.SchemaReadyAsync())
                return RedirectToPage("/FieldWork/Visits/New");
            var c = await _customers.GetVisibleAsync(User, CustomerId);
            if (c == null || !FieldCustomerService.MayWork(User, c))
                return NotFound();
            return RedirectToPage("/FieldWork/Visits/New", new { customerId = CustomerId });
        }

        public Task<IActionResult> OnGetAsync() => RedirectAsync();

        public Task<IActionResult> OnPostAsync() => RedirectAsync();
    }
}
