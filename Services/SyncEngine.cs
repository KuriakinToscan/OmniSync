using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using OmniSync.Models;

namespace OmniSync.Services;

/// <summary>
/// Implementação principal do motor de sincronização de arquivos (<see cref="ISyncEngine"/>).
/// Projetado para alta resiliência em drives locais (NTFS/ext4/exFAT) e compartilhamentos de rede (NAS/SMB/NFS).
/// Não depende da camada de interface gráfica.
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

    /// <summary>
    /// Intervalo mínimo entre relatórios de progresso enviados à interface.
    /// </summary>
    private const long ProgressIntervalMs = 80;

    /// <summary>
    /// Grau de paralelismo do cálculo de hash (cada tarefa lê origem e destino simultaneamente).
    /// </summary>
    private static readonly int HashParallelism = Math.Clamp(Environment.ProcessorCount, 1, 4);

    /// <summary>
    /// Entrada indexada de um arquivo: caminho absoluto, tamanho e data de modificação lidos da própria enumeração.
    /// </summary>
    private readonly record struct FileEntry(string FullPath, long Length, DateTime LastWriteUtc);

    /// <summary>
    /// Entrada bruta de diretório (arquivo ou subpasta) produzida pela enumeração.
    /// </summary>
    private readonly record struct RawEntry(string Name, string FullPath, bool IsDirectory, long Length, DateTime LastWriteUtc);

    /// <summary>
    /// Resultado da indexação de uma árvore.
    /// </summary>
    private sealed class IndexResult
    {
        public Dictionary<string, FileEntry> Files { get; } = new(PathHelper.PathComparer);
        public List<string> UnreadableDirectories { get; } = new();
        public List<string> Warnings { get; } = new();
    }

    /// <inheritdoc />
    public Task<ScanResult> ScanDirectoriesAsync(
        string sourceDir,
        string destDir,
        ScanOptions options,
        IProgress<string>? logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        return Task.Run(async () =>
        {
            if (string.IsNullOrWhiteSpace(sourceDir) || string.IsNullOrWhiteSpace(destDir))
            {
                throw new ArgumentException("Os caminhos de Origem e Destino devem ser informados.");
            }

            if (!Directory.Exists(sourceDir))
            {
                throw new DirectoryNotFoundException($"O diretório de origem não existe ou não está acessível: {sourceDir}");
            }

            sourceDir = PathHelper.NormalizeDirectory(sourceDir);
            destDir = PathHelper.NormalizeDirectory(destDir);
            ValidateDirectoryPair(sourceDir, destDir);

            logger?.Report("Iniciando varredura diferencial...");
            logger?.Report($"Origem: {sourceDir}");
            logger?.Report($"Destino: {destDir}");

            var excludes = options.ExcludePatterns
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .ToArray();
            if (excludes.Length > 0)
            {
                logger?.Report($"Exclusões ativas: {string.Join("; ", excludes)}");
            }

            bool destExists = Directory.Exists(destDir);
            var stopwatch = Stopwatch.StartNew();

            // 1. Indexa Origem e Destino em paralelo (normalmente estão em dispositivos distintos)
            logger?.Report(destExists
                ? "Indexando Origem e Destino em paralelo (excluindo 'lixo/' e 'old/' do destino)..."
                : "Indexando Origem. O Destino ainda não existe: todos os arquivos serão novos.");

            var sourceTask = Task.Run(() => IndexTree(sourceDir, isDestination: false, excludes, logger, cancellationToken), cancellationToken);
            var destTask = destExists
                ? Task.Run(() => IndexTree(destDir, isDestination: true, excludes, logger, cancellationToken), cancellationToken)
                : Task.FromResult(new IndexResult());
            await Task.WhenAll(sourceTask, destTask);

            var source = sourceTask.Result;
            var dest = destTask.Result;

            var result = new ScanResult
            {
                SourceFileCount = source.Files.Count,
                DestinationFileCount = dest.Files.Count
            };
            result.Warnings.AddRange(source.Warnings);
            result.Warnings.AddRange(dest.Warnings);

            logger?.Report($"Indexação concluída em {stopwatch.Elapsed.TotalSeconds:n1} s: Origem {source.Files.Count} arquivos, Destino {dest.Files.Count} arquivos.");

            // 2. Preparação para correspondência tolerante a maiúsculas/minúsculas (apenas em plataformas que diferenciam)
            Dictionary<string, List<string>>? sourceByFolded = null;
            Dictionary<string, List<string>>? destByFolded = null;
            if (PathHelper.IsPlatformCaseSensitive)
            {
                sourceByFolded = GroupByFoldedCase(source.Files.Keys);
                destByFolded = GroupByFoldedCase(dest.Files.Keys);

                var caseDuplicates = sourceByFolded.Values.Where(g => g.Count > 1).ToList();
                if (caseDuplicates.Count > 0)
                {
                    var examples = string.Join(", ", caseDuplicates.Take(3).Select(g => string.Join(" / ", g)));
                    var warning = $"{caseDuplicates.Count} grupo(s) de arquivos na origem diferem apenas por maiúsculas/minúsculas " +
                                  $"(ex.: {examples}). Em destinos que não diferenciam maiúsculas (NTFS, exFAT, SMB) eles colidem; " +
                                  "o OmniSync não sobrescreve e reportará erro nesses itens.";
                    result.Warnings.Add(warning);
                    logger?.Report($"Aviso: {warning}");
                }
            }

            // 3. Comparação Origem -> Destino
            logger?.Report("Comparando diferenças entre Origem e Destino...");
            var matchedDest = new HashSet<string>(PathHelper.PathComparer);
            var hashJobs = new List<(string RelPath, FileEntry Src, FileEntry Dst)>();
            int caseOnlyMatches = 0;

            foreach (var (relPath, src) in source.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string? destKey = null;
                if (dest.Files.ContainsKey(relPath))
                {
                    destKey = relPath;
                }
                else if (destByFolded != null &&
                         destByFolded.TryGetValue(relPath, out var destCandidates) &&
                         destCandidates.Count == 1 &&
                         sourceByFolded![relPath].Count == 1 &&
                         !source.Files.ContainsKey(destCandidates[0]))
                {
                    // Mesmo arquivo com grafia diferente (ex.: destino em exFAT/SMB). Tratar como o mesmo arquivo
                    // evita copiar por cima e, em seguida, mover a própria cópia para lixo/.
                    destKey = destCandidates[0];
                    caseOnlyMatches++;
                }

                if (destKey == null)
                {
                    result.Items.Add(new SyncItem
                    {
                        RelativePath = relPath,
                        SourceFullPath = src.FullPath,
                        DestinationFullPath = Path.Combine(destDir, relPath),
                        SourceSize = src.Length,
                        SourceModifiedUtc = src.LastWriteUtc,
                        ActionType = SyncActionType.New,
                        StatusMessage = "Pendente cópia"
                    });
                    continue;
                }

                matchedDest.Add(destKey);
                var dst = dest.Files[destKey];

                if (src.Length != dst.Length)
                {
                    result.Items.Add(CreatePairItem(relPath, src, dst, identical: false));
                }
                else if (options.UseDeepHash)
                {
                    hashJobs.Add((relPath, src, dst));
                }
                else
                {
                    result.Items.Add(CreatePairItem(relPath, src, dst, AreTimestampsEquivalent(src.LastWriteUtc, dst.LastWriteUtc, options.TolerateDstShift)));
                }
            }

            if (caseOnlyMatches > 0)
            {
                logger?.Report($"Aviso: {caseOnlyMatches} arquivo(s) correspondem ao destino apenas ignorando maiúsculas/minúsculas e foram tratados como o mesmo arquivo.");
            }

            // 4. Verificação profunda SHA-256 em paralelo limitado
            if (hashJobs.Count > 0)
            {
                logger?.Report($"Calculando SHA-256 de {hashJobs.Count} pares de mesmo tamanho (paralelismo {HashParallelism})...");
                var identical = new bool[hashJobs.Count];
                int done = 0;

                await Parallel.ForEachAsync(
                    Enumerable.Range(0, hashJobs.Count),
                    new ParallelOptions { MaxDegreeOfParallelism = HashParallelism, CancellationToken = cancellationToken },
                    async (index, token) =>
                    {
                        var job = hashJobs[index];
                        try
                        {
                            // Origem e destino são lidos simultaneamente (normalmente em dispositivos distintos)
                            var hashes = await Task.WhenAll(
                                ComputeSha256Async(job.Src.FullPath, token),
                                ComputeSha256Async(job.Dst.FullPath, token));
                            identical[index] = hashes[0].AsSpan().SequenceEqual(hashes[1]);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            identical[index] = false;
                            logger?.Report($"Aviso ao calcular hash de '{job.RelPath}': {ex.Message}");
                        }

                        var completed = Interlocked.Increment(ref done);
                        if (completed % 500 == 0)
                        {
                            logger?.Report($"Hash: {completed}/{hashJobs.Count} pares verificados...");
                        }
                    });

                for (int i = 0; i < hashJobs.Count; i++)
                {
                    var job = hashJobs[i];
                    result.Items.Add(CreatePairItem(job.RelPath, job.Src, job.Dst, identical[i]));
                }
            }

            // 5. Comparação Destino -> Origem (órfãos)
            int protectedOrphans = 0;
            foreach (var (relPath, dst) in dest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (matchedDest.Contains(relPath) || source.Files.ContainsKey(relPath))
                {
                    continue;
                }

                // Se a pasta correspondente na Origem não pôde ser lida, a ausência do arquivo não é confiável:
                // nunca classificar como órfão (evita mover dados válidos para lixo/ por falha de leitura).
                if (IsUnderAny(relPath, source.UnreadableDirectories))
                {
                    protectedOrphans++;
                    continue;
                }

                result.Items.Add(new SyncItem
                {
                    RelativePath = relPath,
                    DestinationFullPath = dst.FullPath,
                    DestinationSize = dst.Length,
                    DestinationModifiedUtc = dst.LastWriteUtc,
                    ActionType = SyncActionType.OrphanTrash,
                    StatusMessage = "Será movido para lixo/"
                });
            }

            if (protectedOrphans > 0)
            {
                var warning = $"{protectedOrphans} arquivo(s) do destino não foram classificados como órfãos porque a pasta " +
                              "correspondente na origem não pôde ser lida. Verifique permissões ou conexão e analise novamente.";
                result.Warnings.Add(warning);
                logger?.Report($"Aviso: {warning}");
            }

            result.Items.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));
            logger?.Report($"Varredura concluída em {stopwatch.Elapsed.TotalSeconds:n1} s. Total de itens analisados: {result.Items.Count}");
            return result;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<SyncResult> ExecuteSyncAsync(
        IReadOnlyList<SyncItem> items,
        string destDir,
        Func<SyncItem, Task<ConflictResolution>> resolveConflictAsync,
        IProgress<SyncItemUpdate>? itemUpdates,
        IProgress<SyncProgressReport>? progress,
        IProgress<string>? logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(resolveConflictAsync);
        var itemList = items.ToList();

        return Task.Run(async () =>
        {
            var normalizedDestDir = PathHelper.NormalizeDirectory(destDir);
            var trashDir = Path.Combine(normalizedDestDir, PathHelper.TrashFolderName);
            var oldDir = Path.Combine(normalizedDestDir, PathHelper.OldFolderName);
            Directory.CreateDirectory(normalizedDestDir);

            var result = new SyncResult();
            var pending = itemList.Where(i => !i.IsProcessed && i.ActionType != SyncActionType.Identical).ToList();

            long totalBytesToCopy = pending
                .Where(i => i.ActionType is SyncActionType.New or SyncActionType.Conflict)
                .Sum(i => i.SourceSize);
            int actionableFilesCount = pending.Count;

            var overallStopwatch = Stopwatch.StartNew();
            long lastProgressReportTick = 0;
            long totalBytesCopiedSoFar = 0;
            int processedFilesCount = 0;
            string currentFileName = string.Empty;
            var directoriesToPrune = new List<string>();

            logger?.Report($"Iniciando execução da sincronização ({actionableFilesCount} ações necessárias)...");

            void Update(SyncItem item, string status, bool processed, bool error, ConflictResolution? resolution = null)
            {
                var update = new SyncItemUpdate(item, status, processed, error, resolution);
                if (itemUpdates != null)
                {
                    itemUpdates.Report(update);
                }
                else
                {
                    update.Apply();
                }
            }

            void Fail(SyncItem item, string prefix, Exception ex)
            {
                result.FailedItems.Add(item);
                Update(item, $"{prefix}: {ex.Message}", processed: false, error: true);
                logger?.Report($"[ERRO] {prefix} '{item.RelativePath}': {ex.Message}");
            }

            void OnBytesCopied(long delta)
            {
                Interlocked.Add(ref totalBytesCopiedSoFar, delta);
                ReportProgress(force: false);
            }

            foreach (var item in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                currentFileName = item.RelativePath;

                switch (item.ActionType)
                {
                    case SyncActionType.OrphanTrash:
                        try
                        {
                            var destRel = Path.GetRelativePath(normalizedDestDir, item.DestinationFullPath!);
                            var movedTo = PathHelper.MoveIntoReservedFolder(item.DestinationFullPath!, trashDir, destRel);
                            if (movedTo == null)
                            {
                                result.Skipped++;
                                Update(item, "Pulado: o arquivo já não existia no destino", processed: true, error: false);
                                logger?.Report($"[LIXO] Já não existia: {item.RelativePath}");
                            }
                            else
                            {
                                result.Trashed++;
                                directoriesToPrune.Add(Path.GetDirectoryName(item.DestinationFullPath!)!);
                                Update(item, "Movido para lixo/", processed: true, error: false);
                                logger?.Report($"[LIXO] Movido: {item.RelativePath}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Fail(item, "Erro ao mover para lixo/", ex);
                        }
                        break;

                    case SyncActionType.Conflict:
                        var resolution = item.Resolution;
                        if (resolution == ConflictResolution.AskUser)
                        {
                            logger?.Report($"[CONFLITO] Solicitando decisão do usuário para: {item.RelativePath}");
                            // Pausa o cronômetro enquanto aguarda a resposta humana para não distorcer velocidade/ETA
                            overallStopwatch.Stop();
                            try
                            {
                                resolution = await resolveConflictAsync(item);
                            }
                            finally
                            {
                                overallStopwatch.Start();
                            }
                        }

                        if (resolution != ConflictResolution.Replace)
                        {
                            Interlocked.Add(ref totalBytesToCopy, -item.SourceSize);
                            result.Ignored++;
                            Update(item, "Ignorado (destino mantido)", processed: true, error: false, ConflictResolution.Ignore);
                            logger?.Report($"[CONFLITO] Ignorado: {item.RelativePath}");
                            break;
                        }

                        try
                        {
                            logger?.Report($"[SUBSTITUIR] {item.RelativePath}");
                            await ReplaceFileAsync(item, normalizedDestDir, oldDir, OnBytesCopied, cancellationToken);
                            result.Replaced++;
                            result.BytesCopied += item.SourceSize;
                            Update(item, "Substituído (anterior em old/)", processed: true, error: false, ConflictResolution.Replace);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Add(ref totalBytesToCopy, -item.SourceSize);
                            Fail(item, "Erro ao substituir", ex);
                        }
                        break;

                    case SyncActionType.New:
                        try
                        {
                            logger?.Report($"[NOVO] Copiando: {item.RelativePath}");
                            await CopyNewFileAsync(item, OnBytesCopied, cancellationToken);
                            result.Copied++;
                            result.BytesCopied += item.SourceSize;
                            Update(item, "Copiado", processed: true, error: false);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Add(ref totalBytesToCopy, -item.SourceSize);
                            Fail(item, "Erro ao copiar", ex);
                        }
                        break;
                }

                processedFilesCount++;
                ReportProgress(force: true);
            }

            // Remove pastas que ficaram vazias no destino após mover órfãos (exclusão não recursiva)
            var pruned = PathHelper.RemoveEmptyDirectories(directoriesToPrune, normalizedDestDir);
            if (pruned > 0)
            {
                logger?.Report($"{pruned} pasta(s) vazia(s) removida(s) do destino após mover órfãos.");
            }

            overallStopwatch.Stop();
            result.Duration = overallStopwatch.Elapsed;
            currentFileName = string.Empty;
            ReportProgress(force: true);

            logger?.Report(result.HasFailures
                ? $"[ERRO] Sincronização concluída com {result.Failed} falha(s): {result.Summary}."
                : $"Sincronização concluída sem falhas: {result.Summary}.");
            return result;

            // Função local para envio controlado (throttled) de relatórios de progresso à UI
            void ReportProgress(bool force)
            {
                if (progress == null) return;

                long nowTicks = Environment.TickCount64;
                if (!force && nowTicks - Interlocked.Read(ref lastProgressReportTick) < ProgressIntervalMs)
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
    /// Impede combinações de Origem/Destino que levariam a recursão ou perda de dados.
    /// </summary>
    /// <exception cref="InvalidOperationException">As pastas são iguais ou uma está dentro da outra.</exception>
    private static void ValidateDirectoryPair(string sourceDir, string destDir)
    {
        if (PathHelper.AreSamePath(sourceDir, destDir))
        {
            throw new InvalidOperationException("O diretório de Origem e o de Destino não podem ser a mesma pasta.");
        }
        if (PathHelper.IsSubPathOf(destDir, sourceDir))
        {
            throw new InvalidOperationException(
                "O Destino está dentro da Origem. Isso faria a origem incluir as próprias cópias, 'lixo/' e 'old/' a cada execução. Escolha um destino fora da origem.");
        }
        if (PathHelper.IsSubPathOf(sourceDir, destDir))
        {
            throw new InvalidOperationException(
                "A Origem está dentro do Destino. Todo o restante do destino seria tratado como órfão. Ative 'subpasta com nome da origem' ou escolha outro destino.");
        }
    }

    /// <summary>
    /// Percorre uma árvore com pilha explícita, lendo nome, tipo, tamanho e data diretamente da enumeração
    /// (uma única consulta de metadados por entrada, relevante em SMB/NAS). Pastas ilegíveis são registradas,
    /// não abortam a varredura.
    /// </summary>
    private static IndexResult IndexTree(
        string rootDir,
        bool isDestination,
        IReadOnlyList<string> excludes,
        IProgress<string>? logger,
        CancellationToken cancellationToken)
    {
        var result = new IndexResult();
        var label = isDestination ? "Destino" : "Origem";
        var dirs = new Stack<(string Path, bool IsRoot)>();
        dirs.Push((rootDir, true));

        var enumOptions = new EnumerationOptions
        {
            // Falhas de acesso são tratadas por pasta, abaixo, para que fiquem registradas
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        int nextProgressReport = 20000;

        while (dirs.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (currentDir, isRoot) = dirs.Pop();

            List<RawEntry> entries;
            try
            {
                entries = new FileSystemEnumerable<RawEntry>(
                    currentDir,
                    (ref FileSystemEntry e) => new RawEntry(
                        e.FileName.ToString(),
                        e.ToFullPath(),
                        e.IsDirectory,
                        e.IsDirectory ? 0 : e.Length,
                        e.LastWriteTimeUtc.UtcDateTime),
                    enumOptions).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                result.UnreadableDirectories.Add(Path.GetRelativePath(rootDir, currentDir));
                logger?.Report($"Aviso: pasta ilegível ignorada no {label} '{currentDir}': {ex.Message}");
                continue;
            }

            foreach (var entry in entries)
            {
                if (entry.IsDirectory)
                {
                    if (PathHelper.IsSystemIgnoredDirectoryName(entry.Name) || IsExcluded(entry.Name, excludes))
                    {
                        continue;
                    }

                    if (isRoot && PathHelper.IsReservedFolderName(entry.Name))
                    {
                        if (!isDestination)
                        {
                            var warning = $"A pasta '{entry.Name}' na raiz da origem foi ignorada: o nome é reservado pelo OmniSync " +
                                          "para as pastas de segurança do destino. Renomeie-a para que seja sincronizada.";
                            result.Warnings.Add(warning);
                            logger?.Report($"Aviso: {warning}");
                        }
                        continue;
                    }

                    dirs.Push((entry.FullPath, false));
                    continue;
                }

                if (entry.Name.EndsWith(PathHelper.TempSuffix, StringComparison.OrdinalIgnoreCase) || IsExcluded(entry.Name, excludes))
                {
                    continue;
                }

                result.Files[Path.GetRelativePath(rootDir, entry.FullPath)] =
                    new FileEntry(entry.FullPath, entry.Length, entry.LastWriteUtc);

                if (result.Files.Count >= nextProgressReport)
                {
                    logger?.Report($"{label}: {result.Files.Count} arquivos indexados...");
                    nextProgressReport += 20000;
                }
            }
        }

        if (result.UnreadableDirectories.Count > 0 && !isDestination)
        {
            result.Warnings.Add($"{result.UnreadableDirectories.Count} pasta(s) da origem não puderam ser lidas e foram ignoradas (detalhes no log).");
        }

        return result;
    }

    /// <summary>
    /// Verifica se um nome de arquivo ou pasta corresponde a algum padrão de exclusão (curingas <c>*</c> e <c>?</c>, sem diferenciar maiúsculas).
    /// </summary>
    private static bool IsExcluded(string name, IReadOnlyList<string> excludes)
    {
        for (int i = 0; i < excludes.Count; i++)
        {
            if (FileSystemName.MatchesSimpleExpression(excludes[i], name, ignoreCase: true))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Agrupa caminhos relativos pela grafia sem distinção de maiúsculas.
    /// </summary>
    private static Dictionary<string, List<string>> GroupByFoldedCase(IEnumerable<string> keys)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<string>(1);
                groups[key] = list;
            }
            list.Add(key);
        }
        return groups;
    }

    /// <summary>
    /// Verifica se um caminho relativo está dentro de alguma das pastas relativas informadas.
    /// </summary>
    private static bool IsUnderAny(string relPath, List<string> relativeDirectories)
    {
        foreach (var dir in relativeDirectories)
        {
            if (dir == ".") return true;
            var prefix = dir + Path.DirectorySeparatorChar;
            if (relPath.StartsWith(prefix, PathHelper.PathComparison))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Compara datas de modificação com tolerância de 2 s e, opcionalmente, deslocamento exato de 1 h (horário de verão em FAT).
    /// </summary>
    private static bool AreTimestampsEquivalent(DateTime a, DateTime b, bool tolerateDstShift)
    {
        var delta = Math.Abs((a - b).TotalSeconds);
        if (delta <= TimestampToleranceSeconds) return true;
        return tolerateDstShift && Math.Abs(delta - 3600.0) <= TimestampToleranceSeconds;
    }

    /// <summary>
    /// Cria um item para arquivos presentes nos dois lados.
    /// </summary>
    private static SyncItem CreatePairItem(string relPath, FileEntry src, FileEntry dst, bool identical)
    {
        return new SyncItem
        {
            RelativePath = relPath,
            SourceFullPath = src.FullPath,
            DestinationFullPath = dst.FullPath,
            SourceSize = src.Length,
            DestinationSize = dst.Length,
            SourceModifiedUtc = src.LastWriteUtc,
            DestinationModifiedUtc = dst.LastWriteUtc,
            ActionType = identical ? SyncActionType.Identical : SyncActionType.Conflict,
            IsProcessed = identical,
            StatusMessage = identical ? "Idêntico (ignorado)" : "Requer decisão"
        };
    }

    /// <summary>
    /// Copia um arquivo novo para o destino via arquivo temporário e renomeação. Nunca sobrescreve:
    /// se um arquivo surgiu no destino depois da análise, a operação falha com mensagem explicativa.
    /// </summary>
    private static async Task CopyNewFileAsync(SyncItem item, Action<long> onBytesCopied, CancellationToken cancellationToken)
    {
        var destFile = item.DestinationFullPath!;
        if (File.Exists(destFile) || Directory.Exists(destFile))
        {
            throw new IOException("Um arquivo com este nome surgiu no destino após a análise (ou difere só em maiúsculas). Analise novamente.");
        }

        var tempFile = destFile + PathHelper.TempSuffix;
        await CopyToTempWithRetryAsync(item.SourceFullPath!, tempFile, onBytesCopied, cancellationToken);

        try
        {
            File.Move(tempFile, destFile, overwrite: false);
        }
        catch
        {
            PathHelper.TryDeleteFile(tempFile);
            throw;
        }
    }

    /// <summary>
    /// Substitui um arquivo do destino preservando a versão atual em <c>old/</c>.
    /// Ordem segura: (1) copia a nova versão para temporário; (2) move a versão atual para <c>old/</c>;
    /// (3) renomeia o temporário. Se (3) falhar, a versão anterior é devolvida ao lugar.
    /// </summary>
    private static async Task ReplaceFileAsync(
        SyncItem item,
        string destRoot,
        string oldDir,
        Action<long> onBytesCopied,
        CancellationToken cancellationToken)
    {
        var destFile = item.DestinationFullPath!;
        var tempFile = destFile + PathHelper.TempSuffix;

        await CopyToTempWithRetryAsync(item.SourceFullPath!, tempFile, onBytesCopied, cancellationToken);

        string? archivedPath = null;
        try
        {
            archivedPath = PathHelper.MoveIntoReservedFolder(destFile, oldDir, Path.GetRelativePath(destRoot, destFile));
            File.Move(tempFile, destFile, overwrite: false);
        }
        catch
        {
            PathHelper.TryDeleteFile(tempFile);
            if (archivedPath != null && !File.Exists(destFile))
            {
                try
                {
                    File.Move(archivedPath, destFile, overwrite: false);
                }
                catch
                {
                    // A versão anterior continua íntegra em old/; o erro original é reportado ao chamador
                }
            }
            throw;
        }
    }

    /// <summary>
    /// Copia um arquivo para um caminho temporário em blocos de 2 MB, com reporte contínuo de bytes,
    /// pré-alocação, verificação de tamanho final, preservação da data de modificação e retentativa
    /// apenas para falhas transitórias. Em qualquer falha definitiva ou cancelamento, o temporário é removido.
    /// </summary>
    private static async Task CopyToTempWithRetryAsync(
        string sourceFile,
        string tempFile,
        Action<long> onBytesCopied,
        CancellationToken cancellationToken)
    {
        var destDirectory = Path.GetDirectoryName(tempFile);
        if (!string.IsNullOrEmpty(destDirectory))
        {
            Directory.CreateDirectory(destDirectory);
        }

        int attempt = 0;
        while (true)
        {
            attempt++;
            long bytesAddedInCurrentAttempt = 0;
            try
            {
                PathHelper.TryDeleteFile(tempFile);

                var sourceInfo = new FileInfo(sourceFile);
                if (!sourceInfo.Exists)
                {
                    throw new FileNotFoundException("O arquivo de origem não existe mais.", sourceFile);
                }
                long expectedLength = sourceInfo.Length;
                var lastWriteUtc = sourceInfo.LastWriteTimeUtc;

                var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                try
                {
                    await using var srcStream = new FileStream(sourceFile, new FileStreamOptions
                    {
                        Mode = FileMode.Open,
                        Access = FileAccess.Read,
                        Share = FileShare.Read,
                        BufferSize = 0,
                        Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                    });
                    await using var dstStream = new FileStream(tempFile, new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.Write,
                        Share = FileShare.None,
                        BufferSize = 0,
                        Options = FileOptions.Asynchronous,
                        PreallocationSize = expectedLength
                    });

                    int bytesRead;
                    while ((bytesRead = await srcStream.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)) > 0)
                    {
                        await dstStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                        bytesAddedInCurrentAttempt += bytesRead;
                        onBytesCopied(bytesRead);
                    }

                    if (bytesAddedInCurrentAttempt != expectedLength)
                    {
                        throw new IOException(
                            $"O arquivo de origem mudou durante a cópia ({bytesAddedInCurrentAttempt} de {expectedLength} bytes).");
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                // Preserva a data/hora de modificação original para futuras comparações diferenciais
                File.SetLastWriteTimeUtc(tempFile, lastWriteUtc);
                return;
            }
            catch (Exception ex) when (attempt < MaxRetries && !cancellationToken.IsCancellationRequested && IsTransient(ex))
            {
                // Estorna os bytes contabilizados na tentativa falha antes de tentar novamente
                if (bytesAddedInCurrentAttempt > 0)
                {
                    onBytesCopied(-bytesAddedInCurrentAttempt);
                }
                await Task.Delay(1000 * attempt, cancellationToken);
            }
            catch
            {
                if (bytesAddedInCurrentAttempt > 0)
                {
                    onBytesCopied(-bytesAddedInCurrentAttempt);
                }
                PathHelper.TryDeleteFile(tempFile);
                throw;
            }
        }
    }

    /// <summary>
    /// Indica se uma falha de E/S pode ser resolvida com nova tentativa (rede instável, bloqueio temporário).
    /// Falhas definitivas (arquivo inexistente, caminho longo, permissão) não são repetidas.
    /// </summary>
    private static bool IsTransient(Exception ex)
    {
        return ex is IOException
               and not FileNotFoundException
               and not DirectoryNotFoundException
               and not PathTooLongException
               and not DriveNotFoundException;
    }

    /// <summary>
    /// Calcula o hash SHA-256 de um arquivo em streaming assíncrono.
    /// </summary>
    private static async Task<byte[]> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(filePath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = 1024 * 1024,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
        return await SHA256.HashDataAsync(stream, cancellationToken);
    }
}
