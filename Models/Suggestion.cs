using PlantStockManager.Services;

namespace PlantStockManager.Models
{
    // dbo.SuggestionPostTypes
    public class SuggestionPostType
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    // dbo.SuggestionPosts (+ names, photo and counts for display). IsExpired is evaluated by the
    // DATABASE clock when the row is read (SuggestionRepository), never by the browser.
    public class SuggestionPost
    {
        public int Id { get; set; }
        public string PostNo { get; set; } = string.Empty;
        public string TypeCode { get; set; } = string.Empty;
        public string TypeName { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public int AreaId { get; set; }
        public string AreaName { get; set; } = string.Empty;
        public int? PolyhouseId { get; set; }
        public string? PolyhouseName { get; set; }
        public int PostedById { get; set; }
        public string? PostedByName { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public bool IsExpired { get; set; }
        public string Status { get; set; } = SuggestionRules.Open;
        public string? StatusChangedByName { get; set; }
        public DateTime? StatusChangedAt { get; set; }
        public bool IsPermanent { get; set; }
        public string? MadePermanentByName { get; set; }
        public DateTime? MadePermanentAt { get; set; }
        public bool IsDeleted { get; set; }
        public string? DeletedByName { get; set; }
        public DateTime? DeletedAt { get; set; }
        public string? DeleteReason { get; set; }
        public int? PhotoId { get; set; }
        public int CommentCount { get; set; }

        public bool IsActive => !IsDeleted && !IsExpired;
        public string Location => PolyhouseName == null ? AreaName : $"{AreaName} / {PolyhouseName}";
    }

    public class SuggestionPhoto
    {
        public int Id { get; set; }
        public int PostId { get; set; }
        public string StoredFileName { get; set; } = string.Empty;
        public string OriginalFileName { get; set; } = string.Empty;
        public string ContentType { get; set; } = string.Empty;
        public int SizeBytes { get; set; }
    }

    // dbo.SuggestionComments. A top-level comment carries its replies (one level).
    public class SuggestionComment
    {
        public int Id { get; set; }
        public int PostId { get; set; }
        public int? ParentCommentId { get; set; }
        public int AuthorId { get; set; }
        public string? AuthorName { get; set; }
        public string CommentText { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public bool IsDeleted { get; set; }
        public string? DeletedByName { get; set; }
        public DateTime? DeletedAt { get; set; }
        public List<SuggestionComment> Replies { get; set; } = new();
    }

    public class SuggestionPostEvent
    {
        public string EventType { get; set; } = string.Empty;
        public string? FromStatus { get; set; }
        public string? ToStatus { get; set; }
        public int? CommentId { get; set; }
        public string? Note { get; set; }
        public string? ActorName { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    // List filters. AreaScope = null means every Area (administrator); otherwise only these Areas.
    public class SuggestionFilter
    {
        public string Lifetime { get; set; } = SuggestionRules.LifetimeActive;
        public string? TypeCode { get; set; }
        public string? Status { get; set; }
        public int? AreaId { get; set; }
        public int? PolyhouseId { get; set; }
        public int? PostedById { get; set; }
        public DateTime? FromUtc { get; set; }
        public DateTime? ToUtcExclusive { get; set; }
        public int Page { get; set; } = 1;
    }

    public class SuggestionCounts
    {
        public int Active { get; set; }
        public int ActiveProblems { get; set; }
        public int ActiveSuggestions { get; set; }
        public int Permanent { get; set; }
        public int Expired { get; set; }
        public int Deleted { get; set; }
        public int Comments { get; set; }
    }

    public class SuggestionPoster
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }
}
