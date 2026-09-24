using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PlantStockManager.Pages.Production.SeedIssue
{
    // Phase B: Seed Issue is RETIRED from the user workflow. Sowing now
    // consumes Main Office Seed Stock directly (Production/SeedSowing/Create),
    // so no new Seed Issue can be created. This page only explains that.
    // The dbo.SeedIssues table and every historical record are preserved
    // (read-only history: Production/SeedIssue/MyIssues). Any issue that
    // was still 'PendingConfirmation' when Phase B went live can be
    // confirmed/rejected by a System Administrator on PendingReceipts, so
    // the seed held InTransit is released.
    // SeedIssueRepository.InsertAsync also refuses (defense in depth).
    public class CreateModel : PageModel
    {
        public void OnGet()
        {
        }

        public IActionResult OnPost() => Refuse();

        public IActionResult OnPostSend() => Refuse();

        private IActionResult Refuse()
        {
            TempData["Error"] = "Seed Issue has been retired. Record sowing directly from Main Office Seed Stock (Production > Direct Sowing).";
            return RedirectToPage("/Production/SeedIssue/Create");
        }
    }
}
