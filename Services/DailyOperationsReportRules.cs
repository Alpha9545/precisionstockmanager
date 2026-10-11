using System.Globalization;
using System.Text;
using PlantStockManager.Models;

namespace PlantStockManager.Services
{
    // Daily Operations Report -- every rule that does not need the database:
    // turning the range data (Data/DailyOperationsReportRepository.cs) into one
    // report per India day, the "available vs. no records" decision, the
    // employee categories, Indian number grouping and the plain-text (WhatsApp)
    // layout. Pure and static so each rule is unit-tested without a database.
    // Definitions: docs/DAILY_OPERATIONS_REPORT.md.
    public static class DailyOperationsReportRules
    {
        public const int MaxHistoryDays = 62;
        public const int MaxFullReportDays = 31;
        public const string NotAvailable = "Not available";
        public const string NoNames = "None";
        public static readonly DateTime EarliestSelectableDate = new(2000, 1, 1);

        // ---- Dates -------------------------------------------------------

        // The report date to show: a missing, too-old or future date becomes
        // today (India). Returns a notice when the requested date was changed.
        public static (DateTime Date, string? Notice) ResolveDate(DateTime? requested, DateTime todayIndia)
        {
            var today = todayIndia.Date;
            if (!requested.HasValue)
                return (today, null);
            var d = requested.Value.Date;
            if (d > today)
                return (today, $"{d:yyyy-MM-dd} is in the future; showing today ({today:yyyy-MM-dd}) instead.");
            if (d < EarliestSelectableDate)
                return (today, $"{d:yyyy-MM-dd} is not a valid report date; showing today instead.");
            return (d, null);
        }

        // History range: defaults to the 14 days ending on the report date,
        // never in the future, never longer than MaxHistoryDays.
        public static (DateTime From, DateTime To, string? Notice) ResolveRange(DateTime? from, DateTime? to, DateTime reportDate, DateTime todayIndia)
        {
            var today = todayIndia.Date;
            var t = (to ?? reportDate).Date;
            if (t > today) t = today;
            if (t < EarliestSelectableDate) t = today;
            var f = (from ?? t.AddDays(-13)).Date;
            if (f < EarliestSelectableDate) f = EarliestSelectableDate;
            if (f > t) (f, t) = (t, f);
            string? notice = null;
            if ((t - f).TotalDays + 1 > MaxHistoryDays)
            {
                f = t.AddDays(-(MaxHistoryDays - 1));
                notice = $"The history list is limited to {MaxHistoryDays} days; showing {f:yyyy-MM-dd} to {t:yyyy-MM-dd}.";
            }
            return (f, t, notice);
        }

        public static DateTime? PreviousDay(DateTime reportDate)
            => reportDate.Date > EarliestSelectableDate ? reportDate.Date.AddDays(-1) : null;

        public static DateTime? NextDay(DateTime reportDate, DateTime todayIndia)
            => reportDate.Date < todayIndia.Date ? reportDate.Date.AddDays(1) : null;

        // ---- Numbers -----------------------------------------------------

        // Indian digit grouping: 1,02,546 / 12,34,56,789. Up to `maxDecimals`
        // places, trailing zeroes dropped (DECIMAL(18,2) 100.00 -> "100").
        public static string Indian(decimal value, int maxDecimals = 2)
        {
            var rounded = Math.Round(value, maxDecimals, MidpointRounding.AwayFromZero);
            var negative = rounded < 0;
            var abs = Math.Abs(rounded);
            var whole = decimal.Truncate(abs);
            var fraction = abs - whole;

            var digits = whole.ToString("0", CultureInfo.InvariantCulture);
            var sb = new StringBuilder();
            if (digits.Length <= 3)
                sb.Append(digits);
            else
            {
                var head = digits[..^3];
                var firstGroup = head.Length % 2;
                if (firstGroup > 0) sb.Append(head[..firstGroup]);
                for (var i = firstGroup; i < head.Length; i += 2)
                {
                    if (sb.Length > 0) sb.Append(',');
                    sb.Append(head.AsSpan(i, 2));
                }
                sb.Append(',').Append(digits[^3..]);
            }
            if (fraction > 0)
            {
                var frac = fraction.ToString("0." + new string('#', maxDecimals), CultureInfo.InvariantCulture);
                sb.Append(frac[1..]); // ".5"
            }
            return (negative ? "-" : "") + sb;
        }

