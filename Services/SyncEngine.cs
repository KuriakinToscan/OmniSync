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
using OmniSync.Services.FileSystems;

namespace OmniSync.Services;

/// <summary>
/// Implementação principal do motor de sincronização de arquivos (<see cref="ISyncEngine"/>).
/// Projetado para alta resiliência em drives locais (NTFS/ext4/exFAT) e compartilhamentos de rede (NAS/SMB/NFS).
/// Não depende da camada de interface gráfica. Todo acesso a arquivos passa por <see cref="IFileSystem"/>,
/// um para a origem e outro para o destino.
/// </summary>
public class SyncEngine : ISyncEngine
{
    private readonly Func<string, IFileSystem> _resolveSource;
    private readonly Func<string, IFileSystem> _resolveDest;

    /// <summary>
    /// Cria o motor. Sem argumentos, cada caminho escolhe o próprio sistema de arquivos
    /// (<see cref="FileSystemProvider.For"/>: <c>sftp://</c> ou local).
    /// </summary>
    /// <param name="sourceFileSystem">Sistema de arquivos fixo da origem; <c>null</c> escolhe pelo caminho.</param>
    /// <param name="destinationFileSystem">Sistema de arquivos fixo do destino; <c>null</c> escolhe pelo caminho.</param>
    public SyncEngine(IFileSystem? sourceFileSystem = null, IFileSystem? destinationFileSystem = null)
    {
        _resolveSource = sourceFileSystem != null ? _ => sourceFileSystem : FileSystemProvider.For;
        _resolveDest = destinationFileSystem != null ? _ => destinationFileSystem : FileSystemProvider.For;
    }

    /// <summary>
    /// Tamanho do buffer de leitura/escrita em streaming (2 MB), otimizado para maximizar throughput em redes SMB/NAS e SSDs.
    /// </summary>
    private const int BufferSize = 1024 * 1024 * 2;

    /// <summary>
    /// Número máximo de tentativas automáticas em caso de falha transitória de rede ou bloqueio temporário de I/O.
    /// </summary>
    private const int MaxRetries = 3;

    /// <summary>
    /// Confere cada cópia por SHA-256 antes de ela ocupar o lugar no destino: o hash da origem é calculado
    /// durante a leitura da cópia e comparado com o do arquivo temporário relido do destino.
    /// Ligado por padrão; os testes podem desligar.
    /// </summary>
    public bool VerifyCopies { get; set; } = true;

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

            var sourceFs = _resolveSource(sourceDir);
            var destFs = _resolveDest(destDir);
            if (!sourceFs.DirectoryExists(sourceDir))
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

            bool destExists = destFs.DirectoryExists(destDir);
            var stopwatch = Stopwatch.StartNew();

            // 1. Indexa Origem e Destino em paralelo (normalmente estão em dispositivos distintos)
            logger?.Report(destExists
                ? "Indexando Origem e Destino em paralelo (excluindo 'lixo/' e 'old/' do destino)..."
                : "Indexando Origem. O Destino ainda não existe: todos os arquivos serão novos.");

            var sourceTask = Task.Run(() => IndexTree(sourceFs, sourceDir, isDestination: false, excludes, logger, cancellationToken), cancellationToken);
            var destTask = destExists
                ? Task.Run(() => IndexTree(destFs, destDir, isDestination: true, excludes, logger, cancellationToken), cancellationToken)
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

