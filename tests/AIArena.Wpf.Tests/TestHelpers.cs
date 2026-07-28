using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;
using AIArena.Wpf;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;
using System.Collections;
using System.Runtime.ExceptionServices;
using System.Resources;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;


internal static partial class Program
{
static string LmStudioCatalogJson()
{
    return """
    {
      "models": [
        {
          "type": "llm",
          "publisher": "google",
          "key": "google/gemma-4-26b-a4b",
          "display_name": "Gemma 4 26B A4B",
          "architecture": "gemma4",
          "quantization": {
            "name": "Q4_K_M",
            "bits_per_weight": 4
          },
          "size_bytes": 17990911801,
          "params_string": "26B-A4B",
          "loaded_instances": [
            {
              "id": "google/gemma-4-26b-a4b",
              "config": {
                "context_length": 4096,
                "parallel": 4,
                "flash_attention": true,
                "offload_kv_cache_to_gpu": true
              }
            }
          ],
          "max_context_length": 262144,
          "format": "gguf",
          "capabilities": {
            "vision": true,
            "trained_for_tool_use": true,
            "reasoning": {
              "allowed_options": ["off", "on"],
              "default": "on"
            }
          },
          "selected_variant": "google/gemma-4-26b-a4b@q4_k_m"
        },
        {
          "type": "embedding",
          "publisher": "gaianet",
          "key": "text-embedding-nomic-embed-text-v1.5-embedding",
          "display_name": "Nomic Embed Text v1.5",
          "quantization": {
            "name": "F16",
            "bits_per_weight": 16
          },
          "size_bytes": 274290560,
          "params_string": null,
          "loaded_instances": [],
          "max_context_length": 2048,
          "format": "gguf"
        }
      ]
    }
    """;
}

static string OllamaTagsJson()
{
    return """
    {
      "models": [
        {
          "name": "qwen3:8b",
          "model": "qwen3:8b",
          "size": 5620000000,
          "digest": "sha256:qwen",
          "details": {
            "format": "gguf",
            "family": "qwen3",
            "parameter_size": "8B",
            "quantization_level": "Q4_K_M"
          }
        },
        {
          "name": "llama3.2:latest",
          "model": "llama3.2:latest",
          "size": 2019393189,
          "digest": "sha256:llama",
          "details": {
            "format": "gguf",
            "family": "llama",
            "parameter_size": "3.2B",
            "quantization_level": "Q4_K_M"
          }
        }
      ]
    }
    """;
}

static string OllamaPsJson()
{
    var expires = DateTimeOffset.Now.AddMinutes(5).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
    return $$"""
    {
      "models": [
        {
          "name": "qwen3:8b",
          "model": "qwen3:8b",
          "size": 5620000000,
          "digest": "sha256:qwen",
          "expires_at": "{{expires}}",
          "size_vram": 5620000000,
          "context_length": 8192,
          "details": {
            "format": "gguf",
            "family": "qwen3",
            "parameter_size": "8B",
            "quantization_level": "Q4_K_M"
          }
        }
      ]
    }
    """;
}

static string LmStudioPreloadCatalogJson()
{
    return """
    {
      "models": [
        {
          "type": "llm",
          "key": "test-chat",
          "display_name": "Test Chat",
          "size_bytes": 2147483648,
          "loaded_instances": [],
          "max_context_length": 32768
        }
      ]
    }
    """;
}

static string LmStudioUnknownContextCatalogJson()
{
    return """
    {
      "models": [
        {
          "type": "llm",
          "key": "unknown-context-chat",
          "display_name": "Unknown Context Chat",
          "loaded_instances": [
            {
              "id": "unknown-instance",
              "config": {
                "parallel": 2
              }
            }
          ],
          "max_context_length": 65536
        }
      ]
    }
    """;
}

static string LmStudioMissingInstanceIdCatalogJson()
{
    return """
    {
      "models": [
        {
          "type": "llm",
          "key": "missing-instance-chat",
          "display_name": "Missing Instance Chat",
          "loaded_instances": [
            {
              "config": {
                "context_length": 4096
              }
            }
          ],
          "max_context_length": 65536
        }
      ]
    }
    """;
}

static CollaborateCoordinator CreateCollaborateCoordinatorForTest(
    IModelProviderClient modelClient,
    TextBox promptText,
    TextBlock statusText,
    Func<ArenaViewSnapshot?> snapshot,
    Action<string> setShellStatus,
    CollaborateHistoryStore historyStore,
    TextBlock? promptBudgetText = null,
    Button? contextReceiptButton = null,
    StackPanel? recentItems = null)
{
    var modePicker = new ComboBox();
    var fastMode = new ComboBoxItem { Content = "Fast", Tag = "fast" };
    modePicker.Items.Add(fastMode);
    modePicker.SelectedItem = fastMode;

    var roundsPicker = new ComboBox { Text = "1" };
    var oneRound = new ComboBoxItem { Content = "1", Tag = "1" };
    roundsPicker.Items.Add(oneRound);
    roundsPicker.SelectedItem = oneRound;

    return new CollaborateCoordinator(
        modelClient,
        System.Windows.Threading.Dispatcher.CurrentDispatcher,
        new ScrollViewer(),
        new StackPanel(),
        promptText,
        new Button(),
        new Button(),
        new Button(),
        new Button(),
        promptBudgetText ?? new TextBlock(),
        contextReceiptButton ?? new Button(),
        new Button(),
        new Button(),
        new Button(),
        modePicker,
        roundsPicker,
        statusText,
        new TextBlock(),
        new TextBlock(),
        new TextBlock(),
        new TextBlock(),
        new StackPanel(),
        recentItems ?? new StackPanel(),
        new Button(),
        new Button(),
        new StackPanel(),
        new Button(),
        new Button(),
        new TextBox(),
        new Button(),
        new Button(),
        new StackPanel(),
        new TextBox(),
        new Button(),
        new Button(),
        new StackPanel(),
        snapshot,
        AccentResourceBrush,
        setShellStatus,
        historyStore);
}

static Brush AccentResourceBrush(string key)
{
    return key switch
    {
        "AlphaAccentBrush" => new SolidColorBrush(Color.FromRgb(0x6E, 0xC9, 0xF1)),
        "BetaAccentBrush" => new SolidColorBrush(Color.FromRgb(0xF1, 0xC9, 0x6B)),
        "GammaAccentBrush" => new SolidColorBrush(Color.FromRgb(0x85, 0xD9, 0x9C)),
        "DeltaAccentBrush" => new SolidColorBrush(Color.FromRgb(0x9E, 0xAF, 0xFF)),
        "NarratorAccentBrush" => new SolidColorBrush(Color.FromRgb(0xD1, 0x85, 0xCE)),
        "OperatorAccentBrush" => new SolidColorBrush(Color.FromRgb(0x7F, 0xB7, 0xFF)),
        _ => new SolidColorBrush(Color.FromRgb(0x73, 0x82, 0x94))
    };
}

static Color RequireSolidColor(Brush brush, string message)
{
    if (brush is SolidColorBrush solid)
    {
        return solid.Color;
    }

    throw new InvalidOperationException(message);
}

static ArenaViewSnapshot SnapshotForOverviewTest(
    bool providerOnline,
    string providerModel,
    string providerLastError,
    int turnIndex,
    IReadOnlyList<TranscriptMessage> messages,
    IReadOnlyList<AgentState> agents)
{
    return new ArenaViewSnapshot(
        "session",
        "snapshot.json",
        DateTime.UtcNow,
        "research",
        "",
        "",
        false,
        false,
        "auto",
        "normal",
        "auto",
        "grounded",
        "",
        "auto",
        "",
        [],
        false,
        [],
        messages.Count,
        turnIndex,
        providerModel,
        "",
        "",
        "",
        "",
        "",
        "idle",
        "",
        "default",
        "",
        false,
        "http://127.0.0.1:1234/v1",
        ModelProviderApiModes.OpenAiCompatible,
        "",
        60,
        0.7,
        2048,
        0,
        "",
        true,
        0,
        12,
        4,
        4,
        "",
        "",
        0,
        providerLastError,
        false,
        providerOnline,
        messages,
        agents);
}

static DialogueMessage CoreMessageForTest(int turn, string speaker, string speakerId, string kind, string status, string model, int latencyMs)
{
    return new DialogueMessage
    {
        Turn = turn,
        Speaker = speaker,
        SpeakerId = speakerId,
        Kind = kind,
        Status = status,
        Text = "text",
        Model = new ModelMetadata
        {
            Model = model,
            LatencyMs = latencyMs
        }
    };
}

static TranscriptMessage TranscriptForTest(int turn, string speaker, string speakerId, string kind, string status)
{
    return new TranscriptMessage(
        turn,
        speaker,
        speakerId,
        0,
        "model",
        0,
        0,
        0,
        0,
        status,
        "",
        false,
        kind,
        "text",
        "",
        "",
        "",
        "",
        "",
        "",
        "",
        "",
        false,
        []);
}

static void WithTempSettingsStore(Action<WpfSettingsStore> action)
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-wpf-tests", Guid.NewGuid().ToString("N"));
    var store = new WpfSettingsStore(Path.Combine(root, "configs", "native-wpf-settings.json"));
    try
    {
        action(store);
    }
    finally
    {
        if (File.Exists(store.SettingsPath))
        {
            File.SetAttributes(store.SettingsPath, File.GetAttributes(store.SettingsPath) & ~FileAttributes.ReadOnly);
        }

        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

/// <summary>
/// MainWindow is split across partial files, so structural assertions read the
/// whole class rather than one file. Control-plane code is appended last and is
/// contiguous, which keeps ordering assertions inside it meaningful.
/// </summary>
static string ReadMainWindowSource()
{
    // MainWindow is split across partials, and listing them by hand went stale
    // twice: a guard silently stopped covering whatever had moved into a new
    // partial instead of failing loudly. Enumerating them means adding a partial
    // cannot quietly narrow what the guards see.
    var shell = Path.GetDirectoryName(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml.cs"))!;
    var partials = Directory.GetFiles(shell, "MainWindow*.cs")
        .Where(path => !path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
        .OrderBy(path => path, StringComparer.Ordinal)
        .ToList();

    Require(partials.Count >= 3, $"MainWindow should still be split across partials, found {partials.Count}");
    return string.Join(Environment.NewLine, partials.Select(File.ReadAllText));
}

static string FindWorkspaceFile(string relativePath)
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        var path = Path.Combine(directory.FullName, relativePath);
        if (File.Exists(path))
        {
            return path;
        }

        directory = directory.Parent;
    }

    throw new FileNotFoundException($"Could not locate workspace file: {relativePath}", relativePath);
}

static string XamlElementBlock(string xaml, string elementName, string elementType)
{
    var marker = $"x:Name=\"{elementName}\"";
    var markerIndex = xaml.IndexOf(marker, StringComparison.Ordinal);
    Require(markerIndex >= 0, $"XAML element '{elementName}' should exist");
    var start = xaml.LastIndexOf($"<{elementType}", markerIndex, StringComparison.Ordinal);
    Require(start >= 0, $"XAML element '{elementName}' should start with <{elementType}");
    var end = xaml.IndexOf("/>", markerIndex, StringComparison.Ordinal);
    Require(end >= 0, $"XAML element '{elementName}' should be self-closing for this contract test");
    return xaml[start..(end + 2)];
}

static string XamlStartTag(string xaml, string elementName, string elementType)
{
    var marker = $"x:Name=\"{elementName}\"";
    var markerIndex = xaml.IndexOf(marker, StringComparison.Ordinal);
    Require(markerIndex >= 0, $"XAML element '{elementName}' should exist");
    var start = xaml.LastIndexOf($"<{elementType}", markerIndex, StringComparison.Ordinal);
    Require(start >= 0, $"XAML element '{elementName}' should start with <{elementType}");
    var end = xaml.IndexOf(">", markerIndex, StringComparison.Ordinal);
    Require(end >= 0, $"XAML element '{elementName}' should have a start tag");
    return xaml[start..(end + 1)];
}

static Button ButtonByToolTip(IEnumerable<Button> buttons, string text)
{
    return buttons.FirstOrDefault(button => (button.ToolTip?.ToString() ?? "").Contains(text, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Button with tooltip containing '{text}' was not found.");
}

static IEnumerable<Button> DescendantButtons(DependencyObject root)
{
    if (root is Button button)
    {
        yield return button;
    }

    for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
    {
        foreach (var child in DescendantButtons(VisualTreeHelper.GetChild(root, index)))
        {
            yield return child;
        }
    }
}

static IEnumerable<TextBlock> DescendantTextBlocks(DependencyObject root)
{
    if (root is TextBlock textBlock)
    {
        yield return textBlock;
    }

    for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
    {
        foreach (var child in DescendantTextBlocks(VisualTreeHelper.GetChild(root, index)))
        {
            yield return child;
        }
    }
}

static void RunStaTest(Action action)
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            failure = ex;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();

    if (failure is not null)
    {
        ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

static double Distance2D(Point first, Point second)
{
    return DistanceCoordinates2D(first.X, first.Y, second.X, second.Y);
}

static double DistanceCoordinates2D(double firstX, double firstY, double secondX, double secondY)
{
    var x = firstX - secondX;
    var y = firstY - secondY;
    return Math.Sqrt((x * x) + (y * y));
}

static double AngleDeltaDegrees(double first, double second)
{
    var delta = Math.Abs((first - second) % 360);
    return delta > 180 ? 360 - delta : delta;
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
}
