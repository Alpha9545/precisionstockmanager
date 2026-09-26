namespace PlantStockManager.Services
{
    // Direct customer sale of Potted Plant Stock (DispatchRepository.DirectSaleAsync).
    // Not tied to any particular Area type -- an authorized user (checked by the
    // caller's own permission/Area-access policy) may sell READY stock from any
    // active Area they can reach. Only readiness (available quantity) and the
    // Area being active are business rules here.
    public static class DispatchRules
    {
        public static (bool Ok, string? Error) ValidateDirectSale(
            decimal quantity, string? customerName, bool areaExists, bool areaActive, decimal available)
        {
            if (quantity <= 0 || !DirectSowingRules.IsWholeNumber(quantity))
                return (false, "Quantity must be a whole number greater than zero.");
            if (string.IsNullOrWhiteSpace(customerName))
                return (false, "Customer name is required.");
            if (!areaExists || !areaActive)
                return (false, "This stock does not belong to an active Area.");
            if (quantity > available)
                return (false, $"Only {available:N0} plants are available (not reserved or in transit).");
            return (true, null);
        }
    }
}
