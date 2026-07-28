using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CUE4Parse.UE4.AssetRegistry.Objects;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Engine;
using ValorantPorting.AppUtils;
using ValorantPorting.Views.Controls;

namespace ValorantPorting.ViewModels;

public class AssetHandlerViewModel
{
    private readonly AssetHandlerData _buddyHandler = new()
    {
        AssetType = EAssetType.GunBuddy,
        TargetCollection = AppVM.MainVM.Gunbuddies,
        ClassNames = new List<string> { "EquippableCharmDataAsset" },
        IconGetter = UI_Asset =>
        {
            UI_Asset.TryGetValue(out UTexture2D? previewImage, "DisplayIcon");
            return previewImage;
        }
    };

    private readonly AssetHandlerData _characterHandler = new()
    {
        AssetType = EAssetType.Character,
        TargetCollection = AppVM.MainVM.Outfits,
        ClassNames = new List<string> { "CharacterDataAsset" },
        IconGetter = UI_Asset =>
        {
            UI_Asset.TryGetValue(out UTexture2D? previewImage, "DisplayIcon");
            return previewImage;
        }
    };

    private readonly AssetHandlerData _weaponHandler = new()
    {
        AssetType = EAssetType.Weapon,
        TargetCollection = AppVM.MainVM.Weapons,
        ClassNames = new List<string> { "EquippableSkinDataAsset" },
        IconGetter = UI_Asset =>
        {
            UI_Asset.TryGetValue(out UTexture2D? previewImage, "DisplayIcon");
            return previewImage;
        }
    };

    public readonly Dictionary<EAssetType, AssetHandlerData> Handlers;


    public AssetHandlerViewModel()
    {
        Handlers = new Dictionary<EAssetType, AssetHandlerData>
        {
            { EAssetType.Character, _characterHandler },
            { EAssetType.Weapon, _weaponHandler },
            { EAssetType.GunBuddy, _buddyHandler },
        };
    }

    public async Task Initialize()
    {
        await _characterHandler.Execute(); // default tab
    }
}

public class AssetHandlerData
{
    public EAssetType AssetType;
    public List<string> ClassNames;
    public Func<UObject, UTexture2D?> IconGetter;
    public ObservableCollection<AssetSelectorItem> TargetCollection;
    public bool HasStarted { get; private set; }
    public Pauser PauseState { get; } = new();

    public async Task Execute()
    {
        if (HasStarted) return;
        HasStarted = true;

        var cue4ParseVm = AppVM.CUE4ParseVM;
        if (cue4ParseVm is null || cue4ParseVm.AssetDataBuffers is null || cue4ParseVm.AssetDataBuffers.Count == 0)
        {
            AppLog.Warning("Asset handler could not initialize because no asset data buffers were available.");
            return;
        }

        if (TargetCollection is null || ClassNames is null || IconGetter is null)
        {
            AppLog.Warning("Asset handler could not initialize because one or more required configuration values were missing.");
            return;
        }

        var items = new List<FAssetData>();
        foreach (var variable in cue4ParseVm.AssetDataBuffers)
        {
            if (variable is null || variable.TagsAndValues is null) continue;

            foreach (var tagsAndValue in variable.TagsAndValues)
            {
                if (ClassNames.Contains(tagsAndValue.Value) && tagsAndValue.Key?.PlainText == "PrimaryAssetType")
                    items.Add(variable);
            }
        }

        await Parallel.ForEachAsync(items, async (data, token) => //load if found
        {
            await DoLoad(data);
        });
    }

    private async Task DoLoad(FAssetData data, bool random = false)
    {
        await PauseState.WaitIfPaused();
        var actualAsset = new UObject();
        var uiAsset = new UObject();
        var firstTag = data.ObjectPath;

        if (firstTag.Contains("NPE") || firstTag.Contains("Random")) return;

        actualAsset = await AppVM.CUE4ParseVM.Provider.TryLoadObjectAsync(firstTag);
        if (actualAsset == null) return;

        if (actualAsset is not UBlueprintGeneratedClass uBlueprintGeneratedClass)
            return;

        var classDefaultObject = uBlueprintGeneratedClass.ClassDefaultObject?.Load();
        if (classDefaultObject == null) return;

        actualAsset = classDefaultObject;
        var mainA = actualAsset;

        if (actualAsset.TryGetValue(out UBlueprintGeneratedClass? uiObject, "UIData"))
        {
            var uiDefaultObject = uiObject?.ClassDefaultObject?.Load();
            if (uiDefaultObject != null)
                uiAsset = uiDefaultObject;
        }

        // switch on asset type
        var loadable = "None";
        switch (AssetType)
        {
            case EAssetType.Character:
                loadable = "Character";
                break;
            case EAssetType.Weapon:
            {
                if (actualAsset.TryGetValue<UBlueprintGeneratedClass[]>(out var bGg, "Levels") &&
                    bGg is { Length: > 0 } &&
                    bGg[0]?.ClassDefaultObject?.Load() is { } weaponDefaultObject)
                {
                    actualAsset = weaponDefaultObject;
                }
                else
                {
                    return;
                }

                loadable = "None";
                break;
            }
            case EAssetType.GunBuddy:
            {
                if (actualAsset.TryGetValue<UBlueprintGeneratedClass[]>(out var bGb, "Levels") &&
                    bGb is { Length: > 0 } &&
                    bGb[0]?.ClassDefaultObject?.Load() is { } buddyDefaultObject)
                {
                    actualAsset = buddyDefaultObject;
                }
                else
                {
                    return;
                }

                loadable = "CharmAttachment";
                break;
            }
        }

        if (actualAsset.TryGetValue(out UBlueprintGeneratedClass? blueprintObject, loadable))
        {
            var blueprintDefaultObject = blueprintObject?.ClassDefaultObject?.Load();
            if (blueprintDefaultObject != null)
                actualAsset = blueprintDefaultObject;
            else
                return;
        }
        else
        {
            return;
        }

        var previewImage = IconGetter(uiAsset);
        if (previewImage is null) return;
        await Application.Current.Dispatcher.InvokeAsync(
            () => TargetCollection.Add(new AssetSelectorItem(actualAsset, uiAsset, mainA, previewImage, random)),
            DispatcherPriority.Background);
    }
}