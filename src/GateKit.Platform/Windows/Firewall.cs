using System.Runtime.Versioning;
using Microsoft.Win32;

namespace GateKit.Platform.Windows;

/// <summary>
/// Reads the firewall rules, and only reads them. Windows asks the user whether a program may
/// listen on the network the first time it does, once per program - and a rule is the user's to
/// make, in an elevated prompt, never GateKit's. All this answers is "is there one already?", so
/// `doctor` can say whether the prompt is still coming.
///
/// The rules are registry values of the form "v2.30|Action=Allow|Dir=In|App=C:\...\bg3_dx11.exe|...".
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Firewall
{
    const string LocalRules = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";
    const string PolicyRules = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\WindowsFirewall\FirewallRules";

    /// <summary>True when a rule allows that executable, false when none does, null when the rules
    /// cannot be read on this machine.</summary>
    public static bool? Allows(string executable)
    {
        var seen = false;
        foreach (var key in new[] { LocalRules, PolicyRules })
        {
            foreach (var rule in Values(key, ref seen))
            {
                if (Matches(rule, executable)) return true;
            }
        }
        return seen ? false : null;
    }

    static IEnumerable<string> Values(string path, ref bool seen)
    {
        var values = new List<string>();
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default)
                .OpenSubKey(path["HKEY_LOCAL_MACHINE\\".Length..], writable: false);
            if (key is null) return values;
            seen = true;
            foreach (var name in key.GetValueNames())
            {
                if (key.GetValue(name) is string text) values.Add(text);
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return values;
        }
        return values;
    }

    static bool Matches(string rule, string executable)
    {
        string? app = null;
        var allow = false;
        foreach (var field in rule.Split('|'))
        {
            if (field.StartsWith("App=", StringComparison.OrdinalIgnoreCase)) app = field[4..];
            else if (field.Equals("Action=Allow", StringComparison.OrdinalIgnoreCase)) allow = true;
        }
        if (!allow || app is null) return false;
        try
        {
            return string.Equals(Path.GetFullPath(Environment.ExpandEnvironmentVariables(app)),
                Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
