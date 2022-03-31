# Amusing

A command-line tool that shows what namespaces you am-using.

Intended to help make the best use of the [implicit usings](https://aka.ms/csharp-implicit-usings) features of C# 10 / .NET 6.

## Example

```bash
$ amusing MySolution.sln
225    Xunit
207    Moq
151    BigCorp.Splines.Reticulator
<snip>
```

This shows that both `Xunit` and `Moq` namespaces are widely used.

Assuming C# 10 or later and .NET SDK 6 or later, we can make those explicit by placing a `Directory.Build.props` file in our `test/` path, containing:

```xml
<Project>

  <ItemGroup>
    <Using Include="Xunit" />
    <Using Include="Moq" />
  </ItemGroup>

</Project>
```

With that, all source under that location will implicitly import those namespaces.

You can run a Roslyn codefix to remove the previous `using Xunit;` and `using Moq;` directives, which will now be marked as redundant.

## Installation

This utility is provided as a .NET global tool.

```bash
dotnet tool install -g amusing
```

## Thanks

- [Spectre.Console](https://github.com/spectreconsole/spectre.console) for making the console output more fun.