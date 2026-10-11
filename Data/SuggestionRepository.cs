using System.Text;
using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Data
{
    // dbo.SuggestionPosts / PostTypes / PostPhotos / Comments / PostEvents
    // (Database/Migrations/2026-10-07_SuggestionsProblems.sql).
    //
    // Lifetime is evaluated HERE with the database clock (SYSUTCDATETIME()) -- the "expired"
    // flag of every row read and every lifetime filter -- never with a time from the browser.
    // Every administrative step is ONE conditional UPDATE (WHERE ... the expected state) followed
    // by its SuggestionPostEvents row in one transaction, so two administrators can never both
    // perform it (first wins; the other gets false = "changed by someone else").
    // WHO may call a method is decided by the page (SuggestionAuthorization) BEFORE calling here.
    public class SuggestionRepository
    {
        private readonly DatabaseHelper _dbHelper;

        public SuggestionRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        public sealed record NewPost(string TypeCode, string Title, string Description, int AreaId, int? PolyhouseId);

        private const string PostSelect = @"
SELECT p.Id, p.PostNo, p.TypeCode, t.Name AS TypeName, p.Title, p.Description, p.AreaId, a.Name AS AreaName,
       p.PolyhouseId, ph.Name AS PolyhouseName, p.PostedById, pu.Name AS PostedByName, p.CreatedAt, p.ExpiresAt,
       CAST(CASE WHEN p.IsPermanent = 0 AND p.ExpiresAt <= SYSUTCDATETIME() THEN 1 ELSE 0 END AS BIT) AS IsExpired,
       p.Status, su.Name AS StatusChangedByName, p.StatusChangedAt, p.IsPermanent, mu.Name AS MadePermanentByName, p.MadePermanentAt,
       p.IsDeleted, du.Name AS DeletedByName, p.DeletedAt, p.DeleteReason,
       (SELECT TOP 1 f.Id FROM dbo.SuggestionPostPhotos f WHERE f.PostId = p.Id ORDER BY f.Id) AS PhotoId,
       (SELECT COUNT(*) FROM dbo.SuggestionComments c WHERE c.PostId = p.Id AND c.IsDeleted = 0) AS CommentCount
FROM dbo.SuggestionPosts p
JOIN dbo.SuggestionPostTypes t ON t.Code = p.TypeCode
JOIN dbo.Area a ON a.Id = p.AreaId
LEFT JOIN dbo.Polyhouses ph ON ph.Id = p.PolyhouseId
LEFT JOIN dbo.IMSUsers pu ON pu.Id = p.PostedById
LEFT JOIN dbo.IMSUsers su ON su.Id = p.StatusChangedById
LEFT JOIN dbo.IMSUsers mu ON mu.Id = p.MadePermanentById
LEFT JOIN dbo.IMSUsers du ON du.Id = p.DeletedById";

        // ---- master data -------------------------------------------------------------------------

        public async Task<List<SuggestionPostType>> GetTypesAsync()
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand("SELECT Code, Name FROM dbo.SuggestionPostTypes WHERE IsActive = 1 ORDER BY SortOrder, Name", conn);
            var list = new List<SuggestionPostType>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new SuggestionPostType { Code = r.GetString(0), Name = r.GetString(1) });
            return list;
        }

        // ---- posts -------------------------------------------------------------------------------

        public async Task<int> CreateAsync(NewPost post, int postedById, MaintenancePhotoStore.SavedPhoto? photo)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                using var cmd = new SqlCommand(@"
INSERT INTO dbo.SuggestionPosts (TypeCode, Title, Description, AreaId, PolyhouseId, PostedById)
VALUES (@TypeCode, @Title, @Description, @AreaId, @PolyhouseId, @PostedById);
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn, tx);   // not OUTPUT: the table has a trigger
                cmd.Parameters.AddWithValue("@TypeCode", post.TypeCode);
                cmd.Parameters.AddWithValue("@Title", post.Title.Trim());
                cmd.Parameters.AddWithValue("@Description", post.Description.Trim());
                cmd.Parameters.AddWithValue("@AreaId", post.AreaId);
                cmd.Parameters.AddWithValue("@PolyhouseId", (object?)post.PolyhouseId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@PostedById", postedById);
                var id = (int)(await cmd.ExecuteScalarAsync())!;
                if (photo != null)
                {
                    using var p = new SqlCommand(@"
INSERT INTO dbo.SuggestionPostPhotos (PostId, StoredFileName, OriginalFileName, ContentType, SizeBytes, UploadedById)
VALUES (@PostId, @Stored, @Original, @ContentType, @Size, @User)", conn, tx);
                    p.Parameters.AddWithValue("@PostId", id);
                    p.Parameters.AddWithValue("@Stored", photo.StoredFileName);
                    p.Parameters.AddWithValue("@Original", photo.OriginalFileName);
                    p.Parameters.AddWithValue("@ContentType", photo.ContentType);
                    p.Parameters.AddWithValue("@Size", photo.SizeBytes);
                    p.Parameters.AddWithValue("@User", postedById);
                    await p.ExecuteNonQueryAsync();
                }
                await AddEventAsync(conn, tx, id, "Created", null, SuggestionRules.Open, null, post.TypeCode + (photo != null ? ", with photo" : ""), postedById);
                tx.Commit();
                return id;
            }
            catch
            {
                if (tx.Connection != null) tx.Rollback();
                throw;
            }
        }

        public async Task<SuggestionPost?> GetPostAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(PostSelect + " WHERE p.Id = @Id", conn);
            cmd.Parameters.AddWithValue("@Id", id);
            using var r = await cmd.ExecuteReaderAsync();
            return await r.ReadAsync() ? ReadPost(r) : null;
        }

        // One page of posts, newest first. areaScope null = every Area.
        public async Task<(List<SuggestionPost> Posts, int Total)> SearchAsync(SuggestionFilter f, IReadOnlyCollection<int>? areaScope)
        {
            var where = new StringBuilder(" WHERE 1 = 1");
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand { Connection = conn };
            AppendFilter(where, cmd, f, areaScope);
            var page = Math.Max(1, f.Page);
            cmd.CommandText = $@"
SELECT COUNT(*) FROM dbo.SuggestionPosts p {where};
{PostSelect} {where}
ORDER BY p.CreatedAt DESC, p.Id DESC
OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;";
            cmd.Parameters.AddWithValue("@Skip", (page - 1) * SuggestionRules.PageSize);
            cmd.Parameters.AddWithValue("@Take", SuggestionRules.PageSize);
            var list = new List<SuggestionPost>();
            using var r = await cmd.ExecuteReaderAsync();
            await r.ReadAsync();
            var total = r.GetInt32(0);
            await r.NextResultAsync();
            while (await r.ReadAsync())
                list.Add(ReadPost(r));
            return (list, total);
        }

        // Counts within a scope. Only an administrator's counts include expired / deleted posts
        // (the page shows them only to administrators).
        public async Task<SuggestionCounts> GetCountsAsync(IReadOnlyCollection<int>? areaScope)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand { Connection = conn };
            var scope = AreaScopeSql(cmd, areaScope, "p.AreaId");
            cmd.CommandText = $@"
