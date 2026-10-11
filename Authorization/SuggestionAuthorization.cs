using System.Security.Claims;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Authorization
{
    // WHO may see / post / comment / moderate in Suggestions & Problems -- a GLOBAL module.
    //
    //   Every signed-in user (any role, with or without an Area assignment -- Electrician, Welder
    //   and Plumber included) holds the module's GLOBAL grants:
    //       Suggestion.View      see the active and permanent posts of EVERY Area
    //       Suggestion.Create    post for ANY active Area (the Area / Polyhouse combination is
    //                            still validated by the page and the database)
    //       Suggestion.Comment   comment / reply on what they can see; edit own comment for
    //                            30 minutes; delete own comment
    //   These are granted here, intentionally, to every authenticated principal -- not through
    //   RolePermissions rows -- and the pages use the "@authenticated" page rule
    //   (FeatureAuthorizationConventions), so an anonymous request is still refused.
    //
    //   Administrator (Suggestion.Manage; full-access users have it implicitly)
    //       expired and deleted posts too; make permanent, change status, delete (soft, with
    //       reason), delete any comment; comment on any non-deleted post.
    //
    // Area authorization (AreaAccessService) is deliberately NOT used by this module; it is
    // unchanged for every other module. Every page handler calls this on the post / comment
    // RE-LOADED from the database, so a posted id can never widen what a user may do.
    public class SuggestionAuthorization
    {
        public const string ViewPermission = "Suggestion.View";
        public const string CreatePermission = "Suggestion.Create";
        public const string CommentPermission = "Suggestion.Comment";
        public const string ManagePermission = "Suggestion.Manage";

        // Granted to every authenticated user (see above). Manage is NOT in this list.
        public static readonly IReadOnlyList<string> GlobalPermissions = new[] { ViewPermission, CreatePermission, CommentPermission };

        // A signed-in, identified user -- the only requirement for the global grants.
        public static bool IsSignedIn(ClaimsPrincipal user)
            => user.Identity?.IsAuthenticated == true && user.GetUserId() != null;

        public bool Has(ClaimsPrincipal user, string permission)
            => permission == ManagePermission ? user.HasPermission(ManagePermission)
               : GlobalPermissions.Contains(permission) && IsSignedIn(user);

        public bool IsManager(ClaimsPrincipal user) => user.HasPermission(ManagePermission);

        public bool CanUseModule(ClaimsPrincipal user) => Has(user, ViewPermission);

        public bool CanCreate(ClaimsPrincipal user) => Has(user, CreatePermission);

        // Any active Area may be chosen (global module); WHICH Area / Polyhouse is valid is the
        // page's and the database's check, not an authorization question.
        public bool CanCreateFor(ClaimsPrincipal user, int areaId) => CanCreate(user) && areaId > 0;

        public bool CanView(ClaimsPrincipal user, SuggestionPost post)
        {
            if (IsManager(user))
                return true;
            return CanUseModule(user) && !post.IsDeleted && !post.IsExpired;
        }

        public bool CanComment(ClaimsPrincipal user, SuggestionPost post)
            => !post.IsDeleted && CanView(user, post) && (IsManager(user) || Has(user, CommentPermission));

        public bool CanEditComment(ClaimsPrincipal user, SuggestionPost post, SuggestionComment comment, DateTime nowUtc)
            => !comment.IsDeleted && comment.AuthorId == user.GetUserId() && CanComment(user, post)
               && SuggestionRules.CanEditComment(comment.CreatedAt, nowUtc);

        public bool CanDeleteComment(ClaimsPrincipal user, SuggestionPost post, SuggestionComment comment)
            => !comment.IsDeleted && !post.IsDeleted
               && (IsManager(user) || (comment.AuthorId == user.GetUserId() && CanView(user, post)));

        public bool CanManagePost(ClaimsPrincipal user, SuggestionPost post) => IsManager(user) && !post.IsDeleted;
    }
}
