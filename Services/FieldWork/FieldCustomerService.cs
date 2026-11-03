using System.Security.Claims;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Services.FieldWork
{
    // Field Work Phase 2 (2026-11-03): customers, calls / notes, follow-ups, reassign -- independent of Razor Pages (a page
    // or a future mobile API passes the signed-in user and the submitted values). Rules: FieldCustomerRules (pure). Every
    // change is re-checked on the LOCKED row by FieldCustomerRepository. A customer the user may not see is reported as
    // "not found" (pages answer 404), so changing an id in the URL reveals nothing.
    public class FieldCustomerService
    {
        private readonly FieldCustomerRepository _repo;
        private readonly FieldPhotoStore _photos;
        private readonly FieldWorkOptions _options;

        public FieldCustomerService(FieldCustomerRepository repo, FieldPhotoStore photos, FieldWorkOptions options)
        {
            _repo = repo;
            _photos = photos;
            _options = options;
        }

        public FieldWorkOptions Options => _options;

        public sealed record Outcome(bool Ok, IReadOnlyList<string> Errors, int? Id = null, bool AskNextDate = false,
            IReadOnlyList<FieldCustomerRules.DuplicateMatch>? Duplicates = null, bool Replay = false)
        {
            public static Outcome Fail(params string[] errors) => new(false, errors);
            public static Outcome Fail(IEnumerable<string> errors) => new(false, errors.ToList());
            public bool NeedsDuplicateConfirmation => !Ok && Duplicates is { Count: > 0 };
        }

        public static bool MayManage(ClaimsPrincipal user) => FieldDayService.MayManage(user);

        public Task<bool> SchemaReadyAsync() => _repo.SchemaReadyAsync();

        private static Guid? ParseToken(string? token) => Guid.TryParse(token, out var g) && g != Guid.Empty ? g : null;

        // ---- reading ----------------------------------------------------------------------------------------------------

        public async Task<FieldCustomer?> GetVisibleAsync(ClaimsPrincipal user, int customerId)
        {
            var c = await _repo.GetAsync(customerId);
            var manage = MayManage(user);
            // archived customers are hidden from employees (managers can still open and restore them)
            return c != null && (c.IsActive || manage) && FieldCustomerRules.MaySeeCustomer(c.AssignedUserId, c.CreatedByUserId, user.GetUserId(), manage)
                ? c : null;
        }

        public static bool MaySeePhone(ClaimsPrincipal user, FieldCustomer c) => FieldCustomerRules.MaySeePhone(c.AssignedUserId, user.GetUserId(), MayManage(user));
        public static bool MayEdit(ClaimsPrincipal user, FieldCustomer c) => c.IsActive && FieldCustomerRules.MayEditProfile(c.AssignedUserId, user.GetUserId(), MayManage(user));
        public static bool MayWork(ClaimsPrincipal user, FieldCustomer c) => FieldCustomerRules.MayWork(c.AssignedUserId, user.GetUserId(), MayManage(user), c.IsActive);

        public Task<FieldCustomerTimeline> GetTimelineAsync(int customerId) => _repo.GetTimelineAsync(customerId);

        public async Task<(string Path, string ContentType)?> GetPhotoAsync(ClaimsPrincipal user, int customerId, int photoId)
        {
            if (await GetVisibleAsync(user, customerId) == null)
                return null;
            var photo = await _repo.GetCustomerPhotoAsync(customerId, photoId);
            if (photo == null || photo.ContentType != FieldPhotoStore.JpegContentType)
                return null;
            var path = _photos.PathFor(photo.StoredFileName);
            return path != null && File.Exists(path) ? (path, photo.ContentType) : null;
        }

        public sealed record ListFilter(string? Search, string? Status, string? Type, string? District, int? EmployeeId, int? NoVisitDays,
            bool NoFollowUp, bool NeedsReassign, string Archived, int Page);

        public static readonly IReadOnlyList<string> ArchivedOptions = new[] { "Active", "Archived", "All" };

        // Unknown values are dropped -- a filter can only narrow what the user may already see.
        public static ListFilter NormalizeFilter(string? search, string? status, string? type, string? district, int? employeeId, int? noVisitDays,
            bool noFollowUp, bool needsReassign, string? archived, int? page, bool mayManage, IReadOnlyCollection<string> districtOptions,
            IReadOnlyCollection<int> employeeOptions)
        {
            var s = FieldCustomerRules.Clean(search);
            if (s != null && s.Length > FieldCustomerRules.MaxSearchLength)
                s = s[..FieldCustomerRules.MaxSearchLength];
            var d = FieldCustomerRules.Clean(district);
            return new ListFilter(
                s,
                status != null && FieldCustomerRules.LeadStatuses.ContainsKey(status) ? status : null,
                type != null && FieldCustomerRules.CustomerTypes.ContainsKey(type) ? type : null,
                d != null ? districtOptions.FirstOrDefault(o => string.Equals(o, d, StringComparison.OrdinalIgnoreCase)) : null,
                mayManage && employeeId.HasValue && employeeOptions.Contains(employeeId.Value) ? employeeId : null,
                noVisitDays.HasValue && FieldCustomerRules.NoVisitDayOptions.Contains(noVisitDays.Value) ? noVisitDays : null,
                noFollowUp,
                mayManage && needsReassign,
                mayManage && archived != null && ArchivedOptions.Contains(archived) ? archived : "Active",
                Math.Clamp(page ?? 1, 1, 100_000));
        }

        public async Task<(List<FieldCustomerListRow> Rows, int Total)> ListAsync(ClaimsPrincipal user, ListFilter f)
        {
            var since = f.NoVisitDays.HasValue ? AppClock.IndiaToUtc(AppClock.Today.AddDays(-f.NoVisitDays.Value)) : (DateTime?)null;
            return await _repo.ListAsync(new FieldCustomerRepository.ListQuery(user.GetUserId() ?? 0, MayManage(user), f.Search, f.Status, f.Type,
                f.District, f.EmployeeId, since, f.NoFollowUp, f.NeedsReassign, f.Archived, f.Page, Math.Clamp(_options.CustomerPageSize, 5, 200)));
        }

        public const int MaxBulkRows = 2000;

        // Every ACTIVE customer assigned to one employee (bulk reassign; managers only).
        public async Task<List<FieldCustomerListRow>> ListAssignedToAsync(ClaimsPrincipal user, int employeeId)
        {
            if (!MayManage(user))
                return new List<FieldCustomerListRow>();
            var (rows, _) = await _repo.ListAsync(new FieldCustomerRepository.ListQuery(user.GetUserId() ?? 0, true, null, null, null, null, employeeId,
                null, false, false, "Active", 1, MaxBulkRows));
            return rows;
        }

        public Task<List<string>> VisibleDistrictsAsync(ClaimsPrincipal user) => _repo.VisibleDistrictsAsync(user.GetUserId() ?? 0, MayManage(user));
        public Task<List<string>> AllDistrictsAsync() => _repo.AllDistrictsAsync();
        public Task<List<FieldEmployeeOption>> ListAssignableEmployeesAsync() => _repo.ListAssignableEmployeesAsync();
        public Task<List<FieldCustomerRepository.AssigneeSummary>> ListAssigneesAsync() => _repo.ListAssigneesAsync();
        public Task<int> CountNeedingReassignAsync() => _repo.CountNeedingReassignAsync();

        // Duplicate warning for a typed phone (the page calls it as you type; the server re-checks on save).
        public async Task<IReadOnlyList<FieldCustomerRules.DuplicateMatch>> FindDuplicatesAsync(string? phone, string? alternatePhone, int? excludeCustomerId)
        {
            var keys = new[] { phone, alternatePhone }
                .Select(p => FieldCustomerRules.NormalizePhone(p).Digits)
                .Where(d => d != null).Select(d => FieldCustomerRules.PhoneKey(d!)).Distinct().ToList();
            return keys.Count == 0 ? Array.Empty<FieldCustomerRules.DuplicateMatch>() : await _repo.FindByPhoneKeysAsync(keys, excludeCustomerId);
        }

        // ---- create / edit ----------------------------------------------------------------------------------------------

        public sealed record CreateRequest(FieldCustomerRules.CustomerInput Customer, int? AssignedUserId, decimal? Latitude, decimal? Longitude,
            decimal? AccuracyM, string? GpsExceptionReason, byte[]? Photo, int? ConsentReminderVersion, bool ConfirmDuplicate, string? SubmitToken);

        public async Task<Outcome> CreateAsync(ClaimsPrincipal user, CreateRequest req)
        {
            var userId = user.GetUserId();
            if (userId == null)
                return Outcome.Fail("Sign in again.");
            var token = ParseToken(req.SubmitToken);
            if (token == null)
                return Outcome.Fail("This form has expired. Open Add Customer again.");
            var manage = MayManage(user);
            var fieldEmployee = FieldDayService.IsFieldEmployee(user);
            if (!manage && !fieldEmployee)
                return Outcome.Fail("Only field employees and field managers can add customers.");
            // a repeated submit of a form that already saved: the same customer (before the duplicate-phone check, which
            // would otherwise find the customer this very form created)
            if (await _repo.FindByTokenAsync(token.Value) is { } earlier)
                return earlier.CreatedByUserId == userId
                    ? new Outcome(true, Array.Empty<string>(), earlier.Id, Replay: true)
                    : Outcome.Fail("This form was already used. Open Add Customer again.");

            var errors = new List<string>();
            var (clean, customerErrors) = FieldCustomerRules.ValidateCustomer(req.Customer, await _repo.AllDistrictsAsync());
            errors.AddRange(customerErrors);
            // a field employee is at the customer: GPS required (or a reason); a manager adding from the office: optional
            var pos = FieldCustomerRules.CheckPosition(req.Latitude, req.Longitude, req.AccuracyM, req.GpsExceptionReason,
                _options.MaxGpsAccuracyMeters, required: fieldEmployee, "Customer");
            errors.AddRange(pos.Errors);

            // a manager picks any ACTIVE field employee; a field employee always gets the customer themself
            int assigned;
            if (manage && req.AssignedUserId.HasValue)
            {
                var employees = await _repo.ListAssignableEmployeesAsync();
                if (employees.All(e => e.UserId != req.AssignedUserId.Value))
                    errors.Add("Select an active field employee to assign the customer to.");
                assigned = req.AssignedUserId.Value;
            }
            else if (fieldEmployee)
                assigned = userId.Value;
            else
            {
                errors.Add("Select the field employee to assign the customer to.");
                assigned = 0;
            }

            var hasPhoto = req.Photo is { Length: > 0 };
            if (hasPhoto)
            {
                if (_photos.Validate(req.Photo) is { } photoError) errors.Add(photoError);
                if (req.ConsentReminderVersion != _options.CustomerPhotoConsentVersion)
                    errors.Add("The photo permission reminder has changed. Reload the page and take the photo again.");
            }
            if (errors.Count > 0)
                return Outcome.Fail(errors);

            var duplicates = await FindDuplicatesAsync(clean!.Phone, clean.AlternatePhone, null);
            if (duplicates.Count > 0 && !req.ConfirmDuplicate)
                return new Outcome(false, new[] { FieldCustomerRules.DuplicateWarning(duplicates) }, Duplicates: duplicates);

            string? stored = null;
            try
            {
                FieldCustomerRepository.PhotoInput? photo = null;
                if (hasPhoto)
                {
                    stored = await _photos.SaveAsync(req.Photo!);
                    photo = new FieldCustomerRepository.PhotoInput(stored, req.Photo!.Length, _options.CustomerPhotoConsentVersion);
                }
                var (id, replay) = await _repo.CreateAsync(new FieldCustomerRepository.CreateCommand(clean, assigned, userId.Value, pos.Position,
                    pos.Position == null || pos.Flags.Count > 0 ? pos.Reason : null, FieldDayRules.JoinFlags(pos.Flags), photo, token.Value));
                if (replay)
                    _photos.DeleteQuietly(stored);
                return new Outcome(true, Array.Empty<string>(), id, Replay: replay);
            }
            catch (Exception ex)
            {
                _photos.DeleteQuietly(stored);
                return Outcome.Fail(DbErrorMessage.For(ex));
            }
        }

        public async Task<Outcome> UpdateProfileAsync(ClaimsPrincipal user, int customerId, FieldCustomerRules.CustomerInput input, bool confirmDuplicate)
        {
            var userId = user.GetUserId();
            var existing = await GetVisibleAsync(user, customerId);
            if (existing == null || userId == null)
                return Outcome.Fail("Customer not found.");
            if (!MayEdit(user, existing))
                return Outcome.Fail("Only the assigned employee or a manager can edit this customer.");
            var (clean, errors) = FieldCustomerRules.ValidateCustomer(input, await _repo.AllDistrictsAsync());
            if (errors.Count > 0)
                return Outcome.Fail(errors);
            var duplicates = await FindDuplicatesAsync(clean!.Phone, clean.AlternatePhone, customerId);
            var phoneChanged = FieldCustomerRules.PhoneKey(clean.Phone) != FieldCustomerRules.PhoneKey(existing.Phone)
                               || clean.AlternatePhone != existing.AlternatePhone;
            if (phoneChanged && duplicates.Count > 0 && !confirmDuplicate)
                return new Outcome(false, new[] { FieldCustomerRules.DuplicateWarning(duplicates) }, Duplicates: duplicates);
            try
            {
                await _repo.UpdateProfileAsync(customerId, userId.Value, clean,
                    locked => MayEdit(user, locked) ? null : "Only the assigned employee or a manager can edit this customer.",
                    old => DescribeChanges(old, clean));
                return new Outcome(true, Array.Empty<string>(), customerId);
            }
            catch (Exception ex)
            {
                return Outcome.Fail(DbErrorMessage.For(ex));
            }
        }

        // "Phone: 98xxxxxx10 -> ..." is never written to the history: phone changes are recorded without the numbers.
        public static string DescribeChanges(FieldCustomer old, FieldCustomerRules.CleanCustomer n)
        {
            var parts = new List<string>();
            void Cmp(string label, string? a, string? b)
            {
                if (!string.Equals(a ?? "", b ?? "", StringComparison.Ordinal))
                    parts.Add($"{label}: \"{a}\" → \"{b}\"");
            }
            Cmp("Name", old.Name, n.Name);
            Cmp("Contact", old.ContactPerson, n.ContactPerson);
            if (old.Phone != n.Phone) parts.Add("Phone changed");
            if ((old.AlternatePhone ?? "") != (n.AlternatePhone ?? "")) parts.Add("Alternate phone changed");
            Cmp("Type", old.CustomerType, n.CustomerType);
            Cmp("Village / city", old.VillageCity, n.VillageCity);
            Cmp("District", old.District, n.District);
            Cmp("Address", old.Address, n.Address);
            return string.Join("; ", parts);
        }

        // ---- manager actions --------------------------------------------------------------------------------------------

        private async Task<Outcome> ManagerAsync(ClaimsPrincipal user, int customerId, Func<int, Task> act)
        {
            var userId = user.GetUserId();
            if (userId == null || !MayManage(user))
                return Outcome.Fail("Only a field manager can do this.");
            if (await GetVisibleAsync(user, customerId) == null)
                return Outcome.Fail("Customer not found.");
            try
            {
                await act(userId.Value);
                return new Outcome(true, Array.Empty<string>(), customerId);
            }
            catch (Exception ex)
            {
                return Outcome.Fail(DbErrorMessage.For(ex));
            }
        }

        public Task<Outcome> SetStatusAsync(ClaimsPrincipal user, int customerId, string? status, string? comment)
        {
            if (FieldCustomerRules.ValidateLeadStatus(status) is { } e) return Task.FromResult(Outcome.Fail(e));
            var note = FieldCustomerRules.Clean(comment);
            if (note != null && note.Length > FieldCustomerRules.MaxReasonLength)
                return Task.FromResult(Outcome.Fail($"The comment can be at most {FieldCustomerRules.MaxReasonLength} characters."));
            return ManagerAsync(user, customerId, by => _repo.SetStatusAsync(customerId, by, status!, note,
                locked => locked.IsActive ? null : "Restore the customer first."));
        }

        public Task<Outcome> ArchiveAsync(ClaimsPrincipal user, int customerId, string? reason)
        {
            if (FieldCustomerRules.ValidateArchiveReason(reason) is { } e) return Task.FromResult(Outcome.Fail(e));
            return ManagerAsync(user, customerId, by => _repo.ArchiveAsync(customerId, by, FieldCustomerRules.Clean(reason)!,
                locked => locked.IsActive ? null : "The customer is already archived."));
        }

        public Task<Outcome> RestoreAsync(ClaimsPrincipal user, int customerId)
            => ManagerAsync(user, customerId, by => _repo.RestoreAsync(customerId, by, locked => locked.IsActive ? "The customer is not archived." : null));

        // Single (Details page) or bulk (Reassign page) move to an active field employee, with their pending follow-ups.
        public async Task<Outcome> ReassignAsync(ClaimsPrincipal user, IReadOnlyCollection<int> customerIds, int? fromUserId, int toUserId, string? reason = null)
        {
            var userId = user.GetUserId();
            if (userId == null || !MayManage(user))
                return Outcome.Fail("Only a field manager can reassign customers.");
            var employees = await _repo.ListAssignableEmployeesAsync();
            var error = FieldCustomerRules.ValidateReassign(fromUserId, toUserId, employees.Any(e => e.UserId == toUserId), customerIds.Count);
            if (error != null)
                return Outcome.Fail(error);
            try
            {
                var moved = await _repo.ReassignAsync(customerIds, fromUserId, toUserId, userId.Value, FieldCustomerRules.Clean(reason));
                return new Outcome(true, Array.Empty<string>(), moved);
            }
            catch (Exception ex)
            {
                return Outcome.Fail(DbErrorMessage.For(ex));
            }
        }

        // ---- calls / notes / follow-ups ---------------------------------------------------------------------------------

        public sealed record NoteRequest(int CustomerId, string? NoteType, string? Text, int? VisitId, string? NewLeadStatus, bool CompletesFollowUp,
            DateTime? NextFollowUpDate, string? NextFollowUpNote, string? SubmitToken);

        public async Task<Outcome> AddNoteAsync(ClaimsPrincipal user, NoteRequest req)
        {
            var userId = user.GetUserId();
            var token = ParseToken(req.SubmitToken);
            if (userId == null || token == null)
                return Outcome.Fail("This form has expired. Open it again.");
            var customer = await GetVisibleAsync(user, req.CustomerId);
            if (customer == null)
                return Outcome.Fail("Customer not found.");
            var manage = MayManage(user);
            var isCorrection = req.VisitId.HasValue;
            var type = isCorrection ? FieldCustomerRules.NoteCorrection : req.NoteType;
            var errors = new List<string>();
            if (type is not (FieldCustomerRules.NoteCall or FieldCustomerRules.NoteNote or FieldCustomerRules.NoteCorrection))
                errors.Add("Choose Call or Note.");
            var text = FieldCustomerRules.Clean(req.Text);
            if (text == null) errors.Add(isCorrection ? "Write the correction." : "Write what was discussed.");
            else if (text.Length > FieldCustomerRules.MaxNoteLength) errors.Add($"The note can be at most {FieldCustomerRules.MaxNoteLength} characters.");
            var newStatus = FieldCustomerRules.Clean(req.NewLeadStatus);
            if (isCorrection) newStatus = null;   // a correction is about a past visit, never a status change
            if (newStatus != null && FieldCustomerRules.ValidateLeadStatus(newStatus) is { } se) errors.Add(se);
            var nextDate = isCorrection ? null : req.NextFollowUpDate;
            var nextNote = FieldCustomerRules.Clean(req.NextFollowUpNote);
            if (FieldCustomerRules.ValidateFollowUp(nextDate, nextNote, AppClock.Today) is { } fe) errors.Add(fe);
            if (errors.Count > 0)
                return Outcome.Fail(errors);

            var completes = !isCorrection && req.CompletesFollowUp;
            try
            {
                var result = await _repo.AddNoteAsync(
                    new FieldCustomerRepository.NoteCommand(req.CustomerId, userId.Value, type!, text!, req.VisitId, newStatus, completes, nextDate,
                        nextDate.HasValue ? nextNote : null, token.Value),
                    (locked, pending, visitOwner) =>
                    {
                        if (isCorrection)
                        {
                            if (!FieldCustomerRules.MaySeeCustomer(locked.AssignedUserId, locked.CreatedByUserId, userId, manage)
                                || !FieldCustomerRules.MayCorrectVisit(visitOwner!.Value, userId, manage))
                                return "Only the employee who made the visit, or a manager, can add a correction.";
                            return null;
                        }
                        if (!FieldCustomerRules.MayWork(locked.AssignedUserId, userId, manage, locked.IsActive))
                            return locked.IsActive ? "Only the assigned employee or a manager can log calls and notes." : "The customer is archived.";
                        return FieldCustomerRules.ValidateNextFollowUpSlot(pending != null,
                            FieldCustomerRules.CompletesPending(pending != null, false, completes), nextDate);
                    });
                return new Outcome(true, Array.Empty<string>(), result.Id,
                    AskNextDate: !result.Replay && FieldCustomerRules.AskForNextDate(result.CompletedFollowUp, nextDate), Replay: result.Replay);
            }
            catch (Exception ex)
            {
                return Outcome.Fail(DbErrorMessage.For(ex));
            }
        }

        public async Task<Outcome> SetFollowUpAsync(ClaimsPrincipal user, int customerId, DateTime? date, string? note)
        {
            var userId = user.GetUserId();
            if (await GetVisibleAsync(user, customerId) == null || userId == null)
                return Outcome.Fail("Customer not found.");
            if (date == null)
                return Outcome.Fail("Choose the follow-up date.");
            var n = FieldCustomerRules.Clean(note);
            if (FieldCustomerRules.ValidateFollowUp(date, n, AppClock.Today) is { } e)
                return Outcome.Fail(e);
            var manage = MayManage(user);
            try
            {
                var id = await _repo.SetFollowUpAsync(customerId, userId.Value, date.Value, n, (locked, pending) =>
                    !FieldCustomerRules.MayWork(locked.AssignedUserId, userId, manage, locked.IsActive)
                        ? "Only the assigned employee or a manager can set follow-ups."
                        : pending != null ? "This customer already has a pending follow-up." : null);
                return new Outcome(true, Array.Empty<string>(), id);
            }
            catch (Exception ex)
            {
                return Outcome.Fail(DbErrorMessage.For(ex));
            }
        }

        public async Task<Outcome> CancelFollowUpAsync(ClaimsPrincipal user, int followUpId, string? reason)
        {
            var userId = user.GetUserId();
            var f = await _repo.GetFollowUpAsync(followUpId);
            if (f == null || userId == null || await GetVisibleAsync(user, f.CustomerId) == null)
                return Outcome.Fail("Follow-up not found.");
            var r = FieldCustomerRules.Clean(reason);
            if (r == null) return Outcome.Fail("Write why the follow-up is cancelled.");
            if (r.Length > FieldCustomerRules.MaxReasonLength) return Outcome.Fail($"The reason can be at most {FieldCustomerRules.MaxReasonLength} characters.");
            var manage = MayManage(user);
            try
            {
                await _repo.CancelFollowUpAsync(followUpId, f.CustomerId, userId.Value, r, (locked, pending) =>
                    pending == null ? "The follow-up is no longer pending."
                    : !FieldCustomerRules.MayWork(locked.AssignedUserId, userId, manage, true) ? "Only the assigned employee or a manager can cancel it." : null);
                return new Outcome(true, Array.Empty<string>(), f.CustomerId);
            }
            catch (Exception ex)
            {
                return Outcome.Fail(DbErrorMessage.For(ex));
            }
        }

        public const int MaxFollowUpRows = 500;

        // Employee: own pending follow-ups (all dates). Manager: the team's, optionally one employee / overdue only.
        public Task<List<FieldFollowUp>> ListFollowUpsAsync(ClaimsPrincipal user, int? employeeId, bool overdueOnly)
        {
            var manage = MayManage(user);
            return _repo.ListPendingAsync(user.GetUserId() ?? 0, manage, manage ? employeeId : null,
                manage && overdueOnly ? AppClock.Today.AddDays(-1) : null, MaxFollowUpRows);
        }

        public async Task<FieldFollowUpCounts> CountDueAsync(ClaimsPrincipal user)
        {
            var userId = user.GetUserId();
            if (userId == null || !await _repo.SchemaReadyAsync())
                return new FieldFollowUpCounts(0, 0);
            return await _repo.CountDueAsync(userId.Value, AppClock.Today);
        }
    }
}
