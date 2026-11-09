using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace OmniSync.Tests;

/// <summary>
/// Repassador TCP local que atrasa cada pedaço de dados em <see cref="OneWayDelay"/> nos dois sentidos, como um link
/// de VPN com latência alta. Não limita a banda: dados enviados em sequência chegam em sequência, cada um com o mesmo
/// atraso, de modo que só quem espera resposta a cada pedaço perde velocidade.
/// </summary>
public sealed class LatencyProxy : IDisposable
{
    private readonly TcpListener _listener;
    private readonly IPEndPoint _target;
    private readonly CancellationTokenSource _stop = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<TcpClient, byte> _clients = new();

    /// <summary>Atraso aplicado em cada sentido (a ida e volta é o dobro).</summary>
    public TimeSpan OneWayDelay { get; }

    /// <summary>Porta local em que o repassador escuta (em 127.0.0.1).</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Começa a escutar numa porta livre e a repassar para <paramref name="target"/>.</summary>
    /// <param name="target">Servidor de destino.</param>
    /// <param name="oneWayDelay">Atraso em cada sentido.</param>
    public LatencyProxy(IPEndPoint target, TimeSpan oneWayDelay)
    {
        _target = target;
        OneWayDelay = oneWayDelay;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception)
            {
                return;
            }
            _clients[client] = 0;
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        using (var server = new TcpClient())
        {
            try
            {
                client.NoDelay = true;
                server.NoDelay = true;
                await server.ConnectAsync(_target, _stop.Token);
                var a = Pump(client.GetStream(), server.GetStream());
                var b = Pump(server.GetStream(), client.GetStream());
                await Task.WhenAny(a, b);
            }
            catch (Exception)
            {
                // Conexão de teste encerrada
            }
            finally
            {
                _clients.TryRemove(client, out _);
            }
        }
    }

    /// <summary>Derruba todas as conexões abertas, como uma VPN que cai; as novas continuam sendo aceitas.</summary>
    public void DropAll()
    {
        foreach (var client in _clients.Keys)
        {
            try
            {
                client.Client.Close(0);
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>Lê de <paramref name="from"/> e escreve em <paramref name="to"/> cada pedaço após o atraso.</summary>
    private async Task Pump(NetworkStream from, NetworkStream to)
    {
        var queue = Channel.CreateUnbounded<(long due, byte[] data)>();
        var writer = Task.Run(async () =>
        {
            await foreach (var (due, data) in queue.Reader.ReadAllAsync(_stop.Token))
            {
                long wait = due - Environment.TickCount64;
                if (wait > 0) await Task.Delay((int)wait, _stop.Token);
                await to.WriteAsync(data, _stop.Token);
            }
        });
        var buffer = new byte[64 * 1024];
        try
        {
            int n;
            while ((n = await from.ReadAsync(buffer, _stop.Token)) > 0)
            {
                queue.Writer.TryWrite((Environment.TickCount64 + (long)OneWayDelay.TotalMilliseconds, buffer.AsSpan(0, n).ToArray()));
            }
        }
        finally
        {
            queue.Writer.TryComplete();
        }
        await writer;
        to.Socket.Shutdown(SocketShutdown.Send);
    }

    /// <summary>Para de escutar e derruba as conexões.</summary>
    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }
}
