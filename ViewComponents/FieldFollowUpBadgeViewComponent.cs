using Microsoft.AspNetCore.Mvc;
using PlantStockManager.Services.FieldWork;

namespace PlantStockManager.ViewComponents
{
    // Field Work Phase 2 (2026-11-03): navbar badge with the field employee's due-today + overdue follow-ups. Renders
    // nothing (no database call) for anyone who is not a field employee, and nothing when the Phase 2 migration is missing.
    // A failure is logged and hidden -- the badge must never break a page.
    public class FieldFollowUpBadgeViewComponent : ViewComponent
    {
        private readonly FieldCustomerService _service;
        private readonly ILogger<FieldFollowUpBadgeViewComponent> _logger;

        public FieldFollowUpBadgeViewComponent(FieldCustomerService service, ILogger<FieldFollowUpBadgeViewComponent> logger)
        {
            _service = service;
            _logger = logger;
        }

        public async Task<IViewComponentResult> InvokeAsync()
        {
            if (!FieldDayService.IsFieldEmployee(UserClaimsPrincipal))
                return Content(string.Empty);
            try
            {
                var counts = await _service.CountDueAsync(UserClaimsPrincipal);
                return View(counts);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Field Work follow-up badge could not be loaded.");
                return Content(string.Empty);
            }
        }
    }
}
