using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using OmniSync.Services.Remote;

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
/// (até 3 vezes por arquivo). A operação seguinte a uma queda reconecta sozinha; se a reconexão falhar, a operação
/// lança <see cref="RemoteAccessException"/>, que o motor trata como servidor inacessível e para a execução.
/// <para>
/// A conferência das cópias usa o SHA-256 calculado pelo próprio servidor (<c>sha256sum</c> ou, no OpenSSH do
/// Windows, <c>Get-FileHash</c>), por um canal SSH de comandos aberto à parte. Servidor que só aceita SFTP fica com a
/// conferência por tamanho, salvo se o favorito pedir a releitura pela rede.
/// </para>
/// </remarks>
public sealed class SftpFileSystem : IFileSystem, IDisposable
{
    private readonly SftpClient _client;
    private readonly RemoteAddress _endpoint;
    private readonly string _root;
    private readonly object _connectGate = new();
    private readonly Func<SshClient>? _shellFactory;
    private readonly Func<bool>? _preferReadBack;
    private readonly object _shellGate = new();
    private SshClient? _shell;
    private ServerHashTool? _hashTool;

    /// <summary>Tempo máximo para o servidor calcular o SHA-256 de um arquivo.</summary>
    public static TimeSpan ServerHashTimeout { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Comando que o servidor tem para calcular SHA-256.</summary>
    internal enum ServerHashTool
    {
        None,
        Sha256Sum,
        PowerShell
    }

    /// <summary>
    /// Cria o sistema de arquivos sobre um cliente já configurado (conectado ou não).
    /// </summary>
    /// <param name="client">Cliente SFTP com as credenciais e a verificação da chave do servidor.</param>
    /// <param name="endpoint">Endereço da conexão (o caminho é ignorado).</param>
    /// <param name="shellFactory">
    /// Cria um cliente SSH de comandos com as mesmas credenciais, para o SHA-256 no servidor; <c>null</c> desliga.
    /// </param>
    /// <param name="preferReadBack">Indica se o favorito pede a conferência relendo o arquivo pela rede.</param>
    public SftpFileSystem(SftpClient client, RemoteAddress endpoint, Func<SshClient>? shellFactory = null, Func<bool>? preferReadBack = null)
    {
        _client = client;
        _endpoint = endpoint;
        _root = endpoint.Root;
        _shellFactory = shellFactory;
        _preferReadBack = preferReadBack;
    }

    /// <summary>Endereço da conexão.</summary>
    public RemoteAddress Endpoint => _endpoint;

    /// <summary>Indica se a conexão está aberta.</summary>
    public bool IsConnected => _client.IsConnected;

    /// <inheritdoc />
    public string Kind => "sftp";

    /// <summary>Abre a conexão, se estiver fechada.</summary>
    /// <exception cref="RemoteAccessException">O servidor não respondeu ou recusou o acesso ao reconectar.</exception>
    public void EnsureConnected()
    {
        if (_client.IsConnected) return;
        lock (_connectGate)
        {
            if (_client.IsConnected) return;
            try
            {
                _client.Connect();
            }
            catch (SshAuthenticationException ex)
            {
                throw new RemoteAccessException(RemoteAccessProblem.AuthenticationFailed, _endpoint,
                    $"Ao reconectar, o servidor recusou o acesso de {_endpoint.EndpointKey}.", inner: ex);
            }
            catch (Exception ex) when (ex is SshException or SocketException or IOException or TimeoutException or ProxyException or ObjectDisposedException)
            {
                throw new RemoteAccessException(RemoteAccessProblem.Unreachable, _endpoint,
                    $"O servidor {_endpoint.HostPortKey} ficou inacessível: {ErrorMessages.AfterColon(ex.Message)}", inner: ex);
            }
        }
    }

    // ---------- conferência no servidor ----------

    /// <inheritdoc />
    public CopyCheck CopyCheck
    {
        get
        {
            if (_preferReadBack?.Invoke() == true) return CopyCheck.ReadBack;
            return DetectHashTool() == ServerHashTool.None ? CopyCheck.Size : CopyCheck.ServerHash;
        }
    }

    /// <summary>Comando de SHA-256 detectado no servidor (para o log), ou <c>null</c>.</summary>
    public string? ServerHashCommand => DetectHashTool() switch
    {
        ServerHashTool.Sha256Sum => "sha256sum",
        ServerHashTool.PowerShell => "Get-FileHash",
        _ => null
    };

    /// <inheritdoc />
    public byte[]? ComputeSha256OnServer(string path)
    {
        var tool = DetectHashTool();
        if (tool == ServerHashTool.None) return null;
        var command = BuildHashCommand(tool, Remote(path));
        lock (_shellGate)
        {
            try
            {
                var shell = OpenShell();
                if (shell == null) return null;
                using var cmd = shell.CreateCommand(command);
                cmd.CommandTimeout = ServerHashTimeout;
                cmd.Execute();
                var hash = cmd.ExitStatus == 0 ? ParseHash(cmd.Result) : null;
                if (hash == null)
                {
                    // O comando existe mas não achou o arquivo: o caminho no shell difere do SFTP (ex.: NAS que
                    // mostra /pasta no SFTP e /volume1/pasta no shell). Daqui em diante, só tamanho nesta conexão.
                    _hashTool = ServerHashTool.None;
                    FileLogService.Write($"[AVISO] {_endpoint.HostPortKey}: o servidor não calculou o SHA-256 de {path} " +
                                         $"(saída {cmd.ExitStatus}: {Trim(cmd.Error)}). As cópias desta conexão serão conferidas pelo tamanho.");
                }
                return hash;
            }
            catch (Exception ex) when (ex is SshException or SocketException or IOException or TimeoutException or ProxyException or ObjectDisposedException)
            {
                // Sem canal de comandos agora: esta cópia é conferida pelo tamanho; a próxima tenta de novo
                CloseShell();
                FileLogService.Write($"[AVISO] {_endpoint.HostPortKey}: canal de comandos indisponível ({ex.Message}); conferência pelo tamanho.");
                return null;
            }
        }
    }

    /// <summary>
    /// Descobre, uma vez por conexão, se o servidor executa <c>sha256sum</c> ou o PowerShell (OpenSSH do Windows).
    /// </summary>
    private ServerHashTool DetectHashTool()
    {
        if (_hashTool is { } known) return known;
        lock (_shellGate)
        {
            if (_hashTool is { } again) return again;
            var tool = ServerHashTool.None;
            try
            {
                var shell = OpenShell();
                if (shell != null)
                {
                    // Calcula o hash de um texto conhecido: servidor só-SFTP (ForceCommand internal-sftp) responde
                    // sem saída e com código 0, então só o hash certo prova que o comando existe
                    if (Probe(shell, "printf 'ok' | sha256sum", OkSha256))
                    {
                        tool = ServerHashTool.Sha256Sum;
                    }
                    else if (Probe(shell, EncodedPowerShell("Write-Output 'omnisync-ok'"), "omnisync-ok"))
                    {
                        tool = ServerHashTool.PowerShell;
                    }
                }
            }
            catch (Exception ex) when (ex is SshException or SocketException or IOException or TimeoutException or ProxyException or ObjectDisposedException)
            {
                FileLogService.Write($"[AVISO] {_endpoint.HostPortKey}: o servidor não aceita comandos ({ex.Message}); conferência pelo tamanho.");
            }
            if (tool == ServerHashTool.None) CloseShell();
            _hashTool = tool;
            return tool;
        }
    }

    /// <summary>SHA-256 do texto <c>ok</c>, usado para provar que o comando do servidor funciona.</summary>
    private const string OkSha256 = "2689367b205c16ce32ed4200942b8b8b1e262dfc70d9bc9fbc77c49699a4f1df";

    private static bool Probe(SshClient shell, string command, string expectedOutput)
    {
        using var cmd = shell.CreateCommand(command);
        cmd.CommandTimeout = TimeSpan.FromSeconds(30);
        cmd.Execute();
        return cmd.ExitStatus == 0 && cmd.Result.Contains(expectedOutput, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Canal de comandos aberto (reconecta se caiu); <c>null</c> quando não há como abrir.</summary>
    private SshClient? OpenShell()
    {
        if (_shellFactory == null) return null;
        _shell ??= _shellFactory();
        if (!_shell.IsConnected) _shell.Connect();
        return _shell;
    }

    /// <summary>Derruba a conexão SFTP sem descartar o cliente (teste de reconexão).</summary>
    internal void DropConnectionForTest() => _client.Disconnect();

    private void CloseShell()
    {
        var shell = _shell;
        _shell = null;
        if (shell == null) return;
        try
        {
            if (shell.IsConnected) shell.Disconnect();
        }
        catch (Exception ex) when (ex is SshException or SocketException or ObjectDisposedException)
        {
        }
        shell.Dispose();
    }

    /// <summary>
    /// Monta o comando de SHA-256. O caminho vai como dado, nunca como comando: entre aspas simples no shell POSIX
    /// e codificado em Base64 (<c>-EncodedCommand</c>) no PowerShell.
    /// </summary>
    internal static string BuildHashCommand(ServerHashTool tool, string remotePath)
    {
        if (tool == ServerHashTool.Sha256Sum)
        {
            return "sha256sum -b -- " + PosixQuote(remotePath);
        }
        // OpenSSH do Windows: o SFTP mostra C:\pasta como /C:/pasta
        var windowsPath = remotePath.Length >= 3 && remotePath[0] == '/' && char.IsAsciiLetter(remotePath[1]) && remotePath[2] == ':'
            ? remotePath[1..]
            : remotePath;
        return EncodedPowerShell(
            "$ErrorActionPreference = 'Stop'; (Get-FileHash -Algorithm SHA256 -LiteralPath '" + windowsPath.Replace("'", "''") + "').Hash");
    }

    /// <summary>Texto entre aspas simples para o shell POSIX (aspas internas viram <c>'\''</c>).</summary>
    internal static string PosixQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static string EncodedPowerShell(string script) =>
        "powershell -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    /// <summary>Lê os 64 dígitos hexadecimais do início da saída (o <c>sha256sum</c> põe <c>\</c> antes em nomes com barra invertida).</summary>
    internal static byte[]? ParseHash(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var text = output.TrimStart();
        if (text.StartsWith('\\')) text = text[1..];
        if (text.Length < 64) return null;
        var hex = text[..64];
        if (text.Length > 64 && !char.IsWhiteSpace(text[64])) return null;
        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string Trim(string? text)
    {
        var t = (text ?? string.Empty).Trim().Replace('\r', ' ').Replace('\n', ' ');
        return t.Length > 200 ? t[..200] + "…" : t;
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
        }, path, isDirectory: true, retryOnDrop: true);
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

    private SftpFileAttributes? TryGetAttributes(string remote) => Run(() => TryGetAttributesCore(remote), Full(remote), isDirectory: false, retryOnDrop: true);

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

    /// <summary>
    /// Executa uma operação reconectando se preciso e convertendo as falhas para as do System.IO.
    /// Com <paramref name="retryOnDrop"/> (só leituras, que podem ser repetidas), uma queda no meio reconecta e repete
    /// a operação uma vez, para a análise não marcar uma pasta como ilegível por causa de uma queda curta.
    /// </summary>
    private T Run<T>(Func<T> operation, string displayPath, bool isDirectory, bool retryOnDrop = false)
    {
        try
        {
            EnsureConnected();
            try
            {
                return operation();
            }
            catch (Exception ex) when (retryOnDrop && IsConnectionDrop(ex))
            {
                EnsureConnected();
                return operation();
            }
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
                => new IOException($"A conexão com o servidor caiu ou não respondeu ({displayPath}): {ErrorMessages.AfterColon(ex.Message)}", ex),
            SshException => new IOException($"O servidor recusou a operação em {displayPath}: {ErrorMessages.AfterColon(ex.Message)}", ex),
            _ => null
        };
    }

    private static bool IsConnectionDrop(Exception ex) => ex is SshConnectionException or SocketException or ObjectDisposedException;

    /// <summary>Fecha a conexão.</summary>
    public void Dispose()
    {
        lock (_shellGate)
        {
            CloseShell();
        }
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
