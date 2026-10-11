using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Maintenance
{
    // Ticket Details: the ticket, its durations, photos and full event history, and the ONE
    // next workflow step the signed-in user may take. Every POST handler re-loads the ticket,
    // checks MaintenanceAuthorization for that step (Forbid otherwise), validates its input and
    // calls the repository's conditional UPDATE -- if someone else moved the ticket first the
    // user gets "changed by someone else" and nothing happens. Photos are served by
    // OnGetPhoto only to users who may see the ticket.
    [RequestSizeLimit(RaiseModel.MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = RaiseModel.MaxRequestBytes)]
    public class DetailsModel : PageModel
    {
        private readonly MaintenanceRepository _repo;
        private readonly MaintenanceAuthorization _auth;
        private readonly MaintenancePhotoStore _photos;
        private readonly SubmissionTokenGuard _tokens;

        public DetailsModel(MaintenanceRepository repo, MaintenanceAuthorization auth, MaintenancePhotoStore photos, SubmissionTokenGuard tokens)
        {
            _repo = repo;
            _auth = auth;
            _photos = photos;
            _tokens = tokens;
        }

        [BindProperty(SupportsGet = true)] public int Id { get; set; }
        [BindProperty] public string? SubmitToken { get; set; }
        [BindProperty] public DateTime? ExpectedVisitDate { get; set; }
        [BindProperty] public string? ExpectedVisitRemarks { get; set; }
        [BindProperty] public int? WorkerId { get; set; }
        [BindProperty] public string? ProblemFound { get; set; }
        [BindProperty] public string? WorkPerformed { get; set; }
        [BindProperty] public string? WorkDone { get; set; }
        [BindProperty] public string? Resolution { get; set; }
        [BindProperty] public string? MaterialsUsed { get; set; }
        [BindProperty] public string? WorkerRemarks { get; set; }
        [BindProperty] public List<IFormFile> CompletionPhotos { get; set; } = new();
        [BindProperty] public string? Reason { get; set; }

        public MaintenanceTicket Ticket { get; set; } = null!;
        public List<MaintenanceEvent> Events { get; set; } = new();
        public List<MaintenancePhoto> Photos { get; set; } = new();
        public List<MaintenanceWorker> Workers { get; set; } = new();
        public DateTime Today => AppClock.Today;
        public string? OverdueReason => Ticket.OverdueReason(DateTime.UtcNow, AppClock.Today);

        public bool Can(MaintenanceRules.Action action) => _auth.CanPerform(User, Ticket, action);

        public async Task<IActionResult> OnGetAsync()
        {
            var denied = await LoadAsync();
            if (denied != null)
                return denied;
            ExpectedVisitDate ??= Ticket.ExpectedVisitDate ?? Today;
            // After a rejected completion the ticket is In Progress again with the worker's previous
            // resolution still stored: start the form from it, so re-resolving never silently blanks
            // e.g. the materials used (found in the browser test).
            if (Ticket.Status == MaintenanceRules.InProgress)
            {
                WorkDone ??= Ticket.WorkDone;
                Resolution ??= Ticket.Resolution;
                MaterialsUsed ??= Ticket.MaterialsUsed;
                WorkerRemarks ??= Ticket.WorkerRemarks;
            }
            SubmitToken = SubmissionTokenGuard.NewToken();
            return Page();
        }

        // A photo of this ticket, only for users who may see the ticket.
        public async Task<IActionResult> OnGetPhotoAsync(int photoId)
        {
            var photo = await _repo.GetPhotoAsync(photoId);
            if (photo == null || photo.TicketId != Id)
                return NotFound();
            var denied = await LoadAsync();
            if (denied != null)
                return denied;
            var path = _photos.PathFor(photo.StoredFileName);
            if (path == null || !System.IO.File.Exists(path) || !MaintenancePhotoStore.AllowedContentTypes.Contains(photo.ContentType))
                return NotFound();
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            Response.Headers["Cache-Control"] = "private, max-age=3600";
            return PhysicalFile(path, photo.ContentType);
        }

        public Task<IActionResult> OnPostAcceptAsync() => StepAsync(MaintenanceRules.Action.Accept, () =>
        {
            var error = MaintenanceRules.ValidateExpectedVisitDate(ExpectedVisitDate, Today);
            if (error != null) ModelState.AddModelError(nameof(ExpectedVisitDate), error);
            AddIfError(nameof(ExpectedVisitRemarks), MaintenanceRules.Optional(ExpectedVisitRemarks, "Visit remark", MaintenanceRules.MaxRemarksLength));
            return Task.CompletedTask;
        }, () => _repo.AcceptAsync(Id, Me, ExpectedVisitDate!.Value, ExpectedVisitRemarks),
           "Ticket accepted. Visit the location and press \"I have arrived\" when you reach it.");

        // Administrator: assign an Open ticket, or change the worker before arrival.
        public async Task<IActionResult> OnPostAssignAsync()
        {
            var denied = await LoadAsync();
            if (denied != null)
                return denied;
            var reassign = Ticket.Status == MaintenanceRules.Accepted;
            var action = reassign ? MaintenanceRules.Action.Reassign : MaintenanceRules.Action.Assign;
            MaintenanceWorker? worker = null;
            return await StepAsync(action, async () =>
            {
                worker = (await _repo.GetWorkersForCategoryAsync(Ticket.CategoryId)).FirstOrDefault(w => w.Id == WorkerId);
                if (worker == null)
                    ModelState.AddModelError(nameof(WorkerId), $"Choose an active {Ticket.WorkerRoleName}.");
                else if (reassign && worker.Id == Ticket.AssignedWorkerId)
                    ModelState.AddModelError(nameof(WorkerId), "This worker is already assigned.");
                var error = MaintenanceRules.ValidateExpectedVisitDate(ExpectedVisitDate, Today);
                if (error != null) ModelState.AddModelError(nameof(ExpectedVisitDate), error);
            }, () => _repo.AssignAsync(Id, Me, worker!.Id, worker.Name, Ticket.AssignedWorkerName, ExpectedVisitDate!.Value, ExpectedVisitRemarks, reassign),
               reassign ? "Worker changed." : "Ticket assigned.", alreadyLoaded: true);
        }

        public Task<IActionResult> OnPostArriveAsync() => StepAsync(MaintenanceRules.Action.Arrive, null,
            () => _repo.ArriveAsync(Id, Me), "Arrival recorded. A supervisor of this Area must now confirm that you have arrived.");

        public Task<IActionResult> OnPostConfirmArrivalAsync() => StepAsync(MaintenanceRules.Action.ConfirmArrival, null,
            () => _repo.ConfirmArrivalAsync(Id, Me), "Arrival confirmed. The worker can start the work.");

        public Task<IActionResult> OnPostStartWorkAsync() => StepAsync(MaintenanceRules.Action.StartWork, () =>
        {
            AddIfError(nameof(ProblemFound), MaintenanceRules.Required(ProblemFound, "What is the problem", MaintenanceRules.MaxWorkTextLength));
            AddIfError(nameof(WorkPerformed), MaintenanceRules.Required(WorkPerformed, "What work is being performed", MaintenanceRules.MaxWorkTextLength));
            return Task.CompletedTask;
        }, () => _repo.StartWorkAsync(Id, Me, ProblemFound!, WorkPerformed!), "Work started.");

        public async Task<IActionResult> OnPostResolveAsync()
        {
            var uploads = (CompletionPhotos ?? new()).Where(f => f != null && f.Length > 0).ToList();
            var types = new List<MaintenancePhotoStore.DetectedType>();
            var saved = new List<MaintenancePhotoStore.SavedPhoto>();
            try
            {
                return await StepAsync(MaintenanceRules.Action.Resolve, async () =>
                {
                    AddIfError(nameof(WorkDone), MaintenanceRules.Required(WorkDone, "Work done", MaintenanceRules.MaxWorkTextLength));
                    AddIfError(nameof(Resolution), MaintenanceRules.Required(Resolution, "Resolution", MaintenanceRules.MaxWorkTextLength));
                    AddIfError(nameof(MaterialsUsed), MaintenanceRules.Optional(MaterialsUsed, "Materials used", MaintenanceRules.MaxWorkTextLength));
                    AddIfError(nameof(WorkerRemarks), MaintenanceRules.Optional(WorkerRemarks, "Remarks", MaintenanceRules.MaxRemarksLength));
                    var existing = Photos.Count(p => p.Stage == "Completion");
                    if (existing + uploads.Count > MaintenanceRules.MaxPhotosPerStage)
                        ModelState.AddModelError(nameof(CompletionPhotos), $"At most {MaintenanceRules.MaxPhotosPerStage} completion photos per ticket.");
                    foreach (var file in uploads)
                    {
                        var (type, error) = await MaintenancePhotoStore.ValidateAsync(file);
                        if (error != null) ModelState.AddModelError(nameof(CompletionPhotos), error);
                        else types.Add(type!);
                    }
                }, async () =>
                {
                    for (var i = 0; i < uploads.Count; i++)
                        saved.Add(await _photos.SaveAsync(uploads[i], types[i]));
                    var done = await _repo.ResolveAsync(Id, Me, WorkDone!, Resolution!, MaterialsUsed, WorkerRemarks, saved);
                    if (!done)
                        _photos.DeleteQuietly(saved.Select(s => s.StoredFileName));
                    return done;
                }, "Work completed. A supervisor of this Area must now confirm that the problem is solved.");
            }
            catch
            {
                _photos.DeleteQuietly(saved.Select(s => s.StoredFileName));
                throw;
            }
        }

        public Task<IActionResult> OnPostConfirmCompletionAsync() => StepAsync(MaintenanceRules.Action.ConfirmCompletion, null,
            () => _repo.ConfirmCompletionAsync(Id, Me), "Completion confirmed. The ticket is closed.");

        public Task<IActionResult> OnPostRejectCompletionAsync() => StepAsync(MaintenanceRules.Action.RejectCompletion, () =>
        {
            AddIfError(nameof(Reason), string.IsNullOrWhiteSpace(Reason) ? MaintenanceRules.ReasonRequiredMessage : MaintenanceRules.Optional(Reason, "Reason", MaintenanceRules.MaxRemarksLength));
            return Task.CompletedTask;
        }, () => _repo.RejectCompletionAsync(Id, Me, Reason!), "Completion rejected. The ticket is back In Progress with the worker.");

        public Task<IActionResult> OnPostCancelAsync() => StepAsync(MaintenanceRules.Action.Cancel, () =>
        {
            AddIfError(nameof(Reason), string.IsNullOrWhiteSpace(Reason) ? MaintenanceRules.ReasonRequiredMessage : MaintenanceRules.Optional(Reason, "Reason", MaintenanceRules.MaxRemarksLength));
            return Task.CompletedTask;
        }, () => _repo.CancelAsync(Id, Me, User.IsFullAccess(), Reason!), "Ticket cancelled.");

        // ---- the one pattern every step follows ---------------------------------------------

        private int Me => User.GetUserId() ?? 0;

        private async Task<IActionResult> StepAsync(MaintenanceRules.Action action, Func<Task>? validate, Func<Task<bool>> perform, string success, bool alreadyLoaded = false)
        {
            if (!alreadyLoaded)
            {
                var denied = await LoadAsync();
                if (denied != null)
                    return denied;
            }
            if (!_auth.CanPerform(User, Ticket, action))
            {
                // Not allowed for this user at all -> 403; allowed but the ticket moved on -> message.
                if (MaintenanceRules.IsAllowed(action, Ticket.Status) || !CouldEverPerform(action))
                    return Forbid();
                TempData["Error"] = MaintenanceRules.ChangedMessage;
                return RedirectToPage(new { id = Id });
            }
            if (validate != null)
                await validate();
            if (!ModelState.IsValid)
            {
                ModelState.Remove(nameof(SubmitToken));
                SubmitToken = SubmissionTokenGuard.NewToken();
                return Page();
            }
            if (!_tokens.TryConsume($"Maintenance:{Id}", SubmitToken))
            {
                TempData["Error"] = MaintenanceRules.AlreadySubmittedMessage;
                return RedirectToPage(new { id = Id });
            }
            if (!await perform())
            {
                TempData["Error"] = MaintenanceRules.ChangedMessage;
                return RedirectToPage(new { id = Id });
            }
            TempData["Success"] = success;
            return RedirectToPage(new { id = Id });
        }

        // Would this user be allowed this step if the ticket were in the right status?
        // (Separates "not your ticket" -> 403 from "too late, it already moved on" -> message.)
        private bool CouldEverPerform(MaintenanceRules.Action action)
        {
            var probe = new MaintenanceTicket
            {
                AreaId = Ticket.AreaId, WorkerRoleName = Ticket.WorkerRoleName, AssignedWorkerId = Ticket.AssignedWorkerId,
                RequestedById = Ticket.RequestedById, Status = MaintenanceRules.Transitions[action].From[0],
            };
            if (action == MaintenanceRules.Action.Accept)
                probe.AssignedWorkerId = null;
            return _auth.CanPerform(User, probe, action);
        }

        private async Task<IActionResult?> LoadAsync()
        {
            var ticket = await _repo.GetByIdAsync(Id);
            if (ticket == null)
                return NotFound();
            if (!_auth.CanView(User, ticket))
                return Forbid();
            Ticket = ticket;
            Events = await _repo.GetEventsAsync(Id);
            Photos = await _repo.GetPhotosAsync(Id);
            if (_auth.IsManager(User) && (ticket.Status is MaintenanceRules.Open or MaintenanceRules.Accepted))
                Workers = await _repo.GetWorkersForCategoryAsync(ticket.CategoryId);
            return null;
        }

        private void AddIfError(string key, string? error)
        {
            if (error != null)
                ModelState.AddModelError(key, error);
        }
    }
}
