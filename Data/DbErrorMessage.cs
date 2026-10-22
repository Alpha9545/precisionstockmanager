using System.Data.SqlTypes;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;

namespace PlantStockManager.Data
{
    // 2026-10-21 security review: what a repository may show the user when a write throws.
    //
    // Repositories used to return ex.Message straight to the page, so SQL Server's own text reached the user --
    // "Arithmetic overflow error converting numeric...", "SqlDateTime overflow...", and for a constraint violation
    // (547 / 2601 / 2627) the database, table, column and constraint / index names.
    //
    //   * The app's OWN rules raised by the database -- THROW / RAISERROR with a number >= 50000 in triggers and
    //     procedures (e.g. 51711 "location rule") -- are written for the user and are returned unchanged.
    //   * The app's own business exceptions -- an InvalidOperationException ("This transfer was changed by someone
    //     else...") or an app-defined exception (StoreStockException) THROWN BY THIS APPLICATION'S CODE -- are returned
    //     unchanged too.
    //   * Everything else is not meant for the user: SQL Server / SqlClient text (any other SqlException,
    //     SqlTypeException, an InvalidOperationException raised inside SqlClient) and programming errors
    //     (NullReferenceException, ArgumentOutOfRangeException...). The user gets a safe message (or the caller's own
    //     fallback) and the original is LOGGED (ILogger, category PlantStockManager.Data.DbErrorMessage) with the calling
    //     repository / method -- the full exception, as every other ILogger.LogError(ex, ...) in the app logs it.
    public static class DbErrorMessage
    {
        public const string Generic =
            "A database error stopped this change, so nothing was saved. Please check the values and try again; if it keeps happening, tell the administrator.";
        public const string Busy =
            "The database is busy with another change right now, so nothing was saved. Please try again.";

        private static readonly System.Reflection.Assembly AppAssembly = typeof(DbErrorMessage).Assembly;
        private static ILogger? _logger;

        // Program.cs, once at startup. Without it (e.g. unit tests) the safe message is still returned, only not logged.
        public static void UseLoggerFactory(ILoggerFactory factory) => _logger = factory.CreateLogger(typeof(DbErrorMessage).FullName!);

        // True for a rule the app itself raises in SQL (THROW / RAISERROR >= 50000) -- its message is meant for the user.
        public static bool IsBusinessRule(Exception ex) => ex is SqlException { Number: >= 50000 };

        // True for a business exception this application threw itself (not one raised inside SqlClient / the runtime).
        public static bool IsAppBusinessException(Exception ex)
        {
            var businessType = (ex is InvalidOperationException && ex is not ObjectDisposedException) || ex.GetType().Assembly == AppAssembly;
            return businessType && ex.TargetSite?.DeclaringType?.Assembly == AppAssembly;
        }

        // True for a message that may be shown to the user as it is.
        public static bool IsUserMessage(Exception ex) => IsBusinessRule(ex) || IsAppBusinessException(ex);

        // The message a page may show for ex. fallback replaces Generic when the caller has a more specific safe
        // explanation (e.g. "The database refused the dispatch because it would break a stock rule.").
        public static string For(Exception ex, string? fallback = null,
            [CallerFilePath] string file = "", [CallerMemberName] string member = "")
        {
            if (IsUserMessage(ex))
                return ex.Message;

            // Logging must never turn a handled error into a crash (e.g. a logger whose host was already disposed --
            // the factory is process-wide, set by the most recent application start).
            try
            {
                _logger?.LogError(ex, "Unexpected error in {Source}.{Member} ({ExceptionType}, SQL error {Number}); the user was shown a safe message.",
                    Path.GetFileNameWithoutExtension(file), member, ex.GetType().Name, (ex as SqlException)?.Number);
            }
            catch
            {
            }

            if (fallback != null)
                return fallback;
            return ex is SqlException { Number: 1205 or -2 } ? Busy : Generic;   // deadlock victim / command timeout
        }
    }
}
