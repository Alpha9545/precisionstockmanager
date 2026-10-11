using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Suggestions
{
    // One post with its photo, comments and replies (page rule: any Suggestion.* permission).
    // EVERY handler re-loads the post (and the comment) from the database and asks
    // SuggestionAuthorization -- a posted PostId / CommentId / ParentCommentId can never reach a
    // post the user may not see (IDOR). Administrator actions (permanent, status, delete) are
    // refused on the server for everyone else, whatever the page showed.
    public class DetailsModel : PageModel
    {
        private readonly SuggestionRepository _repo;
        private readonly SuggestionAuthorization _auth;
        private readonly SuggestionPhotoStore _photos;
        private readonly SubmissionTokenGuard _tokens;

        public DetailsModel(SuggestionRepository repo, SuggestionAuthorization auth, SuggestionPhotoStore photos, SubmissionTokenGuard tokens)
        {
            _repo = repo;
            _auth = auth;
            _photos = photos;
            _tokens = tokens;
        }

        [BindProperty(SupportsGet = true)] public int Id { get; set; }
        [BindProperty] public string? SubmitToken { get; set; }
        [BindProperty] public string? CommentText { get; set; }
        [BindProperty] public int? ParentCommentId { get; set; }
        [BindProperty] public int? CommentId { get; set; }
        [BindProperty] public string? NewStatus { get; set; }
        [BindProperty] public string? StatusNote { get; set; }
        [BindProperty] public string? DeleteReason { get; set; }

        public SuggestionPost Post { get; set; } = null!;
        public List<SuggestionComment> Comments { get; set; } = new();
        public List<SuggestionPostEvent> Events { get; set; } = new();
        public bool IsManager { get; set; }
        public bool CanComment { get; set; }
        public int? Me { get; set; }
        public DateTime NowUtc { get; } = DateTime.UtcNow;

        private string TokenScope => $"Suggestion:{Id}";

        public bool CanEdit(SuggestionComment c) => _auth.CanEditComment(User, Post, c, NowUtc);
        public bool CanDelete(SuggestionComment c) => _auth.CanDeleteComment(User, Post, c);

        public async Task<IActionResult> OnGetAsync()
        {
            var denied = await LoadAsync();
            if (denied != null) return denied;
            SubmitToken = SubmissionTokenGuard.NewToken();
            return Page();
        }

        // The post's photo, only to users who may see the post. The file is found by the stored GUID
        // name (never a path from the request) and served with its checked content type.
        public async Task<IActionResult> OnGetPhotoAsync()
        {
            var post = await _repo.GetPostAsync(Id);
            if (post == null) return NotFound();
            if (!_auth.CanView(User, post)) return Forbid();
            var photo = await _repo.GetPhotoAsync(Id);
            if (photo == null) return NotFound();
            var path = _photos.PathFor(photo.StoredFileName);
            if (path == null || !System.IO.File.Exists(path) || !MaintenancePhotoStore.AllowedContentTypes.Contains(photo.ContentType))
                return NotFound();
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            // no-store: a browser shared by several employees must not show one user's cached photo
            // to the next (the check above runs on every request).
            Response.Headers["Cache-Control"] = "no-store";
            Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
            return PhysicalFile(path, photo.ContentType);
        }

        // ---- comments --------------------------------------------------------------------------

        public async Task<IActionResult> OnPostCommentAsync()
        {
            var denied = await LoadAsync();
            if (denied != null) return denied;
            if (!CanComment)
                return Refuse(Post.IsDeleted ? "This post was deleted." : "You cannot comment on this post.");
            var error = SuggestionRules.Required(CommentText, "Comment", SuggestionRules.MaxCommentLength);
            if (error != null) ModelState.AddModelError(nameof(CommentText), error);

            int? parentId = null;
            if (ParentCommentId.HasValue)
            {
                // The comment replied to must be a comment of THIS post (a tampered id is refused).
                var parent = await _repo.GetCommentAsync(ParentCommentId.Value);
                if (parent == null || parent.PostId != Post.Id)
                    ModelState.AddModelError(nameof(ParentCommentId), "The comment you are replying to was not found on this post.");
                else if (parent.IsDeleted)
                    ModelState.AddModelError(nameof(ParentCommentId), "The comment you are replying to was deleted.");
                else
                    parentId = parent.ParentCommentId ?? parent.Id;   // replies stay one level deep
            }
            if (!ModelState.IsValid)
                return PageWithNewToken();
            if (!_tokens.TryConsume(TokenScope, SubmitToken))
                return Refuse(SuggestionRules.AlreadySubmittedMessage);

            var id = await _repo.AddCommentAsync(Post.Id, parentId, CommentText!, Me!.Value);
            if (id == null)
                return Refuse(SuggestionRules.ChangedMessage);
            TempData["Success"] = parentId == null ? "Comment added." : "Reply added.";
            return RedirectToPage("/Suggestions/Details", null, new { id = Post.Id }, $"comment-{id}");
        }

        public async Task<IActionResult> OnPostEditCommentAsync()
        {
            var (denied, comment) = await LoadCommentAsync();
            if (denied != null) return denied;
            if (comment!.AuthorId != Me) return Forbid();
            if (!CanEdit(comment))
                return Refuse(comment.IsDeleted ? "This comment was deleted." : $"A comment can only be edited within {SuggestionRules.CommentEditWindowMinutes} minutes of posting.");
            var error = SuggestionRules.Required(CommentText, "Comment", SuggestionRules.MaxCommentLength);
            if (error != null)
            {
                ModelState.AddModelError(nameof(CommentText), error);
                return PageWithNewToken();
            }
            if (!_tokens.TryConsume(TokenScope, SubmitToken))
                return Refuse(SuggestionRules.AlreadySubmittedMessage);
            if (!await _repo.EditCommentAsync(comment.Id, Me!.Value, CommentText!))
                return Refuse($"A comment can only be edited within {SuggestionRules.CommentEditWindowMinutes} minutes of posting.");
            TempData["Success"] = "Comment updated.";
            return RedirectToPage("/Suggestions/Details", null, new { id = Post.Id }, $"comment-{comment.Id}");
        }

        public async Task<IActionResult> OnPostDeleteCommentAsync()
        {
            var (denied, comment) = await LoadCommentAsync();
            if (denied != null) return denied;
            if (comment!.IsDeleted)
                return Refuse("This comment was already deleted.");
            if (!CanDelete(comment)) return Forbid();
            if (!_tokens.TryConsume(TokenScope, SubmitToken))
                return Refuse(SuggestionRules.AlreadySubmittedMessage);
            if (!await _repo.DeleteCommentAsync(comment.Id, Me!.Value, moderated: comment.AuthorId != Me))
                return Refuse("This comment was already deleted.");
            TempData["Success"] = "Comment deleted.";
            return RedirectToPage("/Suggestions/Details", new { id = Post.Id });
        }

        // ---- administrator ------------------------------------------------------------------------

        public async Task<IActionResult> OnPostMakePermanentAsync()
        {
            var denied = await LoadAsync();
            if (denied != null) return denied;
            if (!IsManager) return Forbid();
            if (Post.IsDeleted) return Refuse("This post was deleted.");
            if (Post.IsPermanent) return Refuse("This post is already permanent.");
            if (!_tokens.TryConsume(TokenScope, SubmitToken))
                return Refuse(SuggestionRules.AlreadySubmittedMessage);
            if (!await _repo.MakePermanentAsync(Post.Id, Me!.Value))
                return Refuse("This post is already permanent.");
            TempData["Success"] = "Post made permanent -- it no longer expires.";
            return RedirectToPage("/Suggestions/Details", new { id = Post.Id });
        }

        public async Task<IActionResult> OnPostStatusAsync()
        {
            var denied = await LoadAsync();
            if (denied != null) return denied;
            if (!IsManager) return Forbid();
            if (Post.IsDeleted) return Refuse("This post was deleted.");
            if (!SuggestionRules.Statuses.Contains(NewStatus))
                ModelState.AddModelError(nameof(NewStatus), "Choose a status.");
            else if (NewStatus == Post.Status)
                ModelState.AddModelError(nameof(NewStatus), $"The post is already {SuggestionRules.StatusLabel(Post.Status)}.");
            if (StatusNote != null && StatusNote.Trim().Length > SuggestionRules.MaxReasonLength)
                ModelState.AddModelError(nameof(StatusNote), $"Note cannot be longer than {SuggestionRules.MaxReasonLength} characters.");
            if (!ModelState.IsValid)
                return PageWithNewToken();
            if (!_tokens.TryConsume(TokenScope, SubmitToken))
                return Refuse(SuggestionRules.AlreadySubmittedMessage);
            if (!await _repo.ChangeStatusAsync(Post.Id, Post.Status, NewStatus!, StatusNote, Me!.Value))
                return Refuse(SuggestionRules.ChangedMessage);
            TempData["Success"] = $"Status changed to {SuggestionRules.StatusLabel(NewStatus)}.";
            return RedirectToPage("/Suggestions/Details", new { id = Post.Id });
        }

        public async Task<IActionResult> OnPostDeleteAsync()
        {
            var denied = await LoadAsync();
            if (denied != null) return denied;
            if (!IsManager) return Forbid();
            if (Post.IsDeleted) return Refuse("This post was already deleted.");
            var error = SuggestionRules.Required(DeleteReason, "Reason", SuggestionRules.MaxReasonLength);
            if (error != null)
            {
                ModelState.AddModelError(nameof(DeleteReason), error);
                return PageWithNewToken();
            }
            if (!_tokens.TryConsume(TokenScope, SubmitToken))
                return Refuse(SuggestionRules.AlreadySubmittedMessage);
            if (!await _repo.DeleteAsync(Post.Id, DeleteReason!, Me!.Value))
                return Refuse("This post was already deleted.");
            TempData["Success"] = $"Post {Post.PostNo} deleted. It is kept for audit (administrators: Show = Deleted).";
            return RedirectToPage("/Suggestions/Index");
        }

        // ---- helpers -------------------------------------------------------------------------------

        // Loads the post for this user: NotFound if it does not exist, Forbid if they may not see it.
        private async Task<IActionResult?> LoadAsync()
        {
            var post = await _repo.GetPostAsync(Id);
            if (post == null) return NotFound();
            if (!_auth.CanView(User, post)) return Forbid();
            Post = post;
            Me = User.GetUserId();
            IsManager = _auth.IsManager(User);
            CanComment = _auth.CanComment(User, post);
            Comments = await _repo.GetCommentsAsync(post.Id);
            if (IsManager)
                Events = await _repo.GetEventsAsync(post.Id);
            return null;
        }

        // The posted comment must exist AND belong to this page's post (otherwise NotFound).
        private async Task<(IActionResult? Denied, SuggestionComment? Comment)> LoadCommentAsync()
        {
            var denied = await LoadAsync();
            if (denied != null) return (denied, null);
            var comment = CommentId.HasValue ? await _repo.GetCommentAsync(CommentId.Value) : null;
            if (comment == null || comment.PostId != Post.Id) return (NotFound(), null);
            return (null, comment);
        }

        private IActionResult Refuse(string message)
        {
            TempData["Error"] = message;
            return RedirectToPage("/Suggestions/Details", new { id = Id });
        }

        private IActionResult PageWithNewToken()
        {
            ModelState.Remove(nameof(SubmitToken));
            SubmitToken = SubmissionTokenGuard.NewToken();
            return Page();
        }
    }
}
