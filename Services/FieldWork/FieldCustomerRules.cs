using System.Globalization;
using System.Text.RegularExpressions;

namespace PlantStockManager.Services.FieldWork
{
    // Field Work Phase 2 (2026-11-03): the pure customer / visit / note / follow-up rules (no database, no HTTP). The
    // database re-checks the shape of every row (2026-11-03_FieldWorkCustomers.sql CHECK constraints + triggers).
    public static class FieldCustomerRules
    {
        public const int MaxNameLength = 150;
        public const int MaxVillageLength = 100;
        public const int MaxDistrictLength = 100;
        public const int MaxAddressLength = 400;
        public const int MaxDiscussedLength = 1000;
        public const int MaxVisitNotesLength = 2000;
        public const int MaxNoteLength = 1000;
        public const int MaxFollowUpNoteLength = 500;
        public const int MaxReasonLength = 300;
        public const int MaxItems = 20;
        public const int MaxItemTextLength = 200;
        public const decimal MaxItemQuantity = 99_999_999m;
        public const int MaxFollowUpDaysAhead = 365;
        public const int MaxSearchLength = 100;

        public static readonly IReadOnlyDictionary<string, string> CustomerTypes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Farmer"] = "Farmer", ["Nursery"] = "Nursery", ["Dealer"] = "Dealer",
            ["Florist"] = "Florist", ["Institution"] = "Institution", ["Other"] = "Other",
        };

