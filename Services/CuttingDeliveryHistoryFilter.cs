namespace PlantStockManager.Services
{
    // Correction #7 -- the filters of the Cutting Delivery History. All optional, combined with AND, applied by the database.
    // Every filter maps onto data that is ALREADY stored (nothing is duplicated or guessed):
    //   Date range          the record's date: the cutting date of the Cutting Entry, or -- for an older delivery that is not
    //                       linked to an entry -- the day the delivery was made
    //   Source Area         the Area the cuttings were cut in / the delivery left from
    //   Source Polyhouse    the Mother Plant's Polyhouse (MotherPlants.PolyhouseId) of the Cutting Entry
    //   Mother Plant        the Cutting Entry's Mother Plant
    //   Cutting Supervisor  the Cutting Entry's supervisor (CuttingProductions.SupervisorId)
    //   Destination         Main Office / Use for Pot Production / Not recorded (CuttingProductions.DestinationType)
    //   Destination Area    the Main Office Area of the delivery (or, for Pot Production, the Area itself)
    //   Delivery status     from InternalTransfers.Status (+ the shortfall of a completed one), or "no delivery"
    //   Entered By          InternalTransfers.CreatedBy / CuttingProductions.CreatedBy (the username that entered it)
    //   Received By         InternalTransfers.ConfirmedBy (the user who confirmed receipt at Main Office)
    // There is NO Destination Polyhouse filter: a cutting delivery does not record one (see CuttingDeliveryHistoryRow).
    // Area SECURITY is not a filter: the repository always restricts the query to the user's Areas.
    public sealed class CuttingDeliveryHistoryFilter
    {
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
        public int? SourceAreaId { get; set; }
        public int? SourcePolyhouseId { get; set; }
        public int? MotherPlantId { get; set; }
        public int? SupervisorId { get; set; }
        public string? Destination { get; set; }
        public int? DestinationAreaId { get; set; }
        public string? DeliveryStatus { get; set; }
        public string? EnteredBy { get; set; }
        public int? ReceivedById { get; set; }

        public const int MaxNameLength = 100;

        // ---- vocabulary ----------------------------------------------------------------------------------
        public const string StatusPending = "PendingConfirmation";
        public const string StatusCompleted = "Completed";                 // received in full
        public const string StatusShortfall = "CompletedShortfall";       // completed, fewer received than sent (transit loss)
        public const string StatusRejected = "Rejected";
        public const string StatusNone = "None";                           // no delivery (Pot Production / destination not recorded)

        public static readonly IReadOnlyList<(string Value, string Label)> DestinationOptions = new[]
        {
            (CuttingDestination.MainOffice, CuttingDestination.Label(CuttingDestination.MainOffice)),
            (CuttingDestination.PotProduction, CuttingDestination.Label(CuttingDestination.PotProduction)),
            (CuttingProductionFilter.DestinationNotRecorded, "Not recorded (older entries)")
        };

        public static readonly IReadOnlyList<(string Value, string Label)> StatusOptions = new[]
        {
            (StatusPending, "Awaiting Main Office"),
            (StatusCompleted, "Completed - received in full"),
            (StatusShortfall, "Completed - with shortfall (transit loss)"),
            (StatusRejected, "Rejected"),
            (StatusNone, "No delivery (Pot Production / not recorded)")
        };

        // Turns whatever arrived in the query string into safe values (ids <= 0 and blanks -> no filter, unknown Destination /
        // status -> no filter, dates as whole days, From after To put in order). Returns the notices the page shows.
        public List<string> Normalize()
        {
            var notices = new List<string>();
            SourceAreaId = Positive(SourceAreaId);
            SourcePolyhouseId = Positive(SourcePolyhouseId);
            MotherPlantId = Positive(MotherPlantId);
            SupervisorId = Positive(SupervisorId);
            DestinationAreaId = Positive(DestinationAreaId);
            ReceivedById = Positive(ReceivedById);

            Destination = string.IsNullOrWhiteSpace(Destination) ? null : Destination.Trim();
            if (Destination != null && !DestinationOptions.Any(o => o.Value == Destination))
            {
                notices.Add($"Unknown destination '{Destination}' was ignored.");
                Destination = null;
            }
            DeliveryStatus = string.IsNullOrWhiteSpace(DeliveryStatus) ? null : DeliveryStatus.Trim();
            if (DeliveryStatus != null && !StatusOptions.Any(o => o.Value == DeliveryStatus))
            {
                notices.Add($"Unknown delivery status '{DeliveryStatus}' was ignored.");
                DeliveryStatus = null;
            }
            EnteredBy = string.IsNullOrWhiteSpace(EnteredBy) ? null : EnteredBy.Trim();
            if (EnteredBy != null && EnteredBy.Length > MaxNameLength)
            {
                notices.Add("The 'Entered By' value was too long and was ignored.");
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

        public bool HasAnyFilter =>
            From.HasValue || To.HasValue || SourceAreaId.HasValue || SourcePolyhouseId.HasValue || MotherPlantId.HasValue || SupervisorId.HasValue
            || Destination != null || DestinationAreaId.HasValue || DeliveryStatus != null || EnteredBy != null || ReceivedById.HasValue;

        private static int? Positive(int? id) => id is > 0 ? id : null;

        public static string StatusLabel(string? key) => StatusOptions.FirstOrDefault(o => o.Value == key).Label ?? (key ?? "-");
        public static string DestinationLabel(string? value) => DestinationOptions.FirstOrDefault(o => o.Value == value).Label ?? "-";
    }
}
