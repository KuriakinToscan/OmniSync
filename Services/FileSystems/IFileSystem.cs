using System;
using System.Collections.Generic;
using System.IO;

namespace OmniSync.Services.FileSystems;

/// <summary>
/// Entrada de diretório lida de um <see cref="IFileSystem"/>: nome, caminho completo, tipo, tamanho e data de modificação.
/// </summary>
/// <param name="Name">Nome do arquivo ou da pasta, sem o caminho.</param>
/// <param name="FullPath">Caminho completo, na notação do sistema de arquivos que o produziu.</param>
/// <param name="IsDirectory"><c>true</c> para pasta.</param>
/// <param name="Length">Tamanho em bytes (0 para pasta).</param>
/// <param name="LastWriteUtc">Data e hora da última modificação, em UTC.</param>
public readonly record struct FsEntry(string Name, string FullPath, bool IsDirectory, long Length, DateTime LastWriteUtc);

/// <summary>
/// Camada única de acesso a arquivos usada pelo motor de sincronização, pela restauração de <c>lixo/</c> e pelas
/// pastas de segurança (<c>lixo/</c> e <c>old/</c>). A implementação local (<see cref="LocalFileSystem"/>) cobre
/// discos e pastas de rede SMB; destinos remotos (SFTP) implementam o mesmo contrato.
/// </summary>
/// <remarks>
/// Contrato de caminhos: caminhos completos estão na notação do próprio sistema de arquivos; caminhos relativos
/// (os de <see cref="Models.SyncItem.RelativePath"/>) usam sempre <see cref="Path.DirectorySeparatorChar"/>, para que
/// origem e destino de tipos diferentes sejam comparáveis. <see cref="Combine"/> aceita esses relativos e
/// <see cref="GetRelativePath"/> os produz.
/// Falhas de E/S são lançadas como <see cref="IOException"/> (ou derivadas) e <see cref="UnauthorizedAccessException"/>,
/// como no <c>System.IO</c>, para que a classificação de falhas transitórias continue valendo.
/// </remarks>
public interface IFileSystem
{
    /// <summary>Nome curto do tipo de sistema de arquivos, para log (ex.: "local").</summary>
    string Kind { get; }

    /// <summary>Junta um caminho completo de pasta com um caminho relativo.</summary>
    string Combine(string directory, string relativePath);

    /// <summary>Pasta que contém <paramref name="path"/>, ou <c>null</c> na raiz.</summary>
    string? GetDirectoryName(string path);

    /// <summary>Caminho de <paramref name="path"/> relativo a <paramref name="root"/>, com <see cref="Path.DirectorySeparatorChar"/>.</summary>
    string GetRelativePath(string root, string path);

    /// <summary>Indica se a pasta existe.</summary>
    bool DirectoryExists(string path);

    /// <summary>Indica se o arquivo existe.</summary>
    bool FileExists(string path);

    /// <summary>Indica se existe arquivo ou pasta no caminho.</summary>
    bool Exists(string path) => FileExists(path) || DirectoryExists(path);

    /// <summary>Cria a pasta e as pastas acima dela que faltarem; não faz nada se já existir.</summary>
    void CreateDirectory(string path);

    /// <summary>
    /// Lista o conteúdo imediato de uma pasta (sem recursão), sem atalhos/pontos de nova análise.
    /// Pasta ilegível lança exceção (<see cref="IOException"/>, <see cref="UnauthorizedAccessException"/> ou
    /// <see cref="System.Security.SecurityException"/>) para que o chamador a registre.
    /// </summary>
    IReadOnlyList<FsEntry> ListDirectory(string path);

    /// <summary>Lista todos os arquivos abaixo de uma pasta, recursivamente, pulando o que não puder ser lido.</summary>
    IEnumerable<FsEntry> EnumerateFilesRecursive(string path);

    /// <summary>Metadados de um arquivo, ou <c>null</c> se ele não existir.</summary>
    FsEntry? GetFile(string path);

    /// <summary>Abre um arquivo para leitura sequencial assíncrona.</summary>
    Stream OpenRead(string path);

    /// <summary>
    /// Cria um arquivo novo (falha se já existir) para gravação assíncrona.
    /// </summary>
    /// <param name="path">Arquivo a criar.</param>
    /// <param name="expectedLength">Tamanho previsto, para pré-alocação (0 se desconhecido).</param>
    /// <param name="readBack"><c>true</c> para abrir também para leitura, permitindo reler o conteúdo pelo mesmo fluxo.</param>
    Stream CreateNew(string path, long expectedLength, bool readBack);

    /// <summary>Define a data de modificação de um arquivo.</summary>
    void SetLastWriteTimeUtc(string path, DateTime lastWriteUtc);

    /// <summary>Renomeia ou move um arquivo dentro do mesmo sistema de arquivos. Nunca sobrescreve: falha se o destino existir.</summary>
    void MoveFile(string sourcePath, string targetPath);

    /// <summary>Exclui um arquivo (sem erro se ele não existir).</summary>
    void DeleteFile(string path);

    /// <summary>Indica se a pasta não tem nenhum arquivo nem subpasta.</summary>
    bool IsDirectoryEmpty(string path);

    /// <summary>Exclui uma pasta vazia (exclusão não recursiva: falha se houver conteúdo).</summary>
    void DeleteEmptyDirectory(string path);
}
