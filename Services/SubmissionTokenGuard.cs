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
