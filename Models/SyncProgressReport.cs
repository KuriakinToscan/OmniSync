using System;

namespace OmniSync.Models;

/// <summary>
/// Estrutura imutável de telemetria enviada periodicamente pelo motor de sincronização
/// para atualizar a barra de progresso, velocidade de transferência e tempo restante estimado (ETA).
/// </summary>
public class SyncProgressReport
{
    /// <summary>
    /// Quantidade de arquivos acionáveis já concluídos na sessão atual.
    /// </summary>
    public int ProcessedFiles { get; set; }

    /// <summary>
    /// Quantidade total de arquivos acionáveis (Novos, Conflitos e Órfãos) previstos na sessão.
    /// </summary>
    public int TotalFiles { get; set; }

    /// <summary>
    /// Total de bytes efetivamente copiados até o momento.
    /// </summary>
    public long ProcessedBytes { get; set; }

    /// <summary>
    /// Total estimado de bytes que precisam ser copiados na sessão atual.
    /// </summary>
    public long TotalBytes { get; set; }

    /// <summary>
    /// Caminho relativo do arquivo que está sendo processado no momento.
    /// </summary>
    public string CurrentFileName { get; set; } = string.Empty;

    /// <summary>
    /// Percentual global de conclusão da sincronização (0.0 a 100.0).
    /// </summary>
    public double OverallProgressPercent { get; set; }

    /// <summary>
    /// Taxa média de transferência de dados calculada em bytes por segundo.
    /// </summary>
    public double BytesPerSecond { get; set; }

    /// <summary>
    /// Tempo restante estimado para conclusão da cópia, ou <c>null</c> quando ainda não mensurável.
    /// </summary>
    public TimeSpan? EstimatedTimeRemaining { get; set; }

    /// <summary>
    /// Velocidade de transferência formatada para exibição na interface (ex.: <c>85,4 MB/s</c>).
    /// </summary>
    public string SpeedFormatted => BytesPerSecond > 0
        ? $"{SyncItem.FormatBytes((long)BytesPerSecond)}/s"
        : "—";

    /// <summary>
    /// Tempo restante estimado formatado para exibição na interface (ex.: <c>02:15</c> ou <c>1h 12m</c>).
    /// </summary>
    public string EtaFormatted => EstimatedTimeRemaining.HasValue
        ? (EstimatedTimeRemaining.Value.TotalHours >= 1
            ? $"{(int)EstimatedTimeRemaining.Value.TotalHours}h {EstimatedTimeRemaining.Value.Minutes}m"
            : $"{EstimatedTimeRemaining.Value.Minutes:D2}:{EstimatedTimeRemaining.Value.Seconds:D2}")
        : "—";
}
