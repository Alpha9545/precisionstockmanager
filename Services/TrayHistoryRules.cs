namespace PlantStockManager.Services
{
    // 2026-10-10: empty-tray history in BUSINESS-DATE order. Each pool's running balance (rows ordered by
    // TransactionDate, then Id) must never go below zero. Since 2026-10-10 no new row can make it do so
    // (TrayStockRepository.AvailableFromAsync); rows written before -- e.g. a transplant dated before its
    // trays were received -- are FLAGGED here, never changed. Pure: the page feeds it the ledger rows.
    public static class TrayHistoryRules
    {
        public sealed record Movement(int PoolId, string Where, DateTime TransactionDateUtc, int Id, decimal Quantity);
        public sealed record NegativeStretch(int PoolId, string Where, DateTime FromIndia, DateTime? UntilIndia, decimal Lowest);

        public static List<NegativeStretch> NegativeStretches(IEnumerable<Movement> movements)
        {
            var result = new List<NegativeStretch>();
            foreach (var pool in movements.GroupBy(m => m.PoolId))
            {
                decimal running = 0, lowest = 0;
                DateTime? from = null;
                string where = "";
                foreach (var m in pool.OrderBy(m => m.TransactionDateUtc).ThenBy(m => m.Id))
                {
                    where = m.Where;
                    running += m.Quantity;
                    if (running < 0)
                    {
                        from ??= AppClock.ToIndiaTime(m.TransactionDateUtc).Date;
                        lowest = Math.Min(lowest, running);
                    }
                    else if (from.HasValue)
                    {
                        result.Add(new NegativeStretch(pool.Key, where, from.Value, AppClock.ToIndiaTime(m.TransactionDateUtc).Date, lowest));
                        from = null;
                        lowest = 0;
                    }
                }
                if (from.HasValue)
                    result.Add(new NegativeStretch(pool.Key, where, from.Value, null, lowest));
            }
            return result;
        }
    }
}