        public static string Indian(int value) => Indian((decimal)value);

        // ---- Building one day's report --------------------------------------

        public static List<DailyOperationsReport> BuildAll(DailyOperationsSourceData data, DateTime todayIndia, DateTime nowIndia)
        {
            var list = new List<DailyOperationsReport>();
            for (var d = data.From.Date; d <= data.To.Date; d = d.AddDays(1))
                list.Add(Build(data, d, todayIndia, nowIndia));
            return list;
        }

        public static DailyOperationsReport Build(DailyOperationsSourceData data, DateTime day, DateTime todayIndia, DateTime nowIndia)
        {
            day = day.Date;
            if (day < data.From.Date || day > data.To.Date)
                throw new ArgumentOutOfRangeException(nameof(day), $"{day:yyyy-MM-dd} is outside the loaded range {data.From:yyyy-MM-dd}..{data.To:yyyy-MM-dd}.");

            var c = data.Coverage;
            var report = new DailyOperationsReport { ReportDate = day, TodayIndia = todayIndia.Date, GeneratedAtIndia = nowIndia };

            // Sowing
            var sowing = data.Sowing.Where(f => f.Date == day).ToList();
            report.Sowing = Figure(day, c.SowingFrom, "sowing", sowing);
            report.SeedSowingQuantity = sowing.Where(f => !string.Equals(f.Key, "Cutting", StringComparison.OrdinalIgnoreCase)).Sum(f => f.Quantity);
            report.CuttingSowingQuantity = sowing.Where(f => string.Equals(f.Key, "Cutting", StringComparison.OrdinalIgnoreCase)).Sum(f => f.Quantity);

            BuildReadyStock(report, data, day);

            report.SeedlingBookings = Figure(day, c.SeedlingBookingsFrom, "seedling booking", data.SeedlingBookings.Where(f => f.Date == day));
            report.PottedBookings = Figure(day, c.PottedBookingsFrom, "potted plant booking", data.PottedBookings.Where(f => f.Date == day));
            report.SeedlingDispatch = Figure(day, c.SeedlingDispatchFrom, "seedling dispatch", data.SeedlingDispatches.Where(f => f.Date == day));
            report.PottedDispatch = Figure(day, c.PottedDispatchFrom, "potted plant dispatch", data.PottedDispatches.Where(f => f.Date == day));
            report.OutletSales = Figure(day, c.OutletSalesFrom, "outlet sale", data.OutletSales.Where(f => f.Date == day));

            var fertilizer = data.Fertilizer.Where(f => f.Date == day).ToList();
            report.Fertilizer = Figure(day, c.FertilizerFrom, "fertilizer issue", fertilizer);
            report.FertilizerByUnit = ByUnit(fertilizer);

            report.Wastage = Figure(day, c.WastageFrom, "production / stock", data.Wastage.Where(f => f.Date == day));
            report.SeedLotWastage = report.Wastage.Available ? ByUnit(data.SeedLotWastage.Where(f => f.Date == day)) : new();

            BuildSoftwareActivity(report, data, day);

            if (report.IsToday)
                report.Notes.Insert(0, $"Today's report is live: figures up to {nowIndia:HH:mm} IST and will change until the day ends.");
            return report;
        }

