using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace OmniSync.Services.FileSystems;

/// <summary>
/// <see cref="IFileSystem"/> sobre SFTP (SSH.NET). Os caminhos completos são endereços canônicos
/// <c>sftp://usuario@servidor[:porta]/caminho</c> (<see cref="RemoteAddress"/>); os relativos usam
/// <see cref="Path.DirectorySeparatorChar"/>, como no local.
/// </summary>
/// <remarks>
/// As falhas do SSH.NET são convertidas para as do <c>System.IO</c>: caminho inexistente vira
/// <see cref="FileNotFoundException"/>/<see cref="DirectoryNotFoundException"/>, permissão negada vira
/// <see cref="UnauthorizedAccessException"/> e queda de conexão vira <see cref="IOException"/>, que o motor repete
/// (até 3 vezes por arquivo). A operação seguinte a uma queda reconecta sozinha.
/// </remarks>
public sealed class SftpFileSystem : IFileSystem, IDisposable
{
    private readonly SftpClient _client;
    private readonly RemoteAddress _endpoint;
    private readonly string _root;
    private readonly object _connectGate = new();

    /// <summary>
    /// Cria o sistema de arquivos sobre um cliente já configurado (conectado ou não).
    /// </summary>
    /// <param name="client">Cliente SFTP com as credenciais e a verificação da chave do servidor.</param>
    /// <param name="endpoint">Endereço da conexão (o caminho é ignorado).</param>
    public SftpFileSystem(SftpClient client, RemoteAddress endpoint)
    {
        _client = client;
        _endpoint = endpoint;
        _root = endpoint.Root;
    }

    /// <summary>Endereço da conexão.</summary>
    public RemoteAddress Endpoint => _endpoint;

    /// <summary>Indica se a conexão está aberta.</summary>
    public bool IsConnected => _client.IsConnected;

    /// <inheritdoc />
    public string Kind => "sftp";

    /// <summary>Abre a conexão, se estiver fechada.</summary>
    public void EnsureConnected()
    {
        if (_client.IsConnected) return;
        lock (_connectGate)
        {
            if (!_client.IsConnected) _client.Connect();
        }
    }

    // ---------- caminhos ----------

    /// <summary>Caminho no servidor a partir do endereço completo.</summary>
    private string Remote(string fullPath)
    {
        if (fullPath.StartsWith(_root, StringComparison.Ordinal))
        {
            var rest = fullPath[_root.Length..];
            if (rest.Length == 0) return "/";
            if (rest[0] == '/') return rest.Length > 1 && rest.EndsWith('/') ? rest.TrimEnd('/') : rest;
        }
        var parsed = RemoteAddress.Parse(fullPath);
        if (!parsed.SameEndpoint(_endpoint))
        {
            throw new ArgumentException($"O endereço {fullPath} não pertence à conexão {_endpoint.Root}.");
        }
        return parsed.Path;
    }

    private string Full(string remotePath) => _root + remotePath;

    private static string ToRemoteSeparators(string relativePath) =>
        Path.DirectorySeparatorChar == '/' ? relativePath : relativePath.Replace(Path.DirectorySeparatorChar, '/');

    /// <inheritdoc />
    public string Combine(string directory, string relativePath)
    {
        if (relativePath is "" or ".") return directory;
        var rel = ToRemoteSeparators(relativePath).TrimStart('/');
        return directory.EndsWith('/') ? directory + rel : directory + "/" + rel;
    }

    /// <inheritdoc />
    public string? GetDirectoryName(string path)
    {
        var remote = Remote(path);
        if (remote == "/") return null;
        int index = remote.LastIndexOf('/');
        return Full(index <= 0 ? "/" : remote[..index]);
    }

    /// <inheritdoc />
    public string GetRelativePath(string root, string path)
    {
        var rootPath = Remote(root);
        var remote = Remote(path);
        if (remote == rootPath) return ".";
        var prefix = rootPath == "/" ? "/" : rootPath + "/";
        if (!remote.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException($"{path} não está dentro de {root}.");
        }
        var rel = remote[prefix.Length..];
        return Path.DirectorySeparatorChar == '/' ? rel : rel.Replace('/', Path.DirectorySeparatorChar);
    }

    // ---------- operações ----------

    /// <inheritdoc />
    public bool DirectoryExists(string path)
    {
        var attributes = TryGetAttributes(Remote(path));
        return attributes is { IsDirectory: true };
    }

    /// <inheritdoc />
    public bool FileExists(string path)
    {
        var attributes = TryGetAttributes(Remote(path));
        return attributes != null && !attributes.IsDirectory;
    }

    /// <inheritdoc />
    public bool Exists(string path) => TryGetAttributes(Remote(path)) != null;

