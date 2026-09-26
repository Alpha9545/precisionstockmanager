namespace PlantStockManager.Services
{
    // Stock History over the stock ledgers:
    //   Opening  = balance of all movements before the period
    //   Incoming = sum of positive movements inside the period
    //   Outgoing = sum of negative movements inside the period (shown positive)
    //   Balance  = Opening + Incoming - Outgoing
    public static class StockHistoryRules
    {
        public sealed record Movement(string ItemKey, DateTime Date, decimal Quantity);

        public sealed record Summary(string ItemKey, decimal Opening, decimal Incoming, decimal Outgoing)
        {
            public decimal Balance => Opening + Incoming - Outgoing;
        }

        public static List<Summary> Summarize(IEnumerable<Movement> movements, DateTime from, DateTime to)
        {
            var start = from.Date;
            var endExclusive = to.Date.AddDays(1);
            return movements
                .Where(m => m.Date < endExclusive)
                .GroupBy(m => m.ItemKey)
                .Select(g => new Summary(
                    g.Key,
                    g.Where(m => m.Date < start).Sum(m => m.Quantity),
                    g.Where(m => m.Date >= start && m.Quantity > 0).Sum(m => m.Quantity),
                    -g.Where(m => m.Date >= start && m.Quantity < 0).Sum(m => m.Quantity)))
                .OrderBy(s => s.ItemKey, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // Ledger rows that move the physical quantity. The potted-plant and
        // Ready Stock ledgers also hold reservation rows whose quantities are
        // reservations, not stock -- they are excluded from balances.
        public static bool MovesPhysicalStock(string transactionType)
            => transactionType is not ("Reservation" or "ReservationRelease");
    }
}
