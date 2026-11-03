using System.Security.Claims;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Services.FieldWork
{
    // Field Work Phase 4 (2026-11-03): expenses. A field employee (holds FieldWork.Day itself) adds an expense with one live
    // bill photo and may cancel it while it is pending; a field manager (FieldWork.Manage) sees every expense and approves or
    // rejects it -- never their own unless an administrator. Everyone else (and another employee) gets "not found" (404).
    // No edit: a wrong expense is cancelled and added again. No Razor dependency.
    public class FieldExpenseService
    {
        private readonly FieldExpenseRepository _repo;
        private readonly FieldPhotoStore _photos;
        private readonly FieldWorkOptions _options;

        public FieldExpenseService(FieldExpenseRepository repo, FieldPhotoStore photos, FieldWorkOptions options)
        {
            _repo = repo;
            _photos = photos;
            _options = options;
        }

        public FieldWorkOptions Options => _options;
        public long MaxPhotoBytes => _photos.MaxBytes;

        public const int MaxListRows = 500;

        public sealed record Outcome(bool Ok, IReadOnlyList<string> Errors, int? Id = null, bool Replay = false)
        {
            public static Outcome Fail(params string[] errors) => new(false, errors);
            public static Outcome Fail(IEnumerable<string> errors) => new(false, errors.ToList());
        }

        public Task<bool> SchemaReadyAsync() => _repo.SchemaReadyAsync();

        // A field employee holds FieldWork.Day as a real claim (an administrator's implicit access does not add expenses).
        public static bool MayAdd(ClaimsPrincipal user)
            => user.Identity?.IsAuthenticated == true && user.GetUserId() != null
               && user.HasClaim(MinimumAuthorizationLevelHandler.PermissionClaimType, FieldDayRules.DayPermission);

        public static bool MayManage(ClaimsPrincipal user) => FieldDayService.MayManage(user);

        public static bool MayCancel(ClaimsPrincipal user, FieldExpense e)
            => user.GetUserId() is { } id && FieldExpenseRules.CheckCancel(e.Status, e.UserId, id) == null;

        public static bool MayDecide(ClaimsPrincipal user, FieldExpense e)
            => user.GetUserId() is { } id && FieldExpenseRules.CheckDecision(e.Status, e.UserId, id, MayManage(user), user.IsFullAccess()) == null;

        // The expense when this user may see it (owner or field manager), else null -- the page answers 404.
        public async Task<FieldExpense?> GetVisibleAsync(ClaimsPrincipal user, int id)
        {
            var userId = user.GetUserId();
            if (userId == null || id <= 0 || !await _repo.SchemaReadyAsync())
                return null;
            var e = await _repo.GetAsync(id);
            return e != null && (e.UserId == userId || MayManage(user)) ? e : null;
        }

        public Task<List<FieldExpenseEvent>> ListEventsAsync(int expenseId) => _repo.ListEventsAsync(expenseId);

        // The bill photo file of an expense this user may see, else null (404).
        public async Task<(string Path, string ContentType)?> GetBillPhotoAsync(ClaimsPrincipal user, int expenseId)
        {
            if (await GetVisibleAsync(user, expenseId) == null)
                return null;
            var path = _photos.PathFor(await _repo.GetBillFileAsync(expenseId));
            return path != null && File.Exists(path) ? (path, FieldPhotoStore.JpegContentType) : null;
        }

        // ---- employee -------------------------------------------------------------------------------------------------

        public sealed record MonthSummary(DateTime From, DateTime To, List<FieldExpense> Expenses, IReadOnlyDictionary<string, decimal> TotalByStatus);

        public async Task<MonthSummary> ListMineAsync(ClaimsPrincipal user, string? month)
        {
            var (from, to) = FieldExpenseRules.MonthRange(month, AppClock.Today);
            var userId = user.GetUserId();
            var list = userId == null ? new List<FieldExpense>() : await _repo.ListAsync(from, to, userId, null, null, MaxListRows);
            var totals = FieldExpenseRules.Statuses.ToDictionary(s => s, s => list.Where(e => e.Status == s).Sum(e => e.Amount));
            return new MonthSummary(from, to, list, totals);
        }

        public sealed record CreateRequest(string? Category, string? Amount, DateTime? Date, string? Note, byte[]? Photo, int? ReminderVersion,
            string? SubmitToken);

        // Everything that can be checked without the database (unit-tested).
        public static IReadOnlyList<string> Validate(CreateRequest r, FieldWorkOptions options, FieldPhotoStore photos, DateTime todayIst,
            out FieldExpenseRules.Clean? clean)
        {
            var errors = FieldExpenseRules.Validate(new FieldExpenseRules.Input(r.Category, r.Amount, r.Date, r.Note), todayIst,
                options.ExpenseBackdateDays, out clean).ToList();
            var hasPhoto = r.Photo is { Length: > 0 };
            if (FieldExpenseRules.CheckBill(hasPhoto, r.ReminderVersion, options.ExpenseBillReminderVersion) is { } billError)
                errors.Add(billError);
            else if (photos.Validate(r.Photo) is { } photoError)
                errors.Add("Bill photo: " + photoError);
            return errors;
        }

        public async Task<Outcome> CreateAsync(ClaimsPrincipal user, CreateRequest req)
        {
            var userId = user.GetUserId();
            if (userId == null || !MayAdd(user))
                return Outcome.Fail("Only field employees add expenses.");
            if (!await _repo.SchemaReadyAsync())
                return Outcome.Fail(FieldExpenseSchema.MigrationMessage);
            if (!Guid.TryParse(req.SubmitToken, out var token) || token == Guid.Empty)
                return Outcome.Fail("This form has expired. Open Add Expense again.");
            // a repeated submit of a form that already saved: the same expense (before the checks -- e.g. the date window
            // may have moved on at midnight)
            if (await _repo.FindByTokenAsync(token) is { } earlier)
                return earlier.UserId == userId
                    ? new Outcome(true, Array.Empty<string>(), earlier.Id, Replay: true)
                    : Outcome.Fail("This form was already used. Open Add Expense again.");

            var errors = Validate(req, _options, _photos, AppClock.Today, out var clean);
            if (errors.Count > 0)
                return Outcome.Fail(errors);

            string? stored = null;
            try
            {
                stored = await _photos.SaveAsync(req.Photo!);
                var result = await _repo.CreateAsync(new FieldExpenseRepository.CreateCommand(userId.Value, clean!.Date, clean.Category, clean.Amount,
                    clean.Note, new FieldExpenseRepository.PhotoInput(stored, req.Photo!.Length), _options.ExpenseBillReminderVersion, token));
                if (result.Replay)
                    _photos.DeleteQuietly(stored);   // the earlier submit kept its own photo
                return new Outcome(true, Array.Empty<string>(), result.ExpenseId, result.Replay);
            }
            catch (Exception ex)
            {
                _photos.DeleteQuietly(stored);
                return Outcome.Fail(DbErrorMessage.For(ex));
            }
        }

        public async Task<Outcome> CancelAsync(ClaimsPrincipal user, int expenseId, string? reason)
        {
            var userId = user.GetUserId();
            var e = await GetVisibleAsync(user, expenseId);
            if (userId == null || e == null)
                return Outcome.Fail("Expense not found.");
            if (FieldExpenseRules.CheckCancel(e.Status, e.UserId, userId.Value) is { } refusal)
                return Outcome.Fail(refusal);
            if (FieldExpenseRules.ValidateReason(reason, "cancelling") is { } reasonError)
                return Outcome.Fail(reasonError);
            try
            {
                // re-checked on the LOCKED row: a manager may have decided it meanwhile
                await _repo.CancelAsync(expenseId, userId.Value, FieldCustomerRules.Clean(reason)!,
                    (status, owner) => FieldExpenseRules.CheckCancel(status, owner, userId.Value));
                return new Outcome(true, Array.Empty<string>(), expenseId);
            }
            catch (Exception ex)
            {
                return Outcome.Fail(DbErrorMessage.For(ex));
            }
        }

        // ---- manager --------------------------------------------------------------------------------------------------

        public async Task<Outcome> DecideAsync(ClaimsPrincipal user, int expenseId, bool approve, string? comment)
        {
            var userId = user.GetUserId();
            var manage = MayManage(user);
            var full = user.IsFullAccess();
            if (userId == null || !manage)
                return Outcome.Fail("Only a field manager can approve or reject expenses.");
            var e = await GetVisibleAsync(user, expenseId);
            if (e == null)
                return Outcome.Fail("Expense not found.");
            if (FieldExpenseRules.CheckDecision(e.Status, e.UserId, userId.Value, manage, full) is { } refusal)
                return Outcome.Fail(refusal);
            if (FieldExpenseRules.ValidateComment(!approve, comment) is { } commentError)
                return Outcome.Fail(commentError);
            try
            {
                await _repo.DecideAsync(expenseId, userId.Value, approve, FieldCustomerRules.Clean(comment),
                    (status, owner) => FieldExpenseRules.CheckDecision(status, owner, userId.Value, manage, full));
                return new Outcome(true, Array.Empty<string>(), expenseId);
            }
            catch (Exception ex)
            {
                return Outcome.Fail(DbErrorMessage.For(ex));
            }
        }

        public Task<List<FieldEmployeeOption>> ListEmployeesAsync() => _repo.ListExpenseEmployeesAsync();

        public sealed record ManagerView(FieldExpenseRules.Filter Filter, List<FieldExpense> Expenses, List<FieldExpenseTotal> Totals, bool Truncated);

        // The manager list and the per-employee totals for ONE normalised filter (the Excel export uses exactly this).
        public async Task<ManagerView> ListForManagerAsync(FieldExpenseRules.Filter f)
        {
            var list = await _repo.ListAsync(f.From, f.To, f.UserId, f.Category, f.Status, MaxListRows + 1);
            var truncated = list.Count > MaxListRows;
            if (truncated)
                list.RemoveAt(list.Count - 1);
            var totals = await _repo.TotalsAsync(f.From, f.To, f.UserId, f.Category);
            return new ManagerView(f, list, totals, truncated);
        }

        public static readonly IReadOnlyList<string> ExportHeaders = new[]
        {
            "Date", "Employee", "Category", "Amount (Rs)", "Note", "Status", "Decided by", "Decided at (IST)", "Comment / cancel reason", "Added at (IST)",
        };

        public static IEnumerable<IReadOnlyList<object?>> ExportRows(IEnumerable<FieldExpense> list)
            => list.Select(e => (IReadOnlyList<object?>)new object?[]
            {
                e.ExpenseDate.Date, e.EmployeeName, e.Category, e.Amount, e.Note, FieldExpenseRules.StatusLabel(e.Status), e.DecidedByName,
                e.DecidedAtUtc.HasValue ? IstText(e.DecidedAtUtc.Value) : null,
                e.Status == FieldExpenseRules.StatusCancelled ? e.CancelReason : e.DecisionComment,
                IstText(e.CreatedAtUtc),
            });

        // ExportHelper formats a DateTime cell as a date only; timestamps go out as text with the time
        private static string IstText(DateTime utc) => AppClock.ToIndiaTime(utc).ToString("dd-MMM-yyyy hh:mm tt", System.Globalization.CultureInfo.InvariantCulture);
    }
}
