namespace PlantStockManager.Pages.FieldWork.Customers
{
    // Values of the shared profile fields (_CustomerFields.cshtml) on Add and Edit customer.
    public sealed record CustomerFieldsModel(string? Name, string? ContactPerson, string? Phone, string? AlternatePhone, string? CustomerType,
        string? VillageCity, string? District, string? Address, IReadOnlyList<string> DistrictSuggestions, string? CheckPhoneUrl);
}
