using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Models;
using PlantStockManager.Services.FieldWork;

namespace PlantStockManager.Pages.FieldWork
{
    // My Day: today's day (End Day button) and the employee's own recent days -- never anyone else's.
    public class IndexModel : PageModel
    {
        private readonly FieldDayService _service;

        public IndexModel(FieldDayService service) => _service = service;

        public bool SchemaReady { get; private set; } = true;
        public FieldDay? Today { get; private set; }
        public List<FieldDay> Recent { get; private set; } = new();
        public string? Message { get; private set; }

        public async Task<IActionResult> OnGetAsync()
        {
            var userId = User.GetUserId();
            if (userId == null)
                return Challenge();
            SchemaReady = await _service.SchemaReadyAsync();
            if (!SchemaReady)
                return Page();
            Message = TempData["FieldWorkMessage"] as string;
            Today = await _service.GetTodayAsync(userId.Value);
            Recent = await _service.ListMyDaysAsync(userId.Value);
            return Page();
        }
    }
}
