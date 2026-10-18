using System.Reflection;

namespace JinJi.Infrastructure.Migrations;

/// <summary>
/// アセンブリに埋め込んだ db/migrations/*.sql を読み出す。
/// </summary>
public static class EmbeddedMigrations
{
    private const string ResourcePrefix = "JinJi.Migrations.";

    /// <summary>埋め込まれたマイグレーションを版番号順に返す。</summary>
    public static IReadOnlyList<MigrationScript> Load()
    {
        var assembly = typeof(EmbeddedMigrations).Assembly;
        var scripts = new List<MigrationScript>();

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            scripts.Add(MigrationScript.Create(resourceName[ResourcePrefix.Length..], ReadResource(assembly, resourceName)));
        }

        return [.. scripts.OrderBy(s => s.Version, StringComparer.Ordinal)];
    }

    private static string ReadResource(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new MigrationException($"埋め込みリソースを読めません: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
