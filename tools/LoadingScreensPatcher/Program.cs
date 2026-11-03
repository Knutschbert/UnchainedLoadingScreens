using System.Text.Json;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

const string Usage = "Usage: LoadingScreensPatcher inspect --pak <path> [--asset <suffix>] [--out <path>] [--dump <dir>] | inspect-asset --asset-path <path> [--out <path>] | plan --marker-pak <path> --config-asset <path> [--out <path>] | merge --marker-pak <path> --config-asset <path> --out-dir <dir> [--pak-out <path>] [--dry-run]";

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine(Usage);
    return 0;
}

if (args[0] is not ("inspect" or "inspect-asset" or "plan" or "merge"))
{
    Console.Error.WriteLine($"Unknown command '{args[0]}'. {Usage}");
    return 2;
}

var pakPath = Option(args, "--pak");
var assetSuffix = Option(args, "--asset");
var assetPath = Option(args, "--asset-path");
var outputPath = Option(args, "--out");
var dumpDirectory = Option(args, "--dump");

if (args[0] == "plan")
{
    var markerPak = Option(args, "--marker-pak");
    var configAsset = Option(args, "--config-asset");
    if (markerPak is null || configAsset is null || !File.Exists(markerPak) || !File.Exists(configAsset))
    {
        Console.Error.WriteLine("plan requires existing --marker-pak and --config-asset paths.");
        return 2;
    }

    try
    {
        WriteJsonOutput(BuildPlan(markerPak, configAsset), outputPath);
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Planning failed: {exception.Message}");
        return 1;
    }
}

if (args[0] == "merge")
{
    var markerPak = Option(args, "--marker-pak");
    var configAsset = Option(args, "--config-asset");
    var outputDirectory = Option(args, "--out-dir");
    var pakOutput = Option(args, "--pak-out") ?? (outputDirectory is null ? null : Path.Combine(outputDirectory, "UnchainedLoadingScreens.pak"));
    var dryRun = args.Contains("--dry-run", StringComparer.Ordinal);
    if (markerPak is null || configAsset is null || outputDirectory is null || !File.Exists(markerPak) || !File.Exists(configAsset))
    {
        Console.Error.WriteLine("merge requires existing --marker-pak and --config-asset paths plus --out-dir.");
        return 2;
    }

    try
    {
        if (dryRun)
        {
            WriteJsonOutput(BuildPlan(markerPak, configAsset), Option(args, "--plan-out"));
            return 0;
        }
        var changed = MergeConfig(markerPak, configAsset, outputDirectory, pakOutput);
        Console.WriteLine($"Wrote {changed} loading-screen entries to {Path.GetFullPath(outputDirectory)}");
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Merge failed: {exception.Message}");
        return 1;
    }
}

if (args[0] == "inspect-asset")
{
    if (assetPath is null || !File.Exists(assetPath))
    {
        Console.Error.WriteLine("--asset-path must point to an existing .uasset file.");
        return 2;
    }

    try
    {
        var asset = new UAsset(assetPath, EngineVersion.VER_UE4_27);
        var json = asset.SerializeJson(true) + Environment.NewLine;
        if (outputPath is null)
            Console.Write(json);
        else
        {
            var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (outputDirectory is not null)
                Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(outputPath, json);
        }
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Inspection failed: {exception.Message}");
        return 1;
    }
}

if (pakPath is null || !File.Exists(pakPath))
{
    Console.Error.WriteLine("--pak must point to an existing pak file.");
    return 2;
}

try
{
    var report = InspectPak(pakPath, assetSuffix, dumpDirectory);
    var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });

    if (outputPath is null)
        Console.WriteLine(json);
    else
    {
        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (outputDirectory is not null)
            Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(outputPath, json + Environment.NewLine);
    }

    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Inspection failed: {exception.Message}");
    return 1;
}

