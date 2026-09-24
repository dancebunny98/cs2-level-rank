using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using CounterStrikeSharp.API;
using Microsoft.Extensions.Logging;

namespace LevelsRanksModulePlayersInfo;

/// <summary>
/// Thin wrapper over the Steamworks flat API of the already-loaded steam_api library:
/// ISteamGameServer::UserHasLicenseForApp(steamId, appId).
/// The original C++ plugin calls the same method through SteamGameServer().
/// Results are cached per SteamID (like the FluteCS2PlayersList fork's g_PrimeCache),
/// since UserHasLicenseForApp is a blocking Steam API call.
/// </summary>
internal static class SteamLicense
{
    // App IDs checked by the original plugin.
    private const uint AppIdA = 624820;
    private const uint AppIdB = 54029;

    // EUserHasLicenseForAppResult::k_EUserHasLicenseResultHasLicense
    private const int HasLicense = 0;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetGameServerDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int UserHasLicenseForAppDelegate(nint self, ulong steamId, uint appId);

    private static nint _library;
    private static GetGameServerDelegate? _getGameServer;
    private static UserHasLicenseForAppDelegate? _userHasLicenseForApp;

    // steamid64 -> has prime. Cleared on map start (ClearCache), same lifetime as the original's cache.
    private static readonly ConcurrentDictionary<ulong, bool> Cache = new();

    public static bool Available => _getGameServer is not null && _userHasLicenseForApp is not null;

    public static void Init(ILogger logger)
    {
        Shutdown();

        try
        {
            _library = LoadSteamApi();
            if (_library == 0)
            {
                logger.LogWarning("steam_api library not found; 'prime' will always be false.");
                return;
            }

            if (!NativeLibrary.TryGetExport(_library, "SteamAPI_ISteamGameServer_UserHasLicenseForApp", out var hasLicenseAddr))
            {
                logger.LogWarning("UserHasLicenseForApp export not found; 'prime' will always be false.");
                return;
            }

            // The accessor is versioned (SteamAPI_SteamGameServer_v015, ...) - take the newest one exported.
            nint getServerAddr = 0;
            for (var version = 25; version >= 10 && getServerAddr == 0; version--)
                NativeLibrary.TryGetExport(_library, $"SteamAPI_SteamGameServer_v{version:000}", out getServerAddr);

            if (getServerAddr == 0)
            {
                logger.LogWarning("SteamAPI_SteamGameServer_vXXX export not found; 'prime' will always be false.");
                return;
            }

            _userHasLicenseForApp = Marshal.GetDelegateForFunctionPointer<UserHasLicenseForAppDelegate>(hasLicenseAddr);
            _getGameServer = Marshal.GetDelegateForFunctionPointer<GetGameServerDelegate>(getServerAddr);
        }
        catch (Exception ex)
        {
            _getGameServer = null;
            _userHasLicenseForApp = null;
            logger.LogWarning(ex, "Failed to bind Steam API; 'prime' will always be false.");
        }
    }

    public static void Shutdown()
    {
        _getGameServer = null;
        _userHasLicenseForApp = null;
        Cache.Clear();
        if (_library != 0)
        {
            NativeLibrary.Free(_library);
            _library = 0;
        }
    }

    /// <summary>Drops all cached results. Call on map start, like the fork's g_PrimeCache.clear().</summary>
    public static void ClearCache() => Cache.Clear();

    public static bool HasPrime(ulong steamId)
    {
        if (steamId == 0 || !Available)
            return false;

        if (Cache.TryGetValue(steamId, out var cached))
            return cached;

        var server = _getGameServer!();
        if (server == 0)
            return false;

        var prime = _userHasLicenseForApp!(server, steamId, AppIdA) == HasLicense ||
                    _userHasLicenseForApp!(server, steamId, AppIdB) == HasLicense;

        Cache[steamId] = prime;
        return prime;
    }

    private static nint LoadSteamApi()
    {
        var binDir = Path.GetFullPath(Path.Combine(Server.GameDirectory, "..", "bin"));

        string[] candidates = OperatingSystem.IsWindows()
            ? ["steam_api64.dll", Path.Combine(binDir, "win64", "steam_api64.dll")]
            : ["libsteam_api.so", Path.Combine(binDir, "linuxsteamrt64", "libsteam_api.so")];

        foreach (var candidate in candidates)
        {
            if (NativeLibrary.TryLoad(candidate, out var handle))
                return handle;
        }

        return 0;
    }
}