SELECT
    SUM(CASE WHEN p.IsDeleted = 0 AND (p.IsPermanent = 1 OR p.ExpiresAt > SYSUTCDATETIME()) THEN 1 ELSE 0 END),
    SUM(CASE WHEN p.IsDeleted = 0 AND (p.IsPermanent = 1 OR p.ExpiresAt > SYSUTCDATETIME()) AND p.TypeCode = N'Problem' THEN 1 ELSE 0 END),
    SUM(CASE WHEN p.IsDeleted = 0 AND (p.IsPermanent = 1 OR p.ExpiresAt > SYSUTCDATETIME()) AND p.TypeCode = N'Suggestion' THEN 1 ELSE 0 END),
    SUM(CASE WHEN p.IsDeleted = 0 AND p.IsPermanent = 1 THEN 1 ELSE 0 END),
    SUM(CASE WHEN p.IsDeleted = 0 AND p.IsPermanent = 0 AND p.ExpiresAt <= SYSUTCDATETIME() THEN 1 ELSE 0 END),
    SUM(CASE WHEN p.IsDeleted = 1 THEN 1 ELSE 0 END),
    (SELECT COUNT(*) FROM dbo.SuggestionComments c JOIN dbo.SuggestionPosts cp ON cp.Id = c.PostId
     WHERE c.IsDeleted = 0 AND cp.IsDeleted = 0 AND (cp.IsPermanent = 1 OR cp.ExpiresAt > SYSUTCDATETIME()) {scope.Replace("p.AreaId", "cp.AreaId")})
