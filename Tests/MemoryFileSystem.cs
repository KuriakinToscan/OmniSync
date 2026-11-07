using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OmniSync.Services;
using OmniSync.Services.FileSystems;

namespace OmniSync.Tests;

/// <summary>
/// <see cref="IFileSystem"/> inteiramente em memória, para provar que o motor e a restauração não acessam o disco
/// por fora da camada. Os caminhos seguem a notação local (raiz inexistente no disco) e as regras de falha imitam
/// o <c>System.IO</c>: criar ou mover sobre algo existente lança <see cref="IOException"/>.
/// </summary>
public sealed class MemoryFileSystem : IFileSystem
{
    private sealed class FileData
    {
        public byte[] Bytes = Array.Empty<byte>();
        public DateTime LastWriteUtc;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, FileData> _files = new(PathHelper.PathComparer);
    private readonly HashSet<string> _dirs = new(PathHelper.PathComparer);

    /// <summary>Número de chamadas recebidas (qualquer operação).</summary>
    public int Calls { get; private set; }

    /// <inheritdoc />
    public string Kind => "memória";

    private static string Norm(string path) => Path.TrimEndingDirectorySeparator(path);

    private void Touch()
    {
        lock (_gate) Calls++;
    }

    /// <summary>Grava um arquivo diretamente (preparação do teste), criando as pastas acima dele.</summary>
    public void Seed(string path, string content, DateTime lastWriteUtc)
    {
        CreateDirectory(Path.GetDirectoryName(path)!);
        lock (_gate) _files[Norm(path)] = new FileData { Bytes = System.Text.Encoding.UTF8.GetBytes(content), LastWriteUtc = lastWriteUtc };
    }

    /// <summary>Conteúdo de um arquivo como texto, ou <c>null</c> se ele não existir.</summary>
    public string? Read(string path)
    {
        lock (_gate) return _files.TryGetValue(Norm(path), out var f) ? System.Text.Encoding.UTF8.GetString(f.Bytes) : null;
    }

    /// <summary>Todos os arquivos guardados.</summary>
    public IReadOnlyList<string> AllFiles()
    {
        lock (_gate) return _files.Keys.ToList();
    }

    /// <inheritdoc />
    public string Combine(string directory, string relativePath) { Touch(); return Path.Combine(directory, relativePath); }

    /// <inheritdoc />
    public string? GetDirectoryName(string path) { Touch(); return Path.GetDirectoryName(path); }

    /// <inheritdoc />
    public string GetRelativePath(string root, string path) { Touch(); return Path.GetRelativePath(root, path); }

    /// <inheritdoc />
    public bool DirectoryExists(string path) { Touch(); lock (_gate) return _dirs.Contains(Norm(path)); }

    /// <inheritdoc />
    public bool FileExists(string path) { Touch(); lock (_gate) return _files.ContainsKey(Norm(path)); }

