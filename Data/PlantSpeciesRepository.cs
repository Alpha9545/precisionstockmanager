using PlantStockManager.Models;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using System.Threading.Tasks;

namespace PlantStockManager.Data
{
    public class PlantSpeciesRepository
    {
        private readonly DatabaseHelper _dbHelper;

        public PlantSpeciesRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        // Needed by Production/MotherPlant/Edit to pre-select the Plant Type
        // dropdown for an existing batch, since MotherPlants only stores
        // SpeciesId (the cascading Plant Type -> Species pattern used
        // elsewhere in the app needs the parent id to redisplay correctly).
        public async Task<int> GetPlantTypeIdBySpeciesId(int speciesId)
        {
            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                var cmd = new SqlCommand("SELECT PlantTypeId FROM PlantSpecies WHERE Id = @Id", conn);
                cmd.Parameters.AddWithValue("@Id", speciesId);
                var result = await cmd.ExecuteScalarAsync();
                return result != null ? Convert.ToInt32(result) : 0;
            }
        }

        // Phase 26 (Phase L): the Management Dashboard's Plant/Variety
        // filter dropdown needs every Species regardless of Plant Type,
        // which no existing method provided (GetSpeciesByPlantType
        // requires a PlantTypeId; GetByIdAsync is single-row). Purely
        // additive read-only SELECT, mirrors GetSpeciesByPlantType's own
        // shape exactly -- no existing caller or method is touched.
        // 2026-10-15: active varieties only unless includeInactive (retired "DUP-..." duplicates are never offered).
        public async Task<List<PlantSpecies>> GetAllAsync(bool includeInactive = false)
        {
            var speciesList = new List<PlantSpecies>();
            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                var cmd = new SqlCommand("SELECT Id, PlantTypeId, Name, ScientificName, ReadyStockDays, Color, IsActive FROM PlantSpecies"
                    + (includeInactive ? "" : " WHERE IsActive = 1") + " ORDER BY Name", conn);
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        speciesList.Add(new PlantSpecies
                        {
                            Id = reader.GetInt32(0),
                            PlantTypeId = reader.GetInt32(1),
                            Name = reader.GetString(2),
                            ScientificName = reader.IsDBNull(3) ? null : reader.GetString(3),
                            ReadyStockDays = reader.IsDBNull(4) ? (int?)null : reader.GetInt32(4),
                            Color = reader.IsDBNull(5) ? null : reader.GetString(5),
                            IsActive = reader.GetBoolean(6)
                        });
                    }
                }
            }
            return speciesList;
        }

        // 2026-10-15: active varieties only unless includeInactive -- every variety dropdown / search for a NEW entry
        // uses the default; only the Variety master (Admin/Plant, "show inactive" filter) asks for inactive ones.
        public async Task<List<PlantSpecies>> GetSpeciesByPlantType(int plantTypeId, bool includeInactive = false)
        {
            var speciesList = new List<PlantSpecies>();
            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                var cmd = new SqlCommand("SELECT * FROM PlantSpecies WHERE PlantTypeId = @PlantTypeId"
                    + (includeInactive ? "" : " AND IsActive = 1") + " ORDER BY Name", conn);
                cmd.Parameters.AddWithValue("@PlantTypeId", plantTypeId);

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    // Phase 24: read ReadyStockDays by name (not a fixed
                    // ordinal) so this SELECT * keeps working regardless
                    // of where the new column landed. Every existing
                    // caller of this method (there are many, across the
                    // legacy SeedEntry/Bookings/Data pages) only ever
                    // reads Id/PlantTypeId/Name/ScientificName, so adding
                    // this field is purely additive and safe.
                    var readyStockDaysOrdinal = reader.GetOrdinal("ReadyStockDays");
                    var colorOrdinal = reader.GetOrdinal("Color");
                    var isActiveOrdinal = reader.GetOrdinal("IsActive");
                    while (await reader.ReadAsync())
                    {
                        speciesList.Add(new PlantSpecies
                        {
                            Id = reader.GetInt32(0),
                            PlantTypeId = reader.GetInt32(1),
                            Name = reader.GetString(2),
                            ScientificName = reader.IsDBNull(3) ? null : reader.GetString(3),
                            ReadyStockDays = reader.IsDBNull(readyStockDaysOrdinal) ? (int?)null : reader.GetInt32(readyStockDaysOrdinal),
                            Color = reader.IsDBNull(colorOrdinal) ? null : reader.GetString(colorOrdinal),
                            IsActive = reader.GetBoolean(isActiveOrdinal)
                        });
                    }
                }
            }
            return speciesList;
        }

        // Phase 24 (Phase J): needed by SeedSowingRepository.InsertAsync
        // to look up a single species' ReadyStockDays at the moment of
        // sowing (SowingDate + ReadyStockDays = ExpectedReadyDate, using
        // the value applicable AT THAT TIME -- never recomputed later).
        // Called the same way AreaRepository.GetAreaById already is from
        // that same method: a plain read-only master lookup on its own
        // connection, outside the SeedStock transaction.
        public async Task<PlantSpecies?> GetByIdAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            var cmd = new SqlCommand("SELECT Id, PlantTypeId, Name, ScientificName, ReadyStockDays, Color, IsActive FROM PlantSpecies WHERE Id = @Id", conn);
            cmd.Parameters.AddWithValue("@Id", id);
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new PlantSpecies
                {
                    Id = reader.GetInt32(0),
                    PlantTypeId = reader.GetInt32(1),
                    Name = reader.GetString(2),
                    ScientificName = reader.IsDBNull(3) ? null : reader.GetString(3),
                    ReadyStockDays = reader.IsDBNull(4) ? (int?)null : reader.GetInt32(4),
                    Color = reader.IsDBNull(5) ? null : reader.GetString(5),
                    IsActive = reader.GetBoolean(6)
                };
            }
            return null;
        }


        // Phase 24: added an optional readyStockDays parameter, defaulted
        // to null. AddPlantSpecies/UpdatePlantSpecies have exactly one
        // caller in the whole codebase -- Pages/Admin/Plant.cshtml.cs --
        // confirmed by grep, so widening the signature here is safe and
        // that page has been updated to pass the new field through.
        //
        // Duplicate-name guard (2026-10-14): refuses a name that already exists under ANY Plant Type, compared by
        // PlantSpeciesNameRules (case and whitespace ignored, punctuation and spelling kept). Returns null when the
        // variety was added, or the existing variety it clashes with (nothing inserted). The read takes UPDLOCK +
        // HOLDLOCK on PlantSpecies inside the transaction, so two simultaneous adds of one name cannot both insert
        // (backed since 2026-10-15 by UX_PlantSpecies_Name_Active: unique Name among active varieties). Plain reads are not blocked.
        // 2026-10-15: only ACTIVE varieties count -- a retired "DUP-..." duplicate never blocks a name.
        public async Task<(int Id, string Name, string? PlantTypeName)?> AddPlantSpecies(int plantTypeId, string name, string scientificName, int? readyStockDays = null, string? color = null)
        {
            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable);
                var key = PlantStockManager.Services.PlantSpeciesNameRules.Key(name);
                var read = new SqlCommand("SELECT s.Id, s.Name, t.Name FROM PlantSpecies s WITH (UPDLOCK, HOLDLOCK) LEFT JOIN PlantTypes t ON t.Id = s.PlantTypeId WHERE s.IsActive = 1 ORDER BY s.Id", conn, tx);
                using (var reader = await read.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        if (PlantStockManager.Services.PlantSpeciesNameRules.Key(reader.GetString(1)) == key)
                        {
                            var existing = (reader.GetInt32(0), reader.GetString(1).Trim(), reader.IsDBNull(2) ? null : reader.GetString(2));
                            reader.Close();
                            tx.Rollback();
                            return existing;
                        }
                    }
                }
                var cmd = new SqlCommand("INSERT INTO PlantSpecies (PlantTypeId, Name, ScientificName, ReadyStockDays, Color) VALUES (@PlantTypeId, @Name, @ScientificName, @ReadyStockDays, @Color)", conn, tx);
                cmd.Parameters.AddWithValue("@PlantTypeId", plantTypeId);
                cmd.Parameters.AddWithValue("@Name", name.Trim());
                cmd.Parameters.AddWithValue("@ScientificName", string.IsNullOrWhiteSpace(scientificName) ? (object)DBNull.Value : scientificName);
                cmd.Parameters.AddWithValue("@ReadyStockDays", readyStockDays.HasValue ? (object)readyStockDays.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("@Color", string.IsNullOrWhiteSpace(color) ? (object)DBNull.Value : color.Trim());
                await cmd.ExecuteNonQueryAsync();
                tx.Commit();
                return null;
            }
        }

        // Duplicate-name guard on rename (2026-10-14), same rule and locking as AddPlantSpecies: refuses a NEW name that
        // another variety already has (its own Id excluded). Keeping the current name (in any capitals/spacing) is always
        // allowed, so a variety that already shares its name with an older duplicate (e.g. the chrysanthemum copies
        // with a trailing line break) can still have its other fields edited. Returns null when saved, else the clash.
        // 2026-10-15: only ACTIVE other varieties clash, and the name is saved trimmed.
        public async Task<(int Id, string Name, string? PlantTypeName)?> UpdatePlantSpecies(int id, string name, string scientificName, int? readyStockDays = null, string? color = null)
        {
            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable);
                var key = PlantStockManager.Services.PlantSpeciesNameRules.Key(name);
                var all = new List<(int Id, string Name, string? PlantTypeName)>();
                var read = new SqlCommand("SELECT s.Id, s.Name, t.Name FROM PlantSpecies s WITH (UPDLOCK, HOLDLOCK) LEFT JOIN PlantTypes t ON t.Id = s.PlantTypeId WHERE s.IsActive = 1 OR s.Id = @Id ORDER BY s.Id", conn, tx);
                read.Parameters.AddWithValue("@Id", id);
                using (var reader = await read.ExecuteReaderAsync())
                    while (await reader.ReadAsync())
                        all.Add((reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
                var current = all.FirstOrDefault(s => s.Id == id);
                var renamed = current.Name == null || PlantStockManager.Services.PlantSpeciesNameRules.Key(current.Name) != key;
                var clash = renamed ? all.FirstOrDefault(s => s.Id != id && PlantStockManager.Services.PlantSpeciesNameRules.Key(s.Name) == key) : default;
                if (clash.Name != null)
                {
                    tx.Rollback();
                    return (clash.Id, clash.Name.Trim(), clash.PlantTypeName);
                }
                // Not renamed while an older active twin still shares the name (a legacy pair the cleanup has not merged):
                // keep the stored name byte-for-byte -- trimming it would collide with the twin (UX_PlantSpecies_Name_Active).
                var keepStored = !renamed && all.Any(s => s.Id != id && PlantStockManager.Services.PlantSpeciesNameRules.Key(s.Name) == key);
                var cmd = new SqlCommand("UPDATE PlantSpecies SET Name = @Name, ScientificName = @ScientificName, ReadyStockDays = @ReadyStockDays, Color = @Color WHERE Id = @Id", conn, tx);
                cmd.Parameters.AddWithValue("@Name", keepStored ? current.Name! : name.Trim());
                cmd.Parameters.AddWithValue("@ScientificName", string.IsNullOrWhiteSpace(scientificName) ? (object)DBNull.Value : scientificName);
                cmd.Parameters.AddWithValue("@ReadyStockDays", readyStockDays.HasValue ? (object)readyStockDays.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("@Color", string.IsNullOrWhiteSpace(color) ? (object)DBNull.Value : color.Trim());
                cmd.Parameters.AddWithValue("@Id", id);
                await cmd.ExecuteNonQueryAsync();
                tx.Commit();
                return null;
            }
        }

        // 2026-10-15: what still USES a variety -- stock with something in it, or bookings / batches / sowings that are still
        // open. Deactivating is refused while any of these exist. Each check runs only when its table/column exists.
        private static readonly (string Table, string Label, string Where)[] ActiveUses =
        {
            ("SeedStock",               "Seed Stock lot(s) with seeds",           "SpeciesId = @Id AND PhysicalQuantity > 0"),
            ("CuttingStock",            "Cutting Stock row(s) with cuttings",     "SpeciesId = @Id AND PhysicalQuantity > 0"),
            ("PottedPlantStock",        "Potted Plant Stock row(s) with plants",  "SpeciesId = @Id AND PhysicalQuantity > 0"),
            ("ReadyStock",              "Ready Stock row(s) with plants",         "SpeciesId = @Id AND PhysicalQuantity > 0"),
            ("MotherPlants",            "active Mother Plant batch(es)",          "SpeciesId = @Id AND Status = N'Active'"),
            ("Bookings",                "pending seedling booking(s)",            "SpeciesId = @Id AND Status = N'Pending'"),
            ("PottedPlantBookings",     "open potted plant booking(s)",           "SpeciesId = @Id AND Status IN (N'Pending', N'PartiallyDispatched')"),
            ("BookingBatchAllocations", "active booking batch allocation(s)",     "(BookedSpeciesId = @Id OR ActualSpeciesId = @Id) AND Status = N'Active'"),
            ("BedSowings",              "bed sowing(s) not fully transplanted",   "SpeciesId = @Id AND Status IN (N'PendingTransplant', N'PartiallyTransplanted')"),
            ("SeedSowings",             "open seed sowing(s)",                    "SpeciesId = @Id AND Status = N'Sown'"),
            ("PropagationBatches",      "open propagation batch(es)",             "SpeciesId = @Id AND Status IN (N'Propagating', N'ReadyForPotting')"),
            ("PotProductionBatches",    "pot batch(es) in production",            "SpeciesId = @Id AND Status = N'InProduction'"),
        };

        // 2026-10-15: replaces the physical Delete (owner rule: no physical deletes). Sets IsActive = 0 so the variety
        // disappears from every dropdown; history keeps its name. Refused -- with what still uses it -- while any active
        // stock / booking / open batch uses it. A merged "DUP-..." duplicate is already inactive.
        public async Task<(bool Success, string Message)> DeactivatePlantSpecies(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable);

            var read = new SqlCommand("SELECT Name, IsActive FROM PlantSpecies WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
            read.Parameters.AddWithValue("@Id", id);
            string? name = null;
            var isActive = false;
            using (var r = await read.ExecuteReaderAsync())
                if (await r.ReadAsync()) { name = r.GetString(0).Trim(); isActive = r.GetBoolean(1); }
            if (name == null) { tx.Rollback(); return (false, "Variety not found."); }
            if (!isActive) { tx.Rollback(); return (false, $"'{name}' is already inactive."); }

            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cols = new SqlCommand("SELECT t.name + N'.' + c.name FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id WHERE SCHEMA_NAME(t.schema_id) = N'dbo'", conn, tx))
            using (var r = await cols.ExecuteReaderAsync())
                while (await r.ReadAsync()) present.Add(r.GetString(0));

            var uses = new List<string>();
            foreach (var (table, label, where) in ActiveUses)
            {
                var needed = where.Contains("BookedSpeciesId") ? new[] { "BookedSpeciesId", "ActualSpeciesId", "Status" }
                           : where.Contains("Status") ? new[] { "SpeciesId", "Status" } : new[] { "SpeciesId", "PhysicalQuantity" };
                if (!needed.All(c => present.Contains(table + "." + c))) continue;
                var cmd = new SqlCommand($"SELECT COUNT(*) FROM dbo.[{table}] WHERE {where}", conn, tx);
                cmd.Parameters.AddWithValue("@Id", id);
                var n = (int)(await cmd.ExecuteScalarAsync())!;
                if (n > 0) uses.Add($"{n} {label}");
            }
            if (uses.Count > 0)
            {
                tx.Rollback();
                return (false, $"Cannot deactivate '{name}': it is still in use -- {string.Join(", ", uses)}. "
                    + "Use up the stock and close these first, then deactivate it.");
            }

            var upd = new SqlCommand("UPDATE PlantSpecies SET IsActive = 0 WHERE Id = @Id AND IsActive = 1", conn, tx);
            upd.Parameters.AddWithValue("@Id", id);
            await upd.ExecuteNonQueryAsync();
            tx.Commit();
            return (true, $"'{name}' is deactivated: it no longer appears in any variety dropdown. Old records keep its name.");
        }

        // 2026-10-15: re-activates a variety that was DEACTIVATED normally (Variety master, "show inactive" filter).
        // Refused for a merged duplicate ("DUP-..."): its history now belongs to the kept variety, named in the message.
        // Also refused while an ACTIVE variety already has the same name (same name rule and locking as AddPlantSpecies).
        public async Task<(bool Success, string Message)> ReactivatePlantSpecies(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable);
            var all = new List<(int Id, string Name, string? PlantTypeName, bool IsActive, int? PlantTypeId)>();
            var read = new SqlCommand("SELECT s.Id, s.Name, t.Name, s.IsActive, s.PlantTypeId FROM PlantSpecies s WITH (UPDLOCK, HOLDLOCK) LEFT JOIN PlantTypes t ON t.Id = s.PlantTypeId WHERE s.IsActive = 1 OR s.Id = @Id ORDER BY s.Id", conn, tx);
            read.Parameters.AddWithValue("@Id", id);
            using (var reader = await read.ExecuteReaderAsync())
                while (await reader.ReadAsync())
                    all.Add((reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetBoolean(3),
                             reader.IsDBNull(4) ? null : reader.GetInt32(4)));
            var current = all.FirstOrDefault(s => s.Id == id);
            if (current.Name == null) { tx.Rollback(); return (false, "Variety not found."); }
            if (current.IsActive) { tx.Rollback(); return (false, $"'{current.Name.Trim()}' is already active."); }

            if (PlantStockManager.Services.PlantSpeciesNameRules.IsMergedDuplicate(current.Name))
            {
                tx.Rollback();
                var kept = await FindMergedIntoAsync(id);
                return (false, kept is { } k
                    ? $"This variety was merged into '{k.Name}' (Id {k.Id}). Use '{k.Name}' instead -- a merged duplicate cannot be reactivated."
                    : "This variety was merged into another variety and cannot be reactivated.");
            }

            var key = PlantStockManager.Services.PlantSpeciesNameRules.Key(current.Name);
            var clash = all.FirstOrDefault(s => s.Id != id && s.IsActive && PlantStockManager.Services.PlantSpeciesNameRules.Key(s.Name) == key);
            if (clash.Name != null)
            {
                tx.Rollback();
                return (false, $"Variety not reactivated: an active variety is already named '{clash.Name.Trim()}' (Id {clash.Id}"
                    + $"{(clash.PlantTypeName == null ? "" : ", " + clash.PlantTypeName.Trim())}). Rename one of them first.");
            }
            var cmd = new SqlCommand("UPDATE PlantSpecies SET IsActive = 1 WHERE Id = @Id AND IsActive = 0", conn, tx);
            cmd.Parameters.AddWithValue("@Id", id);
            await cmd.ExecuteNonQueryAsync();
            tx.Commit();
            return (true, $"'{current.Name.Trim()}' is active again and appears in every variety dropdown.");
        }

        // 2026-10-15: the kept variety a merged "DUP-<Id>-<old name>" duplicate went into: the ACTIVE variety of the same
        // Plant Type whose name is the old name (capitals / spaces ignored); null when it cannot be told. Read-only.
        public async Task<(int Id, string Name)?> FindMergedIntoAsync(int duplicateId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            var read = new SqlCommand(@"SELECT s.Id, s.Name, s.IsActive, s.PlantTypeId FROM PlantSpecies s
WHERE s.IsActive = 1 AND s.PlantTypeId = (SELECT PlantTypeId FROM PlantSpecies WHERE Id = @Id) OR s.Id = @Id ORDER BY s.Id", conn);
            read.Parameters.AddWithValue("@Id", duplicateId);
            var rows = new List<(int Id, string Name, bool IsActive)>();
            using (var r = await read.ExecuteReaderAsync())
                while (await r.ReadAsync()) rows.Add((r.GetInt32(0), r.GetString(1), r.GetBoolean(2)));
            var dup = rows.FirstOrDefault(x => x.Id == duplicateId);
            var from = PlantStockManager.Services.PlantSpeciesNameRules.MergedFromName(dup.Name);
            if (from == null) return null;
            var key = PlantStockManager.Services.PlantSpeciesNameRules.Key(from);
            var kept = rows.FirstOrDefault(x => x.Id != duplicateId && x.IsActive && PlantStockManager.Services.PlantSpeciesNameRules.Key(x.Name) == key);
            return kept.Name == null ? null : (kept.Id, kept.Name.Trim());
        }
    }
}
