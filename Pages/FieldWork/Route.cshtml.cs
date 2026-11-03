using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Models;
using PlantStockManager.Services;
using PlantStockManager.Services.FieldWork;

namespace PlantStockManager.Pages.FieldWork
{
    // My route (employee: own days only) / any employee's route (manager: employee + date). ?dayId= opens one day directly.
    // An employee asking for someone else's day or employee id gets 404 -- the same answer as a day that does not exist.
    public class RouteModel : PageModel
    {
        private readonly FieldRouteService _routes;
        private readonly FieldDayService _days;

        public RouteModel(FieldRouteService routes, FieldDayService days)
        {
            _routes = routes;
            _days = days;
        }

        [BindProperty(SupportsGet = true)] public DateTime? Date { get; set; }
        [BindProperty(SupportsGet = true)] public int? DayId { get; set; }
        [BindProperty(SupportsGet = true)] public int? EmployeeId { get; set; }

        public bool SchemaReady { get; private set; } = true;
        public bool CanManage { get; private set; }
        public FieldRouteService.RouteView? View { get; private set; }
        public DateTime ShownDate { get; private set; }
        public List<FieldEmployeeOption> Employees { get; private set; } = new();
        public List<FieldDay> MyRecentDays { get; private set; } = new();
        public FieldWorkOptions Options => _routes.Options;

        public async Task<IActionResult> OnGetAsync()
        {
            SchemaReady = await _routes.SchemaReadyAsync();
            if (!SchemaReady)
                return Page();
            CanManage = FieldDayService.MayManage(User);
            var me = User.GetUserId();
            if (me == null)
                return Challenge();

            if (DayId.HasValue)
            {
                View = await _routes.GetRouteAsync(User, DayId.Value);
                if (View == null)
                    return NotFound();
                ShownDate = View.Day.WorkDate;
                EmployeeId = View.Day.UserId;
            }
            else
            {
                ShownDate = (Date ?? AppClock.Today).Date;
                if (ShownDate > AppClock.Today)
                    ShownDate = AppClock.Today;
                var (allowed, view) = await _routes.GetRouteForDateAsync(User, CanManage ? EmployeeId : (EmployeeId == me ? null : EmployeeId), ShownDate);
                if (!allowed)
                    return NotFound();
                View = view;
            }
            if (CanManage)
                Employees = await _days.ListFieldEmployeesAsync();
            else
                MyRecentDays = await _days.ListMyDaysAsync(me.Value, 60);
            return Page();
        }
    }
}
