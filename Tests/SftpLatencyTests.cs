using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using OmniSync.Models;
using OmniSync.Services;
using OmniSync.Services.FileSystems;
using OmniSync.Services.Remote;
using Renci.SshNet;
using Xunit;
using Xunit.Abstractions;

namespace OmniSync.Tests;

/// <summary>
/// Velocidade do destino SFTP num link com latência alta (VPN), simulado por <see cref="LatencyProxy"/>.
/// Garante que a gravação não espera a confirmação de cada pedaço e que o custo fixo por arquivo é dividido
/// entre cópias simultâneas.
/// </summary>
[Collection(RemoteStateCollection.Name)]
public sealed class SftpLatencyTests : IDisposable
{
    /// <summary>Atraso em cada sentido: ida e volta de 100 ms, perto da VPN medida no uso real (~95 ms).</summary>
    private static readonly TimeSpan OneWay = TimeSpan.FromMilliseconds(50);

    private readonly ITestOutputHelper _output;
    private readonly MemoryRemoteSettings _settings = new();
    private readonly MemorySecretStore _secrets = new();
    private readonly IRemoteSettings _previousSettings = RemoteConnections.Settings;
    private readonly ISecretStore _previousSecrets = CredentialStore.Current;
    private readonly string _local = Path.Combine(Path.GetTempPath(), "OmniSyncLatency_" + Guid.NewGuid().ToString("N"));
    private readonly LatencyProxy? _proxy;
    private readonly RemoteAddress? _remote;

    /// <summary>Isola as configurações remotas e abre o repassador com latência.</summary>
    public SftpLatencyTests(ITestOutputHelper output)
    {
        _output = output;
        RemoteConnections.DisconnectAll();
        RemoteConnections.Settings = _settings;
        CredentialStore.Current = _secrets;
        Directory.CreateDirectory(_local);
        if (SftpTestServer.Base is { } b)
        {
            var ip = IPAddress.TryParse(b.Host, out var parsed) ? parsed : Dns.GetHostAddresses(b.Host).First();
            _proxy = new LatencyProxy(new IPEndPoint(ip, b.Port), OneWay);
            _remote = RemoteAddress.Create(b.User, "127.0.0.1", _proxy.Port, b.Path + "/omnisync-latencia-" + Guid.NewGuid().ToString("N")[..12]);
        }
    }

