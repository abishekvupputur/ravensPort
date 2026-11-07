using System.Runtime.CompilerServices;
using RavensPort.Core.Filters;

namespace RavensPort.Core.Tests.Filters;

internal static class TestRulesDirectory
{
    /// <summary>An empty folder, so tests use the original secret rules, never the rules file on this PC.</summary>
    public static readonly string Path = Directory.CreateTempSubdirectory("ravensport-rules-").FullName;

    [ModuleInitializer]
    internal static void Use() => SecretRuleSet.DirectoryOverride = Path;
}
