namespace PlantStockManager.Services
{
    // 2026-10-10: business dates on stock-ledger rows (ReadyStockTransactions, and the tray
    // ledger for transplanting / tray transfers). The convention the Seed Stock and Tray Stock
    // ledgers already use:
    //   TransactionDate = WHEN THE MOVEMENT HAPPENED in business terms -- the operation's own
    //                     India business date (dispatch date, sale date, wastage date, transfer
    //                     date, transplanting date) at the time of day it was recorded
    //   CreatedAt       = WHEN IT WAS RECORDED (always SYSUTCDATETIME(); audit)
    // Movements without a business date of their own (approvals, reservations, receipts,
    // rejections, cancellations, reversals) keep TransactionDate = the recorded time.
    // Rows written before this rule are unchanged: their TransactionDate is the recorded time
    // even where the operation carried an earlier business date (see docs/STOCK_DEFINITIONS.md).
    public static class LedgerDates
    {
        public const string FutureDateMessage = "The date cannot be in the future.";

        // The UTC instant to store as TransactionDate for a movement dated businessDateIndia.
        // null => "now" (the caller stores SYSUTCDATETIME()): no business date, or the business
        // date is today in India. A business date in the past keeps the recorded time of day,
        // so the row falls on its business day and keeps a natural order within that day.
        public static (bool Ok, DateTime? TransactionUtc, string? Error) Instant(DateTime? businessDateIndia, DateTime nowIndia)
        {
            if (businessDateIndia is null)
                return (true, null, null);
            var day = businessDateIndia.Value.Date;
            if (day > nowIndia.Date)
                return (false, null, FutureDateMessage);
            if (day == nowIndia.Date)
                return (true, null, null);
            return (true, AppClock.IndiaToUtc(day + nowIndia.TimeOfDay), null);
        }

        public static (bool Ok, DateTime? TransactionUtc, string? Error) Instant(DateTime? businessDateIndia)
            => Instant(businessDateIndia, AppClock.Now);

        // A dispatch-type movement takes plants out of RESERVED stock. Its 'ReservationRelease'
        // row is dated at the later of the business date and the moment the plants were
        // reserved, so a historical "reserved" figure never goes negative when a dispatch is
        // entered with a date earlier than its reservation (data-entry lag):
        //   dispatchUtc null           -> release now (dispatched today)
        //   reservedAtUtc null         -> reserved in this same operation -> release now
        //   reservedAt after dispatch  -> release at the reservation instant (net zero there)
        //   otherwise                  -> release at the dispatch's business instant
        public static DateTime? ReleaseInstant(DateTime? dispatchUtc, DateTime? reservedAtUtc)
        {
            if (dispatchUtc is null || reservedAtUtc is null)
                return null;
            return reservedAtUtc.Value > dispatchUtc.Value ? reservedAtUtc.Value : dispatchUtc.Value;
        }
    }
}
