using AIArena.Wpf.Help;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

internal static partial class Program
{
    static void HelpContentLoadsVersionedOfflineManifest()
    {
        var manifestPath = OfflineHelpContentService.ResolveDefaultManifestPath();
        Require(manifestPath is not null, "default help manifest should resolve in a source or installed tree");

        var service = new OfflineHelpContentService(manifestPath!);
        var catalog = service.LoadCatalog();
        Require(catalog.SchemaVersion == OfflineHelpContentService.SupportedSchemaVersion, "help manifest schema should be explicit and supported");
        Require(catalog.GuideVersion.Length > 0 && catalog.ReviewedUtc.Offset == TimeSpan.Zero, "help catalog should carry version and UTC review metadata");
        Require(catalog.Articles.Count >= 20 && catalog.Groups.Count >= 5 && catalog.Journeys.Count >= 5, "help catalog should expose task articles, groups, and guided journeys");
        Require(catalog.Articles.Select(article => article.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == catalog.Articles.Count, "help article IDs should remain unique");
        Require(catalog.Articles.All(article => article.Route == $"help/{article.Id}"
            && article.Markdown.Length > 0
            && article.IntroducedVersion.Length > 0
            && article.ReviewedVersion.Length > 0), "help articles should retain canonical routes, content, and freshness metadata");
        Require(catalog.Journeys.All(journey => journey.ArticleIds.Contains(journey.StartArticleId, StringComparer.OrdinalIgnoreCase)), "each help journey should include its declared start article");
    }

    static void HelpContentSearchRanksDeterministicallyWithHighlights()
    {
        var manifestPath = OfflineHelpContentService.ResolveDefaultManifestPath();
        Require(manifestPath is not null, "help manifest should resolve for search tests");
        var service = new OfflineHelpContentService(manifestPath!);

        var first = service.Search("default unassigned routing", 8);
        var second = service.Search("  ROUTING default   unassigned ", 8);
        Require(first.Count > 0 && first[0].Article.Id == "models-routing", "routing query should rank the model-routing task first");
        Require(first.Select(result => result.Article.Id).SequenceEqual(second.Select(result => result.Article.Id)), "help search ranking should be stable across query case and spacing");
        Require(first[0].Snippet.Length <= 192 && first[0].SnippetMatches.Count > 0, "search should return a bounded highlighted snippet");
        Require(first.Zip(first.Skip(1), (left, right) => left.Score >= right.Score).All(value => value), "search scores should be descending");
        Require(service.Search("phrase-that-does-not-exist", 8).Count == 0, "search should not invent matches");
        Require(service.Search(string.Empty, 3).Count == 3, "blank search should return a bounded catalog projection");
    }

    static void HelpManifestCoversCurrentFeaturesLabelsAndRoutes()
    {
        var manifestPath = OfflineHelpContentService.ResolveDefaultManifestPath();
        Require(manifestPath is not null, "help manifest should resolve for feature coverage");
        var service = new OfflineHelpContentService(manifestPath!);
        var catalog = service.LoadCatalog();
        var requiredArticles = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["home"] = ["What do you want to do?", "Help Center"],
            ["quick-start"] = ["1 Turn", "Loaded Models"],
            ["models-routing"] = ["Loaded Models", "Available catalog", "Default", "Explicit", "Uses default", "Unassigned"],
            ["model-behavior"] = ["Context window", "Rolling 80%", "Tone", "Reload to apply"],
            ["arena-mode"] = ["Arena mode", "Auto Chat"],
            ["factory-mode"] = ["Factory mode", "Public Operator", "1 Turn"],
            ["context-recovery"] = ["Continue", "Fork", "Skip turn", "Reset", "End match"],
            ["status-center"] = ["four meaningful rows", "newest on top"],
            ["match-setup"] = ["Match Setup", "Readiness"],
            ["operator-controls"] = ["Operator Turn", "Auto Chat", "1 Turn"],
            ["model-comparison"] = ["Capture baseline", "Compare current"],
            ["agent"] = ["Agent Workspace", "command"],
            ["collaborate"] = ["AI Collaborate", "routing"],
            ["experiment-lab"] = ["Experiment Lab", "Matrix Runner"],
            ["ai-world-debug"] = ["AI World", "Debug"],
            ["sessions-setups"] = ["Sessions", "Match Setup v4"],
            ["internet-privacy"] = ["Internet", "Privacy"],
            ["provider-troubleshooting"] = ["Provider", "connection"],
            ["powershell-control"] = ["PowerShell", "control plane"],
            ["licensing"] = ["Licensing", "installation"]
        };
        var requiredJourneys = new[]
        {
            "connect-provider", "assign-model", "first-match", "factory-test",
            "recover-context", "agent-workspace", "compare-models", "advanced-automation"
        };

        Require(requiredArticles.Keys.All(id => catalog.FindArticle(id) is not null), "help manifest should cover every current app feature article");
        Require(requiredJourneys.All(id => catalog.Journeys.Any(journey => journey.Id == id)), "help home should cover every required task journey");
        Require(catalog.Articles.All(article => article.ReviewedVersion == catalog.GuideVersion
            && article.IntroducedVersion.Length > 0
            && article.Route == $"help/{article.Id}"
            && article.Keywords.Count > 0
            && article.Aliases.Count > 0), "every article should carry current freshness, stable route, and search metadata");
        Require(catalog.Journeys.Select(journey => journey.Order).SequenceEqual(catalog.Journeys.Select(journey => journey.Order).Order()), "home journeys should be deterministically ordered");

        foreach (var (articleId, labels) in requiredArticles)
        {
            var article = catalog.FindArticle(articleId)!;
            var visibleText = $"{article.Title}\n{article.Summary}\n{article.Markdown}";
            foreach (var label in labels)
            {
                Require(visibleText.Contains(label, StringComparison.OrdinalIgnoreCase), $"help article '{articleId}' should retain current visible label '{label}'");
            }
        }
    }

