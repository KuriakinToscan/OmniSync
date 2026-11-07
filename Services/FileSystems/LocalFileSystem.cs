using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;

namespace OmniSync.Services.FileSystems;

/// <summary>
/// <see cref="IFileSystem"/> sobre o <c>System.IO</c>: discos locais (NTFS, ext4, exFAT/FAT) e pastas de rede
/// (SMB, NFS, unidade mapeada). Reproduz exatamente as chamadas usadas pelo motor antes da camada existir.
/// </summary>
public sealed class LocalFileSystem : IFileSystem
{
    /// <summary>Instância única (a classe não guarda estado).</summary>
    public static LocalFileSystem Instance { get; } = new();

    private static readonly EnumerationOptions ListOptions = new()
    {
        // Falhas de acesso chegam ao chamador, que registra a pasta como ilegível
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    private static readonly EnumerationOptions RecursiveOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    private LocalFileSystem()
    {
    }

    /// <inheritdoc />
    public string Kind => "local";

    /// <inheritdoc />
    public string Combine(string directory, string relativePath) => Path.Combine(directory, relativePath);

    /// <inheritdoc />
    public string? GetDirectoryName(string path) => Path.GetDirectoryName(path);

    /// <inheritdoc />
    public string GetRelativePath(string root, string path) => Path.GetRelativePath(root, path);

    /// <inheritdoc />
    public bool DirectoryExists(string path) => Directory.Exists(path);

    /// <inheritdoc />
    public bool FileExists(string path) => File.Exists(path);

    /// <inheritdoc />
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    /// <inheritdoc />
    public IReadOnlyList<FsEntry> ListDirectory(string path)
    {
        // Nome, tipo, tamanho e data vêm da própria enumeração: uma consulta de metadados por entrada (relevante em SMB/NAS)
        return new FileSystemEnumerable<FsEntry>(
            path,
            (ref FileSystemEntry e) => new FsEntry(
                e.FileName.ToString(),
                e.ToFullPath(),
                e.IsDirectory,
                e.IsDirectory ? 0 : e.Length,
                e.LastWriteTimeUtc.UtcDateTime),
            ListOptions).ToList();
    }

    /// <inheritdoc />
    public IEnumerable<FsEntry> EnumerateFilesRecursive(string path)
    {
        foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", RecursiveOptions))
        {
            yield return new FsEntry(file.Name, file.FullName, false, file.Length, file.LastWriteTimeUtc);
        }
    }

    /// <inheritdoc />
    public FsEntry? GetFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return null;
        return new FsEntry(info.Name, info.FullName, false, info.Length, info.LastWriteTimeUtc);
    }

    /// <inheritdoc />
    public Stream OpenRead(string path)
    {
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            // Sem buffer interno: o motor já lê em blocos de 2 MB
            BufferSize = 0,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
    }

    /// <inheritdoc />
    public Stream CreateNew(string path, long expectedLength, bool readBack)
    {
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = readBack ? FileAccess.ReadWrite : FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 0,
            Options = FileOptions.Asynchronous,
            PreallocationSize = expectedLength
        });
    }

    /// <inheritdoc />
    public void SetLastWriteTimeUtc(string path, DateTime lastWriteUtc) => File.SetLastWriteTimeUtc(path, lastWriteUtc);

    /// <inheritdoc />
    public void MoveFile(string sourcePath, string targetPath) => File.Move(sourcePath, targetPath, overwrite: false);

    /// <inheritdoc />
    public void DeleteFile(string path) => File.Delete(path);

    /// <inheritdoc />
    public bool IsDirectoryEmpty(string path) => !Directory.EnumerateFileSystemEntries(path).Any();

    /// <inheritdoc />
    public void DeleteEmptyDirectory(string path) => Directory.Delete(path, recursive: false);
}
