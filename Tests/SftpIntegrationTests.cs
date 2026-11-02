using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OmniSync.Cli;
using OmniSync.Models;
using OmniSync.Services;
using OmniSync.Services.FileSystems;
using OmniSync.Services.Remote;
using Renci.SshNet;
using Xunit;

namespace OmniSync.Tests;

/// <summary>
/// Teste que precisa de um servidor SFTP: roda quando <c>OMNISYNC_SFTP_TEST</c> (pasta base, ex.:
/// <c>sftp://usuario@127.0.0.1:2222/home/usuario</c>) e <c>OMNISYNC_SFTP_PASSWORD</c> estão definidas. No CI do
/// Linux, um OpenSSH local fornece o servidor e ele é obrigatório; fora dele, sem as variáveis, o teste aparece como ignorado.
/// </summary>
public sealed class SftpFactAttribute : FactAttribute
{
    /// <summary>Marca o teste como ignorado quando não há servidor configurado.</summary>
    public SftpFactAttribute()
    {
        // No CI do Linux (OMNISYNC_SFTP_REQUIRED=1) o servidor é obrigatório: sem ele, o teste falha em vez de sumir
        if (SftpTestServer.Base == null && Environment.GetEnvironmentVariable("OMNISYNC_SFTP_REQUIRED") != "1")
        {
            Skip = "Sem servidor SFTP de teste (OMNISYNC_SFTP_TEST e OMNISYNC_SFTP_PASSWORD).";
        }
    }
}

/// <summary>Servidor SFTP de teste definido pelas variáveis de ambiente.</summary>
public static class SftpTestServer
{
    /// <summary>Pasta base no servidor.</summary>
    public static RemoteAddress? Base { get; } =
        RemoteAddress.TryParse(Environment.GetEnvironmentVariable("OMNISYNC_SFTP_TEST"), out var a, out _) &&
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OMNISYNC_SFTP_PASSWORD")) ? a : null;

    /// <summary>Senha do usuário de teste.</summary>
    public static string Password => Environment.GetEnvironmentVariable("OMNISYNC_SFTP_PASSWORD") ?? string.Empty;
}

/// <summary>Configurações remotas em memória.</summary>
public sealed class MemoryRemoteSettings : IRemoteSettings
{
    /// <summary>Favoritos.</summary>
    public List<RemoteFavorite> Favorites { get; } = new();

    /// <summary>Chaves confirmadas.</summary>
    public ConcurrentDictionary<string, string> HostKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public RemoteFavorite? FindFavorite(RemoteAddress address) => RemoteConnections.FindFavorite(Favorites, address);

    /// <inheritdoc />
    public string? GetHostKey(string hostPortKey) => HostKeys.TryGetValue(hostPortKey, out var v) ? v : null;

    /// <inheritdoc />
    public void SaveHostKey(string hostPortKey, string fingerprint) => HostKeys[hostPortKey] = fingerprint;
}

/// <summary>Cofre em memória.</summary>
public sealed class MemorySecretStore : ISecretStore
{
    /// <summary>Segredos guardados.</summary>
    public ConcurrentDictionary<string, string> Secrets { get; } = new();

    /// <inheritdoc />
    public string DisplayName => "cofre de teste";

    /// <inheritdoc />
    public bool IsAvailable => true;

    /// <inheritdoc />
    public string? Read(string key) => Secrets.TryGetValue(key, out var v) ? v : null;

    /// <inheritdoc />
    public bool Write(string key, string secret)
    {
        Secrets[key] = secret;
        return true;
    }

    /// <inheritdoc />
    public void Delete(string key) => Secrets.TryRemove(key, out _);
}

/// <summary>Respostas programadas às perguntas da conexão.</summary>
public sealed class ScriptedPrompt : IRemotePrompt
{
    /// <summary>Resposta à chave do servidor.</summary>
    public bool TrustHost { get; set; } = true;