    static void HelpCatalogInternalLinksAndAssetsAreClosed()
    {
        var manifestPath = OfflineHelpContentService.ResolveDefaultManifestPath();
        Require(manifestPath is not null, "help manifest should resolve for link closure");
        var service = new OfflineHelpContentService(manifestPath!);
        var catalog = service.LoadCatalog();
        var contentRoot = Path.GetDirectoryName(manifestPath!)!;
        var linkPattern = new System.Text.RegularExpressions.Regex(
            @"(?<!!)\[[^\]\r\n]+\]\((?<route>[^\)\r\n]+)\)",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var imagePattern = new System.Text.RegularExpressions.Regex(
            @"!\[[^\]\r\n]*\]\((?<path>[^\)\r\n]+)\)",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var supportedImageExtensions = new HashSet<string>([".png", ".jpg", ".jpeg", ".webp"], StringComparer.OrdinalIgnoreCase);

        foreach (var article in catalog.Articles)
        {
            Require(File.Exists(Path.Combine(contentRoot, article.ContentFile.Replace('/', Path.DirectorySeparatorChar))), $"article '{article.Id}' content file should exist under Help/Content");
            Require(article.RelatedArticleIds.All(id => catalog.FindArticle(id) is not null), $"article '{article.Id}' related links should resolve");
            Require(article.Actions.All(action => service.TryResolveLink(action.Route, out var target) && target.Kind == HelpLinkKind.App), $"article '{article.Id}' actions should be allowlisted app routes");

            foreach (System.Text.RegularExpressions.Match match in linkPattern.Matches(article.Markdown))
            {
                var route = match.Groups["route"].Value.Trim();
                Require(service.TryResolveLink(route, out var target), $"article '{article.Id}' internal link '{route}' should resolve safely");
                if (target.Kind == HelpLinkKind.Anchor)
                {
                    Require(article.Headings.Any(heading => heading.Anchor.Equals(target.Anchor, StringComparison.OrdinalIgnoreCase)), $"article '{article.Id}' should contain anchor '{target.Anchor}'");
                }
                else if (target.Kind == HelpLinkKind.Article && target.Anchor is not null)
                {
                    Require(catalog.FindArticle(target.ArticleId)!.Headings.Any(heading => heading.Anchor.Equals(target.Anchor, StringComparison.OrdinalIgnoreCase)), $"link '{route}' should target an existing article heading");
                }
            }

            foreach (System.Text.RegularExpressions.Match match in imagePattern.Matches(article.Markdown))
            {
                var relativePath = match.Groups["path"].Value.Trim();
                Require(relativePath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)
                    && !Path.IsPathRooted(relativePath)
                    && !relativePath.Contains('\\', StringComparison.Ordinal)
                    && !relativePath.Split('/').Any(segment => segment is "." or "..")
                    && supportedImageExtensions.Contains(Path.GetExtension(relativePath)), $"article '{article.Id}' image path '{relativePath}' should be a supported local Help asset");
                var assetPath = Path.GetFullPath(Path.Combine(contentRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
                Require(assetPath.StartsWith(Path.GetFullPath(contentRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(assetPath), $"article '{article.Id}' image asset '{relativePath}' should exist inside Help/Content");
            }
        }
    }

    static void HelpPackagedContentMatchesSourceAndResolutionOrder()
    {
        var sourceManifest = FindWorkspaceFile("src/AIArena.Wpf/Help/Content/guide-manifest.json");
        var sourceRoot = Path.GetDirectoryName(sourceManifest)!;
        var installedRoot = Path.Combine(AppContext.BaseDirectory, "Help", "Content");
        var installedManifest = Path.Combine(installedRoot, "guide-manifest.json");
        Require(File.Exists(installedManifest), "WPF output should package Help/Content/guide-manifest.json beside the app");

        var sourceFiles = HelpFileInventory(sourceRoot);
        var installedFiles = HelpFileInventory(installedRoot);
        Require(sourceFiles.SequenceEqual(installedFiles, StringComparer.Ordinal), "installed Help/Content inventory should exactly match its structured source");
        foreach (var relativePath in sourceFiles)
        {
            Require(File.ReadAllBytes(Path.Combine(sourceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)))
                .SequenceEqual(File.ReadAllBytes(Path.Combine(installedRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)))), $"installed Help file '{relativePath}' should match source bytes");
        }

        var sourceCatalog = new OfflineHelpContentService(sourceManifest).LoadCatalog();
        var installedCatalog = new OfflineHelpContentService(installedManifest).LoadCatalog();
        Require(sourceCatalog.GuideVersion == installedCatalog.GuideVersion
            && sourceCatalog.Articles.Select(article => article.Id).SequenceEqual(installedCatalog.Articles.Select(article => article.Id))
            && sourceCatalog.Journeys.Select(journey => journey.Id).SequenceEqual(installedCatalog.Journeys.Select(journey => journey.Id)), "source and installed loaders should project identical catalog identity");

        var tempRoot = Path.Combine(Path.GetTempPath(), "ai-arena-help-resolution-tests", Guid.NewGuid().ToString("N"));
        var repositoryRoot = Path.Combine(tempRoot, "repo");
        var syntheticBase = Path.Combine(repositoryRoot, "bin", "Release");
        var syntheticSourceRoot = Path.Combine(repositoryRoot, "src", "AIArena.Wpf", "Help", "Content");
        try
        {
            Directory.CreateDirectory(syntheticBase);
            CopyHelpTree(sourceRoot, syntheticSourceRoot);
            var sourceFallback = OfflineHelpContentService.ResolveManifestPath(syntheticBase);
            Require(sourceFallback == Path.Combine(syntheticSourceRoot, "guide-manifest.json"), "loader should walk to structured source only when installed content is absent");

            var syntheticInstalledRoot = Path.Combine(syntheticBase, "Help", "Content");
            CopyHelpTree(sourceRoot, syntheticInstalledRoot);
            var installedFirst = OfflineHelpContentService.ResolveManifestPath(syntheticBase);
            Require(installedFirst == Path.Combine(syntheticInstalledRoot, "guide-manifest.json"), "loader should prefer app-local installed Help content over a reachable source tree");
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    static void HelpReleaseGateRequiresFreshPackagedContent()
    {
        var sanityScript = File.ReadAllText(FindWorkspaceFile("scripts/wpf-release-sanity.ps1"));
        var project = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/AIArena.Wpf.csproj"));
        Require(sanityScript.Contains("scripts/user-guide-content.ps1", StringComparison.Ordinal)
            && sanityScript.Contains("& $userGuideContentScript -Check", StringComparison.Ordinal), "release sanity should run the canonical User Guide freshness validator");
        Require(sanityScript.Contains("$releaseHelpContentRoot", StringComparison.Ordinal)
            && sanityScript.Contains("Compare-Object -ReferenceObject $sourceHelpInventory", StringComparison.Ordinal)
            && sanityScript.Contains("Get-FileHash -LiteralPath (Join-Path $releaseHelpContentRoot $relativePath)", StringComparison.Ordinal), "release sanity should require exact published Help inventory and content hashes");
        Require(sanityScript.Contains("$helpManifest.guideVersion -ne $Version", StringComparison.Ordinal), "release sanity should bind Help freshness metadata to the release version");
        Require(project.Contains("Help\\Content\\**\\*.json", StringComparison.Ordinal)
            && project.Contains("Help\\Content\\**\\*.md", StringComparison.Ordinal)
            && project.Contains("Help\\Content\\assets\\**\\*.png", StringComparison.Ordinal)
            && project.Contains("CopyToPublishDirectory=\"PreserveNewest\"", StringComparison.Ordinal), "WPF publish should carry the offline manifest, articles, and supported local image assets");
    }

    static void HelpDeepLinksRejectUnsafeTargets()
    {
        var articleIds = new HashSet<string>(["home", "models-routing"], StringComparer.OrdinalIgnoreCase);
        Require(HelpDeepLink.TryParse("help/models-routing#route-with-switches", articleIds, out var article)
            && article.Kind == HelpLinkKind.Article
            && article.ArticleId == "models-routing"
            && article.Anchor == "route-with-switches", "help deep links should canonicalize article anchors");
        Require(HelpDeepLink.TryParse("app/settings/provider", articleIds, out var app) && app.Kind == HelpLinkKind.App, "allowlisted app routes should resolve");
        Require(HelpDeepLink.TryParse("https://example.com/guide", articleIds, out var external) && external.Kind == HelpLinkKind.External, "HTTPS links without embedded credentials should resolve as external");
        Require(!HelpDeepLink.TryParse("javascript:alert(1)", articleIds, out _)
            && !HelpDeepLink.TryParse("http://example.com", articleIds, out _)
            && !HelpDeepLink.TryParse("file:///C:/secret.txt", articleIds, out _)
            && !HelpDeepLink.TryParse("help/../secret", articleIds, out _)
            && !HelpDeepLink.TryParse("app/unknown", articleIds, out _)
            && !HelpDeepLink.TryParse("https://user:secret@example.com", articleIds, out _), "unsafe, unknown, credential-bearing, and traversal links should fail closed");
    }

    static void HelpNavigationHistoryTruncatesBranchesAndBoundsEntries()
    {
        var history = new HelpNavigationHistory(3);
        Require(history.Navigate(new HelpLocation("home")), "first navigation should be recorded");
        Require(history.Navigate(new HelpLocation("models-routing", "Route With Switches")), "second navigation should normalize its anchor");
        Require(!history.Navigate(new HelpLocation("models-routing", "route-with-switches")), "duplicate current locations should not pollute history");
        Require(history.Navigate(new HelpLocation("model-behavior")) && history.CanGoBack && !history.CanGoForward, "forward navigation should enable Back only");
        Require(history.TryGoBack(out var prior) && prior?.ArticleId == "models-routing" && history.CanGoForward, "Back should restore the exact previous location");
        Require(history.Navigate(new HelpLocation("context-recovery")) && !history.CanGoForward, "new navigation should truncate a forward branch");
        Require(history.Navigate(new HelpLocation("status-center")) && history.Entries.Count == 3 && history.Entries[0].ArticleId == "models-routing", "history should evict only the oldest location at capacity");
    }

    static void HelpMarkdownRendererBuildsRichSafeDocument()
    {
        RunStaTest(() =>
        {
            var next = HelpTestArticle("next", "Next article", "Plain next content.", [], []);
            var article = HelpTestArticle(
                "home",
                "Help home",
                """
                Intro with **bold**, *emphasis*, `inline code`, and [Next](help/next).

                ## Configure safely

                > [!WARNING]
                > This is an offline callout.

                1. First step
                2. Second step

                - One item
                - Another item

                | State | Meaning |
                |---|---|
                | Loaded | Provider-observed residency |

                ```powershell
                Get-Help
                ```

                :::details Advanced settings
                This content starts collapsed.
                :::

                [Open Models](app/models)

                ![Unsafe image](../secret.png)

                ---
                """,
                ["next"],
                [new HelpAction("Open Models", "app/models")]);
            var catalog = HelpTestCatalog(article, next);
            var service = new OfflineHelpContentService(catalog);
            var document = service.BuildDocument("home", new Border());

            Require(document.Blocks.OfType<Table>().Count() == 1, "help renderer should create a semantic WPF table");
            var semanticLists = document.Blocks.OfType<System.Windows.Documents.List>().ToArray();
            Require(semanticLists.Any(list => list.MarkerStyle == TextMarkerStyle.Decimal)
                && semanticLists.Any(list => list.MarkerStyle == TextMarkerStyle.Disc),
                "help renderer should preserve ordered and unordered list semantics even when related links add another list");
            var containers = document.Blocks.OfType<BlockUIContainer>().ToArray();
            Require(containers.Length >= 5, "help renderer should create callout, code, details, image fallback, and divider blocks");
            Require(containers.SelectMany(container => HelpVisualDescendants(container.Child)).OfType<TextBlock>()
                .Any(text => text.Inlines.OfType<Bold>().Any(bold => new TextRange(bold.ContentStart, bold.ContentEnd).Text == "Warning: ")),
                "GitHub-style WARNING callouts should retain their typed warning label");
            Require(containers.Select(container => container.Child).OfType<Expander>().Single().IsExpanded == false, "advanced details should be collapsed by default");
            var copyButton = containers.SelectMany(container => HelpVisualDescendants(container.Child)).OfType<Button>()
                .Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == "Copy code");
            Require(copyButton.MinHeight >= 44, "code copy should be a named keyboard button with a 44-DIP target");
            Require(containers.SelectMany(container => HelpVisualDescendants(container.Child)).OfType<TextBlock>()
                .Any(text => text.Text.Contains("Image unavailable", StringComparison.Ordinal)), "unsafe image paths should render an informative fallback without loading a file");
            var links = HelpDocumentHyperlinks(document).ToArray();
            Require(links.Length >= 3
                && links.All(link => link.Tag is HelpLinkTarget)
                && links.Any(link => ((HelpLinkTarget)link.Tag).Kind == HelpLinkKind.App)
                && links.Any(link => ((HelpLinkTarget)link.Tag).ArticleId == "next"), "rendered links should retain validated typed targets instead of shell-opening raw Markdown");
            Require(document.Blocks.OfType<Paragraph>().Any(block => Equals(block.Tag, "configure-safely")), "rendered headings should expose stable anchor metadata");
        });
    }

    static void HelpContentLoaderRejectsMalformedAndUnsafePackages()
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-arena-help-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var manifestPath = Path.Combine(root, "guide-manifest.json");
            File.WriteAllText(manifestPath, "{not-json", System.Text.Encoding.UTF8);
            RequireThrows<InvalidDataException>(() => OfflineHelpContentService.LoadFromManifest(manifestPath), "malformed help JSON should fail closed");

            var invalidMetadata = """
                {
                  "schemaVersion":"ai_arena.help_manifest.v1",
                  "guideVersion":"test",
                  "reviewedUtc":"not-a-date",
                  "journeys":[],
                  "groups":[{"id":"test","title":"Test","order":1,"iconGlyph":""}],
                  "articles":[]
                }
                """;
            File.WriteAllText(manifestPath, invalidMetadata, System.Text.Encoding.UTF8);
            RequireThrows<InvalidDataException>(() => OfflineHelpContentService.LoadFromManifest(manifestPath), "invalid freshness metadata should fail closed");

            var traversal = """
                {
                  "schemaVersion":"ai_arena.help_manifest.v1",
                  "guideVersion":"test",
                  "reviewedUtc":"2026-08-13T00:00:00Z",
                  "journeys":[],
                  "groups":[{"id":"test","title":"Test","order":1,"iconGlyph":""}],
                  "articles":[{
                    "id":"home","groupId":"test","title":"Home","summary":"Summary","keywords":[],"aliases":[],
                    "iconGlyph":"","route":"help/home","order":1,"introducedVersion":"test","reviewedVersion":"test",
                    "contentFile":"../outside.md","relatedArticleIds":[],"actions":[]
                  }]
                }
                """;
            File.WriteAllText(manifestPath, traversal, System.Text.Encoding.UTF8);
            RequireThrows<InvalidDataException>(() => OfflineHelpContentService.LoadFromManifest(manifestPath), "article traversal should fail before reading outside the help root");

            using (var stream = new FileStream(manifestPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.SetLength(2 * 1024 * 1024 + 1);
            }
            RequireThrows<InvalidDataException>(() => OfflineHelpContentService.LoadFromManifest(manifestPath), "oversize manifests should fail before JSON parsing");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void RequireThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static HelpCatalog HelpTestCatalog(params HelpArticle[] articles)
    {
        return new HelpCatalog(
            OfflineHelpContentService.SupportedSchemaVersion,
            "test-version",
            DateTimeOffset.Parse("2026-08-13T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            [new HelpJourney("test", "Test journey", "Test the guide.", articles[0].Id, articles.Select(article => article.Id).ToArray(), 10, "")],
            [new HelpGroup("test", "Test", 10, "")],
            articles);
    }

    private static HelpArticle HelpTestArticle(
        string id,
        string title,
        string markdown,
        IReadOnlyList<string> related,
        IReadOnlyList<HelpAction> actions)
    {
        return new HelpArticle(
            id,
            "test",
            title,
            $"Summary for {title}.",
            [id, "help"],
            [],
            "",
            $"help/{id}",
            10,
            "test-version",
            "test-version",
            $"Articles/{id}.md",
            related,
            actions,
            markdown,
            HelpMarkdownRenderer.ExtractHeadings(markdown));
    }

    private static IEnumerable<Hyperlink> HelpDocumentHyperlinks(FlowDocument document)
    {
        foreach (var block in document.Blocks)
        {
            foreach (var hyperlink in HelpBlockHyperlinks(block))
            {
                yield return hyperlink;
            }
        }
    }

    private static IEnumerable<Hyperlink> HelpBlockHyperlinks(Block block)
    {
        if (block is Paragraph paragraph)
        {
            return paragraph.Inlines.SelectMany(HelpInlineHyperlinks);
        }

        if (block is System.Windows.Documents.List list)
        {
            return list.ListItems.SelectMany(item => item.Blocks.SelectMany(HelpBlockHyperlinks));
        }

        if (block is Table table)
        {
            return table.RowGroups.SelectMany(group => group.Rows)
                .SelectMany(row => row.Cells)
                .SelectMany(cell => cell.Blocks.SelectMany(HelpBlockHyperlinks));
        }

        if (block is Section section)
        {
            return section.Blocks.SelectMany(HelpBlockHyperlinks);
        }

        return [];
    }

    private static IEnumerable<Hyperlink> HelpInlineHyperlinks(Inline inline)
    {
        if (inline is Hyperlink hyperlink)
        {
            return [hyperlink];
        }

        return inline is Span span ? span.Inlines.SelectMany(HelpInlineHyperlinks) : [];
    }

    private static IEnumerable<System.Windows.DependencyObject> HelpVisualDescendants(System.Windows.DependencyObject? root)
    {
        if (root is null)
        {
            yield break;
        }

        yield return root;
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            foreach (var descendant in HelpVisualDescendants(System.Windows.Media.VisualTreeHelper.GetChild(root, index)))
            {
                yield return descendant;
            }
        }
    }

    private static string[] HelpFileInventory(string root)
    {
        return Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static void CopyHelpTree(string sourceRoot, string destinationRoot)
    {
        foreach (var relativePath in HelpFileInventory(sourceRoot))
        {
            var destination = Path.Combine(destinationRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(sourceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)), destination, overwrite: true);
        }
    }
}
