namespace PlantStockManager.Services
{
    // Phase 7: Ready Potted Plant Stock may leave a production/Growing
    // Partner Area toward exactly three destinations -- Main Office,
    // Outlet, or a Direct Customer Sale. These are the pure (database-
    // free) predicates the repositories re-check under their own row
    // locks: InternalTransferRepository.InsertAsync (the
    // 'GrowingPartnerToOutlet'/'GrowingPartnerToMainOffice' branches) and
    // PottedPlantBookingRepository.InsertAsync (a Customer Booking's
    // source stock). Kept here, not duplicated inline in each repository,
    // so the "exactly these Area types" rule can never silently drift
    // apart between the transfer side and the booking side, and so it is
    // unit-testable without a database.
    public static class PottedPlantDistributionRules
    {
        public const string MainOfficeAreaType = "MainOffice";
        public const string OutletAreaType = "Outlet";

        // Destination #1: an active Main Office Area (the
        // 'GrowingPartnerToMainOffice' Internal Transfer's own
        // destination check).
        public static bool IsValidMainOfficeDestination(string? areaType, bool isActive)
            => isActive && areaType == MainOfficeAreaType;

        // Destination #2: an active Outlet Area (the
        // 'GrowingPartnerToOutlet' Internal Transfer's own destination
        // check, unchanged in behavior -- now just centralized here).
        public static bool IsValidOutletDestination(string? areaType, bool isActive)
            => isActive && areaType == OutletAreaType;

        // Destination #3: a Direct Customer Sale (PottedPlantBooking) may
        // be made against stock physically held at EITHER an Outlet OR a
        // Main Office Area -- both are customer-facing locations once
        // stock has arrived there via destinations #1/#2 above. Never
        // directly against a Growing Partner/production Area or any other
        // Area type.
        public static bool IsValidCustomerSaleSourceArea(string? areaType, bool isActive)
            => isActive && (areaType == OutletAreaType || areaType == MainOfficeAreaType);
    }
}
