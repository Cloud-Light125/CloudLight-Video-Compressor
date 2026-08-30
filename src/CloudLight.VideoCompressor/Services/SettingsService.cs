using System.Text.Json;
using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

public sealed class SettingsService
{
    private readonly string _settingsPath;
    private readonly string? _legacySettingsPath;

    public SettingsService(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "CloudLight",
            "CloudLight Video Compressor",
            "settings.json");
        _legacySettingsPath = settingsPath is null
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CloudLight Video Compressor",
                "settings.json")
            : null;
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        MigrateLegacySettingsIfNeeded();

        if (!File.Exists(_settingsPath))
        {
            return new AppSettings();
        }

        try
        {
            var json = await File.ReadAllTextAsync(_settingsPath, cancellationToken);
            using var document = JsonDocument.Parse(json);
            var settings = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings);
            var hasExplicitProfile = document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(nameof(AppSettings.CompressionProfile), out _);
            return SettingsMigration.Normalize(settings, hasExplicitProfile);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        settings = SettingsMigration.Normalize(settings);
        var directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _settingsPath + ".tmp";
        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, settings, SettingsJsonContext.Default.AppSettings, cancellationToken);
            }

            File.Move(temporaryPath, _settingsPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private void MigrateLegacySettingsIfNeeded()
    {
        if (string.IsNullOrWhiteSpace(_legacySettingsPath) ||
            File.Exists(_settingsPath) ||
            !File.Exists(_legacySettingsPath))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.Copy(_legacySettingsPath, _settingsPath, overwrite: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Falling back to defaults is safer than preventing the app from starting if a legacy file cannot be copied.
        }
    }
}