    /// <inheritdoc />
    public void CreateDirectory(string path)
    {
        var remote = Remote(path);
        if (remote == "/") return;
        Run(() =>
        {
            // Cria cada nível que faltar, de cima para baixo
            var current = string.Empty;
            foreach (var segment in remote.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                current += "/" + segment;
                var attributes = TryGetAttributesCore(current);
                if (attributes == null)
                {
                    try
                    {
                        _client.CreateDirectory(current);
                    }
                    catch (SshException) when (TryGetAttributesCore(current) is { IsDirectory: true })
                    {
                        // Outra tarefa criou a mesma pasta ao mesmo tempo
                    }
                }
                else if (!attributes.IsDirectory)
                {
                    throw new IOException($"Já existe um arquivo com o nome da pasta: {Full(current)}");
                }
            }
            return true;
        }, path, isDirectory: true);
    }

    /// <inheritdoc />
    public IReadOnlyList<FsEntry> ListDirectory(string path)
    {
        var remote = Remote(path);
        return Run(() =>
        {
            var list = new List<FsEntry>();
            foreach (var file in _client.ListDirectory(remote))
            {
                if (file.Name is "." or ".." || file.IsSymbolicLink) continue;
                if (!file.IsDirectory && !file.IsRegularFile) continue;
                var childPath = remote == "/" ? "/" + file.Name : remote + "/" + file.Name;
                list.Add(new FsEntry(file.Name, Full(childPath), file.IsDirectory, file.IsDirectory ? 0 : file.Length, file.LastWriteTimeUtc));
            }
            return list;
        }, path, isDirectory: true);
    }

