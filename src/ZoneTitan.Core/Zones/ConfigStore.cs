using System.Text.Json;

namespace ZoneTitan.Core.Zones;

/// <summary>Reads and writes ZoneTitan's on-disk state under %APPDATA%\ZoneTitan.</summary>
public static class ConfigStore
{
    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZoneTitan");

    public static string ConfigPath => Path.Combine(RootDirectory, "config.json");
    public static string SnapshotDirectory => Path.Combine(RootDirectory, "snapshots");
    public static string LogDirectory => Path.Combine(RootDirectory, "logs");

    /// <summary>The rolling last-known-good topology, written before every apply.</summary>
    public static string LastKnownGoodPath => Path.Combine(SnapshotDirectory, "_last-known-good.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(SnapshotDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    public static bool ConfigExists => File.Exists(ConfigPath);

    public static ZoneTitanConfig LoadConfig()
    {
        if (!File.Exists(ConfigPath))
        {
            throw new FileNotFoundException(
                $"No config found at {ConfigPath}. Run 'zonetitan --init' to generate one " +
                "from your current display layout.", ConfigPath);
        }

        var json = File.ReadAllText(ConfigPath);
        var config = JsonSerializer.Deserialize<ZoneTitanConfig>(json, Options)
                     ?? throw new InvalidDataException($"Config at {ConfigPath} is empty or invalid.");

        return config;
    }

    public static void SaveConfig(ZoneTitanConfig config)
    {
        EnsureDirectories();
        WriteAtomic(ConfigPath, JsonSerializer.Serialize(config, Options));
    }

    private static string SnapshotPath(string zoneName)
    {
        var safe = string.Concat(zoneName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(SnapshotDirectory, $"{safe}.json");
    }

    public static ZoneSnapshot? LoadSnapshot(string zoneName) => LoadSnapshotFile(SnapshotPath(zoneName));

    public static ZoneSnapshot? LoadLastKnownGood() => LoadSnapshotFile(LastKnownGoodPath);

    private static ZoneSnapshot? LoadSnapshotFile(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ZoneSnapshot>(File.ReadAllText(path), Options);
        }
        catch (JsonException)
        {
            // A corrupt snapshot must never be fatal — the engine rebuilds from scratch.
            return null;
        }
    }

    public static void SaveSnapshot(ZoneSnapshot snapshot)
    {
        EnsureDirectories();
        WriteAtomic(SnapshotPath(snapshot.Zone), JsonSerializer.Serialize(snapshot, Options));
    }

    public static void SaveLastKnownGood(ZoneSnapshot snapshot)
    {
        EnsureDirectories();
        WriteAtomic(LastKnownGoodPath, JsonSerializer.Serialize(snapshot, Options));
    }

    /// <summary>Write via a temp file and replace, so a crash mid-write cannot corrupt state.</summary>
    private static void WriteAtomic(string path, string contents)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, path, overwrite: true);
    }
}