static object InspectPak(string pakPath, string? assetSuffix, string? dumpDirectory)
{
    using var pakStream = File.OpenRead(pakPath);
    using var pakBuilder = new PakBuilder();
    using var pak = pakBuilder.Reader(pakStream);

    var files = pak.Files().Order(StringComparer.OrdinalIgnoreCase).ToArray();
    var markerPaths = FindMarkerPaths(pak, pakStream, files, assetSuffix);

    var markers = markerPaths.Select(path => InspectMarker(pak, pakStream, files, path, dumpDirectory)).ToArray();

    return new
    {
        pak = Path.GetFullPath(pakPath),
        mountPoint = pak.GetMountPoint(),
        pakVersion = pak.GetVersion().ToString(),
        fileCount = files.Length,
        files,
        mapAssets = files.Where(IsMapAsset).ToArray(),
        markerPaths,
        markers,
    };
}

static object BuildPlan(string markerPakPath, string configAssetPath)
{
    var markerReport = JsonSerializer.SerializeToElement(InspectPak(markerPakPath, null, null));
    var config = new UAsset(configAssetPath, EngineVersion.VER_UE4_27);
    var configExport = config.Exports.OfType<NormalExport>().FirstOrDefault(export => export["Configs"] is ArrayPropertyData)
        ?? throw new InvalidDataException("No Configs array was found in the LoadingScreensConfig asset.");
    var configs = (ArrayPropertyData)configExport["Configs"]!;
    var currentMaps = configs.Value.OfType<StructPropertyData>()
        .Select(item => GetString(item["MapName"]))
        .Where(name => name is not null)
        .Cast<string>()
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    var entries = new List<PlanEntry>();
    foreach (var marker in markerReport.GetProperty("markers").EnumerateArray())
    {
        var markerPath = marker.GetProperty("path").GetString() ?? "";
        foreach (var entry in marker.GetProperty("validation").GetProperty("entries").EnumerateArray())
        {
            var mapName = entry.GetProperty("categoryName").GetString();
            var accepted = entry.GetProperty("accepted").GetBoolean();
            entries.Add(new PlanEntry(
                markerPath,
                mapName,
                entry.GetProperty("description").GetString(),
                entry.GetProperty("image").GetString(),
                entry.GetProperty("imageSource").GetString(),
                entry.GetProperty("imageStatus").GetString(),
                accepted ? currentMaps.Contains(mapName ?? "") ? "update" : "add" : "ignore",
                accepted ? null : entry.GetProperty("ignoredReason").GetString()));
        }
    }

    return new
    {
        markerPak = Path.GetFullPath(markerPakPath),
        configAsset = Path.GetFullPath(configAssetPath),
        currentConfigCount = currentMaps.Count,
        markerCount = markerReport.GetProperty("markers").GetArrayLength(),
        entries = entries
            .OrderBy(entry => entry.mapName ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.markerPath, StringComparer.OrdinalIgnoreCase)
            .ToArray(),
        addCount = entries.Count(entry => entry.action == "add"),
        updateCount = entries.Count(entry => entry.action == "update"),
        ignoredCount = entries.Count(entry => entry.action == "ignore"),
    };
}

static void WriteJsonOutput(object value, string? outputPath)
{
    var json = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
    if (outputPath is null)
        Console.Write(json);
    else
    {
        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (outputDirectory is not null)
            Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(outputPath, json);
    }
}

static object InspectMarker(PakReader pak, Stream pakStream, IReadOnlyCollection<string> files, string markerPath, string? dumpDirectory)
{
    var markerDirectory = Path.GetDirectoryName(markerPath)?.Replace('\\', '/') ?? string.Empty;
    var markerName = Path.GetFileNameWithoutExtension(markerPath);
    var sidecarPath = Path.Combine(Path.GetDirectoryName(Path.GetTempFileName())!, $"{Guid.NewGuid():N}", markerName + ".uasset");
    var sidecarDirectory = Path.GetDirectoryName(sidecarPath)!;
    Directory.CreateDirectory(sidecarDirectory);

