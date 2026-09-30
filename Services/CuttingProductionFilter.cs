namespace PlantStockManager.Services
{
    // Correction #5 -- the filters of the Cutting Production register. Every filter is optional and they all
    // combine with AND. The values map onto EXISTING columns/relationships only (nothing is stored twice):
    //   Date range         dbo.CuttingProductions.CuttingDate
    //   Area               CuttingProductions.AreaId            (the Mother Plant's Area = where the cuttings were cut)
    //   Mother Plant       CuttingProductions.MotherPlantId
    //   Variety            CuttingProductions.SpeciesId
    //   Polyhouse          MotherPlants.PolyhouseId             (the Mother Plant's Polyhouse)
    //   Supervisor         CuttingProductions.SupervisorId
    //   Destination        CuttingProductions.DestinationType   (Main Office / Pot Production / not recorded = older entries)
    //   Destination Area   CuttingProductions.DestinationAreaId (the Main Office Area of a Main Office delivery)
    //   Delivery status    InternalTransfers.Status of the delivery created for the entry
    //                      (InternalTransfers.SourceCuttingProductionId), or "no delivery". A cutting delivery is only ever Awaiting Main Office, Completed or Rejected (a pending one is rejected, never cancelled).
    // The register's Area SECURITY is not a filter: the repository always restricts the query to the user's Areas.
    public sealed class CuttingProductionFilter
    {
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
        public int? AreaId { get; set; }
        public int? MotherPlantId { get; set; }
        public int? SpeciesId { get; set; }
        public int? PolyhouseId { get; set; }
        public int? SupervisorId { get; set; }
        public string? Destination { get; set; }
        public int? DestinationAreaId { get; set; }
        public string? DeliveryStatus { get; set; }

        // ---- vocabulary ------------------------------------------------------------------------------------
        public const string DestinationNotRecorded = "NotRecorded";      // entries made before destinations existed
        public const string DeliveryNone = "None";                        // no delivery was created for the entry

        public static readonly IReadOnlyList<(string Value, string Label)> DestinationOptions = new[]
        {
            (CuttingDestination.MainOffice, CuttingDestination.Label(CuttingDestination.MainOffice)),
            (CuttingDestination.PotProduction, CuttingDestination.Label(CuttingDestination.PotProduction)),
            (DestinationNotRecorded, "Not recorded (older entries)")
        };

        public static readonly IReadOnlyList<(string Value, string Label)> DeliveryStatusOptions = new[]
        {
            ("PendingConfirmation", "Awaiting Main Office"),
            ("Completed", "Completed (received by Main Office)"),
            ("Rejected", "Rejected"),
            (DeliveryNone, "No delivery (Pot Production / older entries)")
        };

        // ---- normalisation ---------------------------------------------------------------------------------
        // Turns whatever arrived in the query string into safe, consistent values: blank -> no filter, ids <= 0 -> no
        // filter, unknown Destination / Delivery status values -> no filter (reported in `Notices`), dates as whole days
        // and a From that is after To are put in order. Returns the notices the page shows to the user.
        public List<string> Normalize()
        {
            var notices = new List<string>();
            AreaId = Positive(AreaId);
            MotherPlantId = Positive(MotherPlantId);
            SpeciesId = Positive(SpeciesId);
            PolyhouseId = Positive(PolyhouseId);
            SupervisorId = Positive(SupervisorId);
            DestinationAreaId = Positive(DestinationAreaId);

            Destination = string.IsNullOrWhiteSpace(Destination) ? null : Destination.Trim();
            if (Destination != null && !DestinationOptions.Any(o => o.Value == Destination))
            {
                notices.Add($"Unknown destination '{Destination}' was ignored.");
                Destination = null;
            }
            DeliveryStatus = string.IsNullOrWhiteSpace(DeliveryStatus) ? null : DeliveryStatus.Trim();
            if (DeliveryStatus != null && !DeliveryStatusOptions.Any(o => o.Value == DeliveryStatus))
            {
                notices.Add($"Unknown delivery status '{DeliveryStatus}' was ignored.");
                DeliveryStatus = null;
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

        // true when anything besides the (always present) date range narrows the register
        public bool HasNarrowingFilter =>
            AreaId.HasValue || MotherPlantId.HasValue || SpeciesId.HasValue || PolyhouseId.HasValue || SupervisorId.HasValue
            || Destination != null || DestinationAreaId.HasValue || DeliveryStatus != null;

        private static int? Positive(int? id) => id is > 0 ? id : null;

        public static string DestinationLabel(string? value) => DestinationOptions.FirstOrDefault(o => o.Value == value).Label ?? "-";
        public static string DeliveryStatusLabel(string? value) => DeliveryStatusOptions.FirstOrDefault(o => o.Value == value).Label ?? (value ?? "-");
    }
}
