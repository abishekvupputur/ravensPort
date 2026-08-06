using System.Reflection;

// RavensPort.Helpers, not RavensPort.App.Helpers as under WPF: the Avalonia application class is
// RavensPort.App, and a namespace of that name in the same parent is a compile error rather than a
// style question.
namespace RavensPort.Helpers;

/// <summary>
/// The version this build was stamped with, as something short enough to sit in a title bar.
///
/// Read from the assembly rather than from a constant, so there is one place a release is
/// numbered: Directory.Build.props, which is also what names the installer and the download. A
/// second copy here would be a copy that can disagree with the exe's own file properties.
/// </summary>
public static class AppVersion
{
    /// <summary>
    /// "4.2.0", not "4.2.0+a7866ef…". SourceLink appends the commit to the informational version,
    /// which is what makes a build traceable and also what would put forty hex characters in the
    /// window title. The build metadata is still on the assembly for anyone who needs it.
    /// </summary>
    public static string Display { get; } = Resolve();

    private static string Resolve()
    {
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            // Only reachable if the attribute is stripped; AssemblyVersion is always present, and
            // its trailing ".0" revision is not worth showing.
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version is null ? "" : $"{version.Major}.{version.Minor}.{version.Build}";
        }

        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }
}
