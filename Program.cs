using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Spectre.Console;

var stderr = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });

string? filePath = args switch
{
    [string s] => s,
    _ => null
};

if (filePath is null)
{
    PrintUsage(stderr);
    return 1;
}

var instance = MSBuildLocator.QueryVisualStudioInstances().OrderByDescending(i => i.Version).FirstOrDefault();

if (instance is null)
{
    stderr.WriteLine("[red]Unable to locate MSBuild. Cannot continue.[/]");
    return 1;
}

MSBuildLocator.RegisterInstance(instance);

using var workspace = MSBuildWorkspace.Create();

var errors = new List<string>();

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

bool hasWarnings = false;

foreach (var error in errors)
{
    stderr.MarkupLineInterpolated($"[purple]{error}[/]");
    hasWarnings = true;
}

var collector = new UsingCollector();

await status.StartAsync(
    "Walking syntax trees",
    async ctx =>
    {
        foreach (var project in workspace.CurrentSolution.Projects)
        {
            if (project.Language != "C#")
            {
                AnsiConsole.MarkupLineInterpolated($"[yellow]Skipping non-C# project: {project.FilePath}[/]");
                hasWarnings = true;
                continue;
            }

            foreach (var document in project.Documents)
            {
                if (document.FilePath?.EndsWith("GlobalUsings.g.cs") == true)
                {
                    continue;
                }

                SyntaxNode? root = await document.GetSyntaxRootAsync();

                collector.Visit(root);
            }
        }
    });

if (hasWarnings)
{
    AnsiConsole.WriteLine();
}

foreach ((string name, int count) in collector.Usings.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key))
{
    AnsiConsole.MarkupLineInterpolated($"[blue]{count,-6}[/] {name}");
}

return 0;

static void PrintUsage(IAnsiConsole console)
{
    console.MarkupLine(
        """
        Usage:

            [green]amusing[/] [blue]<file>[/]

        Where:

            [blue]<file>[/] Path to a .sln or MSBuild project file (such as .csproj)
        """);
}

class UsingCollector : CSharpSyntaxWalker
{
    public Dictionary<string, int> Usings { get; } = new();

    public override void VisitUsingDirective(UsingDirectiveSyntax node)
    {
        string name = node.Name.ToString();

        if (!Usings.TryGetValue(name, out int count))
        {
            count = 0;
        }

        count++;
        Usings[name] = count;
    }
}