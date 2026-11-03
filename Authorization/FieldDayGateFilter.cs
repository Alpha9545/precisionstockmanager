using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using PlantStockManager.Services.FieldWork;

namespace PlantStockManager.Authorization
{
    // Field Work Phase 1 (2026-11-03): a field employee (a user holding FieldWork.Day -- never an administrator through
    // full access alone) can open ONLY Start Day, the consent notice and Logout until today's day is started. Registered
    // as a global page filter (Program.cs), so it runs after authorization on every Razor Page; users without
    // FieldWork.Day pass straight through without a database call. The rules are FieldDayRules.Gate.
    public sealed class FieldDayGateFilter : IAsyncPageFilter
    {
        public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

        public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
        {
            var user = context.HttpContext.User;
            if (!FieldDayService.IsFieldEmployee(user))
            {
                await next();
                return;
            }

            var page = FeatureAuthorizationConventions.Normalize(context.ActionDescriptor.ViewEnginePath);
            var service = context.HttpContext.RequestServices.GetRequiredService<FieldDayService>();
            switch (await service.GateAsync(user, page))
            {
                case FieldDayRules.GateResult.Consent:
                    context.Result = new RedirectToPageResult("/FieldWork/Consent");
                    return;
                case FieldDayRules.GateResult.StartDay:
                    context.Result = new RedirectToPageResult("/FieldWork/StartDay");
                    return;
                case FieldDayRules.GateResult.FieldHome:
                    context.Result = new RedirectToPageResult("/FieldWork/Index");
                    return;
                default:
                    await next();
                    return;
            }
        }
    }
}
