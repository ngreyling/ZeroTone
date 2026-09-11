using System.Reflection;

namespace ZeroTone;

/// <summary>
/// Display version for About and similar UI. Reads csproj <c>Version</c> via
/// <see cref="AssemblyInformationalVersionAttribute"/>.
/// </summary>
internal static class AppVersion
{
    /// <summary>
    /// Product version string: informational (strip <c>+metadata</c>), else
    /// three-part assembly version, else <c>unknown</c>.
    /// </summary>
    public static string GetDisplayString(Assembly? assembly = null)
    {
        assembly ??= Assembly.GetExecutingAssembly();

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var cleaned = StripBuildMetadata(informational.Trim());
            if (cleaned.Length > 0)
            {
                return cleaned;
            }
        }

        var assemblyVersion = assembly.GetName().Version;
        if (assemblyVersion is not null)
        {
            return assemblyVersion.ToString(3);
        }

        return "unknown";
    }

    /// <summary>
    /// SDK/CI may append <c>+commit</c> (or similar) to informational version.
    /// </summary>
    private static string StripBuildMetadata(string version)
    {
        var plus = version.IndexOf('+');
        return plus >= 0 ? version[..plus] : version;
    }
}