    /// <inheritdoc />
    public void CreateDirectory(string path)
    {
        Touch();
        lock (_gate)
        {
            for (var current = Norm(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                if (_files.ContainsKey(current)) throw new IOException($"Existe um arquivo com o nome da pasta: {current}");
                if (!_dirs.Add(current)) break;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<FsEntry> ListDirectory(string path)
    {
        Touch();
        var dir = Norm(path);
        lock (_gate)
        {
            if (!_dirs.Contains(dir)) throw new DirectoryNotFoundException(dir);
            var list = new List<FsEntry>();
            foreach (var d in _dirs.Where(d => PathHelper.AreSamePath(Path.GetDirectoryName(d) ?? string.Empty, dir)))
            {
                list.Add(new FsEntry(Path.GetFileName(d), d, true, 0, DateTime.UtcNow));
            }
            foreach (var (p, f) in _files.Where(kv => PathHelper.AreSamePath(Path.GetDirectoryName(kv.Key) ?? string.Empty, dir)))
            {
                list.Add(new FsEntry(Path.GetFileName(p), p, false, f.Bytes.Length, f.LastWriteUtc));
            }
            return list;
        }
    }

    /// <inheritdoc />
    public IEnumerable<FsEntry> EnumerateFilesRecursive(string path)
    {
        Touch();
        var dir = Norm(path);
        lock (_gate)
        {
            return _files.Where(kv => PathHelper.IsSubPathOf(kv.Key, dir))
                .Select(kv => new FsEntry(Path.GetFileName(kv.Key), kv.Key, false, kv.Value.Bytes.Length, kv.Value.LastWriteUtc))
                .ToList();
        }
    }

    /// <inheritdoc />
    public FsEntry? GetFile(string path)
    {
        Touch();
        lock (_gate)
        {
            return _files.TryGetValue(Norm(path), out var f)
                ? new FsEntry(Path.GetFileName(path), Norm(path), false, f.Bytes.Length, f.LastWriteUtc)
                : null;
        }
    }

    /// <inheritdoc />
    public Stream OpenRead(string path)
    {
        Touch();
        lock (_gate)
        {
            if (!_files.TryGetValue(Norm(path), out var f)) throw new FileNotFoundException("Arquivo inexistente.", path);
            return new MemoryStream(f.Bytes, writable: false);
        }
    }

    /// <inheritdoc />
    public Stream CreateNew(string path, long expectedLength, bool readBack)
    {
        Touch();
        var key = Norm(path);
        lock (_gate)
        {
            if (_files.ContainsKey(key) || _dirs.Contains(key)) throw new IOException($"Já existe: {path}");
            if (!_dirs.Contains(Path.GetDirectoryName(key)!)) throw new DirectoryNotFoundException(path);
            var data = new FileData { LastWriteUtc = DateTime.UtcNow };
            _files[key] = data;
            return new CommitStream(this, data);
        }
    }

    /// <inheritdoc />
    public void SetLastWriteTimeUtc(string path, DateTime lastWriteUtc)
    {
        Touch();
        lock (_gate)
        {
            if (!_files.TryGetValue(Norm(path), out var f)) throw new FileNotFoundException("Arquivo inexistente.", path);
            f.LastWriteUtc = lastWriteUtc;
        }
    }

    /// <inheritdoc />
    public void MoveFile(string sourcePath, string targetPath)
    {
        Touch();
        var from = Norm(sourcePath);
        var to = Norm(targetPath);
        lock (_gate)
        {
            if (!_files.TryGetValue(from, out var f)) throw new FileNotFoundException("Arquivo inexistente.", sourcePath);
            if (_files.ContainsKey(to) || _dirs.Contains(to)) throw new IOException($"Já existe: {targetPath}");
            if (!_dirs.Contains(Path.GetDirectoryName(to)!)) throw new DirectoryNotFoundException(targetPath);
            _files.Remove(from);
            _files[to] = f;
        }
    }

    /// <inheritdoc />
    public void DeleteFile(string path)
    {
        Touch();
        lock (_gate) _files.Remove(Norm(path));
    }

    /// <inheritdoc />
    public bool IsDirectoryEmpty(string path)
    {
        Touch();
        var dir = Norm(path);
        lock (_gate)
        {
            return !_files.Keys.Any(k => PathHelper.AreSamePath(Path.GetDirectoryName(k) ?? string.Empty, dir)) &&
                   !_dirs.Any(d => PathHelper.AreSamePath(Path.GetDirectoryName(d) ?? string.Empty, dir));
        }
    }

    /// <inheritdoc />
    public void DeleteEmptyDirectory(string path)
    {
        if (!IsDirectoryEmpty(path)) throw new IOException($"A pasta não está vazia: {path}");
        lock (_gate) _dirs.Remove(Norm(path));
    }

    /// <summary>Fluxo gravável e relegível que publica o conteúdo no arquivo a cada gravação.</summary>
    private sealed class CommitStream : MemoryStream
    {
        private readonly MemoryFileSystem _owner;
        private readonly FileData _data;

        public CommitStream(MemoryFileSystem owner, FileData data)
        {
            _owner = owner;
            _data = data;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            base.Write(buffer, offset, count);
            Commit();
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            base.Write(buffer);
            Commit();
        }

        private void Commit()
        {
            lock (_owner._gate) _data.Bytes = ToArray();
        }
    }
}