            logger?.Report($"Indexação concluída em {FormatElapsed(stopwatch.Elapsed)}: Origem {source.Files.Count} arquivos, Destino {dest.Files.Count} arquivos.");

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
                    var warning = $"{PtText.Count(caseDuplicates.Count, "grupo", "grupos")} de arquivos na origem {PtText.Pick(caseDuplicates.Count, "difere", "diferem")} apenas por maiúsculas/minúsculas " +
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
                        DestinationFullPath = destFs.Combine(destDir, relPath),
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
                logger?.Report($"Aviso: {PtText.Count(caseOnlyMatches, "arquivo corresponde", "arquivos correspondem")} ao destino apenas ignorando maiúsculas/minúsculas e {PtText.Pick(caseOnlyMatches, "foi tratado", "foram tratados")} como o mesmo arquivo.");
            }

            // 4. Verificação profunda SHA-256 em paralelo limitado
            if (hashJobs.Count > 0)
            {
                logger?.Report($"Calculando SHA-256 de {hashJobs.Count} arquivos de mesmo tamanho nos dois lados (paralelismo {HashParallelism})...");
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
                                ComputeSha256Async(sourceFs, job.Src.FullPath, token),
                                ComputeSha256Async(destFs, job.Dst.FullPath, token));
                            identical[index] = hashes[0].AsSpan().SequenceEqual(hashes[1]);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            identical[index] = false;
                            logger?.Report($"Aviso ao calcular hash de '{job.RelPath}': {ErrorMessages.ForLog(ex)}");
                        }

                        var completed = Interlocked.Increment(ref done);
                        if (completed % 500 == 0)
                        {
                            logger?.Report($"Hash: {completed}/{hashJobs.Count} arquivos verificados...");
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
                var warning = $"{PtText.Count(protectedOrphans, "arquivo do destino não foi classificado como órfão", "arquivos do destino não foram classificados como órfãos")} porque a pasta " +
                              "correspondente na origem não pôde ser lida. Verifique permissões ou conexão e analise novamente.";
                result.Warnings.Add(warning);
                logger?.Report($"Aviso: {warning}");
            }

            result.Items.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));
            logger?.Report($"Varredura concluída em {FormatElapsed(stopwatch.Elapsed)}. Total de itens analisados: {result.Items.Count}");
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
            var destFs = _resolveDest(normalizedDestDir);
            var trashDir = destFs.Combine(normalizedDestDir, PathHelper.TrashFolderName);
            var oldDir = destFs.Combine(normalizedDestDir, PathHelper.OldFolderName);
            destFs.CreateDirectory(normalizedDestDir);

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
                Update(item, $"{prefix}: {ErrorMessages.Translate(ex)}", processed: false, error: true);
                logger?.Report($"[ERRO] {prefix} '{item.RelativePath}': {ErrorMessages.ForLog(ex)}");
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
                            var destRel = destFs.GetRelativePath(normalizedDestDir, item.DestinationFullPath!);
                            var movedTo = PathHelper.MoveIntoReservedFolder(destFs, item.DestinationFullPath!, trashDir, destRel);
                            if (movedTo == null)
                            {
                                result.Skipped++;
                                Update(item, "Pulado: o arquivo já não existia no destino", processed: true, error: false);
                                logger?.Report($"[LIXO] Já não existia: {item.RelativePath}");
                            }
                            else
                            {
                                result.Trashed++;
                                directoriesToPrune.Add(destFs.GetDirectoryName(item.DestinationFullPath!)!);
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
                            await ReplaceFileAsync(destFs, item, normalizedDestDir, oldDir, OnBytesCopied, VerifyCopies, cancellationToken);
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
                            await CopyNewFileAsync(destFs, item, OnBytesCopied, VerifyCopies, cancellationToken);
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
            var pruned = PathHelper.RemoveEmptyDirectories(destFs, directoriesToPrune, normalizedDestDir);
            if (pruned > 0)
            {
                logger?.Report($"{PtText.Count(pruned, "pasta vazia removida", "pastas vazias removidas")} do destino após mover órfãos.");
            }

            overallStopwatch.Stop();
            result.Duration = overallStopwatch.Elapsed;
            currentFileName = string.Empty;
            ReportProgress(force: true);

            if (VerifyCopies && result.Copied + result.Replaced > 0)
            {
                logger?.Report($"Verificação: {PtText.Count(result.Copied + result.Replaced, "cópia conferida", "cópias conferidas")} com a origem por SHA-256 antes de ocupar o lugar no destino.");
            }

            logger?.Report(result.HasFailures
                ? $"[ERRO] Sincronização concluída em {FormatElapsed(result.Duration)} com {PtText.Count(result.Failed, "falha", "falhas")}: {result.Summary}."
                : $"Sincronização concluída em {FormatElapsed(result.Duration)}, sem falhas: {result.Summary}.");
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
    /// Formata um tempo de análise para o registro: milissegundos abaixo de 1 s (ex.: <c>42 ms</c>), segundos acima.
    /// </summary>
    /// <param name="elapsed">Tempo decorrido.</param>
    public static string FormatElapsed(TimeSpan elapsed) => elapsed.TotalSeconds < 1
        ? $"{elapsed.TotalMilliseconds:N0} ms"
        : elapsed.TotalSeconds < 10 ? $"{elapsed.TotalSeconds:N2} s" : $"{elapsed.TotalSeconds:N1} s";

    /// <summary>
    /// Impede combinações de Origem/Destino que levariam a recursão ou perda de dados.
    /// </summary>
    /// <exception cref="InvalidOperationException">As pastas são iguais ou uma está dentro da outra.</exception>
    private static void ValidateDirectoryPair(string sourceDir, string destDir)
    {
        var problem = PathHelper.DescribePairProblem(sourceDir, destDir);
        if (problem != null)
        {
            throw new InvalidOperationException(problem);
        }
    }

    /// <summary>
    /// Percorre uma árvore com pilha explícita, lendo nome, tipo, tamanho e data diretamente da listagem de cada pasta
    /// (<see cref="IFileSystem.ListDirectory"/>). Pastas ilegíveis são registradas, não abortam a varredura.
    /// </summary>
    private static IndexResult IndexTree(
        IFileSystem fs,
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

        int nextProgressReport = 20000;

        while (dirs.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (currentDir, isRoot) = dirs.Pop();

            IReadOnlyList<FsEntry> entries;
            try
            {
                // Falhas de acesso são tratadas por pasta, aqui, para que fiquem registradas
                entries = fs.ListDirectory(currentDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                result.UnreadableDirectories.Add(fs.GetRelativePath(rootDir, currentDir));
                logger?.Report($"Aviso: pasta ilegível ignorada no {label} '{currentDir}': {ErrorMessages.ForLog(ex)}");
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

                result.Files[fs.GetRelativePath(rootDir, entry.FullPath)] =
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
            result.Warnings.Add($"{PtText.Count(result.UnreadableDirectories.Count, "pasta da origem não pôde ser lida e foi ignorada", "pastas da origem não puderam ser lidas e foram ignoradas")} (detalhes no log).");
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
    private async Task CopyNewFileAsync(IFileSystem destFs, SyncItem item, Action<long> onBytesCopied, bool verify, CancellationToken cancellationToken)
    {
        var destFile = item.DestinationFullPath!;
        if (destFs.Exists(destFile))
        {
            throw new IOException("Um arquivo com este nome surgiu no destino após a análise (ou difere só em maiúsculas). Analise novamente.");
        }

        var tempFile = destFile + PathHelper.TempSuffix;
        await CopyToTempWithRetryAsync(destFs, item.SourceFullPath!, tempFile, onBytesCopied, verify, cancellationToken);

        try
        {
            destFs.MoveFile(tempFile, destFile);
        }
        catch
        {
            PathHelper.TryDeleteFile(destFs, tempFile);
            throw;
        }
    }

    /// <summary>
    /// Substitui um arquivo do destino preservando a versão atual em <c>old/</c>.
    /// Ordem segura: (1) copia a nova versão para temporário; (2) move a versão atual para <c>old/</c>;
    /// (3) renomeia o temporário. Se (3) falhar, a versão anterior é devolvida ao lugar.
    /// </summary>
    private async Task ReplaceFileAsync(
        IFileSystem destFs,
        SyncItem item,
        string destRoot,
        string oldDir,
        Action<long> onBytesCopied,
        bool verify,
        CancellationToken cancellationToken)
    {
        var destFile = item.DestinationFullPath!;
        var tempFile = destFile + PathHelper.TempSuffix;

        await CopyToTempWithRetryAsync(destFs, item.SourceFullPath!, tempFile, onBytesCopied, verify, cancellationToken);

        string? archivedPath = null;
        try
        {
            archivedPath = PathHelper.MoveIntoReservedFolder(destFs, destFile, oldDir, destFs.GetRelativePath(destRoot, destFile));
            destFs.MoveFile(tempFile, destFile);
        }
        catch
        {
            PathHelper.TryDeleteFile(destFs, tempFile);
            if (archivedPath != null && !destFs.FileExists(destFile))
            {
                try
                {
                    destFs.MoveFile(archivedPath, destFile);
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
    /// Com <paramref name="verify"/>, o temporário é relido pelo mesmo handle e comparado por SHA-256
    /// com os bytes lidos da origem; divergência conta como falha transitória (nova tentativa) e, se persistir,
    /// a operação falha sem tocar no arquivo do destino.
    /// </summary>
    private async Task CopyToTempWithRetryAsync(
        IFileSystem destFs,
        string sourceFile,
        string tempFile,
        Action<long> onBytesCopied,
        bool verify,
        CancellationToken cancellationToken)
    {
        var sourceFs = _resolveSource(sourceFile);
        var destDirectory = destFs.GetDirectoryName(tempFile);
        if (!string.IsNullOrEmpty(destDirectory))
        {
            destFs.CreateDirectory(destDirectory);
        }

        int attempt = 0;
        while (true)
        {
            attempt++;
            long bytesAddedInCurrentAttempt = 0;
            try
            {
                PathHelper.TryDeleteFile(destFs, tempFile);

                var sourceInfo = sourceFs.GetFile(sourceFile)
                    ?? throw new FileNotFoundException("O arquivo de origem não existe mais.", sourceFile);
                long expectedLength = sourceInfo.Length;
                var lastWriteUtc = sourceInfo.LastWriteUtc;

                var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                using var sourceHash = verify ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
                try
                {
                    await using var srcStream = sourceFs.OpenRead(sourceFile);
                    // Leitura também (readBack): a verificação relê o temporário pelo mesmo fluxo, sem reabrir o arquivo
                    await using var dstStream = destFs.CreateNew(tempFile, expectedLength, readBack: verify);

                    int bytesRead;
                    while ((bytesRead = await srcStream.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)) > 0)
                    {
                        sourceHash?.AppendData(buffer, 0, bytesRead);
                        await dstStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                        bytesAddedInCurrentAttempt += bytesRead;
                        onBytesCopied(bytesRead);
                    }

                    if (bytesAddedInCurrentAttempt != expectedLength)
                    {
                        throw new IOException(
                            $"O arquivo de origem mudou durante a cópia ({bytesAddedInCurrentAttempt} de {expectedLength} bytes).");
                    }

                    if (sourceHash != null)
                    {
                        // Relê o que foi gravado pelo mesmo handle. Reabrir o arquivo custaria uma nova
                        // inspeção do antivírus por arquivo, e forçar a gravação física (flush) não ajudaria:
                        // a releitura viria do cache do sistema de qualquer forma.
                        await dstStream.FlushAsync(cancellationToken);
                        dstStream.Position = 0;
                        var expected = sourceHash.GetHashAndReset();
                        var actual = await SHA256.HashDataAsync(dstStream, cancellationToken);
                        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
                        {
                            throw new CopyVerificationException();
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                // Preserva a data/hora de modificação original para futuras comparações diferenciais
                destFs.SetLastWriteTimeUtc(tempFile, lastWriteUtc);
                return;
            }
            catch (Exception ex) when (attempt < MaxRetries && !cancellationToken.IsCancellationRequested && IsTransient(ex))
            {
                PathHelper.TryDeleteFile(destFs, tempFile);
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
                PathHelper.TryDeleteFile(destFs, tempFile);
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
    private static async Task<byte[]> ComputeSha256Async(IFileSystem fs, string filePath, CancellationToken cancellationToken)
    {
        // Buffer de 1 MB sobre o fluxo sem buffer, como antes da camada
        await using var stream = new BufferedStream(fs.OpenRead(filePath), 1024 * 1024);
        return await SHA256.HashDataAsync(stream, cancellationToken);
    }
}

/// <summary>
/// A cópia gravada no destino não confere com a origem (SHA-256 diferente).
/// </summary>
public sealed class CopyVerificationException : IOException
{
    /// <summary>Cria a exceção com a mensagem padrão.</summary>
    public CopyVerificationException()
        : base("A cópia não confere com a origem (SHA-256 diferente), mesmo após novas tentativas. O arquivo do destino não foi alterado; verifique o disco ou a conexão.")
    {
    }
}