    /// <inheritdoc />
    public IEnumerable<FsEntry> EnumerateFilesRecursive(string path)
    {
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.Count > 0)
        {
            IReadOnlyList<FsEntry> entries;
            try
            {
                entries = ListDirectory(pending.Pop());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var entry in entries)
            {
                if (entry.IsDirectory) pending.Push(entry.FullPath);
                else yield return entry;
            }
        }
    }

    /// <inheritdoc />
    public FsEntry? GetFile(string path)
    {
        var remote = Remote(path);
        var attributes = TryGetAttributes(remote);
        if (attributes == null || attributes.IsDirectory) return null;
        var name = remote[(remote.LastIndexOf('/') + 1)..];
        return new FsEntry(name, Full(remote), false, attributes.Size, attributes.LastWriteTimeUtc);
    }

    /// <inheritdoc />
    public Stream OpenRead(string path)
    {
        var remote = Remote(path);
        return Run<Stream>(() => new TranslatingStream(_client.Open(remote, FileMode.Open, FileAccess.Read), path), path, isDirectory: false);
    }

    /// <inheritdoc />
    public Stream CreateNew(string path, long expectedLength, bool readBack)
    {
        var remote = Remote(path);
        return Run<Stream>(
            () => new TranslatingStream(_client.Open(remote, FileMode.CreateNew, readBack ? FileAccess.ReadWrite : FileAccess.Write), path),
            path,
            isDirectory: false);
    }

    /// <inheritdoc />
    public void SetLastWriteTimeUtc(string path, DateTime lastWriteUtc)
    {
        var remote = Remote(path);
        Run(() =>
        {
            var attributes = _client.GetAttributes(remote);
            attributes.LastWriteTimeUtc = lastWriteUtc;
            _client.SetAttributes(remote, attributes);
            return true;
        }, path, isDirectory: false);
    }

    /// <inheritdoc />
    public void MoveFile(string sourcePath, string targetPath)
    {
        var from = Remote(sourcePath);
        var to = Remote(targetPath);
        Run(() =>
        {
            // O rename do SFTP v3 já recusa alvo existente na maioria dos servidores; a conferência garante nos demais
            if (TryGetAttributesCore(to) != null)
            {
                throw new IOException($"Já existe um arquivo ou pasta em {targetPath}.");
            }
            _client.RenameFile(from, to);
            return true;
        }, sourcePath, isDirectory: false);
    }

    /// <inheritdoc />
    public void DeleteFile(string path)
    {
        var remote = Remote(path);
        Run(() =>
        {
            if (TryGetAttributesCore(remote) is { IsDirectory: false }) _client.DeleteFile(remote);
            return true;
        }, path, isDirectory: false);
    }

    /// <inheritdoc />
    public bool IsDirectoryEmpty(string path) => ListDirectoryAll(path) == 0;

    /// <inheritdoc />
    public void DeleteEmptyDirectory(string path)
    {
        var remote = Remote(path);
        Run(() =>
        {
            _client.DeleteDirectory(remote);
            return true;
        }, path, isDirectory: true);
    }

    /// <summary>Conta tudo na pasta, inclusive atalhos e arquivos especiais (para não apagar pasta com conteúdo).</summary>
    private int ListDirectoryAll(string path)
    {
        var remote = Remote(path);
        return Run(() =>
        {
            int count = 0;
            foreach (var file in _client.ListDirectory(remote))
            {
                if (file.Name is not ("." or "..")) count++;
            }
            return count;
        }, path, isDirectory: true);
    }

    private SftpFileAttributes? TryGetAttributes(string remote) => Run(() => TryGetAttributesCore(remote), Full(remote), isDirectory: false);

    private SftpFileAttributes? TryGetAttributesCore(string remote)
    {
        try
        {
            return _client.GetAttributes(remote);
        }
        catch (SftpPathNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Executa uma operação reconectando se preciso e convertendo as falhas para as do System.IO.</summary>
    private T Run<T>(Func<T> operation, string displayPath, bool isDirectory)
    {
        try
        {
            EnsureConnected();
            return operation();
        }
        catch (Exception ex) when (Translate(ex, displayPath, isDirectory) is { } translated)
        {
            throw translated;
        }
    }

    /// <summary>
    /// Converte uma falha do SSH.NET para a exceção equivalente do System.IO; <c>null</c> deixa a original passar.
    /// </summary>
    internal static Exception? Translate(Exception ex, string displayPath, bool isDirectory)
    {
        return ex switch
        {
            SftpPathNotFoundException => isDirectory
                ? new DirectoryNotFoundException($"Pasta remota não encontrada: {displayPath}", ex)
                : new FileNotFoundException($"Arquivo remoto não encontrado: {displayPath}", displayPath, ex),
            SftpPermissionDeniedException => new UnauthorizedAccessException($"Permissão negada no servidor: {displayPath}", ex),
            SshAuthenticationException => new UnauthorizedAccessException($"O servidor recusou o usuário ou a senha ({displayPath}).", ex),
            SshConnectionException or SshOperationTimeoutException or SocketException or ObjectDisposedException
                => new IOException($"A conexão com o servidor caiu ou não respondeu ({displayPath}): {ex.Message}", ex),
            SshException => new IOException($"O servidor recusou a operação em {displayPath}: {ex.Message}", ex),
            _ => null
        };
    }

    /// <summary>Fecha a conexão.</summary>
    public void Dispose()
    {
        try
        {
            if (_client.IsConnected) _client.Disconnect();
        }
        catch (Exception ex) when (ex is SshException or SocketException or ObjectDisposedException)
        {
        }
        _client.Dispose();
    }

    /// <summary>Fluxo que converte as falhas do SSH.NET durante leitura e gravação.</summary>
    private sealed class TranslatingStream : Stream
    {
        private readonly Stream _inner;
        private readonly string _path;

        public TranslatingStream(Stream inner, string path)
        {
            _inner = inner;
            _path = path;
        }

        private T Guard<T>(Func<T> action)
        {
            try
            {
                return action();
            }
            catch (Exception ex) when (Translate(ex, _path, false) is { } translated)
            {
                throw translated;
            }
        }

        private async ValueTask<T> GuardAsync<T>(Func<ValueTask<T>> action)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (Exception ex) when (Translate(ex, _path, false) is { } translated)
            {
                throw translated;
            }
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => Guard(() => _inner.Length);

        public override long Position
        {
            get => Guard(() => _inner.Position);
            set => Guard(() => _inner.Position = value);
        }

        public override void Flush() => Guard(() => { _inner.Flush(); return true; });
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            GuardAsync(async () => { await _inner.FlushAsync(cancellationToken).ConfigureAwait(false); return true; }).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => Guard(() => _inner.Read(buffer, offset, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            GuardAsync(() => _inner.ReadAsync(buffer, cancellationToken));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => Guard(() => _inner.Seek(offset, origin));
        public override void SetLength(long value) => Guard(() => { _inner.SetLength(value); return true; });
        public override void Write(byte[] buffer, int offset, int count) => Guard(() => { _inner.Write(buffer, offset, count); return true; });
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(GuardAsync(async () => { await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false); return true; }).AsTask());
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                bool writable = _inner.CanWrite;
                try
                {
                    _inner.Dispose();
                }
                catch (Exception ex) when (Translate(ex, _path, false) is { } translated)
                {
                    // Na gravação, o fechamento envia o que restou no buffer: a falha tem que chegar ao motor,
                    // senão um temporário incompleto poderia ser renomeado. Na leitura, fechar numa conexão
                    // que caiu não deve mascarar o erro original.
                    if (writable) throw translated;
                }
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            bool writable = _inner.CanWrite;
            try
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (Translate(ex, _path, false) is { } translated)
            {
                if (writable) throw translated;
            }
            GC.SuppressFinalize(this);
        }
    }
}
