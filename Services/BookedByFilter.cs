namespace PlantStockManager.Services
{
    // Correction #6 -- the "Booking By" filter of the booking record lists. It reads the EXISTING booking fields only
    // (dbo.Bookings and dbo.PottedPlantBookings: BookedById, BookedByOther); nothing is stored twice.
    //
    // How a booking says who booked it:
    //   BookedById     the person chosen in the "Booked By" list when the booking was made (dbo.IMSUsers). It is NOT
    //                  necessarily the logged-in user who typed it in -- office staff book on behalf of a salesperson.
    //                  Older bookings may point at a user who is inactive or no longer exists.
    //   BookedByOther  free text for someone who is not in the list ("If not in the list above"). Used by Potted Plant
    //                  bookings; seedling bookings carry the column but do not currently fill it.
    //   neither        not recorded.
    //
    // The filter value that travels in the query string is one of:
    //   ""         everybody (the default / "All")
    //   "U:<id>"   BookedById = id
    //   "O:<text>" BookedByOther = text (trimmed; case-insensitive under the database collation)
    //   "X:none"   no BookedById and no BookedByOther ("Not recorded")
    // Anything else is ignored (everybody is shown, with a notice) and never reaches the database as SQL text: the
    // values are only ever passed as parameters (see Data/BookedBySql.cs).
    public sealed class BookedByFilter
    {
        public enum FilterKind { All, User, Other, NotRecorded }

        public FilterKind Kind { get; private init; } = FilterKind.All;
        public int UserId { get; private init; }
        public string? OtherName { get; private init; }

        public const int MaxOtherLength = 400;

        public static BookedByFilter All { get; } = new();

        public bool IsActive => Kind != FilterKind.All;

        public string Value => Kind switch
        {
            FilterKind.User => UserValue(UserId),
            FilterKind.Other => OtherValue(OtherName!),
            FilterKind.NotRecorded => NotRecordedValue,
            _ => ""
        };

        public const string NotRecordedValue = "X:none";
        public static string UserValue(int userId) => $"U:{userId}";
        public static string OtherValue(string name) => $"O:{name.Trim()}";

        // Parses the query-string value. Returns the filter and, when the value was not understood, a notice for the page.
        public static (BookedByFilter Filter, string? Notice) Parse(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return (All, null);
            var value = raw.Trim();
            if (value == NotRecordedValue)
                return (new BookedByFilter { Kind = FilterKind.NotRecorded }, null);
            if (value.StartsWith("U:", StringComparison.Ordinal)
                && int.TryParse(value.AsSpan(2), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0)
                return (new BookedByFilter { Kind = FilterKind.User, UserId = id }, null);
            if (value.StartsWith("O:", StringComparison.Ordinal))
            {
                var name = value[2..].Trim();
                if (name.Length is > 0 and <= MaxOtherLength)
                    return (new BookedByFilter { Kind = FilterKind.Other, OtherName = name }, null);
            }
            return (All, "The selected Booking By value was not recognised, so bookings of everyone are shown.");
        }

        // ---- how a person is named in lists ------------------------------------------------------------------
        public sealed record Option(string Value, string Label);

        public static string UserLabel(int userId, string? name, bool? isActive)
            => string.IsNullOrWhiteSpace(name)
                ? $"User #{userId} (no longer in the system)"
                : isActive == false ? $"{name.Trim()} (inactive)" : name.Trim();

        public static string OtherLabel(string name) => $"{name.Trim()} (other)";
        public const string NotRecordedLabel = "Not recorded";

        // The name a booking row shows in its "booked by" column: the user when there is one, else the free-text
        // name, else (a user id that no longer resolves) "User #id"; empty when nothing was recorded.
        public static string Display(int? bookedById, string? bookedByName, string? bookedByOther)
        {
            if (bookedById is > 0)
                return string.IsNullOrWhiteSpace(bookedByName)
                    ? (string.IsNullOrWhiteSpace(bookedByOther) ? $"User #{bookedById}" : bookedByOther.Trim())
                    : bookedByName.Trim();
            return string.IsNullOrWhiteSpace(bookedByOther) ? "" : bookedByOther.Trim();
        }
    }
}
