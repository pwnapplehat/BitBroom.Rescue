using System.IO;
using System.Text.Json;

namespace BitBroom.Rescue.App.Services;

/// <summary>
/// Tiny persisted settings for the portable app — currently just the opt-out for the
/// update check. Stored as JSON under %LocalAppData%\BitBroomRescue so it survives across
/// runs of the portable exe without needing an installer or registry keys.
/// </summary>
public static class RescueSettings
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BitBroomRescue");

    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    /// <summary>When true (default), the app does one opt-out check for a newer release at startup.</summary>
    public static bool UpdateCheckEnabled { get; set; } = true;

    static RescueSettings() => Load();

    private sealed class Model
    {
        public bool UpdateCheckEnabled { get; set; } = true;
    }

    private static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                Model? m = JsonSerializer.Deserialize<Model>(File.ReadAllText(FilePath));
                if (m is not null)
                {
                    UpdateCheckEnabled = m.UpdateCheckEnabled;
                }
            }
        }
        catch
        {
            // Corrupt/inaccessible settings must never stop the app — keep defaults.
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Model { UpdateCheckEnabled = UpdateCheckEnabled }));
        }
        catch
        {
            // Best effort — a failed save just means we ask again next launch.
        }
    }
}
