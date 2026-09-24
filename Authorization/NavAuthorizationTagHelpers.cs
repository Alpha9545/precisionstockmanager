using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using System.Security.Claims;

namespace PlantStockManager.Authorization
{
    // Phase A: answers "may this user open page X?" by evaluating the page's
    // rule from FeatureAuthorizationConventions through the normal
    // IAuthorizationService (same policy provider + handler the server uses
    // for the page itself). Used by the navigation; never a replacement for
    // the server-side page authorization.
    public class FeatureAccessService
    {
        private readonly IAuthorizationService _authorization;

        public FeatureAccessService(IAuthorizationService authorization) => _authorization = authorization;

        public async Task<bool> CanAccessPageAsync(ClaimsPrincipal user, string pagePath)
        {
            var rule = FeatureAuthorizationConventions.GetRule(pagePath);
            if (rule.IsAnonymous)
                return true;
            if (user.Identity?.IsAuthenticated != true)
                return false;
            if (rule.IsAuthenticatedOnly)
                return true;
            return (await _authorization.AuthorizeAsync(user, rule.Read)).Succeeded;
        }
    }

    // <li nav-page="/Production/MotherPlant/Index"> ... </li>
    // Renders the element only when the current user may open that page.
    [HtmlTargetElement("li", Attributes = PageAttribute)]
    public class NavPageTagHelper : TagHelper
    {
        public const string PageAttribute = "nav-page";

        private readonly FeatureAccessService _featureAccess;

        public NavPageTagHelper(FeatureAccessService featureAccess) => _featureAccess = featureAccess;

        [HtmlAttributeName(PageAttribute)]
        public string Page { get; set; } = string.Empty;

        [ViewContext, HtmlAttributeNotBound]
        public ViewContext ViewContext { get; set; } = default!;

        public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
        {
            if (!await _featureAccess.CanAccessPageAsync(ViewContext.HttpContext.User, Page))
            {
                output.SuppressOutput();
                return;
            }

            if (context.Items.TryGetValue(typeof(NavSectionState), out var state) && state is NavSectionState section)
                section.VisibleItems++;
        }
    }

    // <li nav-section> ... </li>
    // A menu section: rendered only if at least one nav-page item inside it
    // is visible to the current user.
    [HtmlTargetElement("li", Attributes = "nav-section")]
    public class NavSectionTagHelper : TagHelper
    {
        public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
        {
            output.Attributes.RemoveAll("nav-section");
            var state = new NavSectionState();
            context.Items[typeof(NavSectionState)] = state;
            await output.GetChildContentAsync();
            if (state.VisibleItems == 0)
                output.SuppressOutput();
        }
    }

    internal sealed class NavSectionState
    {
        public int VisibleItems { get; set; }
    }
}
