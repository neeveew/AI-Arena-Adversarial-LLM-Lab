internal static partial class Program
{
    static void StructuralSourceCacheBoundsRepositoryReads()
    {
        const string relativePath = "NOTICE.md";
        WorkspaceSourceCache.TryRemove(relativePath, out _);

        var firstPath = FindWorkspaceFile(relativePath);
        var probesAfterFirstLookup = WorkspaceRootProbeCount;
        for (var index = 0; index < 100; index++)
        {
            Require(FindWorkspaceFile(relativePath) == firstPath,
                "the cached workspace root resolved inconsistent repository paths");
        }

        Require(WorkspaceRootProbeCount == probesAfterFirstLookup,
            "repeated repository lookups walked parent directories after the root was cached");

        var readsBefore = WorkspaceSourceDiskReadCount;
        var firstText = ReadWorkspaceFile(relativePath);
        for (var index = 0; index < 100; index++)
        {
            Require(ReferenceEquals(firstText, ReadWorkspaceFile(relativePath)),
                "immutable repository source text was recreated instead of returned from the process cache");
        }

        Require(WorkspaceSourceDiskReadCount - readsBefore == 1,
            $"101 immutable source reads performed {WorkspaceSourceDiskReadCount - readsBefore} disk reads instead of one");
        Require(ReadWorkspaceFileUncached(relativePath) == firstText,
            "the explicit uncached seam did not preserve source bytes for mutable-fixture callers");

        var mainWindowSource = ReadMainWindowSource();
        var readsAfterMainWindow = WorkspaceSourceDiskReadCount;
        Require(ReferenceEquals(mainWindowSource, ReadMainWindowSource()),
            "the concatenated MainWindow partial source was rebuilt instead of cached");
        Require(WorkspaceSourceDiskReadCount == readsAfterMainWindow,
            "a repeated MainWindow structural read reopened repository files");

        Console.WriteLine(
            $"structural source cache receipt: path_lookups=101; root_walks_after_first=0; source_requests=101; disk_reads=1; cached_sources={WorkspaceSourceCache.Count}");
    }
}
