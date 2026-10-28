using System;
using System.IO;
using System.Text.Json;

namespace OmniSync.Services;

/// <summary>
/// Preferências do usuário persistidas entre sessões.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Último diretório de Origem.</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>Último diretório de Destino.</summary>
    public string DestinationPath { get; set; } = string.Empty;

    /// <summary>Usar subpasta com o nome da origem no destino.</summary>
    public bool UseSourceFolderNameInDestination { get; set; } = true;

    /// <summary>Comparar por SHA-256.</summary>
    public bool EnableDeepHashCheck { get; set; }

    /// <summary>Tolerar diferença exata de 1 h (horário de verão em FAT/exFAT).</summary>
    public bool TolerateDstShift { get; set; }

    /// <summary>Padrões de exclusão separados por ponto e vírgula.</summary>
    public string ExcludePatterns { get; set; } = SettingsService.DefaultExcludePatterns;
}

/// <summary>
/// Carrega e grava <see cref="AppSettings"/> em JSON na pasta de dados do usuário.
/// </summary>
public static class SettingsService
{
    /// <summary>
    /// Exclusões padrão: arquivos de miniatura/sistema e temporários de bloqueio do Office.
    /// </summary>
    public const string DefaultExcludePatterns = "Thumbs.db; desktop.ini; .DS_Store; ~$*";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Lê as configurações salvas; em caso de ausência ou arquivo inválido, retorna os padrões.
    /// </summary>
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            FileLogService.Write($"[AVISO] Configurações ignoradas (arquivo inválido ou inacessível): {ex.Message}");
        }
        return new AppSettings();
    }

    /// <summary>
    /// Grava as configurações de forma atômica (arquivo temporário + renomeação).
    /// </summary>
    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            var temp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, AppPaths.SettingsFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLogService.Write($"[AVISO] Não foi possível salvar as configurações: {ex.Message}");
        }
    }
}
