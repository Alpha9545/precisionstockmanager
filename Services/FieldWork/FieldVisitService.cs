using System.Security.Claims;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Services.FieldWork
{
    // Field Work Phase 2 (2026-11-03): visits -- a GPS check-in on the employee's OPEN Field Day for today (Start Day done),
    // with what was discussed, items given, live photos, a lead status update and the next follow-up. Saved visits change
    // only by a manager (text and items; logged); an employee adds a Correction note instead. No Razor dependency.
    public class FieldVisitService
    {
        private readonly FieldVisitRepository _visits;
        private readonly FieldCustomerService _customers;
        private readonly FieldPhotoStore _photos;
        private readonly FieldWorkOptions _options;

        public FieldVisitService(FieldVisitRepository visits, FieldCustomerService customers, FieldPhotoStore photos, FieldWorkOptions options)
        {
            _visits = visits;
            _customers = customers;
            _photos = photos;
            _options = options;
        }

        public FieldWorkOptions Options => _options;

        public Task<bool> SchemaReadyAsync() => _customers.SchemaReadyAsync();

        public const string NoOpenDayMessage = "You have no open Field Day today. Do Start Day first, then log the visit.";
        public const string DayClosedMessage = "Your Field Day for today is closed, so visits cannot be logged. Log a call / note instead, or ask your manager to reopen the day.";

        // A visit needs TODAY's Field Day to be Open (null = allowed, otherwise the message). Status of today's day, or null.
        public static string? CheckOpenDay(string? todayDayStatus)
            => todayDayStatus == FieldDayRules.StatusOpen ? null
               : todayDayStatus == null ? NoOpenDayMessage
               : DayClosedMessage;

        public async Task<string?> CheckOpenDayAsync(ClaimsPrincipal user)
            => user.GetUserId() is { } id ? CheckOpenDay(await _visits.GetTodayStatusAsync(id, AppClock.Today)) : NoOpenDayMessage;

        public async Task<bool> HasOpenDayAsync(ClaimsPrincipal user) => await CheckOpenDayAsync(user) == null;

        public sealed record VisitRequest(int CustomerId, decimal? Latitude, decimal? Longitude, decimal? AccuracyM, string? GpsExceptionReason,
            string? Discussed, string? Notes, IReadOnlyList<string?> ItemTexts, IReadOnlyList<string?> ItemQuantities, IReadOnlyList<byte[]> Photos,
            int? ConsentReminderVersion, string? NewLeadStatus, DateTime? NextFollowUpDate, string? NextFollowUpNote, string? SubmitToken);

        // Everything that can be checked without the database (unit-tested).
        public static IReadOnlyList<string> Validate(VisitRequest r, FieldWorkOptions options, FieldPhotoStore photos, DateTime todayIst,
            out FieldCustomerRules.PositionCheck position, out IReadOnlyList<FieldCustomerRules.Item> items)
        {
            var errors = new List<string>();
            position = FieldCustomerRules.CheckPosition(r.Latitude, r.Longitude, r.AccuracyM, r.GpsExceptionReason, options.MaxGpsAccuracyMeters,
                required: true, "Visit");
            errors.AddRange(position.Errors);
            var discussed = FieldCustomerRules.Clean(r.Discussed);
            if (discussed == null) errors.Add("Write what was discussed.");
            else if (discussed.Length > FieldCustomerRules.MaxDiscussedLength) errors.Add($"\"Discussed\" can be at most {FieldCustomerRules.MaxDiscussedLength} characters.");
            var notes = FieldCustomerRules.Clean(r.Notes);
            if (notes != null && notes.Length > FieldCustomerRules.MaxVisitNotesLength) errors.Add($"The notes can be at most {FieldCustomerRules.MaxVisitNotesLength} characters.");
            var (parsed, itemErrors) = FieldCustomerRules.ValidateItems(r.ItemTexts, r.ItemQuantities);
            items = parsed;
            errors.AddRange(itemErrors);
            var status = FieldCustomerRules.Clean(r.NewLeadStatus);
            if (status != null && FieldCustomerRules.ValidateLeadStatus(status) is { } se) errors.Add(se);
            if (FieldCustomerRules.ValidateFollowUp(r.NextFollowUpDate, r.NextFollowUpNote, todayIst) is { } fe) errors.Add(fe);
            var max = options.EffectiveMaxPhotosPerVisit;
            if (r.Photos.Count > max)
                errors.Add(max == 0 ? "Photos are not enabled for visits." : $"At most {max} photos per visit.");
            for (var i = 0; i < r.Photos.Count; i++)
                if (photos.Validate(r.Photos[i]) is { } pe)
                    errors.Add($"Photo {i + 1}: {pe}");
            if (r.Photos.Count > 0 && r.ConsentReminderVersion != options.CustomerPhotoConsentVersion)
                errors.Add("The photo permission reminder has changed. Reload the page and take the photos again.");
            return errors;
        }

        public async Task<FieldCustomerService.Outcome> CreateAsync(ClaimsPrincipal user, VisitRequest req)
        {
            var userId = user.GetUserId();
            if (userId == null || !Guid.TryParse(req.SubmitToken, out var token) || token == Guid.Empty)
                return FieldCustomerService.Outcome.Fail("This form has expired. Open the visit form again.");
            var customer = await _customers.GetVisibleAsync(user, req.CustomerId);
            if (customer == null)
                return FieldCustomerService.Outcome.Fail("Customer not found.");
            if (!FieldCustomerService.MayWork(user, customer))
                return FieldCustomerService.Outcome.Fail(customer.IsActive ? "Only the assigned employee can log a visit for this customer." : "The customer is archived.");
            if (await CheckOpenDayAsync(user) is { } dayError)
                return FieldCustomerService.Outcome.Fail(dayError);

            var today = AppClock.Today;
            var errors = Validate(req, _options, _photos, today, out var pos, out var items);
            if (errors.Count > 0)
                return FieldCustomerService.Outcome.Fail(errors);

            var stored = new List<string>();
            try
            {
                var photos = new List<FieldVisitRepository.PhotoInput>();
                foreach (var p in req.Photos)
                {
                    var name = await _photos.SaveAsync(p);
                    stored.Add(name);
                    photos.Add(new FieldVisitRepository.PhotoInput(name, p.Length));
                }
                var manage = FieldCustomerService.MayManage(user);
                var next = req.NextFollowUpDate;
                var result = await _visits.CreateAsync(new FieldVisitRepository.CreateCommand(req.CustomerId, userId.Value, today, pos.Position,
                        pos.Position == null || pos.Flags.Count > 0 ? pos.Reason : null, FieldDayRules.JoinFlags(pos.Flags),
                        FieldCustomerRules.Clean(req.Discussed)!, FieldCustomerRules.Clean(req.Notes), items, photos, _options.CustomerPhotoConsentVersion,
                        FieldCustomerRules.Clean(req.NewLeadStatus), next, next.HasValue ? FieldCustomerRules.Clean(req.NextFollowUpNote) : null, token),
                    (locked, pending) => FieldCustomerRules.MayWork(locked.AssignedUserId, userId, manage, locked.IsActive)
                        ? null
                        : "The customer was reassigned or archived meanwhile.");
                if (result.Replay)
                    foreach (var s in stored) _photos.DeleteQuietly(s);
                return new FieldCustomerService.Outcome(true, Array.Empty<string>(), result.Id,
                    AskNextDate: !result.Replay && FieldCustomerRules.AskForNextDate(result.CompletedFollowUp, next), Replay: result.Replay);
            }
            catch (Exception ex)
            {
                foreach (var s in stored) _photos.DeleteQuietly(s);
                return FieldCustomerService.Outcome.Fail(DbErrorMessage.For(ex));
            }
        }

        // The visit, only when its customer is visible to this user (else null -> 404).
        public async Task<(FieldVisit Visit, FieldCustomer Customer)?> GetVisibleAsync(ClaimsPrincipal user, int visitId)
        {
            var visit = await _visits.GetAsync(visitId);
            if (visit == null)
                return null;
            var customer = await _customers.GetVisibleAsync(user, visit.CustomerId);
            return customer == null ? null : (visit, customer);
        }

        public async Task<FieldCustomerService.Outcome> EditAsync(ClaimsPrincipal user, int visitId, string? discussed, string? notes,
            IReadOnlyList<string?> itemTexts, IReadOnlyList<string?> itemQuantities)
        {
            var userId = user.GetUserId();
            if (userId == null || !FieldCustomerService.MayManage(user))
                return FieldCustomerService.Outcome.Fail("Only a field manager can edit a saved visit.");
            if (await GetVisibleAsync(user, visitId) == null)
                return FieldCustomerService.Outcome.Fail("Visit not found.");
            var errors = new List<string>();
            var d = FieldCustomerRules.Clean(discussed);
            if (d == null) errors.Add("Write what was discussed.");
            else if (d.Length > FieldCustomerRules.MaxDiscussedLength) errors.Add($"\"Discussed\" can be at most {FieldCustomerRules.MaxDiscussedLength} characters.");
            var n = FieldCustomerRules.Clean(notes);
            if (n != null && n.Length > FieldCustomerRules.MaxVisitNotesLength) errors.Add($"The notes can be at most {FieldCustomerRules.MaxVisitNotesLength} characters.");
            var (items, itemErrors) = FieldCustomerRules.ValidateItems(itemTexts, itemQuantities);
            errors.AddRange(itemErrors);
            if (errors.Count > 0)
                return FieldCustomerService.Outcome.Fail(errors);
            try
            {
                await _visits.EditAsync(visitId, userId.Value, d!, n, items, _ => null, old => DescribeChanges(old, d!, n, items));
                return new FieldCustomerService.Outcome(true, Array.Empty<string>(), visitId);
            }
            catch (Exception ex)
            {
                return FieldCustomerService.Outcome.Fail(DbErrorMessage.For(ex));
            }
        }

        public static string DescribeChanges(FieldVisit old, string discussed, string? notes, IReadOnlyList<FieldCustomerRules.Item> items)
        {
            var parts = new List<string>();
            if (old.Discussed != discussed) parts.Add($"Discussed was: \"{old.Discussed}\"");
            if ((old.Notes ?? "") != (notes ?? "")) parts.Add($"Notes were: \"{old.Notes}\"");
            var oldItems = string.Join(", ", old.Items.Select(i => FieldCustomerRules.ItemLabel(i.ItemText, i.Quantity)));
            var newItems = string.Join(", ", items.Select(i => FieldCustomerRules.ItemLabel(i.Text, i.Quantity)));
            if (oldItems != newItems) parts.Add($"Items were: \"{oldItems}\" → \"{newItems}\"");
            return string.Join("; ", parts);
        }
    }
}
