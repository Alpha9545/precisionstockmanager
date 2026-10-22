using Microsoft.Data.SqlClient;
using PlantStockManager.Services;

namespace PlantStockManager.Data
{
    // 2026-10-13 (Database/Migrations/2026-10-13_TraysToSowingApprovals.sql): the ONE way a transplant
    // into cavity trays creates its growing tray batch (dbo.SeedSowings) -- used by Bed Sowing
    // transplanting (SourceType 'BedSowing', Maya Shinde) and by transplanting Ready Stock received in
    // a Mother Plant Area (SourceType 'Transplant', its Mother Plant Supervisor). The batch then goes
    // through the existing Sowing Approvals; nothing here creates Ready Stock.
    //   * complete trays only (the sowing tables' whole-tray rule): Quantity = Trays x cavity;
    //   * Ready Date = ACTUAL TRANSPLANT DATE + the variety's configured Ready Days, both stored on the
    //     batch (a later change of the variety never rewrites them);
    //   * no supervisor is assigned -- any user who may approve sowings approves it (a BedSowing batch
    //     never by its recorder): TR_ReadyConfirmations_AssignedSupervisor;
    //   * the origin's own date is kept as SowingDate (the bed sowing date, or the received batch's
    //     sowing date), separate from the transplant date and the Ready Date.
    public static class TrayBatchWriter
    {
        public sealed record Spec(
            string SourceType, int? BedSowingId, int SpeciesId, int AreaId, int? PolyhouseId, string BatchNo, int? SeedSourceId,
            string CavityType, int Trays, decimal Quantity, DateTime OriginalSowingDate, int ReadyDays, DateTime ReadyDate,
            int? MaterialTypeId, string Remarks, string? CreatedBy, int? CreatedById);

        // The variety's configured Ready Days, read inside the caller's transaction (no second
        // configuration, nothing defaulted).
        public static async Task<(int? Days, string? VarietyName)> ReadyDaysAsync(SqlConnection conn, SqlTransaction tx, int speciesId)
        {
            using var cmd = new SqlCommand("SELECT ReadyStockDays, RTRIM(Name) FROM dbo.PlantSpecies WHERE Id = @Id", conn, tx);
            cmd.Parameters.AddWithValue("@Id", speciesId);
            using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync())
                return (null, null);
            return (r.IsDBNull(0) ? null : r.GetInt32(0), r.IsDBNull(1) ? null : r.GetString(1));
        }

        public static async Task<(int Id, string Code)> InsertAsync(SqlConnection conn, SqlTransaction tx, BatchNumberRepository batchNumbers, Spec s)
        {
            var code = await batchNumbers.GetNextSowingBatchNumberAsync(conn, tx, s.OriginalSowingDate.Date);
            using var cmd = new SqlCommand(@"
INSERT INTO dbo.SeedSowings
(SowingCode, SourceType, SourceSeedStockId, SourceCuttingStockId, BedSowingId, SpeciesId, AreaId, PolyhouseId, BatchNo, SeedSourceId, CavityType,
 NumberOfTrays, QuantitySown, SeedQuantity, SowingDate, ReadyStockDays, ExpectedReadyDate, Status, SupervisorId, Remarks,
 CreatedDate, CreatedBy, CreatedById, GrowingMaterialTypeId)
VALUES
(@Code, @SourceType, NULL, NULL, @BedId, @SpeciesId, @AreaId, @PolyhouseId, @BatchNo, @SeedSourceId, @Cavity,
 @Trays, @Qty, @Qty, @SowingDate, @Days, @ReadyDate, 'Sown', NULL, @Remarks,
 SYSUTCDATETIME(), @CreatedBy, @CreatedById, @MaterialId);
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn, tx);
            cmd.Parameters.AddWithValue("@Code", code);
            cmd.Parameters.AddWithValue("@SourceType", s.SourceType);
            cmd.Parameters.AddWithValue("@BedId", (object?)s.BedSowingId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SpeciesId", s.SpeciesId);
            cmd.Parameters.AddWithValue("@AreaId", s.AreaId);
            cmd.Parameters.AddWithValue("@PolyhouseId", (object?)s.PolyhouseId ?? DBNull.Value);   // 2026-10-14: none for a Mother Plant Area transplant
            cmd.Parameters.AddWithValue("@BatchNo", s.BatchNo);
            cmd.Parameters.AddWithValue("@SeedSourceId", (object?)s.SeedSourceId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Cavity", s.CavityType);
            cmd.Parameters.AddWithValue("@Trays", s.Trays);
            cmd.Parameters.AddWithValue("@Qty", s.Quantity);
            cmd.Parameters.AddWithValue("@SowingDate", s.OriginalSowingDate.Date);
            cmd.Parameters.AddWithValue("@Days", s.ReadyDays);
            cmd.Parameters.Add("@ReadyDate", System.Data.SqlDbType.Date).Value = s.ReadyDate.Date;
            cmd.Parameters.AddWithValue("@Remarks", s.Remarks.Length > 500 ? s.Remarks[..500] : s.Remarks);
            cmd.Parameters.AddWithValue("@CreatedBy", (object?)s.CreatedBy ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@CreatedById", (object?)s.CreatedById ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@MaterialId", (object?)s.MaterialTypeId ?? DBNull.Value);
            return ((int)(await cmd.ExecuteScalarAsync())!, code);
        }
    }
}