    /// <summary>Senhas a responder, em ordem.</summary>
    public Queue<string?> Passwords { get; } = new();

    /// <summary>Guardar no cofre.</summary>
    public bool Store { get; set; } = true;

    /// <summary>Perguntas de chave recebidas.</summary>
    public int HostKeyQuestions { get; private set; }

    /// <summary>Pedidos de senha recebidos (e se o anterior falhou).</summary>
    public List<bool> SecretQuestions { get; } = new();

    /// <inheritdoc />
    public Task<bool> ConfirmHostKeyAsync(RemoteAddress address, string fingerprint)
    {
        HostKeyQuestions++;
        Assert.False(string.IsNullOrWhiteSpace(fingerprint));
        return Task.FromResult(TrustHost);
    }

    /// <inheritdoc />
    public Task<SecretAnswer?> AskSecretAsync(RemoteAddress address, bool isPassphrase, bool previousFailed, bool canStore)
    {
        SecretQuestions.Add(previousFailed);
        var next = Passwords.Count > 0 ? Passwords.Dequeue() : null;
        return Task.FromResult(next == null ? null : new SecretAnswer(next, Store));
    }
}

/// <summary>
/// Testes contra um servidor SFTP real: conexão (chave do servidor, senha, cofre), sincronização local → SFTP e
/// SFTP → local com as garantias de <c>lixo/</c>, <c>old/</c> e cópia conferida, e restauração no remoto.
/// </summary>
[Collection(RemoteStateCollection.Name)]
public sealed class SftpIntegrationTests : IDisposable
{
    private static readonly DateTime OldTime = new(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime NewTime = new(2026, 3, 5, 9, 30, 0, DateTimeKind.Utc);

    private readonly MemoryRemoteSettings _settings = new();
    private readonly MemorySecretStore _secrets = new();
    private readonly IRemoteSettings _previousSettings = RemoteConnections.Settings;
    private readonly ISecretStore _previousSecrets = CredentialStore.Current;
    private readonly string _local = Path.Combine(Path.GetTempPath(), "OmniSyncSftp_" + Guid.NewGuid().ToString("N"));
    private readonly RemoteAddress? _remote;

    /// <summary>Isola as configurações remotas e cria a pasta de teste no servidor.</summary>
    public SftpIntegrationTests()
    {
        RemoteConnections.DisconnectAll();
        RemoteConnections.Settings = _settings;
        CredentialStore.Current = _secrets;
        Directory.CreateDirectory(_local);
        if (SftpTestServer.Base != null)
        {
            _remote = SftpTestServer.Base.WithPath(SftpTestServer.Base.Path + "/omnisync-teste-" + Guid.NewGuid().ToString("N")[..12]);
        }
    }

    /// <summary>Apaga as pastas de teste e devolve as configurações.</summary>
    public void Dispose()
    {
        if (_remote != null)
        {
            try
            {
                using var client = new SftpClient(_remote.Host, _remote.Port, _remote.User, SftpTestServer.Password);
                client.Connect();
                DeleteTree(client, _remote.Path);
            }
            catch (Exception)
            {
                // Limpeza de teste: sobra no servidor não deve mascarar o resultado
            }
        }
        RemoteConnections.DisconnectAll();
        RemoteConnections.ForgetSecret(SftpTestServer.Base ?? RemoteAddress.Parse("sftp://x@y/"), fromStore: false);
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

    private async Task<SftpFileSystem> ConnectTrustingAsync()
    {
        var prompt = new ScriptedPrompt();
        prompt.Passwords.Enqueue(SftpTestServer.Password);
        return await RemoteConnections.ConnectAsync(_remote!, prompt);
    }

    private static void WriteLocal(string path, string content, DateTime time)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, time);
    }

