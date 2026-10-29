using System;
using System.Collections.Generic;
using OmniSync.Models;

namespace OmniSync.ViewModels;

/// <summary>
/// Filtros disponíveis na grade de prévia.
/// </summary>
public enum ItemFilter
{
    /// <summary>Apenas o que será alterado (padrão): novos, conflitos, órfãos e itens com erro.</summary>
    Changes,
    /// <summary>Todos os itens.</summary>
    All,
    /// <summary>Apenas novos.</summary>
    New,
    /// <summary>Apenas conflitos.</summary>
    Conflicts,
    /// <summary>Apenas órfãos.</summary>
    Orphans,
    /// <summary>Apenas idênticos.</summary>
    Identical,
    /// <summary>Apenas itens com erro.</summary>
    Errors
}

/// <summary>
/// Severidade de uma entrada do log visual.
/// </summary>
public enum LogLevel
{
    /// <summary>Informação.</summary>
    Info,
    /// <summary>Aviso.</summary>
    Warning,
    /// <summary>Erro.</summary>
    Error
}

/// <summary>
/// Entrada do Registro de Operações exibida na interface.
/// </summary>
/// <param name="Timestamp">Momento do registro.</param>
/// <param name="Level">Severidade.</param>
/// <param name="Message">Texto.</param>
public sealed record LogEntry(DateTime Timestamp, LogLevel Level, string Message)
{
    /// <summary>Hora formatada.</summary>
    public string Time => Timestamp.ToString("HH:mm:ss");

    /// <summary>Indica aviso (para estilo).</summary>
    public bool IsWarning => Level == LogLevel.Warning;

    /// <summary>Indica erro (para estilo).</summary>
    public bool IsError => Level == LogLevel.Error;

    /// <summary>
    /// Deduz a severidade a partir do prefixo da mensagem (<c>[ERRO</c>, <c>Aviso</c>, <c>[AVISO</c>).
    /// </summary>
    public static LogLevel InferLevel(string message)
    {
        if (message.StartsWith("[ERRO", StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith("Erro", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevel.Error;
        }
        if (message.StartsWith("[AVISO", StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith("Aviso", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevel.Warning;
        }
        return LogLevel.Info;
    }

    /// <inheritdoc />
    public override string ToString() => $"[{Time}] {Message}";
}

/// <summary>
/// Resumo do plano apresentado ao usuário para confirmação antes da execução.
/// </summary>
public sealed class SyncPlanSummary
{
    /// <summary>Destino efetivo.</summary>
    public required string DestinationPath { get; init; }

    /// <summary>Arquivos novos a copiar.</summary>
    public int NewCount { get; init; }

    /// <summary>Volume dos arquivos novos.</summary>
    public long NewBytes { get; init; }

    /// <summary>Volume das versões que substituirão as atuais.</summary>
    public long ReplaceBytes { get; init; }

    /// <summary>Volume de substituição formatado.</summary>
    public string ReplaceBytesFormatted => SyncItem.FormatBytes(ReplaceBytes);

    /// <summary>Caminho de origem.</summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>Conflitos marcados para substituir.</summary>
    public int ReplaceCount { get; init; }

    /// <summary>Conflitos marcados para ignorar.</summary>
    public int IgnoreCount { get; init; }

    /// <summary>Conflitos sem decisão (será perguntado durante a execução).</summary>
    public int AskCount { get; init; }

    /// <summary>Órfãos que irão para <c>lixo/</c>.</summary>
    public int OrphanCount { get; init; }

    /// <summary>Total de arquivos indexados no destino.</summary>
    public int DestinationFileCount { get; init; }

    /// <summary>Avisos da varredura.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>Indica se há avisos.</summary>
    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>
    /// Indica remoção em massa: muitos órfãos em relação ao destino, sinal típico de destino escolhido errado.
    /// Critério: pelo menos 1.000 órfãos, ou pelo menos 10 órfãos representando 20% ou mais dos arquivos do destino.
    /// </summary>
    public bool IsMassOrphan =>
        OrphanCount >= 1000 ||
        (OrphanCount >= 10 && DestinationFileCount > 0 && OrphanCount * 5 >= DestinationFileCount);

    /// <summary>Percentual de arquivos do destino que serão movidos para <c>lixo/</c>.</summary>
    public double OrphanPercent => DestinationFileCount > 0 ? 100.0 * OrphanCount / DestinationFileCount : 0;

    /// <summary>Volume novo formatado.</summary>
    public string NewBytesFormatted => SyncItem.FormatBytes(NewBytes);
}
