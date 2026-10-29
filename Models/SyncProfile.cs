using System;

namespace OmniSync.Models;

/// <summary>
/// Resultado da última execução registrada para um par de sincronização.
/// </summary>
public enum ProfileRunOutcome
{
    /// <summary>Nunca sincronizado.</summary>
    None,
    /// <summary>Concluído sem falhas.</summary>
    Success,
    /// <summary>Concluído com uma ou mais falhas.</summary>
    Failures,
    /// <summary>Interrompido pelo usuário.</summary>
    Cancelled
}

/// <summary>
/// Par de sincronização salvo ("perfil"): origem, destino e opções de análise, mais o estado da última execução.
/// Persistido em <c>settings.json</c>.
/// </summary>
public sealed class SyncProfile
{
    /// <summary>Identificador estável do par.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Nome exibido na barra lateral.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Diretório de Origem.</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>Diretório de Destino.</summary>
    public string DestinationPath { get; set; } = string.Empty;

    /// <summary>Usar subpasta com o nome da origem no destino.</summary>
    public bool UseSourceFolderNameInDestination { get; set; } = true;

    /// <summary>Comparar por SHA-256.</summary>
    public bool EnableDeepHashCheck { get; set; }

    /// <summary>Tolerar diferença exata de 1 h (horário de verão em FAT/exFAT).</summary>
    public bool TolerateDstShift { get; set; }

    /// <summary>Padrões de exclusão separados por ponto e vírgula.</summary>
    public string ExcludePatterns { get; set; } = string.Empty;

    /// <summary>Momento (UTC) da última sincronização executada, ou <c>null</c> se nunca executada.</summary>
    public DateTime? LastRunUtc { get; set; }

    /// <summary>Resultado da última sincronização.</summary>
    public ProfileRunOutcome LastOutcome { get; set; } = ProfileRunOutcome.None;

    /// <summary>Quantidade de falhas da última sincronização.</summary>
    public int LastFailedCount { get; set; }

    /// <summary>
    /// Sugere um nome para o par a partir da pasta de origem (ex.: <c>D:\Fotos</c> → <c>Fotos</c>).
    /// </summary>
    /// <param name="sourcePath">Caminho da origem.</param>
    /// <returns>Nome sugerido, ou "Novo par" quando a origem está vazia.</returns>
    public static string SuggestName(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return "Novo par";
        // Separa por "\" e "/" nos dois sistemas: um settings.json pode vir de outra plataforma
        var trimmed = sourcePath.Trim().TrimEnd('\\', '/');
        if (trimmed.Length == 0) return "Novo par";
        var name = trimmed[(trimmed.LastIndexOfAny(new[] { '\\', '/' }) + 1)..];
        // Raiz de disco (ex.: "E:\") fica com o próprio caminho
        return string.IsNullOrWhiteSpace(name) ? trimmed : name;
    }
}