        public static readonly IReadOnlyDictionary<string, string> LeadStatuses = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["New"] = "New", ["Contacted"] = "Contacted", ["Interested"] = "Interested", ["SampleGiven"] = "Sample Given",
            ["Negotiation"] = "Negotiation", ["Won"] = "Won", ["Lost"] = "Lost",
        };

        // Leads that should always have a next follow-up ("no follow-up set" filter).
        public static readonly IReadOnlyList<string> FollowUpExpectedStatuses = new[] { "Interested", "SampleGiven", "Negotiation" };

        public const string NoteCall = "Call";
        public const string NoteNote = "Note";
        public const string NoteCorrection = "Correction";

        public const string FollowUpPending = "Pending";
        public const string FollowUpDone = "Done";
        public const string FollowUpCancelled = "Cancelled";

        public static readonly IReadOnlyList<int> NoVisitDayOptions = new[] { 7, 15, 30, 60 };

        public static string LeadLabel(string? code) => code != null && LeadStatuses.TryGetValue(code, out var l) ? l : code ?? "";

        public static string? Clean(string? text)
        {
            var t = (text ?? "").Trim();
            return t.Length == 0 ? null : t;
        }

        // ---- phone ------------------------------------------------------------------------------------------------------

        private static readonly Regex PhoneChars = new(@"^[0-9+\-\s().]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Digits only, 10-13 digits (mobile, or a landline with STD code / country code). null + error when invalid.
        public static (string? Digits, string? Error) NormalizePhone(string? input, string label = "Phone")
        {
            var raw = (input ?? "").Trim();
            if (raw.Length == 0)
                return (null, null);
            if (!PhoneChars.IsMatch(raw))
                return (null, $"{label}: only digits, spaces, + and - are allowed.");
            var digits = new string(raw.Where(char.IsAsciiDigit).ToArray());
            if (digits.Length < 10 || digits.Length > 13)
                return (null, $"{label} must have 10 to 13 digits.");
            return (digits, null);
        }

        // Duplicate matching key: the last 10 digits (so 09876543210, +919876543210 and 9876543210 match).
        public static string PhoneKey(string digits) => digits.Length <= 10 ? digits : digits[^10..];

        public static string MaskPhone(string? digits)
        {
            if (string.IsNullOrEmpty(digits))
                return "";
            if (digits.Length <= 4)
                return new string('x', digits.Length);
            return digits[..2] + new string('x', digits.Length - 4) + digits[^2..];
        }

        // ---- district ---------------------------------------------------------------------------------------------------

        private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

        // Trimmed, inner whitespace collapsed; an existing value that matches case-insensitively is reused as it is spelled.
        public static string? NormalizeDistrict(string? input, IEnumerable<string> existing)
        {
            var d = Spaces.Replace((input ?? "").Trim(), " ");
            if (d.Length == 0)
                return null;
            var match = existing.FirstOrDefault(e => string.Equals(Spaces.Replace(e.Trim(), " "), d, StringComparison.OrdinalIgnoreCase));
            return match != null ? Spaces.Replace(match.Trim(), " ") : d;
        }

        // ---- position (as Phase 1: refused when poor / missing unless a reason is given; then flagged) ------------------

        public sealed record PositionCheck(IReadOnlyList<string> Errors, IReadOnlyList<string> Flags, FieldDayRules.Position? Position, string? Reason);

        public static PositionCheck CheckPosition(decimal? lat, decimal? lng, decimal? acc, string? reason, decimal maxAccuracyMeters,
            bool required, string label)
        {
            var errors = new List<string>();
            var flags = new List<string>();
            var r = Clean(reason);
            if (r != null && r.Length > MaxReasonLength)
                errors.Add($"The reason can be at most {MaxReasonLength} characters.");
            FieldDayRules.Position? position = null;
            var given = new[] { lat.HasValue, lng.HasValue, acc.HasValue };
            if (given.Any(g => g) && !given.All(g => g))
                errors.Add("The GPS position is incomplete. Tap \"Retry GPS\".");
            else if (given.All(g => g))
            {
                if (lat < -90 || lat > 90 || lng < -180 || lng > 180 || acc <= 0 || acc > 100_000)
                    errors.Add("The GPS position is not valid. Tap \"Retry GPS\".");
                else
                {
                    position = new FieldDayRules.Position(decimal.Round(lat!.Value, 6), decimal.Round(lng!.Value, 6), decimal.Round(acc!.Value, 1));
                    if (acc > maxAccuracyMeters)
                    {
                        if (r == null)
                            errors.Add($"GPS accuracy is too poor ({acc:0} m; at most {maxAccuracyMeters:0} m is accepted). Move to open sky and tap \"Retry GPS\", or give a reason.");
                        else
                            flags.Add($"{label}: poor GPS accuracy {acc:0} m ({r})");
                    }
                }
            }
            else if (required)
            {
                if (r == null)
                    errors.Add("Location is required. Allow location access and tap \"Retry GPS\", or give a reason why GPS is not working.");
                else
                    flags.Add($"{label}: no GPS ({r})");
            }
            return new PositionCheck(errors, flags, position, r);
        }

        // ---- customer ---------------------------------------------------------------------------------------------------

        public sealed record CustomerInput(string? Name, string? ContactPerson, string? Phone, string? AlternatePhone, string? CustomerType,
            string? VillageCity, string? District, string? Address);

        public sealed record CleanCustomer(string Name, string? ContactPerson, string Phone, string? AlternatePhone, string CustomerType,
            string VillageCity, string? District, string? Address);

        public static (CleanCustomer? Customer, IReadOnlyList<string> Errors) ValidateCustomer(CustomerInput input, IEnumerable<string> existingDistricts)
        {
            var errors = new List<string>();
            var name = Clean(input.Name);
            if (name == null) errors.Add("Enter the customer name.");
            else if (name.Length > MaxNameLength) errors.Add($"The name can be at most {MaxNameLength} characters.");
            var contact = Clean(input.ContactPerson);
            if (contact != null && contact.Length > MaxNameLength) errors.Add($"The contact person can be at most {MaxNameLength} characters.");
            var (phone, phoneError) = NormalizePhone(input.Phone);
            if (phoneError != null) errors.Add(phoneError);
            else if (phone == null) errors.Add("Enter the phone number.");
            var (alt, altError) = NormalizePhone(input.AlternatePhone, "Alternate phone");
            if (altError != null) errors.Add(altError);
            if (phone != null && alt != null && PhoneKey(phone) == PhoneKey(alt)) alt = null;   // same number twice
            var type = Clean(input.CustomerType);
            if (type == null || !CustomerTypes.ContainsKey(type)) errors.Add("Select the customer type.");
            var village = Clean(input.VillageCity);
            if (village == null) errors.Add("Enter the village / city.");
            else if (village.Length > MaxVillageLength) errors.Add($"The village / city can be at most {MaxVillageLength} characters.");
            var district = NormalizeDistrict(input.District, existingDistricts);
            if (district != null && district.Length > MaxDistrictLength) errors.Add($"The district can be at most {MaxDistrictLength} characters.");
            var address = Clean(input.Address);
            if (address != null && address.Length > MaxAddressLength) errors.Add($"The address can be at most {MaxAddressLength} characters.");
            return errors.Count > 0
                ? (null, errors)
                : (new CleanCustomer(name!, contact, phone!, alt, type!, village!, district, address), errors);
        }

        public sealed record DuplicateMatch(int CustomerId, string Name, string VillageCity, string AssignedName, bool IsActive);

        public static string DuplicateWarning(IReadOnlyList<DuplicateMatch> matches)
            => "This phone number is already saved: " + string.Join("; ", matches.Select(m =>
                   $"{m.Name}, {m.VillageCity} — assigned to {m.AssignedName}{(m.IsActive ? "" : " (archived)")}"))
               + ". Save anyway only if this is a different customer.";

        // ---- who may do what --------------------------------------------------------------------------------------------

        public static bool MaySeeCustomer(int assignedUserId, int createdByUserId, int? viewerUserId, bool viewerMayManage)
            => viewerMayManage || (viewerUserId.HasValue && (viewerUserId.Value == assignedUserId || viewerUserId.Value == createdByUserId));

        // Phone numbers: the assigned employee and managers only (a creator who is no longer assigned sees them masked).
        public static bool MaySeePhone(int assignedUserId, int? viewerUserId, bool viewerMayManage)
            => viewerMayManage || (viewerUserId.HasValue && viewerUserId.Value == assignedUserId);

        public static bool MayEditProfile(int assignedUserId, int? viewerUserId, bool viewerMayManage)
            => MaySeePhone(assignedUserId, viewerUserId, viewerMayManage);

        // Calls / notes / visits / status through a visit: whoever may see the (active) customer and is its assigned employee,
        // or a manager. A creator who is no longer assigned only reads.
        public static bool MayWork(int assignedUserId, int? viewerUserId, bool viewerMayManage, bool customerActive)
            => customerActive && MaySeePhone(assignedUserId, viewerUserId, viewerMayManage);

        public static bool MayCorrectVisit(int visitUserId, int? viewerUserId, bool viewerMayManage)
            => viewerMayManage || (viewerUserId.HasValue && viewerUserId.Value == visitUserId);

        public static string? ValidateLeadStatus(string? status)
            => status != null && LeadStatuses.ContainsKey(status) ? null : "Select a valid lead status.";

        // ---- visit items ------------------------------------------------------------------------------------------------

        public sealed record Item(string Text, decimal? Quantity);

        // Rows with neither text nor quantity are ignored; "Rose sample" + "10" -> Rose sample x 10.
        public static (IReadOnlyList<Item> Items, IReadOnlyList<string> Errors) ValidateItems(IReadOnlyList<string?> texts, IReadOnlyList<string?> quantities)
        {
            var items = new List<Item>();
            var errors = new List<string>();
            var n = Math.Max(texts.Count, quantities.Count);
            for (var i = 0; i < n; i++)
            {
                var text = Clean(i < texts.Count ? texts[i] : null);
                var qtyText = Clean(i < quantities.Count ? quantities[i] : null);
                if (text == null && qtyText == null)
                    continue;
                var row = items.Count + errors.Count + 1;
                if (text == null) { errors.Add($"Item {row}: enter what was given."); continue; }
                if (text.Length > MaxItemTextLength) { errors.Add($"Item {row}: at most {MaxItemTextLength} characters."); continue; }
                decimal? qty = null;
                if (qtyText != null)
                {
                    if (!decimal.TryParse(qtyText, NumberStyles.Number, CultureInfo.InvariantCulture, out var q) || q <= 0 || q > MaxItemQuantity
                        || decimal.Round(q, 2) != q)
                    {
                        errors.Add($"Item {row}: the quantity must be a positive number (at most 2 decimals).");
                        continue;
                    }
                    qty = q;
                }
                items.Add(new Item(text, qty));
            }
            if (items.Count > MaxItems)
                errors.Add($"At most {MaxItems} items can be recorded per visit.");
            return (items, errors);
        }

        public static string ItemLabel(string text, decimal? qty) => qty.HasValue ? $"{text} × {qty.Value:0.##}" : text;

        // ---- follow-ups -------------------------------------------------------------------------------------------------

        public enum FollowUpState { Overdue, Today, Upcoming }

        public static FollowUpState StateOf(DateTime dueDate, DateTime todayIst)
            => dueDate.Date < todayIst.Date ? FollowUpState.Overdue : dueDate.Date == todayIst.Date ? FollowUpState.Today : FollowUpState.Upcoming;

        public static string? ValidateFollowUp(DateTime? date, string? note, DateTime todayIst)
        {
            if (date == null)
                return null;
            if (date.Value.Date < todayIst.Date)
                return "The follow-up date cannot be in the past.";
            if (date.Value.Date > todayIst.Date.AddDays(MaxFollowUpDaysAhead))
                return $"The follow-up date can be at most {MaxFollowUpDaysAhead} days ahead.";
            var n = Clean(note);
            return n != null && n.Length > MaxFollowUpNoteLength ? $"The follow-up note can be at most {MaxFollowUpNoteLength} characters." : null;
        }

        // A visit always completes the pending follow-up; a call / note only when "This completes the follow-up" is ticked.
        public static bool CompletesPending(bool hasPending, bool isVisit, bool completesChecked)
            => hasPending && (isVisit || completesChecked);

        // A new next date replaces the pending follow-up only when that one is completed by the same entry; otherwise the
        // existing pending follow-up stays (one Pending per customer) and the new date is refused.
        public static string? ValidateNextFollowUpSlot(bool hasPending, bool completesPending, DateTime? nextDate)
            => nextDate != null && hasPending && !completesPending
                ? "This customer already has a pending follow-up. Tick \"This completes the follow-up\" to replace it, or leave the next date empty."
                : null;

        // After a follow-up was completed and no new date was set, the customer page asks for the next date (not forced).
        public static bool AskForNextDate(bool completedPending, DateTime? nextDate) => completedPending && nextDate == null;

        // ---- reassign ---------------------------------------------------------------------------------------------------

        public static string? ValidateReassign(int? fromUserId, int toUserId, bool toUserIsActiveFieldEmployee, int customerCount)
        {
            if (customerCount <= 0)
                return "Select at least one customer.";
            if (!toUserIsActiveFieldEmployee)
                return "Select an active field employee to move the customers to.";
            if (fromUserId.HasValue && fromUserId.Value == toUserId)
                return "The customers are already assigned to that employee.";
            return null;
        }

        public static string? ValidateArchiveReason(string? reason)
        {
            var r = Clean(reason);
            if (r == null) return "Write why the customer is archived.";
            return r.Length > MaxReasonLength ? $"The reason can be at most {MaxReasonLength} characters." : null;
        }
    }
}
