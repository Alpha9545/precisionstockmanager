using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.ManagementDashboard
{
    // Daily Operations Report: the day's nursery operations and software
    // activity for ANY India date, as a printable / copyable text report.
    //
    // AUTHORIZATION: System Administrator only -- the explicit FullAccessOnly
    // rule for this page in Authorization/FeatureAuthorizationConventions.cs,
    // enforced server-side for EVERY handler of this page (OnGetAsync and the
    // OnGetTextAsync .txt export alike), not just by hiding the dashboard link.
    // No new role or permission exists for it.
    //
    // Read-only: every handler is a GET and nothing here writes. Figures,
    // definitions and historical limits: docs/DAILY_OPERATIONS_REPORT.md.
    public class DailyOperationsReportModel : PageModel
    {
        private readonly DailyOperationsReportRepository _repo;

        public DailyOperationsReportModel(DailyOperationsReportRepository repo)
        {
            _repo = repo;
        }

        // The report date (India). Missing / future / invalid -> today.
        [BindProperty(SupportsGet = true)]
        public DateTime? Date { get; set; }

        // History list range (defaults to the 14 days ending on Date).
        [BindProperty(SupportsGet = true)]
        public DateTime? From { get; set; }

        [BindProperty(SupportsGet = true)]
        public DateTime? To { get; set; }

        // Show every day of the range as a full report (at most MaxFullReportDays).
        [BindProperty(SupportsGet = true)]
        public bool ShowAll { get; set; }

        public DateTime Today { get; private set; }
        public DateTime ReportDate { get; private set; }
        public DateTime HistoryFrom { get; private set; }
        public DateTime HistoryTo { get; private set; }
        public DateTime? PreviousDate { get; private set; }
        public DateTime? NextDate { get; private set; }
        public List<string> Notices { get; } = new();

        public DailyOperationsReport Report { get; private set; } = new();
        public string ReportText { get; private set; } = string.Empty;
        public List<DailyOperationsReport> History { get; private set; } = new();   // newest first
        public List<DailyOperationsReport> FullReports { get; private set; } = new(); // oldest first (ShowAll)
        public string FullReportsText { get; private set; } = string.Empty;

        public async Task OnGetAsync()
        {
            var now = AppClock.Now;
            Today = now.Date;

            var (date, dateNotice) = DailyOperationsReportRules.ResolveDate(Date, Today);
            ReportDate = date;
            if (dateNotice != null) Notices.Add(dateNotice);

            var (from, to, rangeNotice) = DailyOperationsReportRules.ResolveRange(From, To, ReportDate, Today);
            HistoryFrom = from;
            HistoryTo = to;
            if (rangeNotice != null) Notices.Add(rangeNotice);

            PreviousDate = DailyOperationsReportRules.PreviousDay(ReportDate);
            NextDate = DailyOperationsReportRules.NextDay(ReportDate, Today);

            // One batched query for the history range; a second only when the
            // report date lies outside it.
            var history = await _repo.GetAsync(HistoryFrom, HistoryTo);
            var all = DailyOperationsReportRules.BuildAll(history, Today, now);
            History = all.OrderByDescending(r => r.ReportDate).ToList();

            Report = ReportDate >= HistoryFrom && ReportDate <= HistoryTo
                ? all.Single(r => r.ReportDate == ReportDate)
                : DailyOperationsReportRules.Build(await _repo.GetAsync(ReportDate, ReportDate), ReportDate, Today, now);
            ReportText = DailyOperationsReportRules.ToPlainText(Report);

            if (ShowAll)
            {
                FullReports = all.OrderByDescending(r => r.ReportDate).Take(DailyOperationsReportRules.MaxFullReportDays).OrderBy(r => r.ReportDate).ToList();
                if (all.Count > FullReports.Count)
                    Notices.Add($"Full reports are limited to the latest {DailyOperationsReportRules.MaxFullReportDays} days of the selected range.");
                FullReportsText = DailyOperationsReportRules.ToPlainText(FullReports);
            }
        }

        // Plain-text export of one day's report (the same text Copy Report copies).
        public async Task<IActionResult> OnGetTextAsync()
        {
            var now = AppClock.Now;
            var (date, _) = DailyOperationsReportRules.ResolveDate(Date, now.Date);
            var report = DailyOperationsReportRules.Build(await _repo.GetAsync(date, date), date, now.Date, now);
            var bytes = Encoding.UTF8.GetBytes(DailyOperationsReportRules.ToPlainText(report));
            return File(bytes, "text/plain; charset=utf-8", $"daily-operations-{date:yyyy-MM-dd}.txt");
        }

        // Route values for links that keep the current history range.
        public Dictionary<string, string> RouteFor(DateTime date, bool? showAll = null) => new()
        {
            ["Date"] = date.ToString("yyyy-MM-dd"),
            ["From"] = HistoryFrom.ToString("yyyy-MM-dd"),
            ["To"] = HistoryTo.ToString("yyyy-MM-dd"),
            ["ShowAll"] = (showAll ?? ShowAll) ? "true" : "false",
        };
    }
}
