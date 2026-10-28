using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using OmniSync.Models;

namespace OmniSync.Services;

/// <summary>
/// Implementação principal do motor de sincronização de arquivos (<see cref="ISyncEngine"/>).
/// Projetado para alta resiliência em drives locais (NTFS/ext4/exFAT) e compartilhamentos de rede (NAS/SMB/NFS).
/// </summary>
public class SyncEngine : ISyncEngine
{
    /// <summary>
    /// Tamanho do buffer de leitura/escrita em streaming (2 MB), otimizado para maximizar throughput em redes SMB/NAS e SSDs.
    /// </summary>
    private const int BufferSize = 1024 * 1024 * 2;

    /// <summary>
    /// Número máximo de tentativas automáticas em caso de falha transitória de rede ou bloqueio temporário de I/O.
    /// </summary>
    private const int MaxRetries = 3;

    /// <summary>
    /// Tolerância máxima de diferença de timestamp (2 segundos) para compatibilidade entre sistemas de arquivos
    /// distintos (NTFS, ext4, Samba/NAS e FAT/exFAT).
    /// </summary>
    private const double TimestampToleranceSeconds = 2.0;

    /// <inheritdoc />
    public async Task<List<SyncItem>> ScanDirectoriesAsync(
        string sourceDir,
        string destDir,
        bool useDeepHash,
        IProgress<string>? logger,
        CancellationToken cancellationToken)
    {
        return await Task.Run(async () =>
        {
            logger?.Report("Iniciando varredura diferencial...");
            logger?.Report($"Origem: {sourceDir}");
            logger?.Report($"Destino: {destDir}");

            if (!Directory.Exists(sourceDir))
            {
                throw new DirectoryNotFoundException($"O diretório de origem não existe ou não está acessível: {sourceDir}");
            }

            sourceDir = Path.GetFullPath(sourceDir);
            destDir = Path.GetFullPath(destDir);
            bool destExists = Directory.Exists(destDir);

            // Impede que Origem e Destino apontem exatamente para a mesma pasta física
            if (string.Equals(
                sourceDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                destDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("O diretório de Origem e o de Destino não podem ser a mesma pasta.");
            }

            var sourceFiles = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
            var destFiles = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);

            // 1. Indexa recursivamente todos os arquivos da Origem com tolerância a pastas protegidas do sistema
            logger?.Report("Indexando arquivos da Origem...");
            foreach (var filePath in EnumerateFilesResilient(sourceDir, skipReservedSyncFolders: false, logger, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relPath = Path.GetRelativePath(sourceDir, filePath);
                try
                {
                    var fileInfo = new FileInfo(filePath);
                    if (fileInfo.Exists)
                    {
                        sourceFiles[relPath] = fileInfo;
                    }
                }
                catch (Exception ex)
                {
                    logger?.Report($"Aviso: Falha ao ler metadados de '{relPath}': {ex.Message}");
                }
            }
            logger?.Report($"Origem: {sourceFiles.Count} arquivos indexados.");

            // 2. Indexa recursivamente os arquivos do Destino (caso a pasta já exista),
            // ignorando as subpastas reservadas (lixo/ e old/) e diretórios restritos do sistema operacional
            if (destExists)
            {
                logger?.Report("Indexando arquivos do Destino (excluindo 'lixo/' e 'old/')...");
                foreach (var filePath in EnumerateFilesResilient(destDir, skipReservedSyncFolders: true, logger, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relPath = Path.GetRelativePath(destDir, filePath);

                    if (IsReservedFolder(relPath))
                    {
                        continue;
                    }

                    try
                    {
                        var fileInfo = new FileInfo(filePath);
                        if (fileInfo.Exists)
                        {
                            destFiles[relPath] = fileInfo;
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.Report($"Aviso: Falha ao ler metadados no destino de '{relPath}': {ex.Message}");
                    }
                }
                logger?.Report($"Destino: {destFiles.Count} arquivos indexados.");
            }
            else
            {
                logger?.Report("Destino ainda não existe fisicamente. Todos os arquivos da origem serão classificados como novos.");
            }

            var result = new List<SyncItem>(sourceFiles.Count + destFiles.Count);

            // 3. Comparação Origem -> Destino (identifica arquivos Novos, Idênticos e Conflitantes)
            logger?.Report("Comparando diferenças entre Origem e Destino...");
            foreach (var kvp in sourceFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relPath = kvp.Key;
                var srcInfo = kvp.Value;
                var targetFullPath = Path.Combine(destDir, relPath);

                if (!destFiles.TryGetValue(relPath, out var dstInfo))
                {
                    // Arquivo existe na Origem, mas não existe no Destino -> Novo (Copiar)
                    result.Add(new SyncItem
                    {
                        RelativePath = relPath,
                        SourceFullPath = srcInfo.FullName,
                        DestinationFullPath = targetFullPath,
                        SourceSize = srcInfo.Length,
                        DestinationSize = 0,
                        SourceModifiedUtc = srcInfo.LastWriteTimeUtc,
                        DestinationModifiedUtc = null,
                        ActionType = SyncActionType.New,
                        StatusMessage = "Novo (Pendente cópia)"
                    });
                }
                else
                {
                    // Arquivo existe em ambos os diretórios -> Verifica se é Idêntico ou Conflito
                    bool isIdentical = false;
                    bool sizeMatches = srcInfo.Length == dstInfo.Length;

                    if (sizeMatches)
                    {
                        if (useDeepHash)
                        {
                            // Quando a verificação SHA-256 está ativa e os tamanhos são iguais,
                            // compara o conteúdo binário exato dos dois arquivos
                            try
                            {
                                var srcHash = await ComputeSha256Async(srcInfo.FullName, cancellationToken);
                                var dstHash = await ComputeSha256Async(dstInfo.FullName, cancellationToken);
                                isIdentical = string.Equals(srcHash, dstHash, StringComparison.OrdinalIgnoreCase);
                            }
                            catch (Exception ex)
                            {
                                logger?.Report($"Aviso ao calcular hash de '{relPath}': {ex.Message}");
                                isIdentical = false;
                            }
                        }
                        else
                        {
                            // Comparação rápida por tamanho + data de modificação (com tolerância de 2 segundos)
                            isIdentical = Math.Abs((srcInfo.LastWriteTimeUtc - dstInfo.LastWriteTimeUtc).TotalSeconds) <= TimestampToleranceSeconds;
                        }
                    }

                    if (isIdentical)
                    {
                        result.Add(new SyncItem
                        {
                            RelativePath = relPath,
                            SourceFullPath = srcInfo.FullName,
                            DestinationFullPath = dstInfo.FullName,
                            SourceSize = srcInfo.Length,
                            DestinationSize = dstInfo.Length,
                            SourceModifiedUtc = srcInfo.LastWriteTimeUtc,
                            DestinationModifiedUtc = dstInfo.LastWriteTimeUtc,
                            ActionType = SyncActionType.Identical,
                            IsProcessed = true,
                            StatusMessage = "Idêntico (Ignorado)"
                        });
                    }
                    else
                    {
                        result.Add(new SyncItem
                        {
                            RelativePath = relPath,
                            SourceFullPath = srcInfo.FullName,
                            DestinationFullPath = dstInfo.FullName,
                            SourceSize = srcInfo.Length,
                            DestinationSize = dstInfo.Length,
                            SourceModifiedUtc = srcInfo.LastWriteTimeUtc,
                            DestinationModifiedUtc = dstInfo.LastWriteTimeUtc,
                            ActionType = SyncActionType.Conflict,
                            Resolution = ConflictResolution.AskUser,
                            StatusMessage = "Conflito (Requer decisão)"
                        });
                    }
                }
            }

            // 4. Comparação Destino -> Origem (identifica arquivos Órfãos que existem apenas no Destino)
            foreach (var kvp in destFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relPath = kvp.Key;
                var dstInfo = kvp.Value;

                if (!sourceFiles.ContainsKey(relPath))
                {
                    result.Add(new SyncItem
                    {
                        RelativePath = relPath,
                        SourceFullPath = null,
                        DestinationFullPath = dstInfo.FullName,
                        SourceSize = 0,
                        DestinationSize = dstInfo.Length,
                        SourceModifiedUtc = null,
                        DestinationModifiedUtc = dstInfo.LastWriteTimeUtc,
                        ActionType = SyncActionType.OrphanTrash,
                        StatusMessage = "Órfão (Será movido p/ lixo/)"
                    });
                }
            }

            logger?.Report($"Varredura concluída. Total de itens analisados: {result.Count}");
            return result;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task ExecuteSyncAsync(
        IEnumerable<SyncItem> items,
        string sourceDir,
        string destDir,
        Func<SyncItem, Task<ConflictResolution>> resolveConflictAsync,
        IProgress<SyncProgressReport>? progress,
        IProgress<string>? logger,
        CancellationToken cancellationToken)
    {
        var itemList = new List<SyncItem>(items);

        await Task.Run(async () =>
        {
            var normalizedDestDir = Path.GetFullPath(destDir);
            var trashDir = Path.Combine(normalizedDestDir, "lixo");
            var oldDir = Path.Combine(normalizedDestDir, "old");

            Directory.CreateDirectory(normalizedDestDir);

            // Calcula o total de arquivos acionáveis e o volume de bytes previstos para cópia
            long totalBytesToCopy = 0;
            int actionableFilesCount = 0;

            foreach (var item in itemList)
            {
                if (item.ActionType == SyncActionType.New || item.ActionType == SyncActionType.Conflict)
                {
                    totalBytesToCopy += item.SourceSize;
                    actionableFilesCount++;
                }
                else if (item.ActionType == SyncActionType.OrphanTrash)
                {
                    actionableFilesCount++;
                }
            }

            var overallStopwatch = Stopwatch.StartNew();
            long lastProgressReportTick = 0;
            long totalBytesCopiedSoFar = 0;
            int processedFilesCount = 0;
            string currentFileName = string.Empty;

            logger?.Report($"Iniciando execução da sincronização ({actionableFilesCount} ações necessárias)...");

            foreach (var item in itemList)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 1. Arquivos Idênticos -> Nenhuma operação de disco necessária
                if (item.ActionType == SyncActionType.Identical)
                {
                    UpdateItemSafe(item, i =>
                    {
                        i.IsProcessed = true;
                        i.StatusMessage = "Já sincronizado (Idêntico)";
                    });
                    continue;
                }

                // 2. Arquivos Órfãos -> Mover para destino/lixo/<caminho_relativo>
                if (item.ActionType == SyncActionType.OrphanTrash)
                {
                    currentFileName = item.RelativePath;
                    try
                    {
                        logger?.Report($"[LIXO] Movendo órfão: {item.RelativePath}");
                        var targetTrashPath = Path.Combine(trashDir, item.RelativePath);
                        MoveFileSafely(item.DestinationFullPath!, targetTrashPath);

                        processedFilesCount++;
                        UpdateItemSafe(item, i =>
                        {
                            i.IsProcessed = true;
                            i.StatusMessage = "Movido para lixo/ com sucesso";
                        });
                    }
                    catch (Exception ex)
                    {
                        UpdateItemSafe(item, i => i.StatusMessage = $"Erro ao mover para lixo: {ex.Message}");
                        logger?.Report($"Erro ao mover '{item.RelativePath}' para lixo: {ex.Message}");
                    }
                    ReportProgress(force: true);
                    continue;
                }

                // 3. Arquivos em Conflito -> Perguntar ao usuário (se ainda não decidido) e aplicar resolução
                if (item.ActionType == SyncActionType.Conflict)
                {
                    currentFileName = item.RelativePath;
                    var resolution = item.Resolution;

                    if (resolution == ConflictResolution.AskUser)
                    {
                        logger?.Report($"[CONFLITO] Solicitando decisão do usuário para: {item.RelativePath}");
                        // Pausa o cronômetro enquanto aguarda a resposta humana no modal para não distorcer o cálculo de velocidade/ETA
                        overallStopwatch.Stop();
                        try
                        {
                            resolution = await resolveConflictAsync(item);
                        }
                        finally
                        {
                            overallStopwatch.Start();
                        }
                        UpdateItemSafe(item, i => i.Resolution = resolution);
                    }

                    if (resolution == ConflictResolution.Ignore || resolution == ConflictResolution.AskUser)
                    {
                        // Desconta os bytes que não serão copiados do total previsto para manter a precisão da barra de progresso
                        Interlocked.Add(ref totalBytesToCopy, -item.SourceSize);
                        logger?.Report($"[CONFLITO] Arquivo ignorado pelo usuário: {item.RelativePath}");
                        processedFilesCount++;
                        UpdateItemSafe(item, i =>
                        {
                            i.IsProcessed = true;
                            i.StatusMessage = "Ignorado (Mantido no destino)";
                        });
                        ReportProgress(force: true);
                        continue;
                    }

                    if (resolution == ConflictResolution.Replace)
                    {
                        try
                        {
                            logger?.Report($"[SUBSTITUIR] Movendo versão antiga para old/: {item.RelativePath}");
                            var targetOldPath = Path.Combine(oldDir, item.RelativePath);
                            MoveFileSafely(item.DestinationFullPath!, targetOldPath);

                            logger?.Report($"[COPIAR] Copiando nova versão para destino: {item.RelativePath}");
                            await CopyFileWithProgressAndRetryAsync(
                                item.SourceFullPath!,
                                item.DestinationFullPath!,
                                onBytesCopied: bytesDelta =>
                                {
                                    Interlocked.Add(ref totalBytesCopiedSoFar, bytesDelta);
                                    ReportProgress(force: false);
                                },
                                cancellationToken);

                            processedFilesCount++;
                            UpdateItemSafe(item, i =>
                            {
                                i.IsProcessed = true;
                                i.StatusMessage = "Substituído com sucesso (Antigo em old/)";
                            });
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            UpdateItemSafe(item, i => i.StatusMessage = $"Erro ao substituir: {ex.Message}");
                            logger?.Report($"Erro ao substituir '{item.RelativePath}': {ex.Message}");
                        }
                        ReportProgress(force: true);
                        continue;
                    }
                }

                // 4. Arquivos Novos -> Copiar da Origem para o Destino preservando hierarquia e data de modificação
                if (item.ActionType == SyncActionType.New)
                {
                    currentFileName = item.RelativePath;
                    try
                    {
                        logger?.Report($"[NOVO] Copiando: {item.RelativePath}");
                        await CopyFileWithProgressAndRetryAsync(
                            item.SourceFullPath!,
                            item.DestinationFullPath!,
                            onBytesCopied: bytesDelta =>
                            {
                                Interlocked.Add(ref totalBytesCopiedSoFar, bytesDelta);
                                ReportProgress(force: false);
                            },
                            cancellationToken);

                        processedFilesCount++;
                        UpdateItemSafe(item, i =>
                        {
                            i.IsProcessed = true;
                            i.StatusMessage = "Copiado com sucesso";
                        });
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        UpdateItemSafe(item, i => i.StatusMessage = $"Erro ao copiar: {ex.Message}");
                        logger?.Report($"Erro ao copiar '{item.RelativePath}': {ex.Message}");
                    }
                    ReportProgress(force: true);
                }
            }

            overallStopwatch.Stop();
            currentFileName = string.Empty;
            ReportProgress(force: true);
            logger?.Report("Sincronização finalizada com êxito!");

            // Função local para envio controlado (throttled a cada 80ms) de relatórios de progresso à UI
            void ReportProgress(bool force)
            {
                if (progress == null) return;

                long nowTicks = Environment.TickCount64;
                if (!force && nowTicks - Interlocked.Read(ref lastProgressReportTick) < 80)
                {
                    return;
                }
                Interlocked.Exchange(ref lastProgressReportTick, nowTicks);

                var elapsed = overallStopwatch.Elapsed;
                long currentTotalBytes = Math.Max(0, Interlocked.Read(ref totalBytesToCopy));
                long copiedBytes = Math.Max(0, Interlocked.Read(ref totalBytesCopiedSoFar));

                double bytesPerSec = elapsed.TotalSeconds > 0.05 ? copiedBytes / elapsed.TotalSeconds : 0;
                long remainingBytes = Math.Max(0, currentTotalBytes - copiedBytes);

                TimeSpan? eta = null;
                if (bytesPerSec > 1.0 && remainingBytes > 0)
                {
                    double secondsRemaining = remainingBytes / bytesPerSec;
                    if (!double.IsNaN(secondsRemaining) && !double.IsInfinity(secondsRemaining))
                    {
                        // Limita o ETA máximo a 30 dias para evitar OverflowException em TimeSpan.FromSeconds
                        secondsRemaining = Math.Clamp(secondsRemaining, 0, 86400.0 * 30);
                        eta = TimeSpan.FromSeconds(secondsRemaining);
                    }
                }

                double overallPct = currentTotalBytes > 0
                    ? Math.Clamp((double)copiedBytes / currentTotalBytes * 100.0, 0.0, 100.0)
                    : (actionableFilesCount > 0 ? Math.Clamp((double)processedFilesCount / actionableFilesCount * 100.0, 0.0, 100.0) : 100.0);

                progress.Report(new SyncProgressReport
                {
                    ProcessedFiles = processedFilesCount,
                    TotalFiles = actionableFilesCount,
                    ProcessedBytes = copiedBytes,
                    TotalBytes = currentTotalBytes,
                    CurrentFileName = currentFileName,
                    BytesPerSecond = bytesPerSec,
                    EstimatedTimeRemaining = eta,
                    OverallProgressPercent = overallPct
                });
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Atualiza propriedades observáveis de um <see cref="SyncItem"/> de maneira segura em relação à thread de UI.
    /// Quando executado dentro de uma aplicação Avalonia ativa, despacha a notificação para a UIThread;
    /// em execuções headless (como testes unitários xUnit), aplica diretamente na thread atual.
    /// </summary>
    private static void UpdateItemSafe(SyncItem item, Action<SyncItem> updateAction)
    {
        try
        {
            if (Application.Current == null || Dispatcher.UIThread.CheckAccess())
            {
                updateAction(item);
            }
            else
            {
                Dispatcher.UIThread.InvokeAsync(() => updateAction(item)).GetAwaiter().GetResult();
            }
        }
        catch
        {
            updateAction(item);
        }
    }

    /// <summary>
    /// Percorre recursivamente uma árvore de diretórios utilizando pilha explícita (<see cref="Stack{T}"/>),
    /// ignorando pastas sem permissão de acesso, pontos de repetição (symlinks/junctions) e diretórios reservados.
    /// </summary>
    private static IEnumerable<string> EnumerateFilesResilient(
        string rootDir,
        bool skipReservedSyncFolders,
        IProgress<string>? logger,
        CancellationToken cancellationToken)
    {
        var dirs = new Stack<string>();
        dirs.Push(rootDir);

        var enumOptions = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        while (dirs.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentDir = dirs.Pop();

            string[] files;
            try
            {
                files = Directory.GetFiles(currentDir, "*", enumOptions);
            }
            catch (Exception ex)
            {
                logger?.Report($"Aviso: Pasta inacessível ignorada '{currentDir}': {ex.Message}");
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            string[] subDirs;
            try
            {
                subDirs = Directory.GetDirectories(currentDir, "*", enumOptions);
            }
            catch (Exception ex)
            {
                logger?.Report($"Aviso: Subpastas inacessíveis em '{currentDir}': {ex.Message}");
                continue;
            }

            for (int i = subDirs.Length - 1; i >= 0; i--)
            {
                var subDir = subDirs[i];
                var dirName = Path.GetFileName(subDir);

                if (IsSystemIgnoredDirectoryName(dirName))
                {
                    continue;
                }

                // Na raiz do diretório de Destino, ignora as pastas reservadas "lixo" e "old"
                if (skipReservedSyncFolders && string.Equals(currentDir, rootDir, StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(dirName, "lixo", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(dirName, "old", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                dirs.Push(subDir);
            }
        }
    }

    /// <summary>
    /// Verifica se o nome da pasta corresponde a diretórios internos de sistema ou lixeiras de NAS
    /// que não devem ser sincronizados.
    /// </summary>
    private static bool IsSystemIgnoredDirectoryName(string dirName)
    {
        if (string.IsNullOrEmpty(dirName)) return false;
        return dirName.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase) ||
               dirName.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase) ||
               dirName.Equals("#recycle", StringComparison.OrdinalIgnoreCase) ||
               dirName.Equals(".Trash", StringComparison.OrdinalIgnoreCase) ||
               dirName.Equals(".Trash-1000", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Confirma se o primeiro segmento de um caminho relativo pertence às pastas reservadas <c>lixo</c> ou <c>old</c>.
    /// </summary>
    private static bool IsReservedFolder(string relativePath)
    {
        var parts = relativePath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0)
        {
            var first = parts[0].Trim();
            if (first.Equals("lixo", StringComparison.OrdinalIgnoreCase) ||
                first.Equals("old", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Move um arquivo para o caminho de destino (<c>lixo/</c> ou <c>old/</c>), criando a árvore de subpastas
    /// necessária e adicionando sufixo de timestamp caso já exista um arquivo homônimo no destino.
    /// </summary>
    private static void MoveFileSafely(string sourceFilePath, string targetFilePath)
    {
        if (!File.Exists(sourceFilePath)) return;

        var targetDir = Path.GetDirectoryName(targetFilePath);
        if (!string.IsNullOrEmpty(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        var safeDestination = GetSafeTargetPath(targetFilePath);
        File.Move(sourceFilePath, safeDestination, overwrite: true);
    }

    /// <summary>
    /// Gera um caminho de arquivo único adicionando sufixo <c>_yyyyMMdd_HHmmss</c> caso o arquivo já exista
    /// na pasta <c>lixo/</c> ou <c>old/</c>, evitando sobrescrita de históricos anteriores.
    /// </summary>
    private static string GetSafeTargetPath(string targetFilePath)
    {
        if (!File.Exists(targetFilePath)) return targetFilePath;

        var dir = Path.GetDirectoryName(targetFilePath)!;
        var nameWithoutExt = Path.GetFileNameWithoutExtension(targetFilePath);
        var ext = Path.GetExtension(targetFilePath);
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        var candidate = Path.Combine(dir, $"{nameWithoutExt}_{timestamp}{ext}");
        if (!File.Exists(candidate)) return candidate;

        int counter = 1;
        while (File.Exists(Path.Combine(dir, $"{nameWithoutExt}_{timestamp}_{counter}{ext}")))
        {
            counter++;
        }
        return Path.Combine(dir, $"{nameWithoutExt}_{timestamp}_{counter}{ext}");
    }

    /// <summary>
    /// Copia um arquivo em blocos assíncronos de 2 MB com reporte contínuo de bytes transferidos,
    /// preservação do timestamp original de modificação e retentativa automática em falhas de rede.
    /// </summary>
    private static async Task CopyFileWithProgressAndRetryAsync(
        string sourceFile,
        string destFile,
        Action<long> onBytesCopied,
        CancellationToken cancellationToken)
    {
        int attempt = 0;
        while (true)
        {
            attempt++;
            long bytesAddedInCurrentAttempt = 0;
            try
            {
                var destDirectory = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destDirectory))
                {
                    Directory.CreateDirectory(destDirectory);
                }

                var fileInfo = new FileInfo(sourceFile);

                await using (var srcStream = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true))
                await using (var dstStream = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
                {
                    var buffer = new byte[BufferSize];
                    int bytesRead;

                    while ((bytesRead = await srcStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                    {
                        await dstStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                        bytesAddedInCurrentAttempt += bytesRead;
                        onBytesCopied(bytesRead);
                    }
                }

                // Preserva a data/hora exata de modificação do arquivo original para futuras comparações diferenciais
                File.SetLastWriteTimeUtc(destFile, fileInfo.LastWriteTimeUtc);
                break;
            }
            catch (Exception) when (attempt < MaxRetries && !cancellationToken.IsCancellationRequested)
            {
                // Em caso de falha parcial na tentativa atual, estorna os bytes contabilizados antes de tentar novamente
                if (bytesAddedInCurrentAttempt > 0)
                {
                    onBytesCopied(-bytesAddedInCurrentAttempt);
                }
                await Task.Delay(1000 * attempt, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Calcula o hash criptográfico SHA-256 de um arquivo em streaming assíncrono.
    /// </summary>
    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        using var sha = SHA256.Create();
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        var hashBytes = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hashBytes);
    }
}