    try
    {
        WritePakFile(pak, pakStream, markerPath, sidecarPath);

        var markerUexp = CombinePakPath(markerDirectory, markerName + ".uexp");
        if (files.Contains(markerUexp, StringComparer.OrdinalIgnoreCase))
            WritePakFile(pak, pakStream, markerUexp, Path.ChangeExtension(sidecarPath, ".uexp"));

        var asset = new UAsset(sidecarPath, EngineVersion.VER_UE4_27);
        string? dumpPath = null;
        if (dumpDirectory is not null)
        {
            Directory.CreateDirectory(dumpDirectory);
            dumpPath = Path.Combine(dumpDirectory, markerName + ".json");
            File.WriteAllText(dumpPath, asset.SerializeJson(true) + Environment.NewLine);
        }

        var exports = asset.Exports.Select((export, index) => new
        {
            index,
            name = export.ObjectName.ToString(),
            className = export.GetExportClassType().ToString(),
            isClassDefaultObject = export.ObjectFlags.HasFlag(EObjectFlags.RF_ClassDefaultObject),
            properties = export is NormalExport normal
                ? normal.Data.Select(property => new
                {
                    name = property.Name.ToString(),
                    type = property.GetType().Name,
                }).ToArray()
                : Array.Empty<object>(),
        }).ToArray();

        var dataExport = asset.Exports.OfType<NormalExport>().SingleOrDefault();
        object validation;
        if (dataExport is null)
            validation = new { error = "No normal data export was found.", entries = Array.Empty<object>() };
        else
            validation = ValidateMarker(dataExport, files);

        return new { path = markerPath, asset = Path.GetFileName(sidecarPath), dump = dumpPath, exports, validation };
    }
    finally
    {
        Directory.Delete(sidecarDirectory, recursive: true);
    }
}

static void WritePakFile(PakReader pak, Stream pakStream, string pakPath, string outputPath)
{
    pakStream.Position = 0;
    var bytes = pak.Get(pakStream, pakPath) ?? throw new InvalidDataException($"Pak entry not found: {pakPath}");
    File.WriteAllBytes(outputPath, bytes);
}

static string[] FindMarkerPaths(PakReader pak, Stream pakStream, IReadOnlyCollection<string> files, string? suffix = null)
{
    var candidates = files.Where(path =>
        path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) &&
        (suffix is null || path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)));

    return candidates.Where(path => IsMapInfoMarker(pak, pakStream, files, path)).ToArray();
}

