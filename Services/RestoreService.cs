using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using OmniSync.Models;
using OmniSync.Services.FileSystems;

namespace OmniSync.Services;

/// <summary>
/// Lista e restaura arquivos da pasta <c>lixo/</c> do destino de forma não destrutiva:
/// nenhum arquivo é excluído, nada é sobrescrito e somente pastas vazias são removidas.
/// </summary>
public sealed partial class RestoreService
{
    /// <summary>
    /// Sufixo de desambiguação acrescentado por <see cref="PathHelper.GetUniqueTargetPath(IFileSystem, string)"/> (ex.: <c>_20261028_114500</c> ou <c>_20261028_114500_2</c>).
    /// </summary>
    [GeneratedRegex(@"_\d{8}_\d{6}(_\d+)?$")]
    private static partial Regex CollisionSuffixRegex();

    private readonly IFileSystem? _fixedFs;

    /// <summary>
    /// Cria o serviço sobre o sistema de arquivos do destino.
    /// </summary>
    /// <param name="fileSystem">Sistema de arquivos fixo do destino; <c>null</c> escolhe pelo caminho (<see cref="FileSystemProvider.For"/>).</param>
    public RestoreService(IFileSystem? fileSystem = null)
    {
        _fixedFs = fileSystem;
    }

    private IFileSystem Resolve(string destDir) => _fixedFs ?? FileSystemProvider.For(destDir);

    /// <summary>
    /// Lista os arquivos existentes em <c>&lt;destino&gt;/lixo/</c>.
    /// </summary>
    /// <param name="destDir">Diretório de destino da sincronização.</param>
    /// <param name="cancellationToken">Token de cancelamento.</param>
    /// <returns>Arquivos encontrados, ordenados por caminho relativo; lista vazia se <c>lixo/</c> não existir.</returns>
    public Task<IReadOnlyList<TrashEntry>> ListTrashAsync(string destDir, CancellationToken cancellationToken)
    {
        return Task.Run<IReadOnlyList<TrashEntry>>(() =>
        {
            var fs = Resolve(destDir);
            var trashRoot = fs.Combine(PathHelper.NormalizeDirectory(destDir), PathHelper.TrashFolderName);
            if (!fs.DirectoryExists(trashRoot))
            {
                return Array.Empty<TrashEntry>();
            }

            var entries = new List<TrashEntry>();
            foreach (var file in fs.EnumerateFilesRecursive(trashRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                entries.Add(new TrashEntry
                {
                    RelativePath = fs.GetRelativePath(trashRoot, file.FullPath),
                    FullPath = file.FullPath,
                    Size = file.Length,
                    ModifiedUtc = file.LastWriteUtc
                });
            }

            entries.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));
            return entries;
        }, cancellationToken);
    }

    /// <summary>
    /// Devolve os arquivos selecionados de <c>lixo/</c> ao seu caminho original no destino.
    /// Se já existir um arquivo no caminho original, o item permanece em <c>lixo/</c> (nunca sobrescreve nem exclui).
    /// Nomes com sufixo de desambiguação (<c>_yyyyMMdd_HHmmss</c>) são restaurados com o nome original quando ele está livre.
    /// </summary>
    /// <param name="destDir">Diretório de destino da sincronização.</param>
    /// <param name="entries">Arquivos a restaurar (obtidos de <see cref="ListTrashAsync"/>).</param>
    /// <param name="logger">Canal opcional de log.</param>
    /// <param name="cancellationToken">Token de cancelamento.</param>
    /// <returns>Balanço da restauração.</returns>
    public Task<RestoreResult> RestoreAsync(
        string destDir,
        IEnumerable<TrashEntry> entries,
        IProgress<string>? logger,
        CancellationToken cancellationToken)
    {
        var selected = entries.ToList();
        return Task.Run(() =>
        {
            var destRoot = PathHelper.NormalizeDirectory(destDir);
            var fs = Resolve(destRoot);
            var trashRoot = fs.Combine(destRoot, PathHelper.TrashFolderName);
            var result = new RestoreResult();
            var touchedDirs = new List<string>();

            logger?.Report($"[RESTAURAÇÃO] Restaurando {PtText.Count(selected.Count, "arquivo", "arquivos")} de '{trashRoot}'...");

            foreach (var entry in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Garante que a entrada pertence de fato a lixo/ deste destino
                var fullPath = PathHelper.NormalizeFullPath(entry.FullPath);
                if (!PathHelper.IsSubPathOf(fullPath, trashRoot))
                {
                    result.Failed++;
                    logger?.Report($"[AVISO] Ignorado (fora de lixo/): {entry.FullPath}");
                    continue;
                }

                try
                {
                    if (!fs.FileExists(fullPath))
                    {
                        result.Failed++;
                        logger?.Report($"[AVISO] Não encontrado em lixo/: {entry.RelativePath}");
                        continue;
                    }

                    var target = ResolveRestoreTarget(fs, destRoot, entry.RelativePath);
                    if (target == null)
                    {
                        result.KeptInTrash++;
                        logger?.Report($"[AVISO] Já existe arquivo no local original; mantido em lixo/: {entry.RelativePath}");
                        continue;
                    }

                    var targetDir = fs.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(targetDir))
                    {
                        fs.CreateDirectory(targetDir);
                    }

                    fs.MoveFile(fullPath, target);
                    touchedDirs.Add(fs.GetDirectoryName(fullPath)!);
                    result.Restored++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    result.Failed++;
                    logger?.Report($"[ERRO] Não foi possível restaurar '{entry.RelativePath}': {ErrorMessages.ForLog(ex)}");
                }
            }

            // Remove apenas pastas que ficaram vazias dentro de lixo/ (e o próprio lixo/, se vazio)
            PathHelper.RemoveEmptyDirectories(fs, touchedDirs, trashRoot, includeStopAt: true);

            logger?.Report($"[RESTAURAÇÃO] Concluída: {result.Restored} restaurados, {result.KeptInTrash} mantidos em lixo/ por já existirem, {result.Failed} falhas.");
            return result;
        }, cancellationToken);
    }

    /// <summary>
    /// Determina o caminho de restauração: o nome original sem sufixo de desambiguação, se livre;
    /// senão o nome como está em <c>lixo/</c>, se livre; senão <c>null</c>.
    /// </summary>
    private static string? ResolveRestoreTarget(IFileSystem fs, string destRoot, string relativePath)
    {
        var direct = fs.Combine(destRoot, relativePath);
        var dir = fs.GetDirectoryName(direct)!;
        var fileName = Path.GetFileName(direct);
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);

        var stripped = CollisionSuffixRegex().Replace(nameWithoutExt, string.Empty);
        if (stripped.Length > 0 && stripped != nameWithoutExt)
        {
            var original = fs.Combine(dir, stripped + ext);
            if (!fs.Exists(original))
            {
                return original;
            }
        }

        if (!fs.Exists(direct))
        {
            return direct;
        }

        return null;
    }
}
