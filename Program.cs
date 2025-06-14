using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Spectre.Console;

var stderr = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });

var hasWarnings = false;

var countOption = new Option<int?>("--count", "The maximum number of results to return (optional)");
countOption.AddAlias("-c");

var noWarnOption = new Option("--no-warn", "Suppress warnings");
noWarnOption.AddAlias("-q");

var rootCommand = new RootCommand
{
    new Argument<string>("file", "Path to a .sln or MSBuild project file (such as .csproj)"),
    countOption,
    noWarnOption
};

rootCommand.Handler = CommandHandler.Create((string file, int? count, bool noWarn) => RunAsync(file, count, noWarn));

return await rootCommand.InvokeAsync(args);

async Task<int> RunAsync(string filePath, int? maxCount, bool noWarn)
{
    if (!File.Exists(filePath))
    {
        stderr.MarkupLine("[red]File does not exist. Cannot continue.[/]");
        return 1;
    }

    var instance = MSBuildLocator.QueryVisualStudioInstances().OrderByDescending(i => i.Version).FirstOrDefault();
    if (instance is null)
    {
        stderr.MarkupLine("[red]Unable to locate MSBuild. Cannot continue.[/]");
        return 2;
    }

    MSBuildLocator.RegisterInstance(instance);

    using var workspace = MSBuildWorkspace.Create();

    var errors = new List<string>();

    if (!noWarn)
        workspace.WorkspaceFailed += (o, e) => errors.Add(e.Diagnostic.Message);

    var status = AnsiConsole.Status().Spinner(Spinner.Known.Default);

    await status.StartAsync(
        $"Loading {Path.GetFileName(filePath)}",
        ctx =>
        {
            return filePath.EndsWith(".sln")
                ? workspace.OpenSolutionAsync(filePath)
                : workspace.OpenProjectAsync(filePath);
        });

    if (!noWarn)
    {
        foreach (var error in errors)
        {
            stderr.MarkupLineInterpolated($"[purple]{error}[/]");
            hasWarnings = true;
        }
    }

    var collector = new UsingCollector();

    await status.StartAsync(
        "Walking syntax trees",
        ctx => Parallel.ForEachAsync(
            EnumerateDocuments(workspace),
            async (document, token) =>
            {
                SyntaxNode? root = await document.GetSyntaxRootAsync(token);

                collector.Visit(root);
            }));

    if (hasWarnings)
        AnsiConsole.WriteLine();

    IEnumerable<KeyValuePair<string, int>>? results = collector.Usings.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key);

    if (maxCount is not null)
        results = results.Take(maxCount.Value);

    foreach ((string name, int count) in results)
        AnsiConsole.MarkupLineInterpolated($"[blue]{count,-6}[/] {name}");

    return 0;
}

IEnumerable<Document> EnumerateDocuments(MSBuildWorkspace workspace)
{
    foreach (var project in workspace.CurrentSolution.Projects)
    {
        if (project.Language != "C#")
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]Skipping non-C# project: {project.FilePath}[/]");
            hasWarnings = true;
            continue;
        }

        foreach (Document document in project.Documents)
        {
            if (document.FilePath?.EndsWith("GlobalUsings.g.cs") == true)
            {
                continue;
            }

            yield return document;
        }
    }
}

class UsingCollector : CSharpSyntaxWalker
{
    public Dictionary<string, int> Usings { get; } = new();

    public override void VisitUsingDirective(UsingDirectiveSyntax node)
    {
        if (node.Name is null)
            return;

        string name = node.Name.ToString();

        lock (Usings)
        {
            Usings.TryGetValue(name, out int count);

            Usings[name] = count + 1;
        }
    }
}