    private static void WriteRemote(IFileSystem fs, string path, string content, DateTime time)
    {
        fs.CreateDirectory(fs.GetDirectoryName(path)!);
        using (var stream = fs.CreateNew(path, 0, readBack: false))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            stream.Write(bytes, 0, bytes.Length);
        }
        fs.SetLastWriteTimeUtc(path, time);
    }

    private static string ReadRemote(IFileSystem fs, string path)
    {
        using var stream = fs.OpenRead(path);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Sem janela, servidor novo e senha ausente são recusados; com janela, o usuário confirma a chave e informa a
    /// senha, que vai para o cofre e passa a valer sem perguntas.
    /// </summary>
    [SftpFact]
    public async Task Connect_HostKeyAndPassword_ThenStoredForUnattendedRuns()
    {
        var unknown = Assert.Throws<RemoteAccessException>(() => RemoteConnections.Get(_remote!));
        Assert.True(unknown.Problem is RemoteAccessProblem.MissingSecret or RemoteAccessProblem.UnknownHostKey);
        Assert.True(unknown.RequiresUser);

        var prompt = new ScriptedPrompt();
        prompt.Passwords.Enqueue("senha-errada");
        prompt.Passwords.Enqueue(SftpTestServer.Password);
        var fs = await RemoteConnections.ConnectAsync(_remote!, prompt);

        Assert.True(fs.IsConnected);
        Assert.Equal(1, prompt.HostKeyQuestions);
        Assert.Equal(new[] { false, true }, prompt.SecretQuestions);
        Assert.Single(_settings.HostKeys);
        Assert.Equal(SftpTestServer.Password, _secrets.Read(CredentialStore.KeyFor(_remote!.EndpointKey)));

        // Execução agendada (outro processo): só cofre e chave confirmada
        RemoteConnections.DisconnectAll();
        RemoteConnections.ForgetSecret(_remote, fromStore: false);
        Assert.True(RemoteConnections.Get(_remote).IsConnected);
    }

    /// <summary>Chave do servidor diferente da confirmada: recusa, mesmo com a senha certa.</summary>
    [SftpFact]
    public async Task Connect_ChangedHostKey_IsRefused()
    {
        _settings.HostKeys[_remote!.HostPortKey] = "AAAAoutraChaveQualquerAAAAAAAAAAAAAAAAAAAAAA";
        _secrets.Write(CredentialStore.KeyFor(_remote.EndpointKey), SftpTestServer.Password);

        var ex = Assert.Throws<RemoteAccessException>(() => RemoteConnections.Get(_remote));
        Assert.Equal(RemoteAccessProblem.ChangedHostKey, ex.Problem);

        var interactive = await Assert.ThrowsAsync<RemoteAccessException>(() => RemoteConnections.ConnectAsync(_remote, new ScriptedPrompt()));
        Assert.Equal(RemoteAccessProblem.ChangedHostKey, interactive.Problem);
    }

    /// <summary>Senha guardada errada, sem janela: falha de autenticação que depende do usuário.</summary>
    [SftpFact]
    public async Task Connect_WrongStoredPassword_Unattended()
    {
        await ConnectTrustingAsync();
        RemoteConnections.DisconnectAll();
        RemoteConnections.ForgetSecret(_remote!, fromStore: false);
        _secrets.Write(CredentialStore.KeyFor(_remote!.EndpointKey), "senha-errada");

        var ex = Assert.Throws<RemoteAccessException>(() => RemoteConnections.Get(_remote));
        Assert.Equal(RemoteAccessProblem.AuthenticationFailed, ex.Problem);
        Assert.True(ex.RequiresUser);
    }

    /// <summary>
    /// Local → SFTP: novo copiado com data preservada, conflito com a versão anterior em <c>old/</c>, órfão em
    /// <c>lixo/</c>, pasta vazia removida, reanálise idêntica e restauração do <c>lixo/</c> no servidor.
    /// </summary>
    [SftpFact]
    public async Task LocalToSftp_FullCycle()
    {
        var fs = await ConnectTrustingAsync();
        var src = Path.Combine(_local, "Campo");
        WriteLocal(Path.Combine(src, "novo.txt"), "novo", NewTime);
        WriteLocal(Path.Combine(src, "sub", "igual.txt"), "igual", OldTime);
        WriteLocal(Path.Combine(src, "conflito.txt"), "versao_nova", NewTime);
        WriteLocal(Path.Combine(src, "com espaço é.txt"), "acento", NewTime);

        var dst = _remote!.WithPath(_remote.Path + "/Campo").ToString();
        WriteRemote(fs, dst + "/sub/igual.txt", "igual", OldTime);
        WriteRemote(fs, dst + "/conflito.txt", "versao_antiga", OldTime);
        WriteRemote(fs, dst + "/pasta/orfao.txt", "orfao", OldTime);

        var engine = new SyncEngine { VerifyCopies = true };
        var scan = await engine.ScanDirectoriesAsync(src, dst, new ScanOptions(), null, CancellationToken.None);
        Assert.Equal(SyncActionType.New, scan.Items.Single(i => i.RelativePath == "novo.txt").ActionType);
        Assert.Equal(SyncActionType.Identical, scan.Items.Single(i => i.RelativePath == Path.Combine("sub", "igual.txt")).ActionType);
        Assert.Equal(SyncActionType.Conflict, scan.Items.Single(i => i.RelativePath == "conflito.txt").ActionType);
        Assert.Equal(SyncActionType.OrphanTrash, scan.Items.Single(i => i.RelativePath == Path.Combine("pasta", "orfao.txt")).ActionType);

        var result = await engine.ExecuteSyncAsync(scan.Items, dst, _ => Task.FromResult(ConflictResolution.Replace), null, null, null, CancellationToken.None);
        Assert.Equal(0, result.Failed);
        Assert.Equal(2, result.Copied);
        Assert.Equal(1, result.Replaced);
        Assert.Equal(1, result.Trashed);

        Assert.Equal("novo", ReadRemote(fs, dst + "/novo.txt"));
        Assert.Equal("acento", ReadRemote(fs, dst + "/com espaço é.txt"));
        Assert.Equal(NewTime, fs.GetFile(dst + "/novo.txt")!.Value.LastWriteUtc);
        Assert.Equal("versao_nova", ReadRemote(fs, dst + "/conflito.txt"));
        Assert.Equal("versao_antiga", ReadRemote(fs, dst + "/old/conflito.txt"));
        Assert.Equal("orfao", ReadRemote(fs, dst + "/lixo/pasta/orfao.txt"));
        Assert.False(fs.DirectoryExists(dst + "/pasta"));
        Assert.DoesNotContain(fs.EnumerateFilesRecursive(dst), e => e.Name.EndsWith(PathHelper.TempSuffix, StringComparison.Ordinal));

        var again = await engine.ScanDirectoriesAsync(src, dst, new ScanOptions(), null, CancellationToken.None);
        Assert.All(again.Items, i => Assert.Equal(SyncActionType.Identical, i.ActionType));

        var restore = new RestoreService();
        var trash = await restore.ListTrashAsync(dst, CancellationToken.None);
        Assert.Single(trash);
        var restored = await restore.RestoreAsync(dst, trash, null, CancellationToken.None);
        Assert.Equal(1, restored.Restored);
        Assert.Equal("orfao", ReadRemote(fs, dst + "/pasta/orfao.txt"));
        Assert.False(fs.DirectoryExists(dst + "/lixo"));
    }

    /// <summary>SFTP → local: puxar do servidor, com conferência por SHA-256 na análise.</summary>
    [SftpFact]
    public async Task SftpToLocal_PullWithDeepHash()
    {
        var fs = await ConnectTrustingAsync();
        var src = _remote!.WithPath(_remote.Path + "/Levantamento").ToString();
        WriteRemote(fs, src + "/a.txt", "aaa", NewTime);
        WriteRemote(fs, src + "/fotos/b.jpg", new string('b', 300_000), NewTime);
        WriteRemote(fs, src + "/c.txt", "xyz", NewTime);
        var dst = Path.Combine(_local, "Backup");
        WriteLocal(Path.Combine(dst, "c.txt"), "abc", NewTime);

        var engine = new SyncEngine { VerifyCopies = true };
        var scan = await engine.ScanDirectoriesAsync(src, dst, new ScanOptions { UseDeepHash = true }, null, CancellationToken.None);
        Assert.Equal(SyncActionType.Conflict, scan.Items.Single(i => i.RelativePath == "c.txt").ActionType);

        var result = await engine.ExecuteSyncAsync(scan.Items, dst, _ => Task.FromResult(ConflictResolution.Replace), null, null, null, CancellationToken.None);
        Assert.Equal(0, result.Failed);
        Assert.Equal(new string('b', 300_000), File.ReadAllText(Path.Combine(dst, "fotos", "b.jpg")));
        Assert.Equal("xyz", File.ReadAllText(Path.Combine(dst, "c.txt")));
        Assert.Equal("abc", File.ReadAllText(Path.Combine(dst, "old", "c.txt")));
        Assert.Equal(NewTime, File.GetLastWriteTimeUtc(Path.Combine(dst, "a.txt")));
    }

    /// <summary>
    /// Linha de comando agendada com destino SFTP: sem senha guardada para com o código 7, sem alterar nada e com
    /// aviso; depois de conectar uma vez pela interface (senha no cofre), sincroniza sem perguntar.
    /// </summary>
    [SftpFact]
    public async Task Cli_RemotePendingThenRuns()
    {
        var src = Path.Combine(_local, "Campo");
        WriteLocal(Path.Combine(src, "a.txt"), "a", NewTime);
        var settingsPath = Path.Combine(_local, "dados", "settings.json");
        var settings = new AppSettings();
        settings.Profiles.Add(new SyncProfile { Name = "Remota", SourcePath = src, DestinationPath = _remote!.ToString(), UseSourceFolderNameInDestination = true });
        SettingsService.Save(settings, settingsPath);
        var command = new CliCommand(CliAction.Run, "Remota", CliConflictPolicy.Keep, null, false, Scheduled: true);

        var output = new StringWriter();
        var first = new CliRunner(new SyncEngine(), output, settingsPath, new ScheduleTests.FakeScheduler());
        Assert.Equal(CliExitCode.RemotePending, await first.RunAsync(command, CancellationToken.None));
        Assert.Contains("Abra o OmniSync", output.ToString());
        Assert.NotNull(first.Notification);
        Assert.True(first.Notification!.IsProblem);

        await ConnectTrustingAsync();
        RemoteConnections.DisconnectAll();
        RemoteConnections.ForgetSecret(_remote, fromStore: false);

        var second = new CliRunner(new SyncEngine(), new StringWriter(), settingsPath, new ScheduleTests.FakeScheduler());
        Assert.Equal(CliExitCode.Success, await second.RunAsync(command, CancellationToken.None));
        var fs = RemoteConnections.Get(_remote);
        Assert.Equal("a", ReadRemote(fs, _remote + "/Campo/a.txt"));
    }

    /// <summary>Origem remota inexistente: mesma mensagem do local, sem travar.</summary>
    [SftpFact]
    public async Task MissingRemoteSource_Throws()
    {
        await ConnectTrustingAsync();
        var engine = new SyncEngine();
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            engine.ScanDirectoriesAsync(_remote!.WithPath(_remote.Path + "/nao-existe").ToString(), _local, new ScanOptions(), null, CancellationToken.None));
    }
}

/// <summary>Testes que mexem no estado global das conexões remotas não rodam em paralelo.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RemoteStateCollection
{
    /// <summary>Nome da coleção.</summary>
    public const string Name = "Estado remoto";
}
