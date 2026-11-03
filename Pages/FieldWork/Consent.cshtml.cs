using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Services.FieldWork;

namespace PlantStockManager.Pages.FieldWork
{
    // Shown once (per FieldWork:ConsentVersion) before the first Start Day: location, time and photos are recorded for
    // work purposes. Nothing is bound -- accepting records the signed-in user only.
    public class ConsentModel : PageModel
    {
        private readonly FieldDayService _service;

        public ConsentModel(FieldDayService service) => _service = service;

        public bool SchemaReady { get; private set; } = true;

        public async Task<IActionResult> OnGetAsync()
        {
            SchemaReady = await _service.SchemaReadyAsync();
            var userId = User.GetUserId();
            if (SchemaReady && userId != null && await _service.HasConsentAsync(userId.Value))
                return RedirectToPage("/FieldWork/StartDay");
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            SchemaReady = await _service.SchemaReadyAsync();
            var userId = User.GetUserId();
            if (!SchemaReady || userId == null)
                return Page();
            await _service.RecordConsentAsync(userId.Value);
            return RedirectToPage("/FieldWork/StartDay");
        }
    }
}
