namespace JinJi.IntegrationTests.Support;

/// <summary>リポジトリ内のファイルの場所。</summary>
internal static class RepositoryPaths
{
    /// <summary>JinJi.slnx のあるディレクトリ（テストの実行ディレクトリから上にたどる）。</summary>
    public static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "JinJi.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("リポジトリのルート（JinJi.slnx）が見つかりません。");
    }
}
