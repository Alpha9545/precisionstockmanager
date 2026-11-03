using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using PlantStockManager.Authorization;
using PlantStockManager.Models;
using PlantStockManager.Services.FieldWork;

namespace PlantStockManager.Pages.FieldWork
{
    // End Day: the same reading as Start Day (end km >= start km). After saving, the employee is signed out.
    public class EndDayModel : FieldWorkReadingPageModel
    {
        public EndDayModel(FieldDayService service) : base(service) { }

        public FieldDay? Today { get; private set; }

        public async Task<IActionResult> OnGetAsync()
        {
            var userId = User.GetUserId();
            if (userId == null)
                return Challenge();
            SchemaReady = await Service.SchemaReadyAsync();
            if (!SchemaReady)
                return Page();
            Today = await Service.GetTodayAsync(userId.Value);
            if (Today?.Status != FieldDayRules.StatusOpen)
                return RedirectToPage("/FieldWork/StartDay");
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

            var request = await ReadRequestAsync();
            if (Errors.Count == 0)
            {
                var result = await Service.EndDayAsync(userId.Value, request);
                if (result.Ok)
                {
                    await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    return RedirectToPage("/Account/Logout");
                }
                Errors.AddRange(result.Errors);
            }

            Today = await Service.GetTodayAsync(userId.Value);
            if (Today?.Status != FieldDayRules.StatusOpen)
                return RedirectToPage("/FieldWork/StartDay");
            NewToken();
            return Page();
        }
    }
}
