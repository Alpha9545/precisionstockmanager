using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Data
{
    // Material Stock -- Cocopeat / Pitmos per MATERIAL + Main Office AREA + POLYHOUSE
    // (Database/Migrations/2026-10-08_MaterialStock.sql). The same stock pattern as
    // TrayStockRepository: every change is a dbo.MaterialTransactions ledger row
    // plus the matching dbo.MaterialStock balance update, in ONE transaction,
    // after an exclusive transaction-scoped key lock (sp_getapplock) and a row
    // lock (UPDLOCK, HOLDLOCK) on that exact balance -- so concurrent operations
    // on one balance run strictly one after the other and the balance can never
    // go negative. The database triggers repeat every rule.
    //
    // Sowings consume through ConsumeForSowingAsync / ReverseForSowingAsync, called
    // by SeedSowingRepository INSIDE the sowing's own transaction: the sowing and
    // its material deduction commit or roll back together.
    public class MaterialStockRepository
    {
        private readonly DatabaseHelper _dbHelper;

        public MaterialStockRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        // ---- master data ------------------------------------------------------------

        public async Task<List<MaterialType>> GetTypesAsync(bool activeOnly = true)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
SELECT m.Id, m.Code, m.Name, m.Unit, m.ConversionFactor, m.IsActive, m.SortOrder, m.UpdatedAt, u.Name
FROM dbo.MaterialTypes m LEFT JOIN dbo.IMSUsers u ON u.Id = m.UpdatedById
WHERE (@ActiveOnly = 0 OR m.IsActive = 1)
ORDER BY m.SortOrder, m.Name", conn);
            cmd.Parameters.AddWithValue("@ActiveOnly", activeOnly);
            var list = new List<MaterialType>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new MaterialType
                {
                    Id = r.GetInt32(0), Code = r.GetString(1), Name = r.GetString(2), Unit = r.GetString(3),
                    ConversionFactor = r.GetDecimal(4), IsActive = r.GetBoolean(5), SortOrder = r.GetInt32(6),
                    UpdatedAt = r.IsDBNull(7) ? null : r.GetDateTime(7), UpdatedByName = r.IsDBNull(8) ? null : r.GetString(8),
                });
            return list;
        }

        public async Task<List<MaterialConsumptionRule>> GetRulesAsync()
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
SELECT c.Id, c.MaterialTypeId, m.Name, c.CavityType, c.QuantityPerTray, c.IsActive, c.UpdatedAt, u.Name
FROM dbo.MaterialConsumptionRules c
JOIN dbo.MaterialTypes m ON m.Id = c.MaterialTypeId
LEFT JOIN dbo.IMSUsers u ON u.Id = c.UpdatedById
ORDER BY m.SortOrder, CAST(LEFT(c.CavityType, CHARINDEX(N' ', c.CavityType + N' ') - 1) AS INT) DESC", conn);
            var list = new List<MaterialConsumptionRule>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new MaterialConsumptionRule
                {
                    Id = r.GetInt32(0), MaterialTypeId = r.GetInt32(1), MaterialName = r.GetString(2), CavityType = r.GetString(3),
                    QuantityPerTray = r.GetDecimal(4), IsActive = r.GetBoolean(5),
                    UpdatedAt = r.IsDBNull(6) ? null : r.GetDateTime(6), UpdatedByName = r.IsDBNull(7) ? null : r.GetString(7),
                });
            return list;
        }

        // Settings (MaterialStock.Manage): the approximate business values are data.
        // A change applies to stock added / sowings recorded from now on; every ledger
        // row keeps the factor / kg-per-tray it was calculated with.
        public async Task<string?> UpdateConversionFactorAsync(int materialTypeId, decimal factor, int? userId)
        {
            if (factor <= 0 || factor > 1000 || decimal.Round(factor, 4) != factor)
                return "Conversion factor must be greater than zero, with at most 4 decimal places.";
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(
                "UPDATE dbo.MaterialTypes SET ConversionFactor = @F, UpdatedById = @U, UpdatedAt = SYSUTCDATETIME() WHERE Id = @Id", conn);
            cmd.Parameters.AddWithValue("@F", factor);
            cmd.Parameters.AddWithValue("@U", (object?)userId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Id", materialTypeId);
            return await cmd.ExecuteNonQueryAsync() == 1 ? null : "Material not found.";
        }

        public async Task<string?> UpdateQuantityPerTrayAsync(int ruleId, decimal quantityPerTray, int? userId)
        {
            var error = MaterialStockRules.ValidateQuantity(quantityPerTray, "Kg per tray");
            if (error != null)
                return error;
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(
                "UPDATE dbo.MaterialConsumptionRules SET QuantityPerTray = @Q, UpdatedById = @U, UpdatedAt = SYSUTCDATETIME() WHERE Id = @Id", conn);
            cmd.Parameters.AddWithValue("@Q", quantityPerTray);
            cmd.Parameters.AddWithValue("@U", (object?)userId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Id", ruleId);
            return await cmd.ExecuteNonQueryAsync() == 1 ? null : "Consumption rule not found.";
        }

        // ---- locations -----------------------------------------------------------------

        // Server-side check of a material location, under the caller's transaction:
        // the Polyhouse exists and belongs to THIS Area, and the Area is an active
        // Main Office Area. Never trusts a posted AreaId / PolyhouseId pair.
        public static async Task<(bool Ok, string? Error, string? PolyhouseName)> ValidateLocationAsync(SqlConnection conn, SqlTransaction tx, int areaId, int polyhouseId)
        {
            if (areaId <= 0)
                return (false, "Area is required.", null);
            if (polyhouseId <= 0)
                return (false, "Polyhouse is required.", null);
            using var cmd = new SqlCommand(@"
SELECT p.AreaId, p.Name, a.IsActive, a.AreaType
FROM dbo.Polyhouses p LEFT JOIN dbo.Area a ON a.Id = p.AreaId
WHERE p.Id = @P", conn, tx);
            cmd.Parameters.AddWithValue("@P", polyhouseId);
            using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync())
                return (false, "Selected Polyhouse does not exist.", null);
            var polyhouseArea = r.IsDBNull(0) ? (int?)null : r.GetInt32(0);
            var name = r.GetString(1).Trim();
            if (polyhouseArea != areaId)
                return (false, MaterialStockRules.PolyhouseNotInAreaMessage, name);
            var active = !r.IsDBNull(2) && r.GetBoolean(2);
            var type = r.IsDBNull(3) ? null : r.GetString(3);
            if (!MaterialStockRules.IsMainOfficeLocation(type, active))
                return (false, MaterialStockRules.LocationMessage, name);
            return (true, null, name);
        }

        // The Main Office Areas a user may manage material for (canAccessArea =
        // AreaAccessService.CanAccessRequiredArea) and their Polyhouses -- the form
        // dropdowns. The save re-validates everything.
        public async Task<List<(int AreaId, string AreaName, int PolyhouseId, string PolyhouseName)>> GetLocationsAsync(Func<int, bool> canAccessArea)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
SELECT a.Id, a.Name, p.Id, p.Name
FROM dbo.Area a JOIN dbo.Polyhouses p ON p.AreaId = a.Id
WHERE a.IsActive = 1 AND a.AreaType = N'MainOffice'
ORDER BY a.Name, p.Name", conn);
            var list = new List<(int, string, int, string)>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                if (canAccessArea(r.GetInt32(0)))
                    list.Add((r.GetInt32(0), r.GetString(1).Trim(), r.GetInt32(2), r.GetString(3).Trim()));
            return list;
        }

        // ---- the locked write path ---------------------------------------------------------

        // The MATERIAL + AREA + POLYHOUSE balance row, locked for the caller's
        // transaction, or a new zero row (locked) when none exists yet. sp_getapplock
        // first: a true key lock whether or not the row exists, so two first-time
        // callers can never both insert and two operations on one balance never
        // interleave. UQ_MaterialStock_Location is the database backstop.
        public static async Task<int> GetOrCreateLockedAsync(SqlConnection conn, SqlTransaction tx, int materialTypeId, int areaId, int polyhouseId)
        {
            using (var appLock = new SqlCommand("sp_getapplock", conn, tx) { CommandType = System.Data.CommandType.StoredProcedure })
            {
                appLock.Parameters.AddWithValue("@Resource", $"MaterialStock:{materialTypeId}:{areaId}:{polyhouseId}");
                appLock.Parameters.AddWithValue("@LockMode", "Exclusive");
                appLock.Parameters.AddWithValue("@LockOwner", "Transaction");
                appLock.Parameters.AddWithValue("@LockTimeout", 15000);
                var rv = appLock.Parameters.Add("@ReturnValue", System.Data.SqlDbType.Int);
                rv.Direction = System.Data.ParameterDirection.ReturnValue;
                await appLock.ExecuteNonQueryAsync();
                if ((int)rv.Value < 0)
                    throw new InvalidOperationException("This material stock is busy with another request right now. Please try again.");
            }

            using (var find = new SqlCommand(
                "SELECT Id FROM dbo.MaterialStock WITH (UPDLOCK, HOLDLOCK) WHERE MaterialTypeId = @M AND AreaId = @A AND PolyhouseId = @P", conn, tx))
            {
                find.Parameters.AddWithValue("@M", materialTypeId);
                find.Parameters.AddWithValue("@A", areaId);
                find.Parameters.AddWithValue("@P", polyhouseId);
                var existing = await find.ExecuteScalarAsync();
                if (existing is int id)
                    return id;
            }

            using var insert = new SqlCommand(@"
INSERT INTO dbo.MaterialStock (MaterialTypeId, AreaId, PolyhouseId, CurrentQuantity) VALUES (@M, @A, @P, 0);
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn, tx);
            insert.Parameters.AddWithValue("@M", materialTypeId);
            insert.Parameters.AddWithValue("@A", areaId);
            insert.Parameters.AddWithValue("@P", polyhouseId);
            return (int)(await insert.ExecuteScalarAsync())!;
        }

        public sealed record Movement(
            string Type, int Direction, decimal StockQuantity,
            decimal? InputQuantity = null, decimal? ConversionFactor = null,
            int? Trays = null, decimal? QuantityPerTray = null,
            string? ReferenceType = null, int? ReferenceId = null,
            Guid? SubmissionToken = null, string? Remarks = null,
            DateTime? TransactionDateUtc = null);

        // 2026-10-12 (2026-10-12_BedSowing.sql adds MaterialTransactions.TransactionDate):
        // TransactionDate = the BUSINESS instant of a movement (LedgerDates.Instant), CreatedAt = when it
        // was recorded (audit, unchanged). NULL TransactionDate = the movement happened when recorded.
        // Every date-based reader uses the business instant: COALESCE(TransactionDate, CreatedAt).
        public static string BusinessDateSql(bool hasColumn, string alias = "t")
            => hasColumn ? $"COALESCE({alias}.TransactionDate, {alias}.CreatedAt)" : $"{alias}.CreatedAt";

        // Material AVAILABLE to a deduction dated atUtc: the LOWEST balance from that instant onward, in
        // business-date order, computed backward from the current balance. Call under the balance lock.
        public static async Task<decimal> AvailableFromAsync(SqlConnection conn, SqlTransaction tx, int materialStockId, DateTime atUtc)
        {
            var cmd = new SqlCommand(@"
DECLARE @B DECIMAL(18, 3) = (SELECT CurrentQuantity FROM dbo.MaterialStock WHERE Id = @Id)
    - (SELECT ISNULL(SUM(Direction * StockQuantity), 0) FROM dbo.MaterialTransactions
       WHERE MaterialStockId = @Id AND COALESCE(TransactionDate, CreatedAt) > @At);
SELECT CASE WHEN m.MinRunning IS NULL OR m.MinRunning > @B THEN @B ELSE m.MinRunning END
FROM (SELECT MIN(r.Running) AS MinRunning
      FROM (SELECT @B + SUM(Direction * StockQuantity) OVER (ORDER BY COALESCE(TransactionDate, CreatedAt), Id ROWS UNBOUNDED PRECEDING) AS Running
            FROM dbo.MaterialTransactions WHERE MaterialStockId = @Id AND COALESCE(TransactionDate, CreatedAt) > @At) r) m;", conn, tx);
            cmd.Parameters.AddWithValue("@Id", materialStockId);
            cmd.Parameters.Add("@At", System.Data.SqlDbType.DateTime2).Value = atUtc;
            return (decimal)(await cmd.ExecuteScalarAsync())!;
        }

        // Writes one ledger row and moves the balance by it, on a balance the caller
        // has already locked (GetOrCreateLockedAsync). Refuses -- changing nothing --
        // when the result would be negative.
        public static async Task<(bool Ok, string? Message, decimal BalanceAfter)> RecordAsync(SqlConnection conn, SqlTransaction tx, int materialStockId, Movement m, int? userId)
        {
            decimal before; string materialName, polyhouseName, unit;
            using (var read = new SqlCommand(@"
SELECT s.CurrentQuantity, mt.Name, p.Name, mt.Unit
FROM dbo.MaterialStock s WITH (UPDLOCK, HOLDLOCK)
JOIN dbo.MaterialTypes mt ON mt.Id = s.MaterialTypeId
JOIN dbo.Polyhouses p ON p.Id = s.PolyhouseId
WHERE s.Id = @Id", conn, tx))
            {
                read.Parameters.AddWithValue("@Id", materialStockId);
                using var r = await read.ExecuteReaderAsync();
                if (!await r.ReadAsync())
                    return (false, "Material stock not found.", 0);
                before = r.GetDecimal(0);
                materialName = r.GetString(1);
                polyhouseName = r.GetString(2).Trim();
                unit = r.GetString(3);
            }

            var after = before + m.Direction * m.StockQuantity;
            if (after < 0)
                return (false, MaterialStockRules.InsufficientMessage(materialName, polyhouseName, before, m.StockQuantity), before);
            // A back-dated deduction must fit the balance on its business date and at every later point.
            if (m.Direction < 0 && TrayStockRepository.IsBackDated(m.TransactionDateUtc))
            {
                var availableThen = await AvailableFromAsync(conn, tx, materialStockId, m.TransactionDateUtc!.Value);
                if (m.StockQuantity > availableThen)
                    return (false, $"Only {MaterialStockRules.Kg(Math.Max(availableThen, 0), unit)} of {materialName} was in {polyhouseName} from "
                        + $"{AppClock.ToIndiaTime(m.TransactionDateUtc.Value):dd/MM/yyyy} onward (counting every later movement); {MaterialStockRules.Kg(m.StockQuantity, unit)} needed. "
                        + "Record the material's arrival first, or use a later date.", before);
            }

            using (var ledger = new SqlCommand(@"
INSERT INTO dbo.MaterialTransactions
(MaterialStockId, TransactionType, Direction, StockQuantity, InputQuantity, ConversionFactor, Trays, QuantityPerTray,
 BalanceBefore, BalanceAfter, ReferenceType, ReferenceId, SubmissionToken, Remarks, CreatedById, CreatedAt" + (m.TransactionDateUtc.HasValue ? ", TransactionDate" : "") + @")
VALUES
(@S, @Type, @Dir, @Qty, @Input, @Factor, @Trays, @PerTray, @Before, @After, @RefType, @RefId, @Token, @Remarks, @User, SYSUTCDATETIME()" + (m.TransactionDateUtc.HasValue ? ", @TxDate" : "") + @");", conn, tx))
            {
                if (m.TransactionDateUtc.HasValue)
                    ledger.Parameters.Add("@TxDate", System.Data.SqlDbType.DateTime2).Value = m.TransactionDateUtc.Value;
                ledger.Parameters.AddWithValue("@S", materialStockId);
                ledger.Parameters.AddWithValue("@Type", m.Type);
                ledger.Parameters.AddWithValue("@Dir", (short)m.Direction);
                ledger.Parameters.Add("@Qty", System.Data.SqlDbType.Decimal).Value = m.StockQuantity;
                ledger.Parameters["@Qty"].Precision = 18; ledger.Parameters["@Qty"].Scale = 3;
                ledger.Parameters.AddWithValue("@Input", (object?)m.InputQuantity ?? DBNull.Value);
                ledger.Parameters.AddWithValue("@Factor", (object?)m.ConversionFactor ?? DBNull.Value);
                ledger.Parameters.AddWithValue("@Trays", (object?)m.Trays ?? DBNull.Value);
                ledger.Parameters.AddWithValue("@PerTray", (object?)m.QuantityPerTray ?? DBNull.Value);
                ledger.Parameters.AddWithValue("@Before", before);
                ledger.Parameters.AddWithValue("@After", after);
                ledger.Parameters.AddWithValue("@RefType", (object?)m.ReferenceType ?? DBNull.Value);
                ledger.Parameters.AddWithValue("@RefId", (object?)m.ReferenceId ?? DBNull.Value);
                ledger.Parameters.AddWithValue("@Token", (object?)m.SubmissionToken ?? DBNull.Value);
                ledger.Parameters.AddWithValue("@Remarks", (object?)m.Remarks ?? DBNull.Value);
                ledger.Parameters.AddWithValue("@User", (object?)userId ?? DBNull.Value);
                await ledger.ExecuteNonQueryAsync();
            }

            using (var update = new SqlCommand("UPDATE dbo.MaterialStock SET CurrentQuantity = @After, UpdatedAt = SYSUTCDATETIME() WHERE Id = @Id", conn, tx))
            {
                update.Parameters.AddWithValue("@After", after);
                update.Parameters.AddWithValue("@Id", materialStockId);
                await update.ExecuteNonQueryAsync();
            }
            return (true, null, after);
        }

        private static async Task<(bool Ok, string? Error, string Name, string Unit, decimal Factor)> GetActiveMaterialAsync(SqlConnection conn, SqlTransaction tx, int materialTypeId)
        {
            using var cmd = new SqlCommand("SELECT Name, Unit, ConversionFactor, IsActive FROM dbo.MaterialTypes WHERE Id = @Id", conn, tx);
            cmd.Parameters.AddWithValue("@Id", materialTypeId);
            using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync())
                return (false, "Select a valid material.", "", "KG", 0);
            if (!r.GetBoolean(3))
                return (false, $"{r.GetString(0)} is not active.", r.GetString(0), r.GetString(1), 0);
            return (true, null, r.GetString(0), r.GetString(1), r.GetDecimal(2));
        }

        // ---- Add Material / Adjust (self-contained transactions) --------------------------

        public sealed record AddResult(bool Success, string? Message, bool Duplicate, string MaterialName, string PolyhouseName,
            decimal InputQuantity, decimal ConversionFactor, decimal StockAdded, decimal Balance, string Unit);

        // The Sowing Supervisor's Add Material: input x the material's CURRENT
        // conversion factor = usable stock, stored together with the input and the
        // factor used. Never trusts the form: quantity, the user's Area authorization,
        // and the Main Office Polyhouse location are re-checked here. A repeat of the
        // same form (double click, refresh) finds its first row by the one-time token
        // and adds nothing.
        public async Task<AddResult> ReceiveAsync(int areaId, int polyhouseId, int materialTypeId, decimal inputQuantity, Guid submissionToken,
            int? userId, string? remarks, Func<int, bool> canAccessArea)
        {
            AddResult Fail(string message) => new(false, message, false, "", "", inputQuantity, 0, 0, 0, "KG");
            var qtyError = MaterialStockRules.ValidateQuantity(inputQuantity);
            if (qtyError != null) return Fail(qtyError);
            if (submissionToken == Guid.Empty) return Fail(MaterialStockRules.ExpiredFormMessage);
            if (areaId <= 0 || !canAccessArea(areaId)) return Fail(MaterialStockRules.NotAuthorizedAreaMessage);
            remarks = MaterialStockRules.Trimmed(remarks);
            if (remarks?.Length > MaterialStockRules.MaxRemarksLength) return Fail("Remarks are too long (max 500 characters).");

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                var (locationOk, locationError, polyhouseName) = await ValidateLocationAsync(conn, tx, areaId, polyhouseId);
                if (!locationOk) { tx.Rollback(); return Fail(locationError!); }
                var (materialOk, materialError, materialName, unit, factor) = await GetActiveMaterialAsync(conn, tx, materialTypeId);
                if (!materialOk) { tx.Rollback(); return Fail(materialError!); }

                var stockId = await GetOrCreateLockedAsync(conn, tx, materialTypeId, areaId, polyhouseId);

                using (var dup = new SqlCommand(
                    "SELECT InputQuantity, ConversionFactor, StockQuantity FROM dbo.MaterialTransactions WHERE SubmissionToken = @T", conn, tx))
                {
                    dup.Parameters.AddWithValue("@T", submissionToken);
                    using var r = await dup.ExecuteReaderAsync();
                    if (await r.ReadAsync())
                    {
                        var (inQ, fac, added) = (r.IsDBNull(0) ? 0 : r.GetDecimal(0), r.IsDBNull(1) ? 0 : r.GetDecimal(1), r.GetDecimal(2));
                        r.Close();
                        var balance = await BalanceAsync(conn, tx, stockId);
                        tx.Commit();
                        return new AddResult(true, null, true, materialName, polyhouseName!, inQ, fac, added, balance, unit);
                    }
                }

                var stockQuantity = MaterialStockRules.StockQuantityFor(inputQuantity, factor);
                var (ok, message, after) = await RecordAsync(conn, tx, stockId,
                    new Movement(MaterialStockRules.Receive, 1, stockQuantity, inputQuantity, factor, SubmissionToken: submissionToken, Remarks: remarks), userId);
                if (!ok) { tx.Rollback(); return Fail(message!); }
                tx.Commit();
                return new AddResult(true, null, false, materialName, polyhouseName!, inputQuantity, factor, stockQuantity, after, unit);
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                return Fail(DbErrorMessage.For(ex));
            }
        }

        // MaterialStock.Manage: a physical-count correction, + or -, with a REQUIRED
        // reason. Never below zero; the history keeps who, when and why.
        public async Task<(bool Success, string? Message, decimal Balance)> AdjustAsync(int areaId, int polyhouseId, int materialTypeId, int direction,
            decimal quantity, string? reason, Guid submissionToken, int? userId, Func<int, bool> canAccessArea, DateTime? transactionDateUtc = null)
        {
            if (direction is not (1 or -1)) return (false, "Choose whether the adjustment adds or removes stock.", 0);
            var qtyError = MaterialStockRules.ValidateQuantity(quantity);
            if (qtyError != null) return (false, qtyError, 0);
            reason = MaterialStockRules.Trimmed(reason);
            if (reason == null) return (false, "A reason is required for a stock adjustment.", 0);
            if (reason.Length > MaterialStockRules.MaxRemarksLength) return (false, "The reason is too long (max 500 characters).", 0);
            if (userId == null) return (false, "Sign in again to record an adjustment.", 0);
            if (submissionToken == Guid.Empty) return (false, MaterialStockRules.ExpiredFormMessage, 0);
            if (areaId <= 0 || !canAccessArea(areaId)) return (false, MaterialStockRules.NotAuthorizedAreaMessage, 0);

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                var (locationOk, locationError, _) = await ValidateLocationAsync(conn, tx, areaId, polyhouseId);
                if (!locationOk) { tx.Rollback(); return (false, locationError, 0); }
                var (materialOk, materialError, _, _, _) = await GetActiveMaterialAsync(conn, tx, materialTypeId);
                if (!materialOk) { tx.Rollback(); return (false, materialError, 0); }
                var stockId = await GetOrCreateLockedAsync(conn, tx, materialTypeId, areaId, polyhouseId);

                using (var dup = new SqlCommand("SELECT COUNT(*) FROM dbo.MaterialTransactions WHERE SubmissionToken = @T", conn, tx))
                {
                    dup.Parameters.AddWithValue("@T", submissionToken);
                    if ((int)(await dup.ExecuteScalarAsync())! > 0)
                    {
                        var balance = await BalanceAsync(conn, tx, stockId);
                        tx.Commit();
                        return (true, "This adjustment was already saved -- nothing was changed twice.", balance);
                    }
                }

                var (ok, message, after) = await RecordAsync(conn, tx, stockId,
                    new Movement(MaterialStockRules.Adjustment, direction, quantity, SubmissionToken: submissionToken, Remarks: reason, TransactionDateUtc: transactionDateUtc), userId);
                if (!ok) { tx.Rollback(); return (false, message, 0); }
                tx.Commit();
                return (true, null, after);
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                return (false, DbErrorMessage.For(ex), 0);
            }
        }

        private static async Task<decimal> BalanceAsync(SqlConnection conn, SqlTransaction tx, int stockId)
        {
            using var cmd = new SqlCommand("SELECT CurrentQuantity FROM dbo.MaterialStock WHERE Id = @Id", conn, tx);
            cmd.Parameters.AddWithValue("@Id", stockId);
            return (decimal)(await cmd.ExecuteScalarAsync())!;
        }

        // ---- sowings (inside the sowing's own transaction) ---------------------------------

        // Before the sowing row is written: a sowing whose growing Area is a Main
        // Office Area needs exactly one material; a sowing anywhere else needs none
        // (there is no material stock there).
        public static async Task<(bool Ok, string? Error, bool Required)> ValidateSowingMaterialAsync(SqlConnection conn, SqlTransaction tx, int areaId, int? materialTypeId)
        {
            using var cmd = new SqlCommand("SELECT AreaType FROM dbo.Area WHERE Id = @A", conn, tx);
            cmd.Parameters.AddWithValue("@A", areaId);
            var areaType = await cmd.ExecuteScalarAsync() as string;
            var required = areaType == MaterialStockRules.MainOfficeAreaType;
            if (required && materialTypeId is not > 0)
                return (false, MaterialStockRules.MaterialRequiredMessage, true);
            if (!required && materialTypeId is > 0)
                return (false, MaterialStockRules.NotAtThisLocationMessage, false);
            if (required)
            {
                var (ok, error, _, _, _) = await GetActiveMaterialAsync(conn, tx, materialTypeId!.Value);
                if (!ok) return (false, error, true);
            }
            return (true, null, required);
        }

        // After the sowing row exists: deducts trays x kg-per-tray of the sowing's
        // ONE material from its OWN Area + Polyhouse balance. No rule for the cavity,
        // or not enough stock -> the whole sowing rolls back. A sowing outside the
        // Main Office uses no material (a clean no-op).
        public static async Task<(bool Ok, string? Message)> ConsumeForSowingAsync(SqlConnection conn, SqlTransaction tx,
            int areaId, int? polyhouseId, int? materialTypeId, string? cavityType, decimal physicalTrays, int seedSowingId, string sowingCode, int? userId,
            DateTime? transactionDateUtc = null)
        {
            var (valid, error, required) = await ValidateSowingMaterialAsync(conn, tx, areaId, materialTypeId);
            if (!valid) return (false, error);
            if (!required) return (true, null);
            if (polyhouseId is not > 0) return (false, TrayStockRepository.PolyhouseRequiredMessage);
            if (physicalTrays <= 0 || physicalTrays != decimal.Truncate(physicalTrays)) return (false, "The number of trays could not be determined.");

            var (locationOk, locationError, _) = await ValidateLocationAsync(conn, tx, areaId, polyhouseId.Value);
            if (!locationOk) return (false, locationError);
            var (_, _, materialName, _, _) = await GetActiveMaterialAsync(conn, tx, materialTypeId!.Value);
            var (ok, message, _) = await ConsumeAsync(conn, tx, materialTypeId.Value, materialName, areaId, polyhouseId.Value, cavityType, (int)physicalTrays,
                MaterialStockRules.SowingReference, seedSowingId, $"Used for Sowing {sowingCode}", userId, transactionDateUtc: transactionDateUtc);
            return (ok, message);
        }

        // Supervisor Approval of a Cutting Sowing with MORE trays than were sown
        // (overage): the extra trays need material too -- extra trays x the kg per
        // tray the SOWING ITSELF was charged (its own CONSUME row, so one sowing is
        // never charged at two rates and a later Settings change never alters it),
        // of the sowing's OWN material, from the sowing's OWN Area + Polyhouse (all
        // read here from the database, never from the caller), in the approval's
        // transaction. Tagged to the approval (ReferenceType 'ReadyConfirmation') so
        // cancelling just that approval returns exactly it. A sowing without a
        // material (outside the Main Office) -> a clean no-op. Not enough stock ->
        // the caller rolls the whole approval back.
        public static async Task<(bool Ok, string? Message, decimal Consumed)> ConsumeForApprovalAsync(SqlConnection conn, SqlTransaction tx,
            int seedSowingId, decimal extraTrays, int readyConfirmationId, string sowingCode, int? userId)
        {
            if (extraTrays <= 0) return (true, null, 0);
            if (extraTrays != decimal.Truncate(extraTrays)) return (false, "The number of extra trays could not be determined.", 0);
            int areaId; int? polyhouseId, materialTypeId; string? cavityType, materialName; decimal? sowingPerTray;
            using (var cmd = new SqlCommand(@"
SELECT sw.AreaId, sw.PolyhouseId, sw.GrowingMaterialTypeId, sw.CavityType, m.Name,
       (SELECT c.QuantityPerTray FROM dbo.MaterialTransactions c
        WHERE c.ReferenceType = @RT AND c.ReferenceId = sw.Id AND c.TransactionType = 'CONSUME')
FROM dbo.SeedSowings sw LEFT JOIN dbo.MaterialTypes m ON m.Id = sw.GrowingMaterialTypeId
WHERE sw.Id = @Id", conn, tx))
            {
                cmd.Parameters.AddWithValue("@Id", seedSowingId);
                cmd.Parameters.AddWithValue("@RT", MaterialStockRules.SowingReference);
                using var r = await cmd.ExecuteReaderAsync();
                if (!await r.ReadAsync()) return (false, "Seed Sowing record not found.", 0);
                areaId = r.GetInt32(0);
                polyhouseId = r.IsDBNull(1) ? null : r.GetInt32(1);
                materialTypeId = r.IsDBNull(2) ? null : r.GetInt32(2);
                cavityType = r.IsDBNull(3) ? null : r.GetString(3);
                materialName = r.IsDBNull(4) ? null : r.GetString(4);
                sowingPerTray = r.IsDBNull(5) ? null : r.GetDecimal(5);
            }
            if (materialTypeId is not > 0) return (true, null, 0);
            if (polyhouseId is not > 0) return (false, TrayStockRepository.PolyhouseRequiredMessage, 0);
            if (sowingPerTray is not > 0)
                return (false, $"The {materialName} used by this sowing could not be found, so extra trays cannot be approved. Nothing was changed.", 0);
            var trays = (int)extraTrays;
            return await ConsumeAsync(conn, tx, materialTypeId.Value, materialName!, areaId, polyhouseId.Value, cavityType, trays,
                MaterialStockRules.ApprovalReference, readyConfirmationId, $"Extra {trays} trays beyond the sown trays: approval of batch {sowingCode}", userId,
                sowingPerTray.Value);
        }

        // Trays x kg per tray (the ACTIVE rule of the material + cavity, unless the
        // caller passes the rate to use), out of the locked Material + Area + Polyhouse
        // balance. The kg per tray used is kept on the ledger row, so a later change
        // of the rule never changes this row.
        private static async Task<(bool Ok, string? Message, decimal Consumed)> ConsumeAsync(SqlConnection conn, SqlTransaction tx,
            int materialTypeId, string materialName, int areaId, int polyhouseId, string? cavityType, int trays,
            string referenceType, int referenceId, string remarks, int? userId, decimal? fixedPerTray = null, DateTime? transactionDateUtc = null)
        {
            decimal? perTray = fixedPerTray;
            if (perTray == null)
                using (var rule = new SqlCommand(
                    "SELECT QuantityPerTray FROM dbo.MaterialConsumptionRules WHERE MaterialTypeId = @M AND CavityType = @C AND IsActive = 1", conn, tx))
                {
                    rule.Parameters.AddWithValue("@M", materialTypeId);
                    rule.Parameters.AddWithValue("@C", (object?)cavityType ?? DBNull.Value);
                    perTray = await rule.ExecuteScalarAsync() as decimal?;
                }
            if (perTray is not > 0) return (false, MaterialStockRules.NoRuleMessage(materialName, cavityType), 0);

            var stockId = await GetOrCreateLockedAsync(conn, tx, materialTypeId, areaId, polyhouseId);
            var required = MaterialStockRules.RequiredFor(trays, perTray.Value);
            var (ok, message, _) = await RecordAsync(conn, tx, stockId,
                new Movement(MaterialStockRules.Consume, -1, required, Trays: trays, QuantityPerTray: perTray.Value,
                    ReferenceType: referenceType, ReferenceId: referenceId, Remarks: remarks, TransactionDateUtc: transactionDateUtc), userId);
            return (ok, message, ok ? required : 0);
        }

        // A cancelled sowing: returns EXACTLY what its consumption row took (never
        // recalculated with today's kg-per-tray) to the SAME balance. No consumption,
        // or already returned -> a clean no-op.
        public static Task<(bool Ok, string? Message)> ReverseForSowingAsync(SqlConnection conn, SqlTransaction tx, int seedSowingId, int? userId, string? remarks)
            => ReverseAsync(conn, tx, MaterialStockRules.SowingReference, seedSowingId, userId, remarks);

        // A cancelled Supervisor Approval: returns EXACTLY the overage material that
        // approval took -- never the sowing's own consumption, never another
        // approval's. No overage consumption -> a clean no-op.
        public static Task<(bool Ok, string? Message)> ReverseForApprovalAsync(SqlConnection conn, SqlTransaction tx, int readyConfirmationId, int? userId, string? remarks)
            => ReverseAsync(conn, tx, MaterialStockRules.ApprovalReference, readyConfirmationId, userId, remarks);

        private static async Task<(bool Ok, string? Message)> ReverseAsync(SqlConnection conn, SqlTransaction tx, string referenceType, int referenceId, int? userId, string? remarks)
        {
            int stockId, materialTypeId, areaId, polyhouseId; decimal quantity;
            using (var find = new SqlCommand(@"
SELECT c.MaterialStockId, s.MaterialTypeId, s.AreaId, s.PolyhouseId, c.StockQuantity
FROM dbo.MaterialTransactions c JOIN dbo.MaterialStock s ON s.Id = c.MaterialStockId
WHERE c.ReferenceType = @RT AND c.ReferenceId = @Id AND c.TransactionType = 'CONSUME'
  AND NOT EXISTS (SELECT 1 FROM dbo.MaterialTransactions x WHERE x.ReferenceType = @RT AND x.ReferenceId = @Id AND x.TransactionType = 'REVERSAL')", conn, tx))
            {
                find.Parameters.AddWithValue("@RT", referenceType);
                find.Parameters.AddWithValue("@Id", referenceId);
                using var r = await find.ExecuteReaderAsync();
                if (!await r.ReadAsync())
                    return (true, null);
                (stockId, materialTypeId, areaId, polyhouseId, quantity) = (r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetDecimal(4));
            }
            var lockedId = await GetOrCreateLockedAsync(conn, tx, materialTypeId, areaId, polyhouseId);
            if (lockedId != stockId)
                return (false, "The material used cannot be matched to its stock -- nothing was changed.");
            var (ok, message, _) = await RecordAsync(conn, tx, stockId,
                new Movement(MaterialStockRules.Reversal, 1, quantity, ReferenceType: referenceType, ReferenceId: referenceId, Remarks: remarks), userId);
            return (ok, message);
        }

        // The sowing form's live check: for the chosen Area + Polyhouse + cavity and
        // the number of physical trays, each material's available stock, kg per tray,
        // required and remaining. Applicable = the Polyhouse is a Main Office one.
        // Read-only (no locks) -- the save re-checks everything under lock.
        public async Task<(bool Applicable, List<MaterialSowingPreview> Materials)> GetSowingPreviewAsync(int areaId, int polyhouseId, string? cavityType, decimal physicalTrays)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction(System.Data.IsolationLevel.ReadCommitted);
            var (ok, _, _) = await ValidateLocationAsync(conn, tx, areaId, polyhouseId);
            if (!ok) { tx.Commit(); return (false, new()); }
            using var cmd = new SqlCommand(@"
SELECT m.Id, m.Name, m.Unit, ISNULL(s.CurrentQuantity, 0), c.QuantityPerTray
FROM dbo.MaterialTypes m
LEFT JOIN dbo.MaterialStock s ON s.MaterialTypeId = m.Id AND s.AreaId = @A AND s.PolyhouseId = @P
LEFT JOIN dbo.MaterialConsumptionRules c ON c.MaterialTypeId = m.Id AND c.CavityType = @C AND c.IsActive = 1
WHERE m.IsActive = 1
ORDER BY m.SortOrder, m.Name", conn, tx);
            cmd.Parameters.AddWithValue("@A", areaId);
            cmd.Parameters.AddWithValue("@P", polyhouseId);
            cmd.Parameters.AddWithValue("@C", (object?)cavityType ?? DBNull.Value);
            var list = new List<MaterialSowingPreview>();
            using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync())
                {
                    var perTray = r.IsDBNull(4) ? (decimal?)null : r.GetDecimal(4);
                    list.Add(new MaterialSowingPreview
                    {
                        MaterialTypeId = r.GetInt32(0), MaterialName = r.GetString(1), Unit = r.GetString(2), Available = r.GetDecimal(3),
                        QuantityPerTray = perTray, Required = perTray.HasValue && physicalTrays > 0 ? MaterialStockRules.RequiredFor(physicalTrays, perTray.Value) : 0,
                    });
                }
            tx.Commit();
            return (true, list);
        }

        // ---- reads (Area scope in SQL: allowedAreaIds null = every Area) -----------------

        private static string AreaScope(SqlCommand cmd, IReadOnlyCollection<int>? allowedAreaIds, string column)
        {
            if (allowedAreaIds == null) return "";
            cmd.Parameters.AddWithValue("@Allowed", string.Join(",", allowedAreaIds));
            return $" AND {column} IN (SELECT CAST(value AS INT) FROM STRING_SPLIT(@Allowed, ','))";
        }

        public async Task<List<MaterialStockBalance>> GetBalancesAsync(MaterialStockFilter f, IReadOnlyCollection<int>? allowedAreaIds)
        {
            var list = new List<MaterialStockBalance>();
            if (allowedAreaIds != null && allowedAreaIds.Count == 0) return list;
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand { Connection = conn };
            var scope = AreaScope(cmd, allowedAreaIds, "s.AreaId");
            cmd.CommandText = $@"
SELECT s.Id, s.MaterialTypeId, m.Code, m.Name, m.Unit, s.AreaId, a.Name, s.PolyhouseId, p.Name, s.CurrentQuantity, s.UpdatedAt
FROM dbo.MaterialStock s
JOIN dbo.MaterialTypes m ON m.Id = s.MaterialTypeId
JOIN dbo.Area a ON a.Id = s.AreaId
JOIN dbo.Polyhouses p ON p.Id = s.PolyhouseId
WHERE (@M IS NULL OR s.MaterialTypeId = @M) AND (@A IS NULL OR s.AreaId = @A) AND (@P IS NULL OR s.PolyhouseId = @P)
  AND (@Q IS NULL OR m.Name LIKE @Q OR a.Name LIKE @Q OR p.Name LIKE @Q){scope}
ORDER BY a.Name, p.Name, m.SortOrder";
            cmd.Parameters.AddWithValue("@M", (object?)f.MaterialTypeId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@A", (object?)f.AreaId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@P", (object?)f.PolyhouseId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Q", string.IsNullOrWhiteSpace(f.Search) ? DBNull.Value : "%" + f.Search.Trim() + "%");
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new MaterialStockBalance
                {
                    Id = r.GetInt32(0), MaterialTypeId = r.GetInt32(1), MaterialCode = r.GetString(2), MaterialName = r.GetString(3), Unit = r.GetString(4),
                    AreaId = r.GetInt32(5), AreaName = r.GetString(6).Trim(), PolyhouseId = r.GetInt32(7), PolyhouseName = r.GetString(8).Trim(),
                    CurrentQuantity = r.GetDecimal(9), UpdatedAt = r.IsDBNull(10) ? null : r.GetDateTime(10),
                });
            return list;
        }

        // Per material: total stock in scope, and today's net use in sowings (India day).
        public async Task<List<MaterialKpi>> GetKpisAsync(IReadOnlyCollection<int>? allowedAreaIds, DateTime todayIndia)
        {
            var list = new List<MaterialKpi>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand { Connection = conn };
            var day = BusinessDateSql(await BedSowingSchema.ReadyAsync(conn));
            var scopeStock = allowedAreaIds == null ? "" : AreaScope(cmd, allowedAreaIds, "s.AreaId");
            var scopeTx = allowedAreaIds == null ? "" : scopeStock.Replace("s.AreaId", "s2.AreaId");
            cmd.CommandText = $@"
SELECT m.Id, m.Name, m.Unit,
       ISNULL((SELECT SUM(s.CurrentQuantity) FROM dbo.MaterialStock s WHERE s.MaterialTypeId = m.Id{scopeStock}), 0),
       ISNULL((SELECT SUM(CASE t.TransactionType WHEN 'CONSUME' THEN t.StockQuantity ELSE -t.StockQuantity END)
               FROM dbo.MaterialTransactions t JOIN dbo.MaterialStock s2 ON s2.Id = t.MaterialStockId
               WHERE s2.MaterialTypeId = m.Id AND t.TransactionType IN ('CONSUME', 'REVERSAL')
                 AND {day} >= @FromUtc AND {day} < @ToUtc{scopeTx}), 0)
FROM dbo.MaterialTypes m
WHERE m.IsActive = 1
ORDER BY m.SortOrder, m.Name";
            cmd.Parameters.AddWithValue("@FromUtc", AppClock.IndiaToUtc(todayIndia.Date));
            cmd.Parameters.AddWithValue("@ToUtc", AppClock.IndiaToUtc(todayIndia.Date.AddDays(1)));
            if (allowedAreaIds != null && allowedAreaIds.Count == 0)
                cmd.Parameters["@Allowed"].Value = "-1";
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new MaterialKpi { MaterialTypeId = r.GetInt32(0), MaterialName = r.GetString(1), Unit = r.GetString(2), TotalStock = r.GetDecimal(3), ConsumedToday = r.GetDecimal(4) });
            return list;
        }

        public async Task<(List<MaterialTransaction> Rows, int Total)> GetTransactionsAsync(MaterialTransactionFilter f, IReadOnlyCollection<int>? allowedAreaIds)
        {
            var list = new List<MaterialTransaction>();
            if (allowedAreaIds != null && allowedAreaIds.Count == 0) return (list, 0);
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand { Connection = conn };
            var day = BusinessDateSql(await BedSowingSchema.ReadyAsync(conn));
            var scope = AreaScope(cmd, allowedAreaIds, "s.AreaId");
            var where = $@"
WHERE (@M IS NULL OR s.MaterialTypeId = @M) AND (@A IS NULL OR s.AreaId = @A) AND (@P IS NULL OR s.PolyhouseId = @P)
  AND (@Type IS NULL OR t.TransactionType = @Type)
  AND (@FromUtc IS NULL OR {day} >= @FromUtc) AND (@ToUtc IS NULL OR {day} < @ToUtc){scope}";
            cmd.CommandText = $@"
SELECT COUNT(*) FROM dbo.MaterialTransactions t JOIN dbo.MaterialStock s ON s.Id = t.MaterialStockId {where};
SELECT t.Id, t.MaterialStockId, m.Name, m.Unit, s.AreaId, a.Name, s.PolyhouseId, p.Name, t.TransactionType, t.Direction, t.StockQuantity,
       t.InputQuantity, t.ConversionFactor, t.Trays, t.QuantityPerTray, t.BalanceBefore, t.BalanceAfter, t.ReferenceType, t.ReferenceId,
       w.SowingCode, t.Remarks, t.CreatedById, u.Name, t.CreatedAt, w.Id, {day}
FROM dbo.MaterialTransactions t
JOIN dbo.MaterialStock s ON s.Id = t.MaterialStockId
JOIN dbo.MaterialTypes m ON m.Id = s.MaterialTypeId
JOIN dbo.Area a ON a.Id = s.AreaId
JOIN dbo.Polyhouses p ON p.Id = s.PolyhouseId
LEFT JOIN dbo.ReadyConfirmations rc ON t.ReferenceType = 'ReadyConfirmation' AND rc.Id = t.ReferenceId
LEFT JOIN dbo.SeedSowings w ON w.Id = CASE t.ReferenceType WHEN 'SeedSowing' THEN t.ReferenceId ELSE rc.SeedSowingId END
LEFT JOIN dbo.IMSUsers u ON u.Id = t.CreatedById
{where}
ORDER BY {day} DESC, t.Id DESC
OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;";
            cmd.Parameters.AddWithValue("@M", (object?)f.MaterialTypeId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@A", (object?)f.AreaId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@P", (object?)f.PolyhouseId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Type", string.IsNullOrWhiteSpace(f.TransactionType) ? DBNull.Value : f.TransactionType);
            cmd.Parameters.Add("@FromUtc", System.Data.SqlDbType.DateTime2).Value = f.FromDate.HasValue ? AppClock.IndiaToUtc(f.FromDate.Value.Date) : DBNull.Value;
            cmd.Parameters.Add("@ToUtc", System.Data.SqlDbType.DateTime2).Value = f.ToDate.HasValue ? AppClock.IndiaToUtc(f.ToDate.Value.Date.AddDays(1)) : DBNull.Value;
            var pageSize = Math.Clamp(f.PageSize, 10, MaterialTransactionFilter.MaxPageSize);
            cmd.Parameters.AddWithValue("@Skip", (Math.Max(1, f.Page) - 1) * pageSize);
            cmd.Parameters.AddWithValue("@Take", pageSize);
            using var r = await cmd.ExecuteReaderAsync();
            await r.ReadAsync();
            var total = r.GetInt32(0);
            await r.NextResultAsync();
            while (await r.ReadAsync())
                list.Add(new MaterialTransaction
                {
                    Id = r.GetInt32(0), MaterialStockId = r.GetInt32(1), MaterialName = r.GetString(2), Unit = r.GetString(3),
                    AreaId = r.GetInt32(4), AreaName = r.GetString(5).Trim(), PolyhouseId = r.GetInt32(6), PolyhouseName = r.GetString(7).Trim(),
                    TransactionType = r.GetString(8), Direction = r.GetInt16(9), StockQuantity = r.GetDecimal(10),
                    InputQuantity = r.IsDBNull(11) ? null : r.GetDecimal(11), ConversionFactor = r.IsDBNull(12) ? null : r.GetDecimal(12),
                    Trays = r.IsDBNull(13) ? null : r.GetInt32(13), QuantityPerTray = r.IsDBNull(14) ? null : r.GetDecimal(14),
                    BalanceBefore = r.GetDecimal(15), BalanceAfter = r.GetDecimal(16),
                    ReferenceType = r.IsDBNull(17) ? null : r.GetString(17), ReferenceId = r.IsDBNull(18) ? null : r.GetInt32(18),
                    SowingCode = r.IsDBNull(19) ? null : r.GetString(19), Remarks = r.IsDBNull(20) ? null : r.GetString(20),
                    CreatedById = r.IsDBNull(21) ? null : r.GetInt32(21), CreatedByName = r.IsDBNull(22) ? null : r.GetString(22),
                    CreatedAt = r.GetDateTime(23), SeedSowingId = r.IsDBNull(24) ? null : r.GetInt32(24),
                    TransactionDate = r.GetDateTime(25),
                });
            return (list, total);
        }

        // Net use in sowings per material and Polyhouse, for India dates [from, to].
        public async Task<List<MaterialConsumptionSummaryRow>> GetConsumptionSummaryAsync(DateTime fromIndia, DateTime toIndia, IReadOnlyCollection<int>? allowedAreaIds)
        {
            var list = new List<MaterialConsumptionSummaryRow>();
            if (allowedAreaIds != null && allowedAreaIds.Count == 0) return list;
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand { Connection = conn };
            var day = BusinessDateSql(await BedSowingSchema.ReadyAsync(conn));
            var scope = AreaScope(cmd, allowedAreaIds, "s.AreaId");
            cmd.CommandText = $@"
SELECT m.Name, a.Name, p.Name,
       COUNT(DISTINCT CASE WHEN t.TransactionType = 'CONSUME' AND t.ReferenceType = 'SeedSowing' THEN t.ReferenceId END)
         - COUNT(DISTINCT CASE WHEN t.TransactionType = 'REVERSAL' AND t.ReferenceType = 'SeedSowing' THEN t.ReferenceId END),
       ISNULL(SUM(CASE t.TransactionType WHEN 'CONSUME' THEN t.Trays END), 0)
         - ISNULL(SUM(CASE WHEN t.TransactionType = 'REVERSAL' THEN c.Trays END), 0),
       SUM(CASE t.TransactionType WHEN 'CONSUME' THEN t.StockQuantity ELSE -t.StockQuantity END)
FROM dbo.MaterialTransactions t
JOIN dbo.MaterialStock s ON s.Id = t.MaterialStockId
JOIN dbo.MaterialTypes m ON m.Id = s.MaterialTypeId
JOIN dbo.Area a ON a.Id = s.AreaId
JOIN dbo.Polyhouses p ON p.Id = s.PolyhouseId
LEFT JOIN dbo.MaterialTransactions c ON t.TransactionType = 'REVERSAL' AND c.ReferenceType = t.ReferenceType AND c.ReferenceId = t.ReferenceId AND c.TransactionType = 'CONSUME'
WHERE t.TransactionType IN ('CONSUME', 'REVERSAL') AND {day} >= @FromUtc AND {day} < @ToUtc{scope}
GROUP BY m.Name, m.SortOrder, a.Name, p.Name
HAVING SUM(CASE t.TransactionType WHEN 'CONSUME' THEN t.StockQuantity ELSE -t.StockQuantity END) <> 0
ORDER BY 6 DESC, m.SortOrder, a.Name, p.Name";
            cmd.Parameters.AddWithValue("@FromUtc", AppClock.IndiaToUtc(fromIndia.Date));
            cmd.Parameters.AddWithValue("@ToUtc", AppClock.IndiaToUtc(toIndia.Date.AddDays(1)));
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new MaterialConsumptionSummaryRow
                {
                    MaterialName = r.GetString(0), AreaName = r.GetString(1).Trim(), PolyhouseName = r.GetString(2).Trim(),
                    Sowings = r.GetInt32(3), Trays = Convert.ToInt32(r.GetValue(4)), Consumed = r.GetDecimal(5),
                });
            return list;
        }

        // The material rows of one sowing (Details page).
        public async Task<List<MaterialTransaction>> GetForSowingAsync(int seedSowingId)
        {
            var list = new List<MaterialTransaction>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
SELECT t.Id, m.Name, m.Unit, p.Name, t.TransactionType, t.Direction, t.StockQuantity, t.Trays, t.QuantityPerTray, t.BalanceAfter, t.CreatedAt, u.Name,
       t.ReferenceType, t.ReferenceId, t.Remarks
FROM dbo.MaterialTransactions t
JOIN dbo.MaterialStock s ON s.Id = t.MaterialStockId
JOIN dbo.MaterialTypes m ON m.Id = s.MaterialTypeId
JOIN dbo.Polyhouses p ON p.Id = s.PolyhouseId
LEFT JOIN dbo.IMSUsers u ON u.Id = t.CreatedById
WHERE (t.ReferenceType = 'SeedSowing' AND t.ReferenceId = @Id)
   OR (t.ReferenceType = 'ReadyConfirmation' AND t.ReferenceId IN (SELECT rc.Id FROM dbo.ReadyConfirmations rc WHERE rc.SeedSowingId = @Id))
ORDER BY t.Id", conn);
            cmd.Parameters.AddWithValue("@Id", seedSowingId);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new MaterialTransaction
                {
                    Id = r.GetInt32(0), MaterialName = r.GetString(1), Unit = r.GetString(2), PolyhouseName = r.GetString(3).Trim(),
                    TransactionType = r.GetString(4), Direction = r.GetInt16(5), StockQuantity = r.GetDecimal(6),
                    Trays = r.IsDBNull(7) ? null : r.GetInt32(7), QuantityPerTray = r.IsDBNull(8) ? null : r.GetDecimal(8),
                    BalanceAfter = r.GetDecimal(9), CreatedAt = r.GetDateTime(10), CreatedByName = r.IsDBNull(11) ? null : r.GetString(11),
                    ReferenceType = r.GetString(12), ReferenceId = r.GetInt32(13), Remarks = r.IsDBNull(14) ? null : r.GetString(14),
                });
            return list;
        }
    }
}
