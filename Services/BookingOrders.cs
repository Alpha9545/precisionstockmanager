namespace PlantStockManager.Services
{
    // Seedling booking ORDERS. A customer order with several varieties ("Add
    // Another Species") is several dbo.Bookings rows -- one per variety line,
    // each with its own quantity, delivery date, status, reservations,
    // allocations and dispatches -- linked through Bookings.OrderBookingId,
    // which holds the order's first line Id: the Booking Number.
    //
    //   EffectiveOrderId = ISNULL(OrderBookingId, Id)
    //
    // (NULL = a single-line order, including every booking made before the
    // link existed and not linked by the reviewed backfill.) Grouping is for
    // display and order-level counts only; stock is never combined.
    public static class BookingOrders
    {
        // SQL for the effective order of a dbo.Bookings alias, e.g. EffectiveOrderSql("b").
        public static string EffectiveOrderSql(string alias) => $"ISNULL({alias}.OrderBookingId, {alias}.Id)";

        public const int MaxLinesPerOrder = 50;

        // "Booking #5050 – Marigold" (line-level reference); "Booking #5050" without a variety.
        public static string LineLabel(int orderId, string? variety)
            => string.IsNullOrWhiteSpace(variety) ? $"Booking #{orderId}" : $"Booking #{orderId} – {variety.Trim()}";

        public sealed class OrderGroup<T>
        {
            public int OrderId { get; init; }
            public List<T> Lines { get; init; } = new();
            public T First => Lines[0];
        }

        // Groups lines by their order, keeping the orders in the order their
        // first line appears in `items` and each order's lines by line Id.
        public static List<OrderGroup<T>> Group<T>(IEnumerable<T> items, Func<T, int> orderId, Func<T, int> lineId)
        {
            var groups = new List<OrderGroup<T>>();
            var byOrder = new Dictionary<int, OrderGroup<T>>();
            foreach (var item in items)
            {
                var key = orderId(item);
                if (!byOrder.TryGetValue(key, out var g))
                {
                    g = new OrderGroup<T> { OrderId = key };
                    byOrder[key] = g;
                    groups.Add(g);
                }
                g.Lines.Add(item);
            }
            foreach (var g in groups)
                g.Lines.Sort((a, b) => lineId(a).CompareTo(lineId(b)));
            return groups;
        }

        public sealed record NewLine(int PlantId, int SpeciesId, int Quantity, DateTime? DeliveryDate);

        // Validation of a new order's variety lines (each line is checked; one bad
        // line rejects the whole order -- nothing is saved).
        public static (bool Ok, string? Error) ValidateNewOrder(IReadOnlyList<NewLine>? lines)
        {
            if (lines == null || lines.Count == 0)
                return (false, "Add at least one variety to the booking.");
            if (lines.Count > MaxLinesPerOrder)
                return (false, $"A booking can have at most {MaxLinesPerOrder} varieties.");
            for (var i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                var n = lines.Count > 1 ? $" (variety {i + 1})" : "";
                if (l.PlantId <= 0) return (false, $"Species is required{n}.");
                if (l.SpeciesId <= 0) return (false, $"Variety is required{n}.");
                if (l.Quantity < 1) return (false, $"Quantity must be at least 1{n}.");
                if (l.DeliveryDate == null || l.DeliveryDate.Value == default) return (false, $"Delivery date is required{n}.");
            }
            return (true, null);
        }
    }
}
