namespace AIArena.Wpf;

internal enum ShellSurface
{
    Lab,
    World,
    MatchSetup,
    Agent,
    Collaborate
}

internal sealed record ShellCommandState(
    bool ShowMatchSetup,
    bool ShowSearch,
    bool ShowExport,
    bool ShowView,
    string SearchAutomationName,
    string SearchHelpText,
    string ExportAutomationName,
    string ExportHelpText)
{
    public static ShellCommandState For(ShellSurface surface)
    {
        return surface switch
        {
            ShellSurface.Lab => new(
                ShowMatchSetup: true,
                ShowSearch: true,
                ShowExport: true,
                ShowView: true,
                SearchAutomationName: "Search transcripts",
                SearchHelpText: "Search transcript text, speakers, models, and sources.",
                ExportAutomationName: "Export transcript",
                ExportHelpText: "Export the current transcript scope to a file."),
            ShellSurface.World => MatchSetupOnly,
            ShellSurface.MatchSetup => Hidden,
            ShellSurface.Agent => Hidden,
            ShellSurface.Collaborate => new(
                ShowMatchSetup: false,
                ShowSearch: true,
                ShowExport: true,
                ShowView: false,
                SearchAutomationName: "Search AI Collaborate chats",
                SearchHelpText: "Search AI Collaborate prompts and saved chats.",
                ExportAutomationName: "Export AI Collaborate chat",
                ExportHelpText: "Export the current AI Collaborate chat with run reviews and team trace steps."),
            _ => throw new ArgumentOutOfRangeException(nameof(surface), surface, "Unknown shell surface.")
        };
    }

    private static ShellCommandState MatchSetupOnly { get; } = new(
        ShowMatchSetup: true,
        ShowSearch: false,
        ShowExport: false,
        ShowView: false,
        SearchAutomationName: "",
        SearchHelpText: "",
        ExportAutomationName: "",
        ExportHelpText: "");

    private static ShellCommandState Hidden { get; } = new(
        ShowMatchSetup: false,
        ShowSearch: false,
        ShowExport: false,
        ShowView: false,
        SearchAutomationName: "",
        SearchHelpText: "",
        ExportAutomationName: "",
        ExportHelpText: "");
}