    /// <summary>Apaga a pasta de teste no servidor (direto, sem o atraso) e devolve as configurações.</summary>
    public void Dispose()
    {
        RemoteConnections.DisconnectAll();
        if (_remote != null && SftpTestServer.Base is { } b)
        {
            try
            {
                using var client = new SftpClient(b.Host, b.Port, b.User, SftpTestServer.Password);
                client.Connect();
                DeleteTree(client, _remote.Path);
            }
            catch (Exception)
            {
                // Limpeza de teste
            }
        }
        _proxy?.Dispose();
        RemoteConnections.Settings = _previousSettings;
        CredentialStore.Current = _previousSecrets;
        try
        {
            Directory.Delete(_local, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static void DeleteTree(SftpClient client, string path)
    {
        if (!client.Exists(path)) return;
        foreach (var f in client.ListDirectory(path))
        {
            if (f.Name is "." or "..") continue;
            if (f.IsDirectory) DeleteTree(client, f.FullName);
            else client.DeleteFile(f.FullName);
        }
        client.DeleteDirectory(path);
    }

    private async Task<SftpFileSystem> ConnectAsync()
    {
        var prompt = new ScriptedPrompt();
        prompt.Passwords.Enqueue(SftpTestServer.Password);
        return await RemoteConnections.ConnectAsync(_remote!, prompt);
    }

    /// <summary>
    /// 8 MB gravados com ida e volta de 100 ms. Esperando a confirmação de cada pedaço (~32 KB), seriam mais de
    /// 250 idas e voltas (mais de 25 s); com os pedaços em trânsito ao mesmo tempo, leva poucos segundos.
    /// </summary>
    [SftpFact]
    public async Task Write_HighLatency_DoesNotWaitForEachChunk()
    {
        var fs = await ConnectAsync();
        var dir = _remote!.ToString();
        fs.CreateDirectory(dir);
        var data = new byte[8 * 1024 * 1024];
        new Random(7).NextBytes(data);
        var path = dir + "/grande.bin";

        var watch = Stopwatch.StartNew();
        await using (var stream = fs.CreateNew(path, data.Length, readBack: false))
        {
            for (int offset = 0; offset < data.Length; offset += 2 * 1024 * 1024)
            {
                await stream.WriteAsync(data.AsMemory(offset, 2 * 1024 * 1024));
            }
        }
        watch.Stop();
        _output.WriteLine($"8 MB com ida e volta de 100 ms: {watch.Elapsed.TotalSeconds:F1} s");

        Assert.Equal(data.Length, fs.GetFile(path)!.Value.Length);
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(data), fs.ComputeSha256OnServer(path));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Gravação lenta demais: {watch.Elapsed.TotalSeconds:F1} s");
    }

    /// <summary>
    /// 40 arquivos pequenos em 4 subpastas com ida e volta de 100 ms, conferidos pelo servidor. Cada arquivo custa
    /// uma dúzia de idas e voltas (criar, gravar, conferir, data, renomear); uma após a outra, passa de 100 s.
    /// </summary>
    [SftpFact]
    public async Task Sync_ManySmallFiles_HighLatency_RunsCopiesTogether()
    {
        await ConnectAsync();
        var src = Path.Combine(_local, "origem");
        var random = new Random(11);
        for (int i = 0; i < 40; i++)
        {
            var file = Path.Combine(src, "pasta" + (i % 4), $"arquivo {i:D2} é.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var bytes = new byte[random.Next(1000, 60000)];
            random.NextBytes(bytes);
            File.WriteAllBytes(file, bytes);
        }
        var dst = _remote!.ToString();

        var engine = new SyncEngine { VerifyCopies = true };
        var scan = await engine.ScanDirectoriesAsync(src, dst, new ScanOptions(), null, CancellationToken.None);
        var watch = Stopwatch.StartNew();
        var result = await engine.ExecuteSyncAsync(scan.Items, dst, _ => Task.FromResult(ConflictResolution.Replace), null, null, null, CancellationToken.None);
        watch.Stop();
        _output.WriteLine($"40 arquivos com ida e volta de 100 ms: {watch.Elapsed.TotalSeconds:F1} s");

        Assert.Equal(0, result.Failed);
        Assert.Equal(40, result.Copied);
        Assert.Equal(40, result.VerifiedByHash);
        var again = await engine.ScanDirectoriesAsync(src, dst, new ScanOptions(), null, CancellationToken.None);
        Assert.All(again.Items, i => Assert.Equal(SyncActionType.Identical, i.ActionType));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(50), $"Sincronização lenta demais: {watch.Elapsed.TotalSeconds:F1} s");
    }

    /// <summary>
    /// A conexão cai (como a VPN) no meio de uma sincronização com cópias simultâneas: as cópias interrompidas são
    /// repetidas numa conexão nova e todas terminam conferidas, sem temporários sobrando.
    /// </summary>
    [SftpFact]
    public async Task Sync_ConnectionDropsMidway_RetriesAndCompletes()
    {
        var fs = await ConnectAsync();
        var src = Path.Combine(_local, "origem");
        var random = new Random(5);
        for (int i = 0; i < 16; i++)
        {
            var file = Path.Combine(src, $"f{i:D2}.bin");
            Directory.CreateDirectory(src);
            var bytes = new byte[400_000];
            random.NextBytes(bytes);
            File.WriteAllBytes(file, bytes);
        }
        var dst = _remote!.ToString();

        var engine = new SyncEngine { VerifyCopies = true };
        var scan = await engine.ScanDirectoriesAsync(src, dst, new ScanOptions(), null, CancellationToken.None);
        var run = engine.ExecuteSyncAsync(scan.Items, dst, _ => Task.FromResult(ConflictResolution.Replace), null, null, null, CancellationToken.None);
        await Task.Delay(1500);
        _proxy!.DropAll();
        var result = await run.WaitAsync(TimeSpan.FromMinutes(2));

        foreach (var failed in result.FailedItems) _output.WriteLine($"{failed.RelativePath}: {failed.StatusMessage}");
        Assert.Equal(0, result.Failed);
        Assert.Equal(16, result.Copied);
        Assert.Equal(16, result.VerifiedByHash + result.VerifiedBySize);
        Assert.DoesNotContain(fs.EnumerateFilesRecursive(dst), e => e.Name.EndsWith(PathHelper.TempSuffix, StringComparison.Ordinal));
        var again = await engine.ScanDirectoriesAsync(src, dst, new ScanOptions(), null, CancellationToken.None);
        Assert.All(again.Items, i => Assert.Equal(SyncActionType.Identical, i.ActionType));
    }
}
