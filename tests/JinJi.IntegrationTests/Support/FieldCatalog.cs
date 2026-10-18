using System.Text.Json;

namespace JinJi.IntegrationTests.Support;

/// <summary>db/field-catalog.json の読み出し。</summary>
internal sealed class FieldCatalog
{
    private FieldCatalog(IReadOnlySet<string> categories, IReadOnlyList<CatalogColumn> columns)
    {
        Categories = categories;
        Columns = columns;
    }

    public IReadOnlySet<string> Categories { get; }

    public IReadOnlyList<CatalogColumn> Columns { get; }

    public static FieldCatalog Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "field-catalog.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        var categories = root.GetProperty("categories").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var columns = new List<CatalogColumn>();
        foreach (var table in root.GetProperty("tables").EnumerateObject())
        {
            foreach (var column in table.Value.GetProperty("columns").EnumerateObject())
            {
                columns.Add(new CatalogColumn(
                    table.Name,
                    column.Name,
                    column.Value.GetProperty("category").GetString() ?? string.Empty,
                    column.Value.TryGetProperty("description", out var description) ? description.GetString() ?? string.Empty : string.Empty));
            }
        }

        return new FieldCatalog(categories, columns);
    }
}

internal sealed record CatalogColumn(string Table, string Column, string Category, string Description)
{
    public string Key => $"{Table}.{Column}";
}
