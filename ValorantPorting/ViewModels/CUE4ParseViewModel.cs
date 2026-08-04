using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.AssetRegistry;
using CUE4Parse.UE4.AssetRegistry.Objects;
using CUE4Parse.UE4.Versions;
using ValorantPorting.AppUtils;
using ValorantPorting.Services.Endpoints;

namespace ValorantPorting.ViewModels;

public class CUE4ParseViewModel : ObservableObject
{
    public static readonly VersionContainer Version = new(EGame.GAME_UE5_3);

    private static readonly string MappingsPath = FindMappingsFile();

    // Update this URL whenever Valorant patches and the mappings go stale.
    private const string MappingsDownloadUrl = "https://data.uedb.dev/mappings/68c7964faa9ff725d91c8302/VALORANT_13.02_zs.usmap";

    private static string FindMappingsFile()
    {
        var mappingsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Mappings");
        if (Directory.Exists(mappingsDir))
        {
            var usmapFiles = Directory.GetFiles(mappingsDir, "*.usmap");
            if (usmapFiles.Length > 0) return usmapFiles[0];
        }
        return Path.Combine(mappingsDir, "VALORANT_13_00_zs.usmap");
    }

    public readonly List<FAssetData> AssetDataBuffers = new();
    public readonly ValorantPortingFileProvider Provider;

    public FAssetRegistryState? AssetRegistry;
    
    public CUE4ParseViewModel(string directory, EInstallType installType)
    {
        if (installType is EInstallType.Local && !Directory.Exists(directory))
        {
            AppLog.Warning(
                "Installation Not Found, Valorant installation path does not exist or has not been set. Please go to settings to verify you've set the right path and restart. The program will not work properly on Local Installation mode if you do not set it.");
            return;
        }

        Provider = installType switch
        {
            EInstallType.Local => new ValorantPortingFileProvider(new DirectoryInfo(directory), SearchOption.AllDirectories, true, Version),
            EInstallType.Live => new ValorantPortingFileProvider(true, Version)
        };
    }

    private ApiEndpointViewModel _apiEndpointView => ApiEndpointView;

    public async Task Initialize()
    {
        if (Provider is null) return;

        if (!File.Exists(MappingsPath))
        {
            AppLog.Information("Mappings file not found locally, downloading a copy from uedb.dev...");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(MappingsPath)!);
                using var mappingsHttpClient = new System.Net.Http.HttpClient();
                var mappingsBytes = mappingsHttpClient.GetByteArrayAsync(MappingsDownloadUrl).GetAwaiter().GetResult();
                File.WriteAllBytes(MappingsPath, mappingsBytes);
                AppLog.Information("Mappings file downloaded successfully.");
            }
            catch (Exception ex)
            {
                AppLog.Warning($"Automatic Mappings download failed: {ex.Message}");
            }
        }

        if (!File.Exists(MappingsPath))
        {
            AppLog.Warning(
                $"Mappings file not found at \"{MappingsPath}\". UE5 Valorant assets will fail to parse without it.");
        }
        else
        {
            Provider.MappingsContainer = new FileUsmapTypeMappingsProvider(MappingsPath);
        }

        var oodlePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, CUE4Parse.Compression.OodleHelper.OODLE_DLL_NAME);
        if (!File.Exists(oodlePath))
        {
            AppLog.Information("Oodle DLL not found locally, downloading a known-good copy from GitHub...");
            using var oodleHttpClient = new System.Net.Http.HttpClient();
            var downloaded = CUE4Parse.Compression.OodleHelper.DownloadOodleDllFromOodleUEAsync(oodleHttpClient, oodlePath).GetAwaiter().GetResult();
            if (!downloaded)
            {
                AppLog.Warning("Automatic Oodle download from GitHub failed.");
            }
        }
        if (File.Exists(oodlePath))
        {
            CUE4Parse.Compression.OodleHelper.Initialize(oodlePath);
        }
        else
        {
            AppLog.Warning($"Oodle DLL could not be found or downloaded to \"{oodlePath}\". Compressed assets will fail to load.");
        }
        await InitializeProvider();
        await InitializeKeys();

        Provider.LoadVirtualPaths();

        Provider.TryCreateReader("ShooterGame/AssetRegistry.bin", out var assetArchive);
        if (assetArchive is not null)
        {
            AssetRegistry = new FAssetRegistryState(assetArchive);
            AssetDataBuffers.AddRange(AssetRegistry.PreallocatedAssetDataBuffers);
        }
        else
        {
            AppLog.Warning("AssetRegistry.bin could not be loaded, so the asset handler will not have registry data to initialize.");
        }
    }

    private async Task InitializeKeys()
    {
        var keyResponse = AppSettings.Current.AesResponse;
        var keyString = "0x4BE71AF2459CF83899EC9DC2CB60E22AC4B3047E0211034BBABE9D174C069DD6";
        await Provider.SubmitKeyAsync(Globals.ZERO_GUID, new FAesKey(keyString));
    }


    private async Task InitializeProvider()
    {
        switch (AppSettings.Current.InstallType)
        {
            case EInstallType.Local:
            {
                Provider.InitializeLocal();
                break;
            }
            case EInstallType.Live:
            {
                var manifestInfo = _apiEndpointView.ValorantApi.GetManifest(CancellationToken.None);
                if (manifestInfo == null)
                    throw new Exception(
                        "Could not load latest Valorant manifest, you may have to switch to your local installation.");
                for (var i = 0; i < manifestInfo.Paks.Length; i++)
                    Provider.Initialize(manifestInfo.Paks[i].GetFullName(), new[] { manifestInfo.GetPakStream(i) });
                break;
            }
        }
    }
}
