using System.Text.Json;
using JinJi.Domain.Text;
using JinJi.IntegrationTests.Support;

namespace JinJi.IntegrationTests;

/// <summary>読みの照合キー（<see cref="ReadingKey"/>）のテスト。ベクタは Web UI の readingKey.ts と共有する（DB を使わない）。</summary>
public sealed class ReadingKeyTests
{
    /// <summary>共有のテストベクタ（web/src/lib/readingKey.vectors.json）。</summary>
    public static TheoryData<string, string?> Vectors => [.. LoadVectors()];

    [Theory(DisplayName = "共有のテストベクタのとおりに変換する")]
    [MemberData(nameof(Vectors))]
    public void From_MatchesVector(string input, string? expected) => Assert.Equal(expected, ReadingKey.From(input));

    [Fact(DisplayName = "null は null")]
    public void Null_IsNull() => Assert.Null(ReadingKey.From(null));

    [Fact(DisplayName = "結果は英小文字だけで、もう一度通しても変わらない（冪等）")]
    public void From_IsIdempotent()
    {
        foreach (var (input, _) in LoadVectors())
        {
            var key = ReadingKey.From(input);
            if (key is null)
            {
                continue;
            }

            Assert.Matches("^[a-z]+$", key);
            Assert.Equal(key, ReadingKey.From(key));
        }
    }

    [Fact(DisplayName = "照合に使う最小の長さは 3")]
    public void MinMatchLength_Is3() => Assert.Equal(3, ReadingKey.MinMatchLength);

    private static IEnumerable<(string Input, string? Expected)> LoadVectors()
    {
        var path = Path.Combine(RepositoryPaths.Root, "web", "src", "lib", "readingKey.vectors.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.EnumerateArray()
            .Select(e => (e.GetProperty("input").GetString()!, e.GetProperty("expected").GetString()))
            .ToList();
    }
}
