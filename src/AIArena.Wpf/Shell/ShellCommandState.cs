namespace AIArena.Wpf;

internal enum ShellSurface
{
    Lab,
    ExperimentLab,
    World,
    MatchSetup,
    Models,
    Agent,
    Collaborate
}

internal sealed record ShellCommandState(
    bool ShowMatchSetup,
    bool ShowModels,
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
            ShellSurface.Lab => Lab,
            ShellSurface.ExperimentLab => Hidden,
            ShellSurface.World => MatchSetupOnly,
            ShellSurface.MatchSetup => Lab,
            ShellSurface.Models => Lab,
            ShellSurface.Agent => Hidden,
            ShellSurface.Collaborate => new(
                ShowMatchSetup: false,
                ShowModels: false,
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

    private static ShellCommandState Lab { get; } = new(
        ShowMatchSetup: true,
        ShowModels: true,
        ShowSearch: true,
        ShowExport: true,
        ShowView: true,
        SearchAutomationName: "Search transcripts",
        SearchHelpText: "Search transcript text, speakers, models, and sources.",
        ExportAutomationName: "Export transcript",
        ExportHelpText: "Export the current transcript scope to a file.");

    private static ShellCommandState MatchSetupOnly { get; } = new(
        ShowMatchSetup: true,
        ShowModels: true,
        ShowSearch: false,
        ShowExport: false,
        ShowView: false,
        SearchAutomationName: "",
        SearchHelpText: "",
        ExportAutomationName: "",
        ExportHelpText: "");

    private static ShellCommandState Hidden { get; } = new(
        ShowMatchSetup: false,
        ShowModels: false,
        ShowSearch: false,
        ShowExport: false,
        ShowView: false,
        SearchAutomationName: "",
        SearchHelpText: "",
        ExportAutomationName: "",
        ExportHelpText: "");
}
