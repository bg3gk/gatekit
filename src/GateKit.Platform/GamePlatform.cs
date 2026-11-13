namespace GateKit.Platform;

/// <summary>Picks the platform this process runs on.</summary>
public static class GamePlatform
{
    static IGamePlatform? _current;

    /// <summary>The platform of this machine: Windows, or Linux with the game under Proton.</summary>
    public static IGamePlatform Current => _current ??= Create();

    static IGamePlatform Create()
    {
        if (OperatingSystem.IsWindows()) return new WindowsPlatform();
        if (OperatingSystem.IsLinux()) return new LinuxProtonPlatform();
        throw new PlatformNotSupportedException($"GateKit runs on Windows and on Linux (Proton), not on {Environment.OSVersion}");
    }

    /// <summary>The Steam app id of Baldur's Gate 3.</summary>
    public const string AppId = "1086940";

    /// <summary>The game's folder name in a Steam library when no app manifest says otherwise.</summary>
    public const string DefaultInstallDir = "Baldurs Gate 3";

    /// <summary>The executables of the game: Vulkan and DirectX 11.</summary>
    public static readonly IReadOnlyList<string> ProcessNames = ["bg3_dx11.exe", "bg3.exe"];

    /// <summary>The installed game in one of the platform's Steam libraries, or null.</summary>
    public static string? FindGameFolder(this IGamePlatform platform)
    {
        foreach (var library in platform.SteamLibraries())
        {
            var folder = Steam.GameFolderIn(library);
            if (folder is not null) return folder;
        }
        return null;
    }
}
