using System.Text.Json;

namespace Livereon.Modules.PrintBooth;

/// <summary>
/// Réglages du jeu « Print Booth » : quelles actions TikTok déclenchent
/// une impression, à quels seuils, et l'apparence du papier.
/// </summary>
public sealed class PrintBoothSettings
{
    public int SchemaVersion { get; set; } = 1;

    // ---------------------------------------------------------------- décor
    public string HeaderTitle { get; set; } = "PRINT BOOTH";

    public string HeaderSubtitle { get; set; } = "PENCIL PRINTS";

    public bool ShowHeader { get; set; } = true;

    public bool ShowLiveBadge { get; set; } = true;

    public string PaperTone { get; set; } = "Ivoire";

    public bool ShowCaption { get; set; }

    public string WoodTone { get; set; } = "Miel";

    // ----------------------------------------------------------- déclencheurs
    public bool PrintOnGift { get; set; } = true;

    /// <summary>Nombre minimum de diamants pour imprimer un cadeau (0 = tous).</summary>
    public int MinGiftDiamonds { get; set; }

    public bool PrintOnLike { get; set; } = true;

    /// <summary>Nombre de likes nécessaires pour une impression.</summary>
    public int LikesPerPrint { get; set; } = 50;

    public bool PrintOnComment { get; set; } = true;

    public int CommentsPerPrint { get; set; } = 1;

    public bool PrintOnShare { get; set; } = true;

    public int SharesPerPrint { get; set; } = 1;

    // ------------------------------------------------------------ impression
    /// <summary>Durée d'impression d'une carte, en secondes.</summary>
    public double PrintDurationSeconds { get; set; } = 2.4;

    /// <summary>Nombre maximum de cartes en attente (file d'impression).</summary>
    public int MaxPending { get; set; } = 24;

    public PrintBoothSettings Clone() => JsonSerializer.Deserialize<PrintBoothSettings>(JsonSerializer.Serialize(this))!;
}

public sealed class PrintBoothSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public PrintBoothSettingsStore(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Livereon", "print-booth.json");

    public PrintBoothSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return Normalize(JsonSerializer.Deserialize<PrintBoothSettings>(File.ReadAllText(_path), JsonOptions) ?? new());
        }
        catch { }
        return Normalize(new());
    }

    public void Save(PrintBoothSettings settings)
    {
        PrintBoothSettings normalized = Normalize(settings.Clone());
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        string temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(normalized, JsonOptions));
        File.Move(temporary, _path, true);
    }

    private static PrintBoothSettings Normalize(PrintBoothSettings settings)
    {
        settings.SchemaVersion = 1;
        settings.HeaderTitle = string.IsNullOrWhiteSpace(settings.HeaderTitle) ? "PRINT BOOTH" : settings.HeaderTitle.Trim();
        settings.HeaderSubtitle = settings.HeaderSubtitle?.Trim() ?? string.Empty;
        settings.PaperTone = settings.PaperTone is "Blanc" or "Kraft" ? settings.PaperTone : "Ivoire";
        settings.WoodTone = settings.WoodTone is "Clair" or "Foncé" ? settings.WoodTone : "Miel";
        settings.MinGiftDiamonds = Math.Clamp(settings.MinGiftDiamonds, 0, 10_000_000);
        settings.LikesPerPrint = Math.Clamp(settings.LikesPerPrint, 1, 1_000_000);
        settings.CommentsPerPrint = Math.Clamp(settings.CommentsPerPrint, 1, 10_000);
        settings.SharesPerPrint = Math.Clamp(settings.SharesPerPrint, 1, 10_000);
        settings.PrintDurationSeconds = Math.Clamp(settings.PrintDurationSeconds, .6, 8);
        settings.MaxPending = Math.Clamp(settings.MaxPending, 1, 200);
        return settings;
    }
}
