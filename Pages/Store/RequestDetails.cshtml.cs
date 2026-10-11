using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Store
{
    // Request Details: the request, every line's requested / approved / issued / consumed / kept /
    // returned quantities, the request's ledger movements, and the next step the signed-in user
    // may take:
    //   Store Keeper    review (approve / partial / reject per line), issue, confirm returns, cancel
    //   Area supervisor settle each issued line (consumed / kept / returned), cancel own request
    // Every POST re-loads the request and checks StoreAuthorization for that step (Forbid
    // otherwise); the repository's conditional UPDATEs make a step that someone else already took
    // a harmless "changed" message; the database refuses any stock or quantity rule violation.
    public class RequestDetailsModel : PageModel
    {
        private readonly StoreRepository _repo;
        private readonly StoreAuthorization _auth;
        private readonly SubmissionTokenGuard _tokens;

        public RequestDetailsModel(StoreRepository repo, StoreAuthorization auth, SubmissionTokenGuard tokens)
        {
            _repo = repo;
            _auth = auth;
            _tokens = tokens;
        }

        [BindProperty(SupportsGet = true)] public int Id { get; set; }
        [BindProperty] public string? SubmitToken { get; set; }
        [BindProperty] public List<int> LineIds { get; set; } = new();
        [BindProperty] public List<decimal?> Quantities { get; set; } = new();
        [BindProperty] public List<string?> LineRemarks { get; set; } = new();
        [BindProperty] public string? Remarks { get; set; }
        [BindProperty] public int? LineId { get; set; }
        [BindProperty] public decimal? Consumed { get; set; }
        [BindProperty] public decimal? Kept { get; set; }
        [BindProperty] public decimal? Returned { get; set; }
        [BindProperty] public string? ReturnCondition { get; set; }
        [BindProperty] public string? Reason { get; set; }

        public StoreRequest Current { get; set; } = null!;
        public List<StoreTransaction> Movements { get; set; } = new();
        public bool CanReview => _auth.CanReview(User, Current);
        public bool CanIssue => _auth.CanIssue(User, Current);
        public bool CanSettle => _auth.CanSettle(User, Current);
        public bool CanConfirmReturn => _auth.CanConfirmReturn(User, Current);
        public bool CanCancel => _auth.CanCancel(User, Current);

        public async Task<IActionResult> OnGetAsync()
        {
            var denied = await LoadAsync();
            if (denied != null) return denied;
            SubmitToken = SubmissionTokenGuard.NewToken();
            return Page();
        }

        public Task<IActionResult> OnPostReviewAsync() => StepAsync(() => CanReview, new[] { StoreRules.Pending }, () =>
        {
            var decisions = new Dictionary<int, (decimal, string?)>();
            foreach (var line in Current.Lines)
            {
                var i = LineIds.IndexOf(line.Id);
                var qty = i >= 0 && i < Quantities.Count ? Quantities[i] : null;
                var error = StoreRules.ValidateQuantity(qty, $"{line.ItemName} approved", line.ItemType, allowZero: true);
                if (error == null && qty > line.RequestedQty)
                    error = $"{line.ItemName}: at most the {StoreRules.Qty(line.RequestedQty)} requested can be approved.";
                if (error != null) { ModelState.AddModelError(nameof(Quantities), error); continue; }
                decisions[line.Id] = (qty!.Value, i < LineRemarks.Count ? LineRemarks[i] : null);
            }
            AddIfError(nameof(Remarks), StoreRules.Optional(Remarks, "Remarks"));
            return (decisions, ModelState.IsValid);
        }, async d =>
        {
            var result = await _repo.ReviewAsync(Id, d, Remarks, Me);
            return (result, d.Values.Any(v => v.Item1 > 0)
                ? (d.Values.All(v => v.Item1 > 0) && Current.Lines.All(l => d[l.Id].Item1 == l.RequestedQty) ? "Request approved." : "Request partially approved.")
                : "Request rejected (nothing approved).");
        });

        public Task<IActionResult> OnPostIssueAsync() => StepAsync(() => CanIssue, new[] { StoreRules.Approved }, () =>
        {
            var issue = new Dictionary<int, decimal>();
            foreach (var line in Current.Lines.Where(l => l.ApprovedQty > 0))
            {
                var i = LineIds.IndexOf(line.Id);
                var qty = i >= 0 && i < Quantities.Count ? Quantities[i] : null;
                var error = StoreRules.ValidateQuantity(qty, $"{line.ItemName} issued", line.ItemType, allowZero: true);
                if (error == null && qty > line.ApprovedQty)
                    error = $"{line.ItemName}: at most the {StoreRules.Qty(line.ApprovedQty!.Value)} approved can be issued.";
                if (error == null && qty > line.ItemAvailable)
                    error = $"{line.ItemName}: only {StoreRules.Qty(line.ItemAvailable)} in stock.";
                if (error != null) { ModelState.AddModelError(nameof(Quantities), error); continue; }
                issue[line.Id] = qty!.Value;
            }
            return (issue, ModelState.IsValid);
        }, async issue =>
        {
            var result = await _repo.IssueAsync(Id, issue, Me);
            return (result, issue.Values.Any(q => q > 0) ? "Items issued. The Area now settles each item (consumed / kept / returned)." : "Nothing issued -- the request is closed.");
        });

        public Task<IActionResult> OnPostSettleAsync() => StepAsync(() => CanSettle, new[] { StoreRules.Issued }, () =>
        {
            var line = Current.Lines.FirstOrDefault(l => l.Id == LineId && l.NeedsSettlement);
            if (line == null)
            {
                ModelState.AddModelError(nameof(LineId), "This item is not waiting for settlement.");
                return (line, false);
            }
            foreach (var e in StoreRules.ValidateSettlement(line.ItemName, line.ItemType, line.IssuedQty, Consumed, Kept, Returned))
                ModelState.AddModelError(nameof(Consumed), e);
            AddIfError(nameof(Remarks), StoreRules.Optional(Remarks, "Remarks"));
            return (line, ModelState.IsValid);
        }, async line =>
        {
            var result = await _repo.SettleLineAsync(Id, line!.Id, Consumed ?? 0, Kept ?? 0, Returned ?? 0, Remarks, Me);
            return (result, $"{line.ItemName} settled" + ((Returned ?? 0) > 0 ? $" -- {StoreRules.Qty(Returned!.Value)} to return: the Store Keeper confirms when it arrives." : "."));
        });

        public Task<IActionResult> OnPostConfirmReturnAsync() => StepAsync(() => CanConfirmReturn, new[] { StoreRules.Issued }, () =>
        {
            var line = Current.Lines.FirstOrDefault(l => l.Id == LineId && l.ReturnPending);
            if (line == null) ModelState.AddModelError(nameof(LineId), "This item has no return waiting for confirmation.");
            if (!StoreRules.ReturnConditions.Contains(ReturnCondition)) ModelState.AddModelError(nameof(ReturnCondition), "Choose the condition of the returned item.");
            AddIfError(nameof(Remarks), StoreRules.Optional(Remarks, "Remarks"));
            return (line, ModelState.IsValid);
        }, async line =>
        {
            var result = await _repo.ConfirmReturnAsync(Id, line!.Id, ReturnCondition!, Remarks, Me);
            return (result, $"Return of {StoreRules.Qty(line.ReturnDeclaredQty)} {line.UnitName} {line.ItemName} confirmed -- back in Main Office stock.");
        });

        public Task<IActionResult> OnPostCancelAsync() => StepAsync(() => CanCancel, new[] { StoreRules.Pending, StoreRules.Approved }, () =>
        {
            AddIfError(nameof(Reason), string.IsNullOrWhiteSpace(Reason) ? "A reason is required." : StoreRules.Optional(Reason, "Reason"));
            return (Reason, ModelState.IsValid);
        }, async reason => (await _repo.CancelAsync(Id, reason!, Me, _auth.IsManager(User)), "Request cancelled."));

        // ---- the one pattern every step follows ---------------------------------------------

        private int Me => User.GetUserId() ?? 0;

        private async Task<IActionResult> StepAsync<T>(Func<bool> allowed, string[] stepStatuses, Func<(T Value, bool Valid)> validate, Func<T, Task<(StoreRepository.StepResult, string)>> perform)
        {
            var denied = await LoadAsync();
            if (denied != null) return denied;
            if (!allowed())
            {
                // The request already moved past this step (and the user is a Store / Area user)
                // -> message; not this user's step at all -> 403.
                if (!stepStatuses.Contains(Current.Status) && (_auth.IsManager(User) || _auth.CanRequestFor(User, Current.AreaId)))
                {
                    TempData["Error"] = StoreRules.ChangedMessage;
                    return RedirectToPage(new { id = Id });
                }
                return Forbid();
            }
            var (value, valid) = validate();
            if (!valid)
            {
                ModelState.Remove(nameof(SubmitToken));
                SubmitToken = SubmissionTokenGuard.NewToken();
                return Page();
            }
            if (!_tokens.TryConsume($"Store:{Id}", SubmitToken))
            {
                TempData["Error"] = StoreRules.AlreadySubmittedMessage;
                return RedirectToPage(new { id = Id });
            }
            try
            {
                var (result, message) = await perform(value);
                if (result == StoreRepository.StepResult.Changed)
                    TempData["Error"] = StoreRules.ChangedMessage;
                else
                    TempData["Success"] = message;
            }
            catch (StoreStockException ex)
            {
                TempData["Error"] = ex.Message + " Nothing was issued.";
            }
            catch (SqlException ex) when (ex.Number is 547 or 51814 or 51815 or 51817)
            {
                TempData["Error"] = "Refused by the store rules (stock or quantity would become invalid). Nothing was changed -- please check the quantities.";
            }
            return RedirectToPage(new { id = Id });
        }

        private async Task<IActionResult?> LoadAsync()
        {
            var request = await _repo.GetRequestAsync(Id);
            if (request == null) return NotFound();
            if (!_auth.CanView(User, request)) return Forbid();
            Current = request;
            Movements = (await _repo.SearchTransactionsAsync(new StoreTransactionFilter { RequestId = Id }, null)).OrderBy(t => t.Id).ToList();
            return null;
        }

        private void AddIfError(string key, string? error)
        {
            if (error != null) ModelState.AddModelError(key, error);
        }
    }
}
