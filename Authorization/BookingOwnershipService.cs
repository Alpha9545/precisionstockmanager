using System.Security.Claims;
using Microsoft.Extensions.Options;

namespace PlantStockManager.Authorization
{
    // "Own bookings only" for SEEDLING bookings (dbo.Bookings). The users
    // listed in SecurityOptions.OwnBookingsOnlyUserIds (configuration
    // Authorization:OwnBookingsOnlyUserIds, by dbo.IMSUsers.Id -- never by
    // name) may see and act on only the bookings whose BookedById is their
    // own Id. This sits ON TOP of the page permissions
    // (FeatureAuthorizationConventions), it never grants anything:
    //   * list queries take OwnerFilter(user) and add
    //     "AND b.BookedById = @Owner" in SQL (search / filters / paging / PDF
    //     can therefore never reach another user's row);
    //   * every handler that receives a booking Id checks CanAccessBooking
    //     against the booking's stored BookedById before reading or changing
    //     it, and answers "Booking not found." otherwise.
    // Full-access users (System Administrator) are never restricted.
    // Potted Plant Bookings (dbo.PottedPlantBookings) are not affected.
    public class BookingOwnershipService
    {
        private readonly HashSet<int> _restrictedUserIds;

        public BookingOwnershipService(IOptions<SecurityOptions> options)
        {
            _restrictedUserIds = new HashSet<int>((options.Value.OwnBookingsOnlyUserIds ?? Array.Empty<int>()).Where(id => id > 0));
        }

        // The signed-in user's Id when they are restricted to their own
        // bookings, otherwise null (= no owner filter).
        public int? OwnerFilter(ClaimsPrincipal user)
        {
            if (user.Identity?.IsAuthenticated != true || user.IsFullAccess())
                return null;
            var userId = user.GetUserId();
            return userId.HasValue && _restrictedUserIds.Contains(userId.Value) ? userId : null;
        }

        public bool IsRestricted(ClaimsPrincipal user) => OwnerFilter(user).HasValue;

        // True when the user may see / act on a booking with this BookedById.
        public bool CanAccessBooking(ClaimsPrincipal user, int? bookedById)
        {
            var owner = OwnerFilter(user);
            return owner == null || bookedById == owner;
        }

        // For handlers that receive only a booking Id (POSTs): the owner is read
        // from dbo.Bookings, never from the request. Unrestricted users skip
        // the lookup entirely (the handler's own "not found" handling applies).
        public async Task<bool> CanAccessBookingIdAsync(ClaimsPrincipal user, int bookingId, Data.SeedlingFulfilmentRepository repo)
        {
            if (!IsRestricted(user))
                return true;
            var (exists, bookedById) = await repo.GetBookingOwnerAsync(bookingId);
            return exists && CanAccessBooking(user, bookedById);
        }

        public const string NotFoundMessage = "Booking not found.";
        public const string OwnBookingsNotice = "Showing your bookings";
    }
}
