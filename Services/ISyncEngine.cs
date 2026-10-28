using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OmniSync.Models;

namespace OmniSync.Services;

/// <summary>
/// Contrato do motor de sincronização responsável pela análise diferencial (Dry-Run)
/// e pela execução física das operações de arquivo em drives locais ou unidades de rede (NAS/SMB).
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
    /// <param name="useDeepHash">Se <c>true</c>, valida a igualdade do conteúdo por hash criptográfico SHA-256 quando os tamanhos coincidirem.</param>
    /// <param name="logger">Canal opcional para envio de mensagens de log em tempo real.</param>
    /// <param name="cancellationToken">Token para cancelamento cooperativo da varredura.</param>
    /// <returns>Lista completa de itens analisados e classificados.</returns>
    Task<List<SyncItem>> ScanDirectoriesAsync(
        string sourceDir,
        string destDir,
        bool useDeepHash,
        IProgress<string>? logger,
        CancellationToken cancellationToken);

    /// <summary>
    /// Executa fisicamente as ações de sincronização previamente mapeadas:
    /// copia arquivos novos, move arquivos órfãos para <c>destino/lixo/</c> e resolve conflitos
    /// movendo versões substituídas para <c>destino/old/</c>.
    /// </summary>
    /// <param name="items">Coleção de itens resultantes da varredura diferencial.</param>
    /// <param name="sourceDir">Caminho do diretório de Origem.</param>
    /// <param name="destDir">Caminho do diretório de Destino.</param>
    /// <param name="resolveConflictAsync">Callback assíncrono invocado quando um item em conflito requer decisão do usuário.</param>
    /// <param name="progress">Canal opcional para reporte contínuo de progresso, taxa de transferência e ETA.</param>
    /// <param name="logger">Canal opcional para envio de mensagens de log em tempo real.</param>
    /// <param name="cancellationToken">Token para cancelamento cooperativo da execução.</param>
    Task ExecuteSyncAsync(
        IEnumerable<SyncItem> items,
        string sourceDir,
        string destDir,
        Func<SyncItem, Task<ConflictResolution>> resolveConflictAsync,
        IProgress<SyncProgressReport>? progress,
        IProgress<string>? logger,
        CancellationToken cancellationToken);
}