        // A figure is established only from the first day its source holds any
        // record: before that the system cannot tell "nothing happened" from
        // "not recorded in this system yet", so it is reported as not available.
        private static ReportFigure Figure(DateTime day, DateTime? recordsFrom, string what, IEnumerable<DayFigure> rows)
        {
            var reason = CoverageGap(day, recordsFrom, what);
            if (reason != null)
                return ReportFigure.NotAvailable(reason);
            var list = rows as ICollection<DayFigure> ?? rows.ToList();
            return new ReportFigure
            {
                Available = true,
                Count = list.Sum(f => f.Count),
                Quantity = list.Sum(f => f.Quantity),
                Lines = list.Sum(f => f.Lines),
            };
        }

        private static string? CoverageGap(DateTime day, DateTime? recordsFrom, string what)
        {
            if (!recordsFrom.HasValue)
                return $"No {what} records exist in the system yet.";
            if (day < recordsFrom.Value.Date)
                return $"No {what} records exist before {recordsFrom.Value:yyyy-MM-dd}.";
            return null;
        }

        private static List<UnitQuantityLine> ByUnit(IEnumerable<DayFigure> rows)
            => rows.GroupBy(f => string.IsNullOrWhiteSpace(f.Key) ? "(no unit)" : f.Key!.Trim(), StringComparer.OrdinalIgnoreCase)
                   .Select(g => new UnitQuantityLine(g.Key, g.Sum(f => f.Quantity)))
                   .Where(u => u.Quantity != 0)
                   .OrderByDescending(u => u.Quantity).ThenBy(u => u.Unit, StringComparer.OrdinalIgnoreCase)
                   .ToList();

        // Ready Stock at the END of the India day (or "now" for today), per Plant
        // Type: the ledger's opening balance plus that Plant Type's movements up to
        // and including the day, separately for owned, reserved and in-transit plants
        // (StockKind); physical and available follow (ReadyStockLine). Movements are
        // dated by their business date where the operation has one (2026-10-10,
        // Services/LedgerDates.cs); older rows by when they were recorded -- the days
        // that affects are listed in the report's notes (RecordingLags). Exact when the
        // ledger explains every current balance (Coverage.ReadyStockUnreconciledBatches
        // == 0); otherwise only today's figure can be given (from the current table) and
        // past days are reported as not reconstructable -- never today's stock under a
        // past date.
        private static void BuildReadyStock(DailyOperationsReport report, DailyOperationsSourceData data, DateTime day)
        {
            var c = data.Coverage;
            var names = data.PlantTypes.ToDictionary(p => p.Id, p => p.Name);
            string Name(int id) => names.TryGetValue(id, out var n) ? n : $"Plant Type {id}";

            if (c.ReadyStockUnreconciledBatches > 0)
            {
                var reason = $"The Ready Stock ledger does not fully explain the current balance of {c.ReadyStockUnreconciledBatches} batch(es), so the stock position for a past date cannot be reconstructed.";
                if (report.IsToday)
                {
                    report.ReadyStockAvailable = true;
                    report.ReadyStockBasis = "Current balance (Ready Stock table)";
                    report.ReadyStock = Lines(data.ReadyStockCurrent, Name);
                    report.Notes.Add("Ready Stock: " + reason + " Today's figure is the current balance.");
                }
                else
                {
                    report.ReadyStockAvailable = false;
                    report.ReadyStockUnavailableReason = reason;
                }
                return;
            }

            var gap = CoverageGap(day, c.ReadyStockFrom, "Ready Stock");
            if (gap != null)
            {
                report.ReadyStockAvailable = false;
                report.ReadyStockUnavailableReason = gap;
                return;
            }

            var balances = data.ReadyStockMovements
                .Where(m => m.Date == null || m.Date <= day)
                .GroupBy(m => m.PlantTypeId)
                .Select(g => new ReadyStockBalance(g.Key,
                    g.Where(m => m.Kind == StockKind.Owned).Sum(m => m.Quantity),
                    g.Where(m => m.Kind == StockKind.Reserved).Sum(m => m.Quantity),
                    g.Where(m => m.Kind == StockKind.InTransit).Sum(m => m.Quantity)));
            report.ReadyStockAvailable = true;
            report.ReadyStockBasis = report.IsToday ? "As at now (ledger)" : $"As at end of {day:yyyy-MM-dd} (ledger)";
            report.ReadyStock = Lines(balances, Name);
            report.Notes.AddRange(RecordingLagNotes(data.RecordingLags, day));
            report.Notes.AddRange(ReconstructionNotes(data.ReadyStockDatingIssues, report.ReadyStock, day, Name));
        }

