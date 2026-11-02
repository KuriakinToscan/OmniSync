using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OmniSync.Services.FileSystems;

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
    /// Endereços <c>sftp://</c> vão para a forma canônica de <see cref="RemoteAddress"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Endereço <c>sftp://</c> inválido.</exception>
    public static string NormalizeDirectory(string path)
    {
        if (RemoteAddress.IsRemote(path)) return RemoteAddress.Parse(path).ToString();
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
    }

    /// <summary>
    /// Verifica se dois caminhos normalizados apontam para a mesma pasta. Endereços remotos diferenciam maiúsculas.
    /// </summary>
    public static bool AreSamePath(string a, string b) =>
        string.Equals(a, b, RemoteAddress.IsRemote(a) || RemoteAddress.IsRemote(b) ? StringComparison.Ordinal : PathComparison);

    /// <summary>
    /// Verifica se <paramref name="candidate"/> está contido (em qualquer profundidade) em <paramref name="parent"/>.
    /// Ambos devem estar normalizados por <see cref="NormalizeDirectory"/>. Local e remoto nunca se contêm.
    /// </summary>
    public static bool IsSubPathOf(string candidate, string parent)
    {
        if (AreSamePath(candidate, parent)) return false;
        bool remote = RemoteAddress.IsRemote(parent);
        if (remote != RemoteAddress.IsRemote(candidate)) return false;
        if (remote)
        {
            var remotePrefix = parent.EndsWith('/') ? parent : parent + "/";
            return candidate.StartsWith(remotePrefix, StringComparison.Ordinal);
        }
        var prefix = Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, PathComparison);
    }

    /// <summary>
    /// Forma canônica de um caminho de arquivo, para comparar com <see cref="IsSubPathOf"/>: absoluto no local,
    /// canônico no remoto.
    /// </summary>
    public static string NormalizeFullPath(string path) =>
        RemoteAddress.IsRemote(path) ? RemoteAddress.Parse(path).ToString() : Path.GetFullPath(path);

    /// <summary>
    /// Expande variáveis de ambiente (<c>%USERPROFILE%</c>, <c>%OneDrive%</c>...) e, no Linux, <c>~</c> e
    /// <c>$HOME</c> no início do caminho. Variáveis inexistentes ficam como estão.
    /// </summary>
    /// <param name="path">Caminho digitado.</param>
    /// <returns>Caminho expandido e sem espaços nas pontas.</returns>
    public static string ExpandUserPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        if (RemoteAddress.IsRemote(path)) return path.Trim();
        var result = Environment.ExpandEnvironmentVariables(path.Trim());
        if (!OperatingSystem.IsWindows())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                if (result == "~" || result == "$HOME") return home;
                if (result.StartsWith("~/", StringComparison.Ordinal)) return home + result[1..];
                if (result.StartsWith("$HOME/", StringComparison.Ordinal)) return home + result[5..];
            }
        }
        return result;
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

        if (RemoteAddress.IsRemote(sourcePath) || RemoteAddress.IsRemote(destPath))
        {
            return ResolveEffectiveRemoteDestination(sourcePath.Trim(), destPath, useSourceFolderName);
        }

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
    /// <see cref="ResolveEffectiveDestination"/> quando origem ou destino é remoto: o nome da origem é o último
    /// segmento do caminho, e a subpasta é anexada com <c>/</c> no remoto. Endereço inválido devolve o destino como está.
    /// </summary>
    private static string ResolveEffectiveRemoteDestination(string sourcePath, string destPath, bool useSourceFolderName)
    {
        string sourceFolderName;
        if (RemoteAddress.IsRemote(sourcePath))
        {
            if (!RemoteAddress.TryParse(sourcePath, out var src, out _)) return destPath;
            sourceFolderName = src.Leaf;
        }
        else
        {
            sourceFolderName = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        if (string.IsNullOrWhiteSpace(sourceFolderName)) return destPath;

        if (!RemoteAddress.IsRemote(destPath))
        {
            var trimmedDest = destPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var destName = Path.GetFileName(trimmedDest);
            var root = Path.GetPathRoot(destPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            bool isRoot = string.IsNullOrEmpty(destName) || string.Equals(trimmedDest, root, StringComparison.OrdinalIgnoreCase);
            return (useSourceFolderName || isRoot) && !string.Equals(sourceFolderName, destName, StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(destPath, sourceFolderName)
                : destPath;
        }

        if (!RemoteAddress.TryParse(destPath, out var dst, out _)) return destPath;
        bool isRemoteRoot = dst.Path == "/";
        return (useSourceFolderName || isRemoteRoot) && !string.Equals(sourceFolderName, dst.Leaf, StringComparison.Ordinal)
            ? dst.WithPath(dst.Path + "/" + sourceFolderName).ToString()
            : destPath;
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
    /// Move um arquivo local para dentro de uma pasta reservada. Equivale a
    /// <see cref="MoveIntoReservedFolder(IFileSystem, string, string, string)"/> com <see cref="LocalFileSystem"/>.
    /// </summary>
    public static string? MoveIntoReservedFolder(string filePath, string reservedRoot, string relativePath)
        => MoveIntoReservedFolder(LocalFileSystem.Instance, filePath, reservedRoot, relativePath);

    /// <summary>
    /// Move um arquivo para dentro de uma pasta reservada (<c>lixo/</c> ou <c>old/</c>) preservando o caminho relativo.
    /// Nunca sobrescreve: se já houver homônimo, acrescenta sufixo de data/hora.
    /// </summary>
    /// <param name="fs">Sistema de arquivos onde estão o arquivo e a pasta reservada.</param>
    /// <param name="filePath">Arquivo a mover.</param>
    /// <param name="reservedRoot">Raiz da pasta reservada.</param>
    /// <param name="relativePath">Caminho relativo a preservar dentro da pasta reservada.</param>
    /// <returns>Caminho final do arquivo movido, ou <c>null</c> se o arquivo de origem não existia mais.</returns>
    public static string? MoveIntoReservedFolder(IFileSystem fs, string filePath, string reservedRoot, string relativePath)
    {
        if (!fs.FileExists(filePath)) return null;

        var target = fs.Combine(reservedRoot, relativePath);
        var targetDir = fs.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(targetDir))
        {
            fs.CreateDirectory(targetDir);
        }

        var safeTarget = GetUniqueTargetPath(fs, target);
        fs.MoveFile(filePath, safeTarget);
        return safeTarget;
    }

    /// <summary>
    /// Caminho local único para <paramref name="targetFilePath"/>. Equivale a
    /// <see cref="GetUniqueTargetPath(IFileSystem, string)"/> com <see cref="LocalFileSystem"/>.
    /// </summary>
    public static string GetUniqueTargetPath(string targetFilePath)
        => GetUniqueTargetPath(LocalFileSystem.Instance, targetFilePath);

    /// <summary>
    /// Gera um caminho de arquivo único acrescentando o sufixo <c>_yyyyMMdd_HHmmss</c> (e contador, se necessário)
    /// quando o caminho desejado já existe.
    /// </summary>
    /// <param name="fs">Sistema de arquivos onde o caminho será usado.</param>
    /// <param name="targetFilePath">Caminho desejado.</param>
    public static string GetUniqueTargetPath(IFileSystem fs, string targetFilePath)
    {
        if (!fs.Exists(targetFilePath)) return targetFilePath;

        var dir = fs.GetDirectoryName(targetFilePath)!;
        var fileName = Path.GetFileName(targetFilePath);
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        var candidate = fs.Combine(dir, $"{nameWithoutExt}_{timestamp}{ext}");
        int counter = 1;
        while (fs.Exists(candidate))
        {
            candidate = fs.Combine(dir, $"{nameWithoutExt}_{timestamp}_{counter}{ext}");
            counter++;
        }
        return candidate;
    }

    /// <summary>
    /// Remove pastas locais que ficaram vazias. Equivale a
    /// <see cref="RemoveEmptyDirectories(IFileSystem, IEnumerable{string}, string, bool)"/> com <see cref="LocalFileSystem"/>.
    /// </summary>
    public static int RemoveEmptyDirectories(IEnumerable<string> startDirectories, string stopAt, bool includeStopAt = false)
        => RemoveEmptyDirectories(LocalFileSystem.Instance, startDirectories, stopAt, includeStopAt);

    /// <summary>
    /// Remove, de baixo para cima, as pastas que ficaram vazias, sem nunca subir acima de <paramref name="stopAt"/>
    /// (que também é removida apenas se <paramref name="includeStopAt"/> for <c>true</c>).
    /// Usa exclusão não recursiva: uma pasta com qualquer conteúdo nunca é apagada.
    /// </summary>
    /// <param name="fs">Sistema de arquivos das pastas.</param>
    /// <param name="startDirectories">Pastas de onde partir.</param>
    /// <param name="stopAt">Pasta-limite.</param>
    /// <param name="includeStopAt">Remove também a pasta-limite, se ficar vazia.</param>
    /// <returns>Número de pastas removidas.</returns>
    public static int RemoveEmptyDirectories(IFileSystem fs, IEnumerable<string> startDirectories, string stopAt, bool includeStopAt = false)
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
                    if (!fs.DirectoryExists(current) || !fs.IsDirectoryEmpty(current))
                    {
                        break;
                    }
                    fs.DeleteEmptyDirectory(current);
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
                current = fs.GetDirectoryName(current);
            }
        }
        return removed;
    }

    /// <summary>
    /// Tenta excluir um arquivo local. Equivale a <see cref="TryDeleteFile(IFileSystem, string)"/> com <see cref="LocalFileSystem"/>.
    /// </summary>
    public static void TryDeleteFile(string path) => TryDeleteFile(LocalFileSystem.Instance, path);

    /// <summary>
    /// Tenta excluir um arquivo temporário, ignorando falhas (o arquivo pode já ter sido removido).
    /// </summary>
    /// <param name="fs">Sistema de arquivos do arquivo.</param>
    /// <param name="path">Arquivo a excluir.</param>
    public static void TryDeleteFile(IFileSystem fs, string path)
    {
        try
        {
            if (fs.FileExists(path))
            {
                fs.DeleteFile(path);
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
