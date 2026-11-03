using PlantStockManager.Services.FieldWork;

namespace PlantStockManager.Pages.FieldWork
{
    // What the shared route block (_RouteMap.cshtml) needs: the day + its computed route and the map tile settings.
    public sealed record RouteMapModel(FieldRouteService.RouteView View, FieldWorkOptions Options, string ElementId = "fwRouteMap");
}