        // 2026-10-11: say plainly when a past day's Ready Stock for a Plant Type cannot be relied on:
        // (a) ledger rows dated before their batch first held stock (ReadyStockDatingIssue), and
        // (b) as a safety net, any figure that rebuilds to a negative number. Figures are still shown
        // (they are what the ledger says) -- never replaced by today's stock.
        public static List<string> ReconstructionNotes(IEnumerable<ReadyStockDatingIssue> issues, IEnumerable<ReadyStockLine> lines,
            DateTime day, Func<int, string> name)
        {
            var notes = new List<string>();
            foreach (var g in issues.Where(i => i.BusinessDate.Date <= day && day < i.BatchStartDate.Date).GroupBy(i => i.PlantTypeId))
                notes.Add($"Ready Stock: {name(g.Key)} on this date cannot be reliably reconstructed -- {Indian(g.Sum(i => i.Plants))} plants of movements are dated "
                    + $"before their batch was approved Ready ({string.Join(", ", g.Select(i => i.BatchStartDate.ToString("yyyy-MM-dd")).Distinct())}).");
            foreach (var l in lines.Where(l => l.Owned < 0 || l.Reserved < 0 || l.InTransit < 0 || l.Physical < 0 || l.Available < 0))
                notes.Add($"Ready Stock: {l.PlantTypeName} rebuilds to a negative figure on this date (owned {Indian(l.Owned)}, reserved {Indian(l.Reserved)}, "
                    + $"in transit {Indian(l.InTransit)}) -- its history for this date cannot be reliably reconstructed.");
            return notes;
        }

        private static List<ReadyStockLine> Lines(IEnumerable<ReadyStockBalance> balances, Func<int, string> name)
            => balances.Where(b => b.Owned != 0 || b.Reserved != 0 || b.InTransit != 0)
                       .Select(b => new ReadyStockLine(name(b.PlantTypeId), b.Owned, b.Reserved, b.InTransit))
                       .OrderByDescending(l => l.Available).ThenBy(l => l.PlantTypeName, StringComparer.OrdinalIgnoreCase)
                       .ToList();

        // Older movements recorded on a different day from their business date: the day's
        // Ready Stock shows them on the RECORDED day. Stated plainly, never "corrected".
        public static List<string> RecordingLagNotes(IEnumerable<RecordingLag> lags, DateTime day)
        {
            var notes = new List<string>();
            foreach (var g in lags.Where(l => l.BusinessDate.Date == day && l.RecordedDate.Date != day).GroupBy(l => l.What))
                notes.Add($"Ready Stock: {Indian(g.Sum(l => l.Plants))} plants of {g.Key} dated {day:yyyy-MM-dd} were recorded later "
                    + $"({string.Join(", ", g.Select(l => l.RecordedDate.ToString("yyyy-MM-dd")).Distinct())}), before business dates were stored, so they are counted on the day they were recorded, not on this date.");
            foreach (var g in lags.Where(l => l.RecordedDate.Date == day && l.BusinessDate.Date != day).GroupBy(l => l.What))
                notes.Add($"Ready Stock: {Indian(g.Sum(l => l.Plants))} plants of {g.Key} recorded on this date belong to earlier dates "
                    + $"({string.Join(", ", g.Select(l => l.BusinessDate.ToString("yyyy-MM-dd")).Distinct())}) and are counted here, as recorded.");
            return notes;
        }

