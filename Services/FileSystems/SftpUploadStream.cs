using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace OmniSync.Services.FileSystems;

/// <summary>
/// Fluxo de gravação de um arquivo novo no SFTP que não espera a confirmação de cada pedaço.
/// </summary>
/// <remarks>
/// O fluxo do SSH.NET aberto com <c>SftpClient.Open</c> envia um pedaço (~32 KB) e espera o servidor confirmar antes
/// do próximo: num link com 100 ms de ida e volta, isso limita a gravação a ~0,3 MB/s, qualquer que seja a banda.
/// Aqui, o que o motor escreve vai para uma fila curta, e uma tarefa de fundo entrega a fila ao
/// <c>SftpClient.UploadFileAsync</c>, que manda os pedaços seguidos e só espera as confirmações no fim (como o
/// cliente <c>sftp</c> do OpenSSH). O arquivo é criado com a mesma regra de antes: falha se já existir.
/// <para>
/// Falhas do envio (arquivo já existente, permissão, queda de conexão) aparecem na escrita seguinte ou no fechamento,
/// já convertidas para as do <c>System.IO</c>. O fechamento só termina quando o servidor confirmou tudo e fechou o
/// arquivo, e lança a falha, se houver: um temporário incompleto nunca chega a ser renomeado.
/// </para>
/// </remarks>
internal sealed class SftpUploadStream : Stream
{
    /// <summary>Pedaços (do tamanho que o motor escreve, até 2 MB) que podem esperar na fila.</summary>
    private const int QueueCapacity = 2;

    private readonly Channel<ReadOnlyMemory<byte>> _queue =
        Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(QueueCapacity) { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _abort = new();
    private readonly Task _upload;
    private readonly string _displayPath;
    private readonly Action<Exception>? _onFailure;
    private long _written;
    private bool _failed;
    private bool _closed;
    private volatile bool _connectionLost;

    /// <summary>Começa o envio para <paramref name="remotePath"/> (criado novo; falha se existir).</summary>
    /// <param name="client">Cliente conectado.</param>
    /// <param name="remotePath">Caminho no servidor.</param>
    /// <param name="displayPath">Endereço completo, para as mensagens.</param>
    /// <param name="onFailure">Recebe a falha original do envio (para o sistema de arquivos reconectar depois de uma queda).</param>
    public SftpUploadStream(SftpClient client, string remotePath, string displayPath, Action<Exception>? onFailure = null)
    {
        _displayPath = displayPath;
        _onFailure = onFailure;
        var source = new QueueReadStream(_queue.Reader);
        _upload = Task.Run(() => client.UploadFileAsync(source, remotePath, canOverride: false, uploadProgress: null, _abort.Token));
        _ = WatchConnectionAsync(client);
        // Envio que parou (falha ou fim): a fila fecha, e quem esperava espaço nela recebe a falha em vez de travar
        _upload.ContinueWith(_ => _queue.Writer.TryComplete(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_closed;
    public override long Length => _written;

    public override long Position
    {
        get => _written;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (buffer.IsEmpty) return;
        // O motor reaproveita o buffer na leitura seguinte: a fila guarda uma cópia
        var copy = buffer.ToArray();
        try
        {
            await _queue.Writer.WriteAsync(copy, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            _failed = true;
            await ThrowUploadFailureAsync().ConfigureAwait(false);
            throw new IOException($"O envio para o servidor parou antes do fim ({_displayPath}).");
        }
        catch (OperationCanceledException)
        {
            _failed = true;
            throw;
        }
        _written += copy.Length;
    }

    /// <summary>
    /// Interrompe o envio se a conexão cair: sem isso, o SSH.NET esperaria as confirmações pendentes até o
    /// tempo-limite da operação (2 min) antes de desistir.
    /// </summary>
    private async Task WatchConnectionAsync(SftpClient client)
    {
        try
        {
            while (!_upload.IsCompleted)
            {
                await Task.WhenAny(_upload, Task.Delay(250)).ConfigureAwait(false);
                if (!_upload.IsCompleted && !client.IsConnected)
                {
                    _connectionLost = true;
                    _abort.Cancel();
                    return;
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // Fluxo já fechado
        }
    }

    /// <summary>Avisa a falha ao sistema de arquivos sem tratá-la (sempre devolve <c>false</c>, para o filtro seguir).</summary>
    private bool NotifyFailure(Exception ex)
    {
        if (ex is not OperationCanceledException || _connectionLost)
        {
            _onFailure?.Invoke(ex is OperationCanceledException ? new SshConnectionException("Conexão perdida durante o envio.") : ex);
        }
        return false;
    }

    /// <summary>Lança a falha do envio já convertida, se ele terminou com falha.</summary>
    private async Task ThrowUploadFailureAsync()
    {
        try
        {
            await _upload.ConfigureAwait(false);
        }
        catch (Exception ex) when (NotifyFailure(ex))
        {
            throw; // inalcançável: NotifyFailure devolve false
        }
        catch (OperationCanceledException ex) when (_connectionLost)
        {
            throw new IOException($"A conexão com o servidor caiu durante o envio ({_displayPath}).", ex);
        }
        catch (Exception ex) when (SftpFileSystem.Translate(ex, _displayPath, false) is { } translated)
        {
            throw translated;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not IOException and not UnauthorizedAccessException)
        {
            // O SSH.NET pode falhar com NullReferenceException quando a conexão cai no meio do envio (a sessão SFTP
            // some por baixo dele): para o motor, é uma queda como outra qualquer, que ele repete
            throw new IOException($"A conexão com o servidor caiu durante o envio ({_displayPath}): {ErrorMessages.AfterColon(ex.Message)}", ex);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_closed)
        {
            CloseAsync().GetAwaiter().GetResult();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_closed)
        {
            await CloseAsync().ConfigureAwait(false);
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Fecha a fila e espera o servidor confirmar o que falta. Depois de uma escrita cancelada ou com falha, o envio é
    /// interrompido (o motor apaga o temporário em seguida); no caminho normal, a falha do envio é lançada.
    /// </summary>
    private async Task CloseAsync()
    {
        _closed = true;
        if (_failed) _abort.Cancel();
        _queue.Writer.TryComplete();
        try
        {
            await ThrowUploadFailureAsync().ConfigureAwait(false);
        }
        catch (Exception) when (_failed)
        {
            // A escrita já relatou a falha (ou o cancelamento); o fechamento não deve mascarar o erro original
        }
        finally
        {
            _abort.Dispose();
        }
    }

    /// <summary>Lado de leitura da fila, entregue ao <c>UploadFileAsync</c>.</summary>
    private sealed class QueueReadStream : Stream
    {
        private readonly ChannelReader<ReadOnlyMemory<byte>> _reader;
        private ReadOnlyMemory<byte> _current;

        public QueueReadStream(ChannelReader<ReadOnlyMemory<byte>> reader) => _reader = reader;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_current.IsEmpty)
            {
                if (!await _reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) return 0;
                _reader.TryRead(out _current);
            }
            int n = Math.Min(buffer.Length, _current.Length);
            _current[..n].CopyTo(buffer);
            _current = _current[n..];
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
