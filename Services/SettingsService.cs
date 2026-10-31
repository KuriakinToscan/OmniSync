using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using OmniSync.Models;

namespace OmniSync.Services;

/// <summary>
/// Preferências do usuário persistidas entre sessões.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Tarefas de sincronização salvas.</summary>
    public List<SyncProfile> Profiles { get; set; } = new();

    /// <summary>Tarefa selecionada na última sessão.</summary>
    public Guid? ActiveProfileId { get; set; }

    /// <summary>
    /// O usuário marcou "Não perguntar de novo" na oferta de instalar o OmniSync na pasta fixa.
    /// </summary>
    public bool InstallOfferDismissed { get; set; }

    /// <summary>Último diretório de Origem (formato 1.1, migrado para <see cref="Profiles"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourcePath { get; set; }

    /// <summary>Último diretório de Destino (formato 1.1, migrado para <see cref="Profiles"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DestinationPath { get; set; }

    /// <summary>Usar subpasta com o nome da origem (formato 1.1).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? UseSourceFolderNameInDestination { get; set; }

    /// <summary>Comparar por SHA-256 (formato 1.1).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EnableDeepHashCheck { get; set; }

    /// <summary>Tolerar 1 h (formato 1.1).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? TolerateDstShift { get; set; }

    /// <summary>Padrões de exclusão (formato 1.1).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExcludePatterns { get; set; }
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Lê as configurações salvas em <see cref="AppPaths.SettingsFile"/>.
    /// </summary>
    public static AppSettings Load() => Load(AppPaths.SettingsFile);

    /// <summary>
    /// Lê as configurações do arquivo indicado e as normaliza (<see cref="Normalize"/>).
    /// Em caso de ausência ou arquivo inválido, retorna os padrões.
    /// </summary>
    /// <param name="path">Caminho do arquivo JSON.</param>
    public static AppSettings Load(string path)
    {
        AppSettings? settings = null;
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            FileLogService.Write($"[AVISO] Configurações ignoradas (arquivo inválido ou inacessível): {ex.Message}");
        }
        return Normalize(settings ?? new AppSettings());
    }

    /// <summary>
    /// Migra o formato 1.1 (uma única tarefa em campos soltos) para a lista de tarefas, descarta entradas
    /// inválidas e garante que <see cref="AppSettings.ActiveProfileId"/> aponte para uma tarefa existente.
    /// </summary>
    /// <param name="settings">Configurações lidas do disco.</param>
    /// <returns>A mesma instância, normalizada.</returns>
    public static AppSettings Normalize(AppSettings settings)
    {
        settings.Profiles ??= new List<SyncProfile>();
        settings.Profiles.RemoveAll(p => p == null);

        bool hasLegacyPair = !string.IsNullOrWhiteSpace(settings.SourcePath) || !string.IsNullOrWhiteSpace(settings.DestinationPath);
        if (settings.Profiles.Count == 0 && hasLegacyPair)
        {
            var migrated = new SyncProfile
            {
                Name = SyncProfile.SuggestName(settings.SourcePath),
                SourcePath = settings.SourcePath ?? string.Empty,
                DestinationPath = settings.DestinationPath ?? string.Empty,
                UseSourceFolderNameInDestination = settings.UseSourceFolderNameInDestination ?? true,
                EnableDeepHashCheck = settings.EnableDeepHashCheck ?? false,
                TolerateDstShift = settings.TolerateDstShift ?? false,
                ExcludePatterns = settings.ExcludePatterns ?? DefaultExcludePatterns
            };
            settings.Profiles.Add(migrated);
            settings.ActiveProfileId = migrated.Id;
        }

        settings.SourcePath = null;
        settings.DestinationPath = null;
        settings.UseSourceFolderNameInDestination = null;
        settings.EnableDeepHashCheck = null;
        settings.TolerateDstShift = null;
        settings.ExcludePatterns = null;

        // Ids duplicados (edição manual do JSON) quebrariam a seleção
        var seen = new HashSet<Guid>();
        foreach (var profile in settings.Profiles)
        {
            if (profile.Id == Guid.Empty || !seen.Add(profile.Id))
            {
                profile.Id = Guid.NewGuid();
                seen.Add(profile.Id);
            }
            profile.Name = string.IsNullOrWhiteSpace(profile.Name) ? SyncProfile.SuggestName(profile.SourcePath) : profile.Name.Trim();
            profile.SourcePath ??= string.Empty;
            profile.DestinationPath ??= string.Empty;
            profile.ExcludePatterns ??= string.Empty;
            if (profile.UseSourceFolderNameInDestination)
            {
                profile.DestinationPath = StripSourceFolderFromDestination(profile.SourcePath, profile.DestinationPath);
            }
        }

        if (settings.ActiveProfileId.HasValue && settings.Profiles.All(p => p.Id != settings.ActiveProfileId.Value))
        {
            settings.ActiveProfileId = null;
        }
        if (!settings.ActiveProfileId.HasValue && settings.Profiles.Count > 0)
        {
            settings.ActiveProfileId = settings.Profiles[0].Id;
        }

        return settings;
    }

    /// <summary>
    /// Até a versão 1.2.0, a opção "Criar subpasta com o nome da origem" era gravada no próprio texto do destino
    /// (<c>G:\Backup</c> virava <c>G:\Backup\Fotos</c>), e desmarcar a opção não a removia. O destino passa a guardar só a
    /// pasta escolhida; esta função devolve essa pasta. Com a opção ligada, o destino efetivo continua o mesmo,
    /// porque a subpasta é anexada de novo na resolução.
    /// </summary>
    /// <param name="sourcePath">Pasta de Origem.</param>
    /// <param name="destPath">Destino salvo.</param>
    /// <returns>Destino sem a subpasta da origem no final, ou o próprio destino quando não termina nela.</returns>
    public static string StripSourceFolderFromDestination(string sourcePath, string destPath)
    {
        static string LastSegment(string path)
        {
            var trimmed = path.Trim().TrimEnd('\\', '/');
            return trimmed[(trimmed.LastIndexOfAny(new[] { '\\', '/' }) + 1)..];
        }

        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(destPath)) return destPath;
        var sourceName = LastSegment(sourcePath);
        var trimmedDest = destPath.Trim().TrimEnd('\\', '/');
        int cut = trimmedDest.LastIndexOfAny(new[] { '\\', '/' });
        if (sourceName.Length == 0 || cut < 0 ||
            !string.Equals(trimmedDest[(cut + 1)..], sourceName, StringComparison.OrdinalIgnoreCase))
        {
            return destPath;
        }

        var parent = trimmedDest[..cut];
        // Raízes: "G:" volta a ser "G:\", "" (Unix) volta a ser "/". UNC "\\servidor" sem pasta não é um destino válido.
        if (parent.Length == 0) return trimmedDest[..1];
        if (parent.Length == 2 && parent[1] == ':') return parent + trimmedDest[cut];
        if (parent.TrimStart('\\', '/').Length == 0 || (parent.StartsWith(@"\\") && parent.IndexOf('\\', 2) < 0)) return destPath;
        return parent;
    }

    /// <summary>
    /// Grava as configurações em <see cref="AppPaths.SettingsFile"/>.
    /// </summary>
    public static void Save(AppSettings settings) => Save(settings, AppPaths.SettingsFile);

    /// <summary>
    /// Grava as configurações de forma atômica (arquivo temporário + renomeação).
    /// </summary>
    /// <param name="settings">Configurações a gravar.</param>
    /// <param name="path">Caminho do arquivo JSON.</param>
    public static void Save(AppSettings settings, string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLogService.Write($"[AVISO] Não foi possível salvar as configurações: {ex.Message}");
        }
    }
}
