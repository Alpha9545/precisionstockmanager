using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Data
{
    // Phase D: Mother Plant -> Cutting Production -> Cutting Stock.
    // One atomic transaction: lock the Mother Plant, credit the
    // (variety, Area) Cutting Stock pool with a 'Harvest' ledger entry and
    // save the production record that references it.
    public class CuttingProductionRepository
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly BatchNumberRepository _batchNumberRepo;
        private readonly CuttingStockRepository _cuttingStockRepo;
        private readonly UserRoleRepository _userRoleRepo;

        public CuttingProductionRepository(DatabaseHelper dbHelper, BatchNumberRepository batchNumberRepo,
            CuttingStockRepository cuttingStockRepo, UserRoleRepository userRoleRepo)
        {
            _dbHelper = dbHelper;
            _batchNumberRepo = batchNumberRepo;
            _cuttingStockRepo = cuttingStockRepo;
            _userRoleRepo = userRoleRepo;
        }

        private const string BaseSelect = @"
SELECT cp.Id, cp.ProductionCode, cp.MotherPlantId, mp.MotherPlantCode, cp.SpeciesId, ps.Name AS SpeciesName, ps.Color,
       pt.Name AS PlantTypeName, cp.AreaId, a.Name AS AreaName, cp.CuttingStockId, cp.CuttingDate, cp.Quantity,
       cp.SupervisorId, sup.Name AS SupervisorName, cp.Remarks, cp.CreatedById, cp.CreatedBy, cp.CreatedDate
FROM dbo.CuttingProductions cp
INNER JOIN dbo.MotherPlants mp ON mp.Id = cp.MotherPlantId
INNER JOIN dbo.PlantSpecies ps ON ps.Id = cp.SpeciesId
INNER JOIN dbo.PlantTypes pt ON pt.Id = ps.PlantTypeId
INNER JOIN dbo.Area a ON a.Id = cp.AreaId
LEFT JOIN dbo.IMSUsers sup ON sup.Id = cp.SupervisorId";

        public async Task<List<CuttingProduction>> GetAllAsync(DateTime? from = null, DateTime? to = null, int? motherPlantId = null)
        {
            var list = new List<CuttingProduction>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(BaseSelect + @"
WHERE (@From IS NULL OR cp.CuttingDate >= @From)
  AND (@To IS NULL OR cp.CuttingDate <= @To)
  AND (@MotherPlantId IS NULL OR cp.MotherPlantId = @MotherPlantId)
ORDER BY cp.CuttingDate DESC, cp.Id DESC", conn);
            cmd.Parameters.AddWithValue("@From", (object?)from?.Date ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@To", (object?)to?.Date ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@MotherPlantId", (object?)motherPlantId ?? DBNull.Value);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                list.Add(Map(reader));
            return list;
        }

        public async Task<(bool Success, string? Message, int Id)> InsertAsync(CuttingProduction entry, int? userId)
        {
            var (quantityOk, quantityError) = CuttingRules.ValidateProductionQuantity(entry.Quantity);
            if (!quantityOk)
                return (false, quantityError, 0);
            if (entry.CuttingDate == default)
                return (false, "Cutting date is required.", 0);
            if (entry.CuttingDate.Date > DateTime.Today)
                return (false, "Cutting date cannot be in the future.", 0);

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                // The variety and Area always come from the Mother Plant.
                var mpCmd = new SqlCommand(
                    "SELECT SpeciesId, AreaId, Status FROM dbo.MotherPlants WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
                mpCmd.Parameters.AddWithValue("@Id", entry.MotherPlantId);
                int speciesId; int? areaId; string status;
                using (var reader = await mpCmd.ExecuteReaderAsync())
                {
                    if (!await reader.ReadAsync())
                    {
                        reader.Close();
                        tx.Rollback();
                        return (false, "Mother Plant not found.", 0);
                    }
                    speciesId = reader.GetInt32(0);
                    areaId = reader.IsDBNull(1) ? null : reader.GetInt32(1);
                    status = reader.GetString(2);
                }
                if (!string.Equals(status, "Active", StringComparison.OrdinalIgnoreCase))
                {
                    tx.Rollback();
                    return (false, $"This Mother Plant is '{status}'; cuttings can only be recorded for an Active Mother Plant.", 0);
                }
                if (!areaId.HasValue)
                {
                    tx.Rollback();
                    return (false, "This Mother Plant has no Area. Set its Area (Mother Plants > Edit) before recording cuttings.", 0);
                }

                var supervisors = (await _userRoleRepo.GetUsersInRoleAsync(SupervisorRules.MotherPlantSupervisor, areaId.Value, conn, tx))
                    .Select(u => u.EmployeeID).ToHashSet();
                if (!supervisors.Contains(entry.SupervisorId))
                {
                    tx.Rollback();
                    return (false, "The supervisor must be an active Mother Plant Supervisor of this Mother Plant's Area.", 0);
                }

                var stockId = await _cuttingStockRepo.GetOrCreateLockedAsync(conn, tx, speciesId, areaId.Value, entry.CreatedBy);
                var code = await _batchNumberRepo.GetNextBatchNumberAsync(conn, tx, "CP", entry.CuttingDate.Year);

                var insert = new SqlCommand(@"
INSERT INTO dbo.CuttingProductions
(ProductionCode, MotherPlantId, SpeciesId, AreaId, CuttingStockId, CuttingDate, Quantity, SupervisorId, Remarks, CreatedById, CreatedBy)
VALUES
(@Code, @MotherPlantId, @SpeciesId, @AreaId, @CuttingStockId, @CuttingDate, @Quantity, @SupervisorId, @Remarks, @CreatedById, @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn, tx);
                insert.Parameters.AddWithValue("@Code", code);
                insert.Parameters.AddWithValue("@MotherPlantId", entry.MotherPlantId);
                insert.Parameters.AddWithValue("@SpeciesId", speciesId);
                insert.Parameters.AddWithValue("@AreaId", areaId.Value);
                insert.Parameters.AddWithValue("@CuttingStockId", stockId);
                insert.Parameters.AddWithValue("@CuttingDate", entry.CuttingDate.Date);
                insert.Parameters.AddWithValue("@Quantity", entry.Quantity);
                insert.Parameters.AddWithValue("@SupervisorId", entry.SupervisorId);
                insert.Parameters.AddWithValue("@Remarks", (object?)entry.Remarks ?? DBNull.Value);
                insert.Parameters.AddWithValue("@CreatedById", (object?)userId ?? DBNull.Value);
                insert.Parameters.AddWithValue("@CreatedBy", (object?)entry.CreatedBy ?? DBNull.Value);
                var newId = (int)(await insert.ExecuteScalarAsync())!;

                var (ok, message) = await _cuttingStockRepo.RecordTransactionAsync(
                    conn, tx, stockId, entry.Quantity, "Harvest", "CuttingProduction", newId, userId, entry.Remarks);
                if (!ok)
                {
                    tx.Rollback();
                    return (false, message, 0);
                }

                tx.Commit();
                entry.Id = newId;
                entry.ProductionCode = code;
                entry.SpeciesId = speciesId;
                entry.AreaId = areaId.Value;
                entry.CuttingStockId = stockId;
                return (true, null, newId);
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                return (false, ex.Message, 0);
            }
        }

        private static CuttingProduction Map(SqlDataReader r) => new()
        {
            Id = r.GetInt32(r.GetOrdinal("Id")),
            ProductionCode = r.GetString(r.GetOrdinal("ProductionCode")),
            MotherPlantId = r.GetInt32(r.GetOrdinal("MotherPlantId")),
            MotherPlantCode = r.GetString(r.GetOrdinal("MotherPlantCode")),
            SpeciesId = r.GetInt32(r.GetOrdinal("SpeciesId")),
            SpeciesName = r.GetString(r.GetOrdinal("SpeciesName")).Trim(),
            Color = r.IsDBNull(r.GetOrdinal("Color")) ? null : r.GetString(r.GetOrdinal("Color")),
            PlantTypeName = r.GetString(r.GetOrdinal("PlantTypeName")),
            AreaId = r.GetInt32(r.GetOrdinal("AreaId")),
            AreaName = r.GetString(r.GetOrdinal("AreaName")),
            CuttingStockId = r.GetInt32(r.GetOrdinal("CuttingStockId")),
            CuttingDate = r.GetDateTime(r.GetOrdinal("CuttingDate")),
            Quantity = r.GetDecimal(r.GetOrdinal("Quantity")),
            SupervisorId = r.GetInt32(r.GetOrdinal("SupervisorId")),
            SupervisorName = r.IsDBNull(r.GetOrdinal("SupervisorName")) ? null : r.GetString(r.GetOrdinal("SupervisorName")),
            Remarks = r.IsDBNull(r.GetOrdinal("Remarks")) ? null : r.GetString(r.GetOrdinal("Remarks")),
            CreatedById = r.IsDBNull(r.GetOrdinal("CreatedById")) ? null : r.GetInt32(r.GetOrdinal("CreatedById")),
            CreatedBy = r.IsDBNull(r.GetOrdinal("CreatedBy")) ? null : r.GetString(r.GetOrdinal("CreatedBy")),
            CreatedDate = r.GetDateTime(r.GetOrdinal("CreatedDate"))
        };
    }
}
