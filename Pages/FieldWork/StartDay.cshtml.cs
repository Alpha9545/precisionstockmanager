using Microsoft.AspNetCore.Mvc;
using PlantStockManager.Authorization;
using PlantStockManager.Models;
using PlantStockManager.Services.FieldWork;

namespace PlantStockManager.Pages.FieldWork
{
    // Start Day: live meter photo, GPS, km (and the vehicle type only on a database without
    // 2026-11-03_FieldWorkVehicleOptional.sql). The day's date and time are the server's (IST date, UTC time).
    public class StartDayModel : FieldWorkReadingPageModel
    {
        public StartDayModel(FieldDayService service) : base(service) { }

        // only asked on a database without 2026-11-03_FieldWorkVehicleOptional.sql
        [BindProperty] public string? VehicleType { get; set; }
        public bool VehicleRequired { get; private set; }

        // today's day when it is already closed (then no new start is offered -- one day per date)
        public FieldDay? ClosedToday { get; private set; }

        public async Task<IActionResult> OnGetAsync()
        {
            var userId = User.GetUserId();
            if (userId == null)
                return Challenge();
            SchemaReady = await Service.SchemaReadyAsync();
            if (!SchemaReady)
                return Page();
            VehicleRequired = await Service.VehicleRequiredAsync();
            if (!await Service.HasConsentAsync(userId.Value))
                return RedirectToPage("/FieldWork/Consent");
            var today = await Service.GetTodayAsync(userId.Value);
            if (today?.Status == FieldDayRules.StatusOpen)
                return RedirectToPage("/FieldWork/Index");
            ClosedToday = today;
            NewToken();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            var userId = User.GetUserId();
            if (userId == null)
                return Challenge();
            SchemaReady = await Service.SchemaReadyAsync();
            if (!SchemaReady)
                return Page();
            VehicleRequired = await Service.VehicleRequiredAsync();

            var request = await ReadRequestAsync();
            if (Errors.Count == 0)
            {
                var result = await Service.StartDayAsync(userId.Value, VehicleType, request);
                if (result.Ok)
                {
                    TempData["FieldWorkMessage"] = "Your day has started. Have a good day!";
                    return RedirectToPage("/FieldWork/Index");
                }
                Errors.AddRange(result.Errors);
            }

            var today = await Service.GetTodayAsync(userId.Value);
            if (today?.Status == FieldDayRules.StatusOpen)
                return RedirectToPage("/FieldWork/Index");
            ClosedToday = today;
            NewToken();
            return Page();
        }
    }
}
