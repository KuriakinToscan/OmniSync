using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OmniSync.Models;

namespace OmniSync.Services;

/// <summary>
/// Contrato do motor de sincronização responsável pela análise diferencial (Dry-Run)
/// e pela execução física das operações de arquivo em drives locais ou unidades de rede (NAS/SMB).
/// O motor não depende da camada de interface: atualizações de itens são reportadas via <see cref="IProgress{T}"/>.
/// </summary>
public interface ISyncEngine
{
    /// <summary>
    /// Realiza a varredura recursiva dos diretórios de Origem e Destino e classifica cada arquivo
    /// em <see cref="SyncActionType.Identical"/>, <see cref="SyncActionType.New"/>,
    /// <see cref="SyncActionType.OrphanTrash"/> ou <see cref="SyncActionType.Conflict"/>,
    /// sem realizar qualquer alteração em disco (modo Dry-Run).
    /// </summary>
    /// <param name="sourceDir">Caminho do diretório de Origem.</param>
    /// <param name="destDir">Caminho do diretório de Destino.</param>
    /// <param name="options">Opções de comparação e exclusão.</param>
    /// <param name="logger">Canal opcional para envio de mensagens de log em tempo real.</param>
    /// <param name="cancellationToken">Token para cancelamento cooperativo da varredura.</param>
    /// <returns>Itens classificados e avisos da varredura.</returns>
    /// <exception cref="System.IO.DirectoryNotFoundException">A Origem não existe.</exception>
    /// <exception cref="InvalidOperationException">Origem e Destino são a mesma pasta ou estão aninhados.</exception>
    Task<ScanResult> ScanDirectoriesAsync(
        string sourceDir,
        string destDir,
        ScanOptions options,
        IProgress<string>? logger,
        CancellationToken cancellationToken);

    /// <summary>
    /// Executa fisicamente as ações de sincronização previamente mapeadas:
    /// copia arquivos novos, move arquivos órfãos para <c>destino/lixo/</c> e resolve conflitos
    /// movendo versões substituídas para <c>destino/old/</c>. Toda gravação passa por arquivo temporário
    /// e renomeação, de modo que o destino nunca fica com arquivo truncado.
    /// </summary>
    /// <param name="items">Itens resultantes da varredura diferencial.</param>
    /// <param name="destDir">Caminho do diretório de Destino.</param>
    /// <param name="resolveConflictAsync">Callback assíncrono invocado quando um item em conflito requer decisão do usuário.</param>
    /// <param name="itemUpdates">
    /// Canal opcional para atualizações de estado dos itens. Quando <c>null</c>, o motor aplica as atualizações
    /// diretamente (uso headless/testes).
    /// </param>
    /// <param name="progress">Canal opcional para reporte contínuo de progresso, taxa de transferência e ETA.</param>
    /// <param name="logger">Canal opcional para envio de mensagens de log em tempo real.</param>
    /// <param name="cancellationToken">Token para cancelamento cooperativo da execução.</param>
    /// <returns>Balanço da execução, incluindo os itens que falharam.</returns>
    Task<SyncResult> ExecuteSyncAsync(
        IReadOnlyList<SyncItem> items,
        string destDir,
        Func<SyncItem, Task<ConflictResolution>> resolveConflictAsync,
        IProgress<SyncItemUpdate>? itemUpdates,
        IProgress<SyncProgressReport>? progress,
        IProgress<string>? logger,
        CancellationToken cancellationToken);

    /// <summary>
    /// Apaga os temporários de execução anterior listados pela análise (<see cref="ScanResult.StaleTempFiles"/>),
    /// conferindo de novo, um a um, se ainda existem e continuam com mais de 1 hora. Falhas só vão para o log.
    /// </summary>
    /// <param name="destDir">Pasta de destino da análise.</param>
    /// <param name="files">Temporários encontrados na análise.</param>
    /// <param name="logger">Canal opcional de log.</param>
    /// <returns>Quantos foram apagados.</returns>
    int RemoveStaleTempFiles(string destDir, IReadOnlyList<string> files, IProgress<string>? logger) => 0;
}
