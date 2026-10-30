using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OmniSync.Services;

/// <summary>
/// Utilitários de caminho sensíveis ao sistema operacional e operações de arquivo seguras
/// compartilhadas pelo motor de sincronização e pelo serviço de restauração.
/// </summary>
public static class PathHelper
{
    /// <summary>
    /// Nome da pasta reservada, na raiz do destino, que recebe os arquivos órfãos.
    /// </summary>
    public const string TrashFolderName = "lixo";

    /// <summary>
    /// Nome da pasta reservada, na raiz do destino, que recebe as versões substituídas.
    /// </summary>
    public const string OldFolderName = "old";

    /// <summary>
    /// Sufixo dos arquivos temporários gravados durante a cópia, antes da renomeação final.
    /// </summary>
    public const string TempSuffix = ".omnisync-tmp";

    /// <summary>
    /// Indica se o sistema de arquivos nativo da plataforma diferencia maiúsculas de minúsculas.
    /// Windows (NTFS) e macOS (APFS padrão) não diferenciam; Linux (ext4, btrfs, xfs) diferencia.
    /// </summary>
    public static bool IsPlatformCaseSensitive { get; } = !(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS());

    /// <summary>
    /// Comparador de caminhos adequado à plataforma.
    /// </summary>
    public static StringComparer PathComparer { get; } =
        IsPlatformCaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Modo de comparação de caminhos adequado à plataforma.
    /// </summary>
    public static StringComparison PathComparison { get; } =
        IsPlatformCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// Converte para caminho absoluto e remove o separador final (preservando raízes como <c>C:\</c> e <c>/</c>).
    /// </summary>
    public static string NormalizeDirectory(string path)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
    }

    /// <summary>
    /// Verifica se dois caminhos normalizados apontam para a mesma pasta.
    /// </summary>
    public static bool AreSamePath(string a, string b) => string.Equals(a, b, PathComparison);

    /// <summary>
    /// Verifica se <paramref name="candidate"/> está contido (em qualquer profundidade) em <paramref name="parent"/>.
    /// Ambos devem estar normalizados por <see cref="NormalizeDirectory"/>.
    /// </summary>
    public static bool IsSubPathOf(string candidate, string parent)
    {
        if (AreSamePath(candidate, parent)) return false;
        var prefix = Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, PathComparison);
    }

    /// <summary>
    /// Calcula o diretório de destino efetivo. Com <paramref name="useSourceFolderName"/> ativo (ou quando o destino é
    /// a raiz de um disco, ex.: <c>G:\</c>), anexa o nome da pasta de origem, exceto se o destino já terminar nele.
    /// </summary>
    /// <param name="sourcePath">Pasta de Origem.</param>
    /// <param name="destPath">Pasta de Destino escolhida pelo usuário.</param>
    /// <param name="useSourceFolderName">Opção "Criar subpasta com o nome da origem".</param>
    /// <returns>Destino efetivo, ou vazio quando o destino não foi informado.</returns>
    public static string ResolveEffectiveDestination(string? sourcePath, string? destPath, bool useSourceFolderName)
    {
        if (string.IsNullOrWhiteSpace(destPath)) return string.Empty;
        destPath = destPath.Trim();
        if (string.IsNullOrWhiteSpace(sourcePath)) return destPath;

        var trimmedSource = sourcePath.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var sourceFolderName = Path.GetFileName(trimmedSource);
        if (string.IsNullOrWhiteSpace(sourceFolderName))
        {
            return destPath;
        }

        var trimmedDest = destPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var destFolderName = Path.GetFileName(trimmedDest);
        var destRoot = Path.GetPathRoot(destPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        bool isDestDriveRoot = string.IsNullOrEmpty(destFolderName) ||
                               string.Equals(trimmedDest, destRoot, StringComparison.OrdinalIgnoreCase);

        if ((useSourceFolderName || isDestDriveRoot) &&
            !string.Equals(sourceFolderName, destFolderName, StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(destPath, sourceFolderName);
        }

        return destPath;
    }

    /// <summary>
    /// Descreve por que um par Origem/Destino não pode ser sincronizado (mesma pasta ou uma dentro da outra).
    /// Os caminhos podem vir crus da interface; são normalizados aqui.
    /// </summary>
    /// <param name="sourceDir">Diretório de Origem.</param>
    /// <param name="destDir">Diretório de Destino efetivo.</param>
    /// <returns>Mensagem para o usuário, ou <c>null</c> quando o par é válido (ou ainda incompleto).</returns>
    public static string? DescribePairProblem(string? sourceDir, string? destDir)
    {
        if (string.IsNullOrWhiteSpace(sourceDir) || string.IsNullOrWhiteSpace(destDir)) return null;
        string source, dest;
        try
        {
            source = NormalizeDirectory(sourceDir);
            dest = NormalizeDirectory(destDir);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (AreSamePath(source, dest))
        {
            return "O diretório de Origem e o de Destino não podem ser a mesma pasta.";
        }
        if (IsSubPathOf(dest, source))
        {
            return "O Destino está dentro da Origem. Isso faria a origem incluir as próprias cópias, 'lixo/' e 'old/' a cada execução. Escolha um destino fora da origem.";
        }
        if (IsSubPathOf(source, dest))
        {
            return "A Origem está dentro do Destino. Todo o restante do destino seria tratado como órfão. Marque 'Criar subpasta com o nome da origem' ou escolha outro destino.";
        }
        return null;
    }

    /// <summary>
    /// Confirma se o primeiro segmento de um caminho relativo é uma pasta reservada (<c>lixo</c> ou <c>old</c>).
    /// </summary>
    public static bool IsReservedFolderName(string name)
    {
        return name.Equals(TrashFolderName, StringComparison.OrdinalIgnoreCase) ||
               name.Equals(OldFolderName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifica se o nome da pasta corresponde a diretórios internos de sistema ou lixeiras de NAS
    /// que não devem ser sincronizados.
    /// </summary>
    public static bool IsSystemIgnoredDirectoryName(string dirName)
    {
        if (string.IsNullOrEmpty(dirName)) return false;
        return dirName.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase) ||
               dirName.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase) ||
               dirName.Equals("#recycle", StringComparison.OrdinalIgnoreCase) ||
               dirName.Equals("@eaDir", StringComparison.OrdinalIgnoreCase) ||
               dirName.Equals(".Trash", StringComparison.OrdinalIgnoreCase) ||
               dirName.StartsWith(".Trash-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Move um arquivo para dentro de uma pasta reservada (<c>lixo/</c> ou <c>old/</c>) preservando o caminho relativo.
    /// Nunca sobrescreve: se já houver homônimo, acrescenta sufixo de data/hora.
    /// </summary>
    /// <param name="filePath">Arquivo a mover.</param>
    /// <param name="reservedRoot">Raiz da pasta reservada.</param>
    /// <param name="relativePath">Caminho relativo a preservar dentro da pasta reservada.</param>
    /// <returns>Caminho final do arquivo movido, ou <c>null</c> se o arquivo de origem não existia mais.</returns>
    public static string? MoveIntoReservedFolder(string filePath, string reservedRoot, string relativePath)
    {
        if (!File.Exists(filePath)) return null;

        var target = Path.Combine(reservedRoot, relativePath);
        var targetDir = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        var safeTarget = GetUniqueTargetPath(target);
        File.Move(filePath, safeTarget, overwrite: false);
        return safeTarget;
    }

    /// <summary>
    /// Gera um caminho de arquivo único acrescentando o sufixo <c>_yyyyMMdd_HHmmss</c> (e contador, se necessário)
    /// quando o caminho desejado já existe.
    /// </summary>
    public static string GetUniqueTargetPath(string targetFilePath)
    {
        if (!File.Exists(targetFilePath) && !Directory.Exists(targetFilePath)) return targetFilePath;

        var dir = Path.GetDirectoryName(targetFilePath)!;
        var nameWithoutExt = Path.GetFileNameWithoutExtension(targetFilePath);
        var ext = Path.GetExtension(targetFilePath);
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        var candidate = Path.Combine(dir, $"{nameWithoutExt}_{timestamp}{ext}");
        int counter = 1;
        while (File.Exists(candidate) || Directory.Exists(candidate))
        {
            candidate = Path.Combine(dir, $"{nameWithoutExt}_{timestamp}_{counter}{ext}");
            counter++;
        }
        return candidate;
    }

    /// <summary>
    /// Remove, de baixo para cima, as pastas que ficaram vazias, sem nunca subir acima de <paramref name="stopAt"/>
    /// (que também é removida apenas se <paramref name="includeStopAt"/> for <c>true</c>).
    /// Usa exclusão não recursiva: uma pasta com qualquer conteúdo nunca é apagada.
    /// </summary>
    public static int RemoveEmptyDirectories(IEnumerable<string> startDirectories, string stopAt, bool includeStopAt = false)
    {
        int removed = 0;
        var ordered = startDirectories
            .Where(d => !string.IsNullOrEmpty(d))
            .Distinct(PathComparer)
            .OrderByDescending(d => d.Length)
            .ToList();

        foreach (var start in ordered)
        {
            var current = start;
            while (!string.IsNullOrEmpty(current) &&
                   (IsSubPathOf(current, stopAt) || (includeStopAt && AreSamePath(current, stopAt))))
            {
                try
                {
                    if (!Directory.Exists(current) || Directory.EnumerateFileSystemEntries(current).Any())
                    {
                        break;
                    }
                    Directory.Delete(current, recursive: false);
                    removed++;
                }
                catch (IOException)
                {
                    break;
                }
                catch (UnauthorizedAccessException)
                {
                    break;
                }
                current = Path.GetDirectoryName(current);
            }
        }
        return removed;
    }

    /// <summary>
    /// Tenta excluir um arquivo temporário, ignorando falhas (o arquivo pode já ter sido removido).
    /// </summary>
    public static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
