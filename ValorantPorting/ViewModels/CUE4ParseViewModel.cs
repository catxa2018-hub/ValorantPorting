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
    // Valorant moved to Unreal Engine 5.3 with patch 11.02 (July 2025).
    // If your CUE4Parse fork's EGame.GAME_Valorant entry has already been
    // updated upstream to point at the UE5.3 feature set, you can switch
    // this back to `new(EGame.GAME_Valorant)`. Until then, pin it explicitly
    // so the serializer versioning matches the current client.
    public static readonly VersionContainer Version = new(EGame.GAME_UE5_3);

    // Path to the .usmap you extracted for the current client build.
    // UE5 strips reflection data from the paks, so CUE4Parse can no longer
    // infer struct layouts on its own the way it could under UE4 - it needs
    // this file to know how to walk each UStruct's properties.
    private static readonly string MappingsPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Mappings", "VALORANT_13_00_zs.usmap");
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
            AppLog.Warning(
                $"Mappings file not found at \"{MappingsPath}\". UE5 Valorant assets will fail to parse without it.");
        }
        else
        {
            Provider.MappingsContainer = new FileUsmapTypeMappingsProvider(MappingsPath);
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