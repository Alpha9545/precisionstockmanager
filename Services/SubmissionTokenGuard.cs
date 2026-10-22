using System.Collections.Concurrent;

namespace PlantStockManager.Services
{
    // One-time form tokens for actions that must never run twice from the same
    // form (double click, browser retry, repeated POST) -- used by Allocate
    // Batch on the Seedling Dispatch page. Every GET of the page issues a new
    // token; the POST consumes it ATOMICALLY (TryAdd), so two simultaneous
    // submits of the same form can never both pass. Tokens are kept for 24
    // hours. Registered as a singleton (one application instance); a token
    // issued before an application restart is not remembered after it -- the
    // allocation transaction's own quantity checks still apply then.
    //
    // LIMITS (2026-10-21 security review -- since then also the stock-entry forms: Seed Stock, Outlet, Potted
    // Plant Booking, Fertilizer Stock, Purchase Order Receive, Empty Pot, Pot Batch production entry):
    //   * Used tokens live in THIS process's memory only. An application restart (or an IIS app-pool recycle)
    //     forgets them, so a form submitted before the restart is accepted again if it is replayed after it
    //     (verified). It gives NO protection across several app instances (a web farm / load balancer): each
    //     instance has its own memory. Production runs one instance; if that changes, use a durable token
    //     instead -- a unique SubmissionToken column on the written table, as dbo.InternalTransfers,
    //     dbo.PotProductionBatches and dbo.CuttingProductions already do (or a shared store such as a
    //     dbo.UsedSubmissionTokens table / distributed cache).
    //   * A used token is remembered for 24 hours; a replay after that is accepted.
    //   * The server does not check that it ISSUED a token: any unused value (1-64 characters) is accepted once.
    //     It stops the same form being processed twice, not a user who deliberately posts a fresh value (who
    //     could equally reload the form) -- authorization and each repository's own rules still apply.
    public class SubmissionTokenGuard
    {
        private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
        private readonly ConcurrentDictionary<string, DateTime> _used = new();
        private DateTime _nextPurge = DateTime.UtcNow;

        public static string NewToken() => Guid.NewGuid().ToString("N");

        // True the FIRST time a (scope, token) pair is presented; false for an
        // empty token or any repeat.
        public bool TryConsume(string scope, string? token)
        {
            if (string.IsNullOrWhiteSpace(token) || token.Length > 64)
                return false;
            var now = DateTime.UtcNow;
            if (now >= _nextPurge)
            {
                _nextPurge = now.AddHours(1);
                foreach (var entry in _used)
                    if (now - entry.Value > Lifetime)
                        _used.TryRemove(entry.Key, out _);
            }
            return _used.TryAdd(scope + ":" + token.Trim(), now);
        }
    }
}