static bool IsMapInfoMarker(PakReader pak, Stream pakStream, IReadOnlyCollection<string> files, string assetPath)
{
    var directory = Path.Combine(Path.GetTempPath(), "unchained-loading-screens", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var assetFile = Path.Combine(directory, "candidate.uasset");
        WritePakFile(pak, pakStream, assetPath, assetFile);
        var uexpPath = CombinePakPath(Path.GetDirectoryName(assetPath)?.Replace('\\', '/') ?? string.Empty,
            Path.GetFileNameWithoutExtension(assetPath) + ".uexp");
        if (files.Contains(uexpPath, StringComparer.OrdinalIgnoreCase))
            WritePakFile(pak, pakStream, uexpPath, Path.ChangeExtension(assetFile, ".uexp"));

        var asset = new UAsset(assetFile, EngineVersion.VER_UE4_27);
        return asset.Exports.Any(export =>
            export.GetExportClassType().ToString().Equals("DA_MapInfo_C", StringComparison.OrdinalIgnoreCase) &&
            export is NormalExport normal &&
            normal["VariantDescriptions"] is ArrayPropertyData &&
            normal["DefaultImage"] is SoftObjectPropertyData);
    }
    catch (Exception)
    {
        // A pak may contain cooked packages that this parser/version cannot read.
        // They are not marker candidates, so continue scanning.
        return false;
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static bool IsMapAsset(string path) => path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase);

static object ValidateMarker(NormalExport marker, IReadOnlyCollection<string> files)
{
    var maps = files
        .Where(IsMapAsset)
        .Select(path => Path.GetFileNameWithoutExtension(path))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    var defaultImage = GetSoftObjectPath(marker["DefaultImage"]);
    var imageProperties = marker.Data
        .Where(property => property.Name.ToString().Contains("image", StringComparison.OrdinalIgnoreCase))
        .Select(property => property.Name.ToString())
        .Where(name => !name.Equals("DefaultImage", StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    var descriptions = marker["VariantDescriptions"] as ArrayPropertyData;
    if (descriptions is null)
        return new { error = "VariantDescriptions is missing or is not an array.", defaultImage, entries = Array.Empty<object>() };

    var entries = descriptions.Value.Select(item =>
    {
        var tuple = item as StructPropertyData;
        var category = GetText(tuple?["CategoryName"]);
        var description = GetText(tuple?["SubcategoryName"]);
        string? imageProperty = null;
        var variantImage = category is null ? null : FindVariantImage(marker, category, out imageProperty);
        var image = variantImage ?? defaultImage;
        var mapFound = category is not null && maps.Contains(category);
        var imageCheck = ValidateImageReference(image, files);
        return new
        {
            categoryName = category,
            description,
            image,
            imageSource = variantImage is null ? "DefaultImage" : $"variant:{imageProperty}",
            imageStatus = imageCheck.status,
            mapFound,
            accepted = mapFound && imageCheck.accepted,
            ignoredReason = !mapFound
                ? "No matching .umap in source pak"
                : imageCheck.accepted ? null : imageCheck.reason,
        };
    }).ToArray();

    return new
    {
        defaultImage,
        imageProperties,
        imageHandling = imageProperties.Length == 0
            ? "default-only"
            : "optional-variant-properties-detected",
        mapAssets = maps.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
        entries,
        acceptedCount = entries.Count(entry => entry.accepted),
        ignoredCount = entries.Count(entry => !entry.accepted),
    };
}

static int MergeConfig(string markerPakPath, string configAssetPath, string outputDirectory, string? pakOutput)
{
    var entries = ReadAcceptedEntries(markerPakPath);
    if (entries.Count == 0)
        return 0;

    var config = new UAsset(configAssetPath, EngineVersion.VER_UE4_27);
    var configExport = config.Exports.OfType<NormalExport>().FirstOrDefault(export => export["Configs"] is ArrayPropertyData)
        ?? throw new InvalidDataException("No Configs array was found in the LoadingScreensConfig asset.");
    var configs = (ArrayPropertyData)configExport["Configs"]!;
    var template = configs.Value.OfType<StructPropertyData>().FirstOrDefault()
        ?? throw new InvalidDataException("Configs array is empty; cannot construct a new entry template.");

    foreach (var entry in entries)
    {
        var existing = configs.Value.OfType<StructPropertyData>().FirstOrDefault(item =>
            GetString(item["MapName"])?.Equals(entry.MapName, StringComparison.OrdinalIgnoreCase) == true);
        var target = existing ?? (StructPropertyData)template.Clone();
        SetString(target["MapName"] ?? throw new InvalidDataException("Config entry has no MapName property."), entry.MapName!);
        SetSoftObjectPath(target["Texture"] ?? throw new InvalidDataException("Config entry has no Texture property."), entry.Image, config);

        if (existing is null)
        {
            target.Name = configs.Name;
            configs.Value = configs.Value.Append(target).ToArray();
        }
    }

    Directory.CreateDirectory(outputDirectory);
    var outputAsset = Path.Combine(outputDirectory, Path.GetFileName(configAssetPath));
    config.Write(outputAsset);
    if (pakOutput is not null)
        WriteReplacementPak(outputDirectory, Path.GetFileName(outputAsset), pakOutput);
    return entries.Count;
}

static void WriteReplacementPak(string outputDirectory, string assetFileName, string pakOutput)
{
    var outputPath = Path.GetFullPath(pakOutput);
    var outputParent = Path.GetDirectoryName(outputPath);
    if (outputParent is not null)
        Directory.CreateDirectory(outputParent);

    using var pakStream = File.Create(outputPath);
    using var pakBuilder = new PakBuilder();
    using var writer = pakBuilder.Writer(pakStream, PakVersion.V9, "../../../TBL/Content/");
    const string virtualDirectory = "Mods/ArgonSDK/Mods/ModLoading/_Content/Config";
    var assetPath = Path.Combine(outputDirectory, assetFileName);
    writer.WriteFile($"{virtualDirectory}/{assetFileName}", File.ReadAllBytes(assetPath));

    var uexpPath = Path.ChangeExtension(assetPath, ".uexp");
    if (File.Exists(uexpPath))
        writer.WriteFile($"{virtualDirectory}/{Path.GetFileName(uexpPath)}", File.ReadAllBytes(uexpPath));
    writer.WriteIndex();
}

static List<LoadingScreenEntry> ReadAcceptedEntries(string markerPakPath)
{
    using var pakStream = File.OpenRead(markerPakPath);
    using var pakBuilder = new PakBuilder();
    using var pak = pakBuilder.Reader(pakStream);
    var files = pak.Files();
    var markerPaths = FindMarkerPaths(pak, pakStream, files);
    if (markerPaths.Length == 0)
        throw new InvalidDataException("No DA_MapInfo marker asset was found in the pak.");

    var directory = Path.Combine(Path.GetTempPath(), "unchained-loading-screens", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var maps = files.Where(IsMapAsset).Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var accepted = new List<LoadingScreenEntry>();
        foreach (var markerPath in markerPaths)
        {
            var markerFile = Path.Combine(directory, $"{Guid.NewGuid():N}.uasset");
            WritePakFile(pak, pakStream, markerPath, markerFile);
            var markerUexp = Path.ChangeExtension(markerPath, ".uexp");
            if (files.Contains(markerUexp, StringComparer.OrdinalIgnoreCase))
                WritePakFile(pak, pakStream, markerUexp, Path.ChangeExtension(markerFile, ".uexp"));

            var marker = new UAsset(markerFile, EngineVersion.VER_UE4_27);
            var export = marker.Exports.OfType<NormalExport>().Single(normal =>
                normal["VariantDescriptions"] is ArrayPropertyData && normal["DefaultImage"] is SoftObjectPropertyData);
            var descriptions = (ArrayPropertyData)export["VariantDescriptions"]!;
            var defaultImage = GetSoftObjectPath(export["DefaultImage"])
                ?? throw new InvalidDataException($"Marker {markerPath} has no DefaultImage reference.");

            accepted.AddRange(descriptions.Value.OfType<StructPropertyData>()
                .Select(item =>
                {
                    var mapName = GetText(item["CategoryName"]);
                    var image = mapName is null ? null : FindVariantImage(export, mapName, out _);
                    return new LoadingScreenEntry(mapName, GetText(item["SubcategoryName"]), image ?? defaultImage,
                        mapName is not null && maps.Contains(mapName), ValidateImageReference(image ?? defaultImage, files).accepted);
                })
                .Where(entry => entry.MapFound && entry.ImageValid));
        }
        return accepted;
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static string? GetString(PropertyData property) => property switch
{
    StrPropertyData str => str.Value?.Value,
    _ => null,
};

static void SetString(PropertyData property, string value)
{
    if (property is not StrPropertyData str)
        throw new InvalidDataException($"Expected string property, got {property.GetType().Name}.");
    str.Value = FString.FromString(value);
}

static void SetSoftObjectPath(PropertyData property, string path, UAsset asset)
{
    if (property is not StructPropertyData texture || texture.Value.OfType<SoftObjectPathPropertyData>().FirstOrDefault() is not { } softPath)
        throw new InvalidDataException("Expected Texture to be a SoftObjectPath struct.");
    softPath.Value = new FSoftObjectPath(new FTopLevelAssetPath(null, FName.FromString(asset, path)), null);
}

static string? GetText(PropertyData? property)
{
    return property is TextPropertyData text
        ? text.CultureInvariantString?.Value ?? text.Value?.Value
        : null;
}

static string? GetSoftObjectPath(PropertyData? property)
{
    if (property is SoftObjectPropertyData softObject && softObject.Value.AssetPath.AssetName is not null)
        return softObject.Value.AssetPath.AssetName.ToString();

    if (property is StructPropertyData structProperty)
    {
        var nested = structProperty.Value.OfType<SoftObjectPathPropertyData>().FirstOrDefault();
        if (nested?.Value.AssetPath.AssetName is not null)
            return nested.Value.AssetPath.AssetName.ToString();
    }

    return null;
}

static string? FindVariantImage(NormalExport marker, string categoryName, out string? propertyName)
{
    propertyName = null;
    foreach (var property in marker.Data.Where(property =>
                 property.Name.ToString().Contains("image", StringComparison.OrdinalIgnoreCase) &&
                 !property.Name.ToString().Equals("DefaultImage", StringComparison.OrdinalIgnoreCase)))
    {
        propertyName = property.Name.ToString();
        if (property is MapPropertyData map)
        {
            foreach (var pair in map.Value)
            {
                if (GetString(pair.Key)?.Equals(categoryName, StringComparison.OrdinalIgnoreCase) == true ||
                    GetText(pair.Key)?.Equals(categoryName, StringComparison.OrdinalIgnoreCase) == true)
                    return GetSoftObjectPath(pair.Value);
            }
        }

        if (property is ArrayPropertyData array)
        {
            foreach (var item in array.Value.OfType<StructPropertyData>())
            {
                var itemCategory = GetString(item["CategoryName"]) ?? GetText(item["CategoryName"])
                    ?? GetString(item["MapName"]) ?? GetText(item["MapName"])
                    ?? GetString(item["VariantName"]) ?? GetText(item["VariantName"]);
                if (itemCategory is null || !itemCategory.Equals(categoryName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var imageProperty = item.Value.FirstOrDefault(child =>
                    child.Name.ToString().Contains("image", StringComparison.OrdinalIgnoreCase) ||
                    child.Name.ToString().Contains("texture", StringComparison.OrdinalIgnoreCase));
                return GetSoftObjectPath(imageProperty);
            }
        }
    }

    propertyName = null;
    return null;
}

static (bool accepted, string status, string? reason) ValidateImageReference(string? image, IReadOnlyCollection<string> files)
{
    if (string.IsNullOrWhiteSpace(image))
        return (false, "missing", "No image reference");
    if (!image.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase))
        return (true, "external-reference", null);

    var packagePath = image.Split('.', 2)[0]["/Game/".Length..] + ".uasset";
    var present = files.Any(file => file.Replace('\\', '/').EndsWith(packagePath, StringComparison.OrdinalIgnoreCase));
    return present
        ? (true, "in-mod-present", null)
        : (false, "in-mod-missing", $"In-mod image is not present in source pak: {image}");
}

static string CombinePakPath(string directory, string fileName) =>
    string.IsNullOrEmpty(directory) ? fileName : $"{directory.TrimEnd('/')}/{fileName}";

static string? Option(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

record LoadingScreenEntry(string? MapName, string? Description, string Image, bool MapFound, bool ImageValid);

record PlanEntry(
    string markerPath,
    string? mapName,
    string? description,
    string? image,
    string? imageSource,
    string? imageStatus,
    string action,
    string? ignoredReason);
