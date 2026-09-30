namespace PlantStockManager.Services
{
    // One relationship that can stop a parent row (an Area, a Mother Plant)
    // from being deleted: rows of Schema.Table whose Column holds the
    // parent's Id.
    public sealed record DependencyDefinition(string Label, string Schema, string Table, string Column);

    public sealed record DependencyCount(string Label, string Table, string Column, int Count);

    public enum DeleteOutcome { Deleted, Blocked, NotFound }

    public sealed record DeleteResult(DeleteOutcome Outcome, string Message, IReadOnlyList<DependencyCount> Dependencies)
    {
        public bool Succeeded => Outcome == DeleteOutcome.Deleted;
    }

    // "Does this Area hold CURRENT stock?" -- rows of dbo.Table matching the
    // fixed Condition (which filters on @Id). Columns must all exist in the
    // database for the check to run.
    public sealed record StockCheckDefinition(string Label, string Table, string[] Columns, string Condition);

    public sealed record AreaDeactivateResult(bool Succeeded, bool NotFound, string Message, IReadOnlyList<DependencyCount> Stock);

    // Dependency-aware Delete for master records. A record is deleted only
    // when NOTHING references it; otherwise nothing is changed, the blocking
    // relationships are reported, and Deactivate is the safe alternative.
    // Child records are never deleted and foreign keys are never relaxed.
    public static class DeletionRules
    {
        public const string AreaTable = "dbo.Area";
        public const string MotherPlantTable = "dbo.MotherPlants";
        public const string FertilizerStockTable = "dbo.FertilizerStock";

        // Every column known to hold a dbo.Area.Id. Most are foreign keys;
        // the Outlet item / wastage OutletAreaId columns are only reached
        // through composite stock keys, so they are listed explicitly.
        // Tables that do not exist in a database are skipped at run time.
        // Any other foreign key the database declares against dbo.Area is
        // added automatically (Data/DependencyChecker.cs).
        public static readonly IReadOnlyList<DependencyDefinition> AreaDependencies = new[]
        {
            Dbo("Mother Plants", "MotherPlants", "AreaId"),
            Dbo("Polyhouses", "Polyhouses", "AreaId"),
            Dbo("User Role assignments", "UserRoles", "AreaId"),
            Dbo("Seed Stock", "SeedStock", "AreaId"),
            Dbo("Seed Sowings", "SeedSowings", "AreaId"),
            Dbo("Ready Stock", "ReadyStock", "AreaId"),
            Dbo("Cutting Stock", "CuttingStock", "AreaId"),
            Dbo("Cutting Productions", "CuttingProductions", "AreaId"),
            Dbo("Empty Pot Stock", "EmptyPotInventory", "AreaId"),
            Dbo("Empty Pot Purchases", "EmptyPotPurchases", "AreaId"),
            Dbo("Pot Production Batches", "PotProductionBatches", "AreaId"),
            Dbo("Potted Plant Stock", "PottedPlantStock", "AreaId"),
            Dbo("Potted Plant Bookings", "PottedPlantBookings", "AreaId"),
            Dbo("Potted Plant Dispatches", "Dispatches", "AreaId"),
            Dbo("Stock Movements (from)", "InternalTransfers", "SourceAreaId"),
            Dbo("Stock Movements (to)", "InternalTransfers", "DestinationAreaId"),
            Dbo("Stock Movements (awaiting confirmation)", "InternalTransfers", "PendingConfirmationAreaId"),
            Dbo("Lab Requests", "LabRequests", "AreaId"),
            Dbo("Purchase Order lines", "PurchaseOrderItems", "AreaId"),
            Dbo("Outlet Purchases", "OutletPurchases", "OutletAreaId"),
            Dbo("Outlet Sales", "OutletSales", "OutletAreaId"),
            Dbo("Outlet Sale items", "OutletSaleItems", "OutletAreaId"),
            Dbo("Outlet Bookings", "OutletBookings", "OutletAreaId"),
            Dbo("Outlet Booking items", "OutletBookingItems", "OutletAreaId"),
            Dbo("Outlet Wastages", "OutletWastages", "OutletAreaId"),
            Dbo("Labour Logs (history)", "LabourLogs", "AreaId"),
            Dbo("Seed Issues from (history)", "SeedIssues", "SourceAreaId"),
            Dbo("Seed Issues to (history)", "SeedIssues", "DestinationAreaId"),
            Dbo("Pot Production (history)", "PotProduction", "AreaId"),
            Dbo("Propagation Batches (history)", "PropagationBatches", "AreaId"),
        };

        // Every foreign key to dbo.MotherPlants.Id.
        public static readonly IReadOnlyList<DependencyDefinition> MotherPlantDependencies = new[]
        {
            Dbo("Pot Production", "PotProduction", "MotherPlantId"),
            Dbo("Cutting Plans", "CuttingPlans", "MotherPlantId"),
            Dbo("Actual Cuttings", "ActualCuttings", "MotherPlantId"),
            Dbo("Cutting Deliveries", "CuttingDeliveries", "MotherPlantId"),
            Dbo("Propagation Batches", "PropagationBatches", "MotherPlantId"),
            Dbo("Cutting Productions", "CuttingProductions", "MotherPlantId"),
        };

        // Correction I3 -- the only foreign key to dbo.FertilizerStock.Id (FK_FertilizerUsage_Stock).
        public static readonly IReadOnlyList<DependencyDefinition> FertilizerStockDependencies = new[]
        {
            Dbo("Fertilizer Usage", "FertilizerUsage", "StockId"),
        };

        // An Area can be deactivated only when it holds no CURRENT stock or
        // work in progress (a deactivated Area disappears from the working
        // screens, so anything still in it would be hidden). Historical
        // records alone never block deactivation.
        public static readonly IReadOnlyList<StockCheckDefinition> AreaStockChecks = new[]
        {
            new StockCheckDefinition("Seed Stock", "SeedStock",
                new[] { "AreaId", "PhysicalQuantity", "InTransitQuantity" },
                "AreaId = @Id AND (PhysicalQuantity > 0 OR InTransitQuantity > 0)"),
            new StockCheckDefinition("Seed Sowings in progress", "SeedSowings",
                new[] { "AreaId", "Status" },
                "AreaId = @Id AND Status = N'Sown'"),
            new StockCheckDefinition("Ready Stock", "ReadyStock",
                new[] { "AreaId", "Quantity", "DispatchedQuantity" },
                "AreaId = @Id AND Quantity - DispatchedQuantity > 0"),
            new StockCheckDefinition("Cutting Stock", "CuttingStock",
                new[] { "AreaId", "PhysicalQuantity", "InTransitQuantity" },
                "AreaId = @Id AND (PhysicalQuantity > 0 OR InTransitQuantity > 0)"),
            new StockCheckDefinition("Potted Plant Stock", "PottedPlantStock",
                new[] { "AreaId", "PhysicalQuantity", "ReservedQuantity", "InTransitQuantity" },
                "AreaId = @Id AND (PhysicalQuantity > 0 OR ReservedQuantity > 0 OR InTransitQuantity > 0)"),
            new StockCheckDefinition("Empty Pot Stock", "EmptyPotInventory",
                new[] { "AreaId", "PhysicalQuantity" },
                "AreaId = @Id AND PhysicalQuantity > 0"),
            new StockCheckDefinition("Pot Production Batches in production", "PotProductionBatches",
                new[] { "AreaId", "Status" },
                "AreaId = @Id AND Status = N'InProduction'"),
            new StockCheckDefinition("Stock movements awaiting confirmation", "InternalTransfers",
                new[] { "Status", "SourceAreaId", "DestinationAreaId", "PendingConfirmationAreaId" },
                "Status = N'PendingConfirmation' AND (SourceAreaId = @Id OR DestinationAreaId = @Id OR PendingConfirmationAreaId = @Id)"),
        };

        public static bool CanDeactivateArea(IEnumerable<DependencyCount> stock) => stock.All(s => s.Count == 0);

        public static string AreaDeactivationBlockedMessage(IEnumerable<DependencyCount> stock)
        {
            var detail = string.Join(", ", Blocking(stock).Select(b => $"{b.Label}: {b.Count}"));
            return "This Area cannot be deactivated because it currently contains stock"
                   + (detail.Length > 0 ? $" ({detail})" : string.Empty)
                   + ". Move or clear the stock before deactivating this Area.";
        }

        // "Deactivate" for a Mother Plant is the existing 'Removed' status
        // (the Mother Plant Edit page offers Active / Completed / Removed).
        // Only 'Active' Mother Plants accept new cuttings, so a Removed batch
        // stops being used while all of its history stays.
        public const string MotherPlantDeactivatedStatus = "Removed";

        public static bool CanDeactivateMotherPlant(string? status)
            => !string.Equals(status?.Trim(), MotherPlantDeactivatedStatus, StringComparison.OrdinalIgnoreCase);

        public static bool CanDelete(IEnumerable<DependencyCount> dependencies)
            => dependencies.All(d => d.Count == 0);

        public static IReadOnlyList<DependencyCount> Blocking(IEnumerable<DependencyCount> dependencies)
            => dependencies.Where(d => d.Count > 0).ToList();

        // Blocking relationships first, then the rest, each group in list order.
        public static IReadOnlyList<DependencyCount> ForDisplay(IEnumerable<DependencyCount> dependencies)
            => dependencies.Select((d, i) => (d, i)).OrderBy(x => x.d.Count > 0 ? 0 : 1).ThenBy(x => x.i).Select(x => x.d).ToList();

        public static string BlockedMessage(string entity, IEnumerable<DependencyCount> dependencies)
        {
            var blocking = Blocking(dependencies);
            var detail = blocking.Count == 0
                ? "a related record in the database"
                : string.Join(", ", blocking.Select(b => $"{b.Label}: {b.Count}"));
            return $"This {entity} cannot be deleted because it is being used by existing records ({detail}). " +
                   $"Remove/archive the dependent records first, then delete this {entity}, or deactivate it instead.";
        }

        public static string DeletedMessage(string entity, string name) => $"{entity} '{name}' was deleted.";

        // Adds every foreign key the database reports (schema, table, column)
        // that is not already a known relationship, so none is ever missed.
        public static IReadOnlyList<DependencyDefinition> Merge(
            IEnumerable<DependencyDefinition> known, IEnumerable<(string Schema, string Table, string Column)> discovered)
        {
            var list = known.ToList();
            foreach (var (schema, table, column) in discovered)
            {
                var exists = list.Any(k => string.Equals(k.Schema, schema, StringComparison.OrdinalIgnoreCase)
                                           && string.Equals(k.Table, table, StringComparison.OrdinalIgnoreCase)
                                           && string.Equals(k.Column, column, StringComparison.OrdinalIgnoreCase));
                if (!exists)
                    list.Add(new DependencyDefinition($"{table} ({column})", schema, table, column));
            }
            return list;
        }

        // Table/column names are placed in SQL text (they cannot be
        // parameters), so only plain identifiers are ever accepted.
        public static bool IsSafeIdentifier(string? name)
            => !string.IsNullOrEmpty(name) && name.Length <= 128 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

        private static DependencyDefinition Dbo(string label, string table, string column) => new(label, "dbo", table, column);
    }
}
