using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PlantStockManager.Pages.Account
{
    // F1: the cookie options have always pointed AccessDeniedPath at
    // "/Account/AccessDenied", but the page did not exist, so every
    // permission denial ended in a 404. This page is AllowAnonymous (see
    // Program.cs) and deliberately reveals nothing about the resource that
    // was refused. ReturnUrl is intentionally ignored (never echoed or
    // redirected to) to avoid an open redirect.
    public class AccessDeniedModel : PageModel
    {
        public void OnGet()
        {
            Response.StatusCode = 403;
        }
    }
}