FROM dbo.SuggestionPosts p WHERE 1 = 1 {scope}";
            using var r = await cmd.ExecuteReaderAsync();
            await r.ReadAsync();
            int At(int i) => r.IsDBNull(i) ? 0 : r.GetInt32(i);
            return new SuggestionCounts
            {
                Active = At(0), ActiveProblems = At(1), ActiveSuggestions = At(2), Permanent = At(3),
                Expired = At(4), Deleted = At(5), Comments = At(6)
            };
        }

        // "Posted by" filter choices for the administrator: people who have posted.
        public async Task<List<SuggestionPoster>> GetPostersAsync()
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
SELECT u.Id, u.Name FROM dbo.IMSUsers u WHERE EXISTS (SELECT 1 FROM dbo.SuggestionPosts p WHERE p.PostedById = u.Id) ORDER BY u.Name", conn);
            var list = new List<SuggestionPoster>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new SuggestionPoster { Id = r.GetInt32(0), Name = r.IsDBNull(1) ? $"User {r.GetInt32(0)}" : r.GetString(1) });
            return list;
        }

        public async Task<SuggestionPhoto?> GetPhotoAsync(int postId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
SELECT TOP 1 Id, PostId, StoredFileName, OriginalFileName, ContentType, SizeBytes FROM dbo.SuggestionPostPhotos WHERE PostId = @PostId ORDER BY Id", conn);
            cmd.Parameters.AddWithValue("@PostId", postId);
            using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return null;
            return new SuggestionPhoto
            {
                Id = r.GetInt32(0), PostId = r.GetInt32(1), StoredFileName = r.GetString(2), OriginalFileName = r.GetString(3),
                ContentType = r.GetString(4), SizeBytes = r.GetInt32(5)
            };
        }

        // ---- administrative steps (true = done; false = not in the expected state any more) ------

        public Task<bool> MakePermanentAsync(int id, int actorId)
            => StepAsync(id, actorId, "MadePermanent", null, null, null,
                "IsPermanent = 1, MadePermanentById = @Actor, MadePermanentAt = SYSUTCDATETIME()",
                " AND IsPermanent = 0 AND IsDeleted = 0");

        public Task<bool> ChangeStatusAsync(int id, string expectedStatus, string newStatus, string? note, int actorId)
            => StepAsync(id, actorId, "StatusChanged", expectedStatus, newStatus, SuggestionRules.Trimmed(note),
                "Status = @To, StatusChangedById = @Actor, StatusChangedAt = SYSUTCDATETIME()",
                " AND IsDeleted = 0 AND Status = @From AND @From <> @To");

        public Task<bool> DeleteAsync(int id, string reason, int actorId)
            => StepAsync(id, actorId, "Deleted", null, null, reason.Trim(),
                "IsDeleted = 1, DeletedById = @Actor, DeletedAt = SYSUTCDATETIME(), DeleteReason = @Note",
                " AND IsDeleted = 0");

        private async Task<bool> StepAsync(int id, int actorId, string eventType, string? from, string? to, string? note, string set, string condition)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                using var cmd = new SqlCommand($"UPDATE dbo.SuggestionPosts SET {set} WHERE Id = @Id{condition}", conn, tx);
                cmd.Parameters.AddWithValue("@Id", id);
                cmd.Parameters.AddWithValue("@Actor", actorId);
                cmd.Parameters.AddWithValue("@From", (object?)from ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@To", (object?)to ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Note", (object?)note ?? DBNull.Value);
                if (await cmd.ExecuteNonQueryAsync() != 1)
                {
                    tx.Rollback();
                    return false;
                }
                await AddEventAsync(conn, tx, id, eventType, from, to, null, note, actorId);
                tx.Commit();
                return true;
            }
            catch
            {
                if (tx.Connection != null) tx.Rollback();
                throw;
            }
        }

        // ---- comments ----------------------------------------------------------------------------

        // Comments of one post with their replies, oldest first (one query; replies attached in memory).
        public async Task<List<SuggestionComment>> GetCommentsAsync(int postId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
SELECT c.Id, c.PostId, c.ParentCommentId, c.AuthorId, au.Name, c.CommentText, c.CreatedAt, c.UpdatedAt, c.IsDeleted, du.Name, c.DeletedAt
FROM dbo.SuggestionComments c
LEFT JOIN dbo.IMSUsers au ON au.Id = c.AuthorId
LEFT JOIN dbo.IMSUsers du ON du.Id = c.DeletedById
WHERE c.PostId = @PostId
ORDER BY c.CreatedAt, c.Id", conn);
            cmd.Parameters.AddWithValue("@PostId", postId);
            var all = new List<SuggestionComment>();
            using (var r = await cmd.ExecuteReaderAsync())
            {
                while (await r.ReadAsync())
                    all.Add(ReadComment(r));
            }
            var byId = all.Where(c => c.ParentCommentId == null).ToDictionary(c => c.Id);
            foreach (var reply in all.Where(c => c.ParentCommentId != null))
                if (byId.TryGetValue(reply.ParentCommentId!.Value, out var parent))
                    parent.Replies.Add(reply);
            return byId.Values.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).ToList();
        }

        public async Task<SuggestionComment?> GetCommentAsync(int commentId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
SELECT c.Id, c.PostId, c.ParentCommentId, c.AuthorId, au.Name, c.CommentText, c.CreatedAt, c.UpdatedAt, c.IsDeleted, du.Name, c.DeletedAt
FROM dbo.SuggestionComments c
LEFT JOIN dbo.IMSUsers au ON au.Id = c.AuthorId
LEFT JOIN dbo.IMSUsers du ON du.Id = c.DeletedById
WHERE c.Id = @Id", conn);
            cmd.Parameters.AddWithValue("@Id", commentId);
            using var r = await cmd.ExecuteReaderAsync();
            return await r.ReadAsync() ? ReadComment(r) : null;
        }

        // Adds a comment / reply to a NOT deleted post. The first comment by someone other than the
        // poster moves an Open post to In Discussion (recorded in the history). Returns the new id,
        // or null when the post is gone / deleted.
        public async Task<int?> AddCommentAsync(int postId, int? parentCommentId, string text, int authorId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                using var cmd = new SqlCommand(@"
DECLARE @New TABLE (Id INT);   -- OUTPUT ... INTO: the table has a trigger
INSERT INTO dbo.SuggestionComments (PostId, ParentCommentId, AuthorId, CommentText)
OUTPUT inserted.Id INTO @New
SELECT p.Id, @Parent, @Author, @Text FROM dbo.SuggestionPosts p WITH (UPDLOCK) WHERE p.Id = @PostId AND p.IsDeleted = 0;
SELECT TOP 1 Id FROM @New;", conn, tx);
                cmd.Parameters.AddWithValue("@PostId", postId);
                cmd.Parameters.AddWithValue("@Parent", (object?)parentCommentId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Author", authorId);
                cmd.Parameters.AddWithValue("@Text", text.Trim());
                var result = await cmd.ExecuteScalarAsync();
                if (result is not int id)
                {
                    tx.Rollback();
                    return null;
                }
                using var status = new SqlCommand(@"
UPDATE dbo.SuggestionPosts SET Status = N'InDiscussion', StatusChangedById = @Author, StatusChangedAt = SYSUTCDATETIME()
WHERE Id = @PostId AND Status = N'Open' AND PostedById <> @Author AND IsDeleted = 0", conn, tx);
                status.Parameters.AddWithValue("@PostId", postId);
                status.Parameters.AddWithValue("@Author", authorId);
                if (await status.ExecuteNonQueryAsync() == 1)
                    await AddEventAsync(conn, tx, postId, "StatusChanged", SuggestionRules.Open, SuggestionRules.InDiscussion, id, "First comment from another user", authorId);
                tx.Commit();
                return id;
            }
            catch
            {
                if (tx.Connection != null) tx.Rollback();
                throw;
            }
        }

        // The author's own edit inside the edit window (checked with the database clock). The
        // previous text is kept in the history.
        public async Task<bool> EditCommentAsync(int commentId, int authorId, string newText)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                using var read = new SqlCommand(@"
SELECT PostId, CommentText FROM dbo.SuggestionComments WITH (UPDLOCK, ROWLOCK)
WHERE Id = @Id AND AuthorId = @Author AND IsDeleted = 0 AND CreatedAt > DATEADD(MINUTE, -@Window, SYSUTCDATETIME())", conn, tx);
                read.Parameters.AddWithValue("@Id", commentId);
                read.Parameters.AddWithValue("@Author", authorId);
                read.Parameters.AddWithValue("@Window", SuggestionRules.CommentEditWindowMinutes);
                int postId;
                string oldText;
                using (var r = await read.ExecuteReaderAsync())
                {
                    if (!await r.ReadAsync())
                    {
                        r.Close();
                        tx.Rollback();
                        return false;
                    }
                    postId = r.GetInt32(0);
                    oldText = r.GetString(1);
                }
                using var cmd = new SqlCommand("UPDATE dbo.SuggestionComments SET CommentText = @Text, UpdatedAt = SYSUTCDATETIME() WHERE Id = @Id", conn, tx);
                cmd.Parameters.AddWithValue("@Id", commentId);
                cmd.Parameters.AddWithValue("@Text", newText.Trim());
                await cmd.ExecuteNonQueryAsync();
                await AddEventAsync(conn, tx, postId, "CommentEdited", null, null, commentId, "Previous text: " + oldText, authorId);
                tx.Commit();
                return true;
            }
            catch
            {
                if (tx.Connection != null) tx.Rollback();
                throw;
            }
        }

        // Soft delete (the text stays in the database for audit). WHO may delete is the page's check.
        public async Task<bool> DeleteCommentAsync(int commentId, int actorId, bool moderated)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                using var cmd = new SqlCommand(@"
DECLARE @Done TABLE (PostId INT);
UPDATE dbo.SuggestionComments SET IsDeleted = 1, DeletedById = @Actor, DeletedAt = SYSUTCDATETIME()
OUTPUT inserted.PostId INTO @Done
WHERE Id = @Id AND IsDeleted = 0;
SELECT TOP 1 PostId FROM @Done;", conn, tx);
                cmd.Parameters.AddWithValue("@Id", commentId);
                cmd.Parameters.AddWithValue("@Actor", actorId);
                if (await cmd.ExecuteScalarAsync() is not int postId)
                {
                    tx.Rollback();
                    return false;
                }
                await AddEventAsync(conn, tx, postId, "CommentDeleted", null, null, commentId, moderated ? "Removed by an administrator" : "Deleted by its author", actorId);
                tx.Commit();
                return true;
            }
            catch
            {
                if (tx.Connection != null) tx.Rollback();
                throw;
            }
        }

        public async Task<List<SuggestionPostEvent>> GetEventsAsync(int postId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
SELECT e.EventType, e.FromStatus, e.ToStatus, e.CommentId, e.Note, u.Name, e.CreatedAt
FROM dbo.SuggestionPostEvents e LEFT JOIN dbo.IMSUsers u ON u.Id = e.ActorId
WHERE e.PostId = @PostId ORDER BY e.CreatedAt, e.Id", conn);
            cmd.Parameters.AddWithValue("@PostId", postId);
            var list = new List<SuggestionPostEvent>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new SuggestionPostEvent
                {
                    EventType = r.GetString(0),
                    FromStatus = r.IsDBNull(1) ? null : r.GetString(1),
                    ToStatus = r.IsDBNull(2) ? null : r.GetString(2),
                    CommentId = r.IsDBNull(3) ? null : r.GetInt32(3),
                    Note = r.IsDBNull(4) ? null : r.GetString(4),
                    ActorName = r.IsDBNull(5) ? null : r.GetString(5),
                    CreatedAt = r.GetDateTime(6)
                });
            return list;
        }

        // ---- helpers -----------------------------------------------------------------------------

        private static async Task AddEventAsync(SqlConnection conn, SqlTransaction tx, int postId, string type, string? from, string? to, int? commentId, string? note, int actorId)
        {
            using var cmd = new SqlCommand(@"
INSERT INTO dbo.SuggestionPostEvents (PostId, EventType, FromStatus, ToStatus, CommentId, Note, ActorId)
VALUES (@PostId, @Type, @From, @To, @CommentId, @Note, @Actor)", conn, tx);
            cmd.Parameters.AddWithValue("@PostId", postId);
            cmd.Parameters.AddWithValue("@Type", type);
            cmd.Parameters.AddWithValue("@From", (object?)from ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@To", (object?)to ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@CommentId", (object?)commentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Note", note == null ? DBNull.Value : note.Length > 2000 ? note[..2000] : note);
            cmd.Parameters.AddWithValue("@Actor", actorId);
            await cmd.ExecuteNonQueryAsync();
        }

        // " AND p.AreaId IN (@A0, @A1 ...)" -- parameterised; an empty scope matches nothing.
        private static string AreaScopeSql(SqlCommand cmd, IReadOnlyCollection<int>? areaScope, string column)
        {
            if (areaScope == null) return "";
            if (areaScope.Count == 0) return " AND 1 = 0";
            var names = new List<string>();
            var i = 0;
            foreach (var id in areaScope.Distinct())
            {
                var name = $"@A{i++}";
                names.Add(name);
                cmd.Parameters.AddWithValue(name, id);
            }
            return $" AND {column} IN ({string.Join(", ", names)})";
        }

        private static void AppendFilter(StringBuilder where, SqlCommand cmd, SuggestionFilter f, IReadOnlyCollection<int>? areaScope)
        {
            where.Append(AreaScopeSql(cmd, areaScope, "p.AreaId"));
            where.Append(f.Lifetime switch
            {
                SuggestionRules.LifetimeAll => "",
                SuggestionRules.LifetimeExpired => " AND p.IsDeleted = 0 AND p.IsPermanent = 0 AND p.ExpiresAt <= SYSUTCDATETIME()",
                SuggestionRules.LifetimePermanent => " AND p.IsDeleted = 0 AND p.IsPermanent = 1",
                SuggestionRules.LifetimeDeleted => " AND p.IsDeleted = 1",
                _ => " AND p.IsDeleted = 0 AND (p.IsPermanent = 1 OR p.ExpiresAt > SYSUTCDATETIME())"   // Active
            });
            void Add(string sql, string name, object? value)
            {
                if (value == null) return;
                where.Append(sql);
                cmd.Parameters.AddWithValue(name, value);
            }
            Add(" AND p.TypeCode = @TypeCode", "@TypeCode", string.IsNullOrWhiteSpace(f.TypeCode) ? null : f.TypeCode);
            Add(" AND p.Status = @Status", "@Status", string.IsNullOrWhiteSpace(f.Status) ? null : f.Status);
            Add(" AND p.AreaId = @AreaId", "@AreaId", f.AreaId);
            Add(" AND p.PolyhouseId = @PolyhouseId", "@PolyhouseId", f.PolyhouseId);
            Add(" AND p.PostedById = @PostedById", "@PostedById", f.PostedById);
            Add(" AND p.CreatedAt >= @From", "@From", f.FromUtc);
            Add(" AND p.CreatedAt < @To", "@To", f.ToUtcExclusive);
        }

        private static SuggestionPost ReadPost(SqlDataReader r)
        {
            string? S(string col) => r.IsDBNull(r.GetOrdinal(col)) ? null : r.GetString(r.GetOrdinal(col));
            DateTime? D(string col) => r.IsDBNull(r.GetOrdinal(col)) ? null : r.GetDateTime(r.GetOrdinal(col));
            int? I(string col) => r.IsDBNull(r.GetOrdinal(col)) ? null : r.GetInt32(r.GetOrdinal(col));
            return new SuggestionPost
            {
                Id = r.GetInt32(r.GetOrdinal("Id")),
                PostNo = S("PostNo")!,
                TypeCode = S("TypeCode")!,
                TypeName = S("TypeName")!,
                Title = S("Title")!,
                Description = S("Description")!,
                AreaId = r.GetInt32(r.GetOrdinal("AreaId")),
                AreaName = S("AreaName")!,
                PolyhouseId = I("PolyhouseId"),
                PolyhouseName = S("PolyhouseName"),
                PostedById = r.GetInt32(r.GetOrdinal("PostedById")),
                PostedByName = S("PostedByName"),
                CreatedAt = D("CreatedAt")!.Value,
                ExpiresAt = D("ExpiresAt")!.Value,
                IsExpired = r.GetBoolean(r.GetOrdinal("IsExpired")),
                Status = S("Status")!,
                StatusChangedByName = S("StatusChangedByName"),
                StatusChangedAt = D("StatusChangedAt"),
                IsPermanent = r.GetBoolean(r.GetOrdinal("IsPermanent")),
                MadePermanentByName = S("MadePermanentByName"),
                MadePermanentAt = D("MadePermanentAt"),
                IsDeleted = r.GetBoolean(r.GetOrdinal("IsDeleted")),
                DeletedByName = S("DeletedByName"),
                DeletedAt = D("DeletedAt"),
                DeleteReason = S("DeleteReason"),
                PhotoId = I("PhotoId"),
                CommentCount = r.GetInt32(r.GetOrdinal("CommentCount"))
            };
        }

        private static SuggestionComment ReadComment(SqlDataReader r) => new()
        {
            Id = r.GetInt32(0),
            PostId = r.GetInt32(1),
            ParentCommentId = r.IsDBNull(2) ? null : r.GetInt32(2),
            AuthorId = r.GetInt32(3),
            AuthorName = r.IsDBNull(4) ? null : r.GetString(4),
            CommentText = r.GetString(5),
            CreatedAt = r.GetDateTime(6),
            UpdatedAt = r.IsDBNull(7) ? null : r.GetDateTime(7),
            IsDeleted = r.GetBoolean(8),
            DeletedByName = r.IsDBNull(9) ? null : r.GetString(9),
            DeletedAt = r.IsDBNull(10) ? null : r.GetDateTime(10)
        };
    }
}
