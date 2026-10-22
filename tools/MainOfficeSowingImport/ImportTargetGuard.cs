// Which database the Main Office sowing import may write to. Decided from what the CONNECTED server reports about
// itself (@@SERVERNAME, SERVERPROPERTY('ServerName'), DB_NAME()), never from the database name alone: live on the
// VPS is ALSO named PlantsIMS2_Test, so a name-only rule would treat live as the local test database.
//
//   - the verified local development server: only PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy;
//   - any other server (live, a VPS, anything unknown): only with --allow-live-target <server>/<database> naming
//     exactly that server and database;
//   - fails closed when the server identity cannot be verified (blank, or the two server names disagree).
// Pure rule, no database: compiled into the test project too (tests/PlantStockManager.Tests, ImportTargetGuardTests).
namespace MainOfficeSowingImport
{
    public sealed record ImportTarget(string? ServerName, string? ServerPropertyName, string? Database);

    public static class ImportTargetGuard
    {
        public const string LocalDevelopmentServer = @"LAPTOP-R87BKQGH\MSSQLSERVER02";
        public const string LocalTestDatabase = "PlantsIMS2_Test";
        public const string ScratchPrefix = "PlantsIMS2_Scratch_";
        public const string LiveFlag = "--allow-live-target";
        public const string RetiredLiveFlag = "--allow-live-PlantsIMS2";

        public static (bool Allowed, bool IsLive, string Reason) Evaluate(ImportTarget target, string? liveAuthorization)
        {
            var (server, property, db) = (target.ServerName?.Trim(), target.ServerPropertyName?.Trim(), target.Database?.Trim());
            if (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(property) || string.IsNullOrEmpty(db))
                return (false, false, "the server identity cannot be verified (server name or database is blank)");
            if (!string.Equals(server, property, StringComparison.OrdinalIgnoreCase))
                return (false, false, $"the server identity cannot be verified (@@SERVERNAME '{server}' <> SERVERPROPERTY('ServerName') '{property}')");

            if (string.Equals(server, LocalDevelopmentServer, StringComparison.OrdinalIgnoreCase))
            {
                if (liveAuthorization != null)
                    return (false, false, $"{LiveFlag} was given, but the target is the local development server {server}");
                if (db == LocalTestDatabase || db.StartsWith(ScratchPrefix, StringComparison.Ordinal))
                    return (true, false, $"local development server {server} / {db}");
                return (false, false, $"on the local development server only {LocalTestDatabase} or a {ScratchPrefix}* copy is allowed, not '{db}'");
            }

            if (string.IsNullOrWhiteSpace(liveAuthorization))
                return (false, false, $"{server} / {db} is not the local development server; writing there needs {LiveFlag} \"{server}/{db}\"");
            var slash = liveAuthorization.LastIndexOf('/');
            var (authServer, authDb) = slash > 0 ? (liveAuthorization[..slash].Trim(), liveAuthorization[(slash + 1)..].Trim()) : ("", "");
            if (!string.Equals(authServer, server, StringComparison.OrdinalIgnoreCase) || authDb != db)
                return (false, false, $"{LiveFlag} names '{liveAuthorization}', but the target is '{server}/{db}'");
            return (true, true, $"LIVE target {server} / {db}, authorized by {LiveFlag}");
        }
    }
}