        // Employee categories (a partition of Total Employees):
        //   Logged In            = >= 1 successful login recorded that India day
        //     Active Users       = Logged In AND >= 1 qualifying recorded activity that day
        //     No Activity Recorded = Logged In AND no qualifying recorded activity
        //   Not Logged In        = no login recorded that day
        //     (Activity Without New Login = Not Logged In but with recorded activity --
        //      working in a session from an earlier login; disclosed, not re-labelled)
        // So Active + No Activity = Logged In, and Logged In + Not Logged In = Total.
        private static void BuildSoftwareActivity(DailyOperationsReport report, DailyOperationsSourceData data, DateTime day)
        {
            var employees = data.Employees;
            report.TotalEmployees = employees.Count;

            var trackingStartUtc = data.Coverage.LoginTrackingStartUtc;
            if (!trackingStartUtc.HasValue)
            {
                report.LoginDataAvailable = false;
                report.LoginUnavailableReason = "No login has been recorded in the system yet, so login and activity categories cannot be established.";
                return;
            }
            var trackingStartIndia = AppClock.ToIndiaTime(trackingStartUtc.Value);
            if (day < trackingStartIndia.Date)
            {
                report.LoginDataAvailable = false;
                report.LoginUnavailableReason = $"Login tracking began on {trackingStartIndia:yyyy-MM-dd}; logins before that date were never recorded.";
                return;
            }
            if (day == trackingStartIndia.Date)
                report.LoginPartialDayNote = $"Login tracking began at {trackingStartIndia:HH:mm} IST on this date; logins earlier that day were not recorded.";

            var logins = data.Logins.Where(l => l.Date == day).ToDictionary(l => l.UserId);
            var active = data.Activity.Where(a => a.Date == day).Select(a => a.UserId).ToHashSet();

            report.LoginDataAvailable = true;
            report.LoginEvents = employees.Where(e => logins.ContainsKey(e.Id)).Sum(e => logins[e.Id].Logins);
            foreach (var e in employees)
            {
                if (logins.ContainsKey(e.Id))
                {
                    report.LoggedIn.Add(e.Name);
                    (active.Contains(e.Id) ? report.ActiveUsers : report.NoActivityRecorded).Add(e.Name);
                }
                else
                {
                    report.NotLoggedIn.Add(e.Name);
                    if (active.Contains(e.Id))
                        report.ActivityWithoutLogin.Add(e.Name);
                }
            }
            if (report.LoginPartialDayNote != null)
                report.Notes.Add(report.LoginPartialDayNote);
        }

        // ---- Plain text (Copy Report / .txt export) -------------------------

        public static string ToPlainText(DailyOperationsReport r)
        {
            var sb = new StringBuilder();
            void Line(string s) => sb.Append(s).Append('\n');

            Line($"Date: {r.ReportDate:yyyy-MM-dd}");
            Line("Daily Operations");
            Line($"Sowing: {Qty(r.Sowing)}");
            if (!r.ReadyStockAvailable)
                Line($"Ready Stock: {NotAvailable}");
            else if (r.ReadyStock.Count == 0)
                Line($"{ReadyStockHeading}: 0");
            else
            {
                Line($"{ReadyStockHeading}:");
                foreach (var l in r.ReadyStock)
                    Line($"{l.PlantTypeName}: {Indian(l.Available)}");
                Line($"Ready Stock totals: physical {Indian(r.ReadyStockPhysicalTotal)}, reserved {Indian(r.ReadyStockReservedTotal)}, in transit {Indian(r.ReadyStockInTransitTotal)}");
            }
            Line($"Bookings: Seedling: {Count(r.SeedlingBookings)}, Potted: {Count(r.PottedBookings)}");
            Line($"Dispatch: Seedling: {Count(r.SeedlingDispatch)}, Potted: {Count(r.PottedDispatch)}");
            Line($"Outlet Sales: {Count(r.OutletSales)}");
            Line($"Fertilizer Usage: {FertilizerText(r)}");
            Line($"Wastage: {WastageText(r)}");

            Line("Software Activity");
            Line($"Total Employees: {Indian(r.TotalEmployees)}");
            if (!r.LoginDataAvailable)
            {
                Line($"Logged In: {NotAvailable}");
                Line($"Active Users: {NotAvailable}");
                Line($"No Activity Recorded: {NotAvailable}");
                Line($"Not Logged In: {NotAvailable}");
            }
            else
            {
                Line($"Logged In: {Indian(r.LoggedIn.Count)}");
                Line($"Active Users: {Indian(r.ActiveUsers.Count)}");
                Line($"No Activity Recorded: {Names(r.NoActivityRecorded)}");
                Line($"Not Logged In: {Names(r.NotLoggedIn)}");
                if (r.ActivityWithoutLogin.Count > 0)
                    Line($"Activity Without New Login: {Names(r.ActivityWithoutLogin)}");
            }

            var notes = UnavailableNotes(r).Concat(r.Notes).ToList();
            if (notes.Count > 0)
            {
                Line("Notes:");
                foreach (var n in notes)
                    Line("- " + n);
            }
            sb.Append("Thank You!");
            return sb.ToString();
        }

