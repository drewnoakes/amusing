using System.Collections.Concurrent;
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
    new Argument<string>("file", () => GetDefaultFilePath(), "Path to a .sln or MSBuild project file (such as .csproj)"),
    countOption,
    noWarnOption
};

rootCommand.Handler = CommandHandler.Create((string file, int? count, bool noWarn) => RunAsync(file, count, noWarn));

return await rootCommand.InvokeAsync(args);

string GetDefaultFilePath() => Path.GetDirectoryName(Environment.GetCommandLineArgs()[0])!;

async Task<int> RunAsync(string filePath, int? maxCount, bool noWarn)
{
    if (!PathValidator.Exists(filePath))
    {
        stderr.WriteLine("[red]File or folder do not exist. Cannot continue.[/]");
        return 1;
    }
    if (!PathValidator.IsValid(filePath))
    {
        stderr.WriteLine("[red]File or folder not valid. Cannot continue.[/]");
        return 2;
    }
    if (!WorkLocator.TryLocate(filePath, out var workPath))
    {
        stderr.WriteLine("[red]Unable to locate any .sln or MSBuild project file (such as .csproj). Cannot continue.[/]");
        return 3;
    }

    var instance = MSBuildLocator.QueryVisualStudioInstances().OrderByDescending(i => i.Version).FirstOrDefault();
    if (instance is null)
    {
        stderr.WriteLine("[red]Unable to locate MSBuild. Cannot continue.[/]");
        return 4;
    }

    MSBuildLocator.RegisterInstance(instance);

    using var workspace = MSBuildWorkspace.Create();

    var errors = new List<string>();

    if (!noWarn)
        workspace.WorkspaceFailed += (o, e) => errors.Add(e.Diagnostic.Message);

    var status = AnsiConsole.Status().Spinner(Spinner.Known.Default);

    await status.StartAsync(
        $"Loading {Path.GetFileName(workPath)}",
        ctx =>
        {
            return workPath!.EndsWith(".sln")
                ? workspace.OpenSolutionAsync(workPath)
                : workspace.OpenProjectAsync(workPath);
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
    public ConcurrentDictionary<string, int> Usings { get; } = new();

    public override void VisitUsingDirective(UsingDirectiveSyntax node)
    {
        string name = node.Name.ToString();
        Usings.TryGetValue(name, out int count);
        Usings[name] = count + 1;
    }
}

class WorkLocator : PathBase
{
    public static bool TryLocate(string path, out string? workPath)
    {
        if (PathValidator.IsValidFile(path))
        {
            workPath = path;
            return true;
        }

        foreach (var ext in allowedExt)
        {
            var work = Directory.EnumerateFiles(path, $"*{ext}", SearchOption.AllDirectories);
            if (work.Any())
            {
                workPath = work.First();
                return true;
            }
        }

        workPath = null;
        return false;
    }
}

class PathValidator : PathBase
{
    public static bool Exists(string path)
        => Directory.Exists(path) || File.Exists(path);

    public static bool IsValidFile(string path)
        => allowedExt.Select(ext => path.EndsWith(ext, StringComparison.CurrentCultureIgnoreCase)).Any(res => res);

    public static bool IsValid(string path)
        => Directory.Exists(path) || IsValidFile(path);
}

abstract class PathBase
{
    protected static readonly string[] allowedExt = new[] { ".sln", ".csproj" };
}