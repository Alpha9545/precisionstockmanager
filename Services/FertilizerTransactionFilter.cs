namespace PlantStockManager.Services
{
    // Correction #8 -- the filters of the Fertilizer Transactions history. Every filter is optional and they all combine
    // with AND. Each maps onto data that IS stored (nothing is invented):
    //   Date range       dbo.FertilizerUsage.IssueDate
    //   Fertilizer       FertilizerStock.FertilizerId   (the batch the fertilizer was issued from)
    //   Source           FertilizerStock.SourceId       (the supplier the batch was bought from)
    //   Received By      FertilizerUsage.ReceivedById (a user) -- or, for issues made before receivers were selected, the typed
    //                    ReceivedBy text -- or "Not recorded" (no user and no text)
    //   Entered By       FertilizerUsage.EnteredById, or "Not recorded" (issues made before it was stored)
    // Not filters, because the data does not exist: Area/location (fertilizer stock is one shared store), Destination,
    // Transaction Type (the only transaction is an Issue).
    public sealed class FertilizerTransactionFilter
    {
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
        public int? FertilizerId { get; set; }
        public int? SourceId { get; set; }
        // "" / null = All; "none" = Not recorded; "u:<user id>" = a selected user; "t:<name>" = an older, typed name
        public string? Receiver { get; set; }
        // "" / null = All; "none" = Not recorded; "<user id>"
        public string? EnteredBy { get; set; }

        public const string NotRecorded = "none";
        public const string UserPrefix = "u:";
        public const string TextPrefix = "t:";

        // the parsed forms of Receiver / EnteredBy, set by Normalize()
        public enum ReceiverMode { Any, NotRecorded, User, Text }
        public ReceiverMode ReceiverKind { get; private set; }
        public int ReceiverUserId { get; private set; }
        public string? ReceiverText { get; private set; }
        public bool EnteredByNotRecorded { get; private set; }
        public int? EnteredByUserId { get; private set; }

        // Turns whatever arrived in the query string into safe values: blank / malformed -> no filter (reported in the
        // returned notices), ids <= 0 -> no filter, dates as whole days, From after To swapped. The value strings that
        // stay are the ones the page puts back into the form.
        public List<string> Normalize()
        {
            var notices = new List<string>();
            FertilizerId = FertilizerId is > 0 ? FertilizerId : null;
            SourceId = SourceId is > 0 ? SourceId : null;

            ReceiverKind = ReceiverMode.Any; ReceiverUserId = 0; ReceiverText = null;
            var receiver = string.IsNullOrWhiteSpace(Receiver) ? null : Receiver.Trim();
            if (receiver == null)
            {
                Receiver = null;
            }
            else if (receiver == NotRecorded)
            {
                ReceiverKind = ReceiverMode.NotRecorded;
                Receiver = receiver;
            }
            else if (receiver.StartsWith(UserPrefix, StringComparison.Ordinal) && int.TryParse(receiver.AsSpan(UserPrefix.Length), out var uid) && uid > 0)
            {
                ReceiverKind = ReceiverMode.User; ReceiverUserId = uid;
                Receiver = UserPrefix + uid;
            }
            else if (receiver.StartsWith(TextPrefix, StringComparison.Ordinal) && receiver.Substring(TextPrefix.Length).Trim().Length > 0)
            {
                ReceiverKind = ReceiverMode.Text; ReceiverText = receiver.Substring(TextPrefix.Length).Trim();
                Receiver = TextPrefix + ReceiverText;
            }
            else
            {
                notices.Add("The Received By choice was not recognised and was ignored.");
                Receiver = null;
            }

            EnteredByNotRecorded = false; EnteredByUserId = null;
            var enteredBy = string.IsNullOrWhiteSpace(EnteredBy) ? null : EnteredBy.Trim();
            if (enteredBy == null)
            {
                EnteredBy = null;
            }
            else if (enteredBy == NotRecorded)
            {
                EnteredByNotRecorded = true;
                EnteredBy = enteredBy;
            }
            else if (int.TryParse(enteredBy, out var eid) && eid > 0)
            {
                EnteredByUserId = eid;
                EnteredBy = eid.ToString();
            }
            else
            {
                notices.Add("The Entered By choice was not recognised and was ignored.");
                EnteredBy = null;
            }

            From = From?.Date;
            To = To?.Date;
            if (From.HasValue && To.HasValue && From > To)
            {
                (From, To) = (To, From);
                notices.Add("The From date was after the To date, so the two were swapped.");
            }
            return notices;
        }

        // A value that is not one of the dropdown choices (a made-up id, or a name that never received anything) is dropped
        // with a notice instead of silently returning an empty page. Call after Normalize().
        public List<string> RestrictTo(IEnumerable<int> fertilizerIds, IEnumerable<int> sourceIds,
                                       IEnumerable<string> receiverValues, IEnumerable<string> enteredByValues)
        {
            var notices = new List<string>();
            if (FertilizerId.HasValue && !fertilizerIds.Contains(FertilizerId.Value))
            {
                notices.Add("The chosen fertilizer has no recorded issues and was ignored.");
                FertilizerId = null;
            }
            if (SourceId.HasValue && !sourceIds.Contains(SourceId.Value))
            {
                notices.Add("The chosen source has no recorded issues and was ignored.");
                SourceId = null;
            }
            if (Receiver != null && Receiver != NotRecorded
                && !receiverValues.Contains(Receiver, StringComparer.OrdinalIgnoreCase))
            {
                notices.Add("The chosen receiver has no recorded issues and was ignored.");
                Receiver = null; ReceiverKind = ReceiverMode.Any; ReceiverUserId = 0; ReceiverText = null;
            }
            if (EnteredBy != null && EnteredBy != NotRecorded && !enteredByValues.Contains(EnteredBy))
            {
                notices.Add("The chosen Entered By person has no recorded issues and was ignored.");
                EnteredBy = null; EnteredByUserId = null;
            }
            return notices;
        }

        public bool HasAnyFilter =>
            From.HasValue || To.HasValue || FertilizerId.HasValue || SourceId.HasValue || Receiver != null || EnteredBy != null;
    }

    // How a receiver / entered-by is shown, and the rules an issue must satisfy. Pure, so it is unit-tested.
    public static class FertilizerIssueRules
    {
        public const string NotRecordedLabel = "Not recorded";
        public const string TransactionType = "Issue";

        // Received By as shown in the history: the selected user's name (marked when that user is no longer active), else the
        // name that was typed before receivers were selected, else "Not recorded". Never guessed from anything else.
        public static string ReceiverLabel(string? userName, bool? userActive, string? typedName)
        {
            if (!string.IsNullOrWhiteSpace(userName))
                return userActive == false ? $"{userName.Trim()} (inactive)" : userName.Trim();
            return string.IsNullOrWhiteSpace(typedName) ? NotRecordedLabel : typedName.Trim();
        }

        public static string EnteredByLabel(string? userName, bool? userActive)
        {
            if (string.IsNullOrWhiteSpace(userName)) return NotRecordedLabel;
            return userActive == false ? $"{userName.Trim()} (inactive)" : userName.Trim();
        }

        // (ok, error) -- the checks that do not need the database
        public static (bool Ok, string? Error) ValidateIssue(decimal quantity, DateTime issueDate, int? receiverId)
        {
            if (quantity <= 0) return (false, "Quantity to use must be greater than zero.");
            if (issueDate == default) return (false, "Issue date is required.");
            if (!receiverId.HasValue || receiverId <= 0) return (false, "Choose who received the fertilizer.");
            return (true, null);
        }

        // An identical issue (same batch, quantity, date, receiver, entered by) saved again within this many seconds is the
        // same form submitted twice (double click / refresh / two tabs), not a new issue.
        public const int DuplicateWindowSeconds = 10;
    }
}