        // One "Label: reason" per figure that is not available, for the text and the page.
        public static List<string> UnavailableNotes(DailyOperationsReport r)
        {
            var notes = new List<string>();
            void Add(string label, ReportFigure f) { if (!f.Available) notes.Add($"{label}: {f.UnavailableReason}"); }
            Add("Sowing", r.Sowing);
            if (!r.ReadyStockAvailable) notes.Add($"Ready Stock: {r.ReadyStockUnavailableReason}");
            Add("Seedling bookings", r.SeedlingBookings);
            Add("Potted bookings", r.PottedBookings);
            Add("Seedling dispatch", r.SeedlingDispatch);
            Add("Potted dispatch", r.PottedDispatch);
            Add("Outlet sales", r.OutletSales);
            Add("Fertilizer usage", r.Fertilizer);
            Add("Wastage", r.Wastage);
            if (!r.LoginDataAvailable) notes.Add($"Software activity: {r.LoginUnavailableReason}");
            return notes;
        }

        // The primary Ready Stock figure (docs/STOCK_DEFINITIONS.md).
        public const string ReadyStockHeading = "Ready Stock (available for booking/dispatch)";

        public static string Count(ReportFigure f) => f.Available ? Indian(f.Count) : NotAvailable;

        public static string Qty(ReportFigure f, int maxDecimals = 2) => f.Available ? Indian(f.Quantity, maxDecimals) : NotAvailable;

        public static string FertilizerText(DailyOperationsReport r)
        {
            if (!r.Fertilizer.Available) return NotAvailable;
            if (r.FertilizerByUnit.Count == 0) return "0";
            return string.Join(", ", r.FertilizerByUnit.Select(u => $"{Indian(u.Quantity, 3)} {u.Unit}"));
        }

        public static string WastageText(DailyOperationsReport r)
        {
            if (!r.Wastage.Available) return NotAvailable;
            var text = Indian(r.Wastage.Quantity);
            if (r.SeedLotWastage.Count > 0)
                text += " (Seed lots: " + string.Join(", ", r.SeedLotWastage.Select(u => $"{Indian(u.Quantity)} {u.Unit}")) + ")";
            return text;
        }

        public const string EmployeeListNote =
            "Total Employees is the current list of active users: the system keeps no joining/leaving dates, so a past date is counted against today's list.";

        public static string Names(IReadOnlyCollection<string> names) => names.Count == 0 ? NoNames : string.Join(", ", names);

        // Several days, oldest first, separated for pasting into one message.
        public static string ToPlainText(IEnumerable<DailyOperationsReport> reports)
            => string.Join("\n\n----------\n\n", reports.Select(ToPlainText));
    }
}
