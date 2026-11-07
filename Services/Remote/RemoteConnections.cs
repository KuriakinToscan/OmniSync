using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using OmniSync.Models;
using OmniSync.Services.FileSystems;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace OmniSync.Services.Remote;

/// <summary>Por que uma conexão remota não pôde ser aberta.</summary>
public enum RemoteAccessProblem
{
    /// <summary>Não há senha (ou frase-senha da chave) guardada nem informada.</summary>
    MissingSecret,
    /// <summary>O servidor recusou o usuário, a senha ou a chave.</summary>
    AuthenticationFailed,
    /// <summary>Primeira conexão com este servidor: a chave dele ainda não foi confirmada.</summary>
    UnknownHostKey,
    /// <summary>A chave do servidor mudou desde a confirmação: a conexão é recusada.</summary>
    ChangedHostKey,
    /// <summary>O arquivo de chave privada não existe ou não pôde ser lido.</summary>
    KeyFileProblem,
    /// <summary>O servidor não respondeu (endereço, porta, rede).</summary>
    Unreachable
}

/// <summary>
/// Falha ao abrir uma conexão remota. Sem janela (agendamento e linha de comando), as pendências que dependem do
/// usuário (<see cref="RequiresUser"/>) encerram a execução com o código 7.
/// </summary>
public sealed class RemoteAccessException : Exception
{
    /// <summary>Cria a exceção.</summary>
    public RemoteAccessException(RemoteAccessProblem problem, RemoteAddress address, string message, string? fingerprint = null, bool isPassphrase = false, Exception? inner = null)
        : base(message, inner)
    {
        Problem = problem;
        Address = address;
        Fingerprint = fingerprint;
        IsPassphrase = isPassphrase;
    }

    /// <summary>Tipo de problema.</summary>
    public RemoteAccessProblem Problem { get; }

    /// <summary>Endereço da conexão.</summary>
    public RemoteAddress Address { get; }

    /// <summary>Impressão digital SHA-256 apresentada pelo servidor (chave desconhecida ou alterada).</summary>
    public string? Fingerprint { get; }

    /// <summary>O segredo que falta é a frase-senha de um arquivo de chave (e não a senha do usuário).</summary>
    public bool IsPassphrase { get; }

    /// <summary>Indica se o problema se resolve abrindo o OmniSync (senha, chave do servidor).</summary>
    public bool RequiresUser => Problem is RemoteAccessProblem.MissingSecret or RemoteAccessProblem.AuthenticationFailed
        or RemoteAccessProblem.UnknownHostKey or RemoteAccessProblem.ChangedHostKey;
}

/// <summary>Configurações remotas que a conexão consulta: favoritos e chaves de servidor confirmadas.</summary>
public interface IRemoteSettings
{
    /// <summary>Favorito da mesma conexão (usuário, servidor e porta), se houver.</summary>
    RemoteFavorite? FindFavorite(RemoteAddress address);

    /// <summary>Impressão digital confirmada para <c>servidor:porta</c>, ou <c>null</c>.</summary>
    string? GetHostKey(string hostPortKey);

    /// <summary>Guarda a impressão digital confirmada pelo usuário.</summary>
    void SaveHostKey(string hostPortKey, string fingerprint);
}

/// <summary>Resposta do usuário ao pedido de senha.</summary>
/// <param name="Secret">Senha ou frase-senha digitada.</param>
/// <param name="Store">Guardar no cofre do sistema.</param>
public sealed record SecretAnswer(string Secret, bool Store);

/// <summary>Perguntas ao usuário durante a conexão (só com janela).</summary>
public interface IRemotePrompt
{
    /// <summary>Mostra a impressão digital de um servidor novo e pergunta se ele é confiável.</summary>
    Task<bool> ConfirmHostKeyAsync(RemoteAddress address, string fingerprint);

    /// <summary>Pede a senha (ou a frase-senha da chave); <c>null</c> se o usuário cancelar.</summary>
    Task<SecretAnswer?> AskSecretAsync(RemoteAddress address, bool isPassphrase, bool previousFailed, bool canStore);
}

/// <summary>
/// <see cref="IRemoteSettings"/> que lê o <c>settings.json</c> a cada consulta (linha de comando e agendamento).
/// </summary>
public sealed class FileRemoteSettings : IRemoteSettings
{
    private readonly string _path;

    /// <summary>Cria sobre o arquivo de configurações indicado.</summary>
    public FileRemoteSettings(string path) => _path = path;

    /// <inheritdoc />
    public RemoteFavorite? FindFavorite(RemoteAddress address) => RemoteConnections.FindFavorite(SettingsService.Load(_path).Favorites, address);

    /// <inheritdoc />
    public string? GetHostKey(string hostPortKey) =>
        SettingsService.Load(_path).KnownHostKeys.TryGetValue(hostPortKey, out var fingerprint) ? fingerprint : null;

    /// <inheritdoc />
    public void SaveHostKey(string hostPortKey, string fingerprint)
    {
        var settings = SettingsService.Load(_path);
        settings.KnownHostKeys[hostPortKey] = fingerprint;
        SettingsService.Save(settings, _path);
    }
}

/// <summary>
/// Abre e guarda as conexões SFTP do processo, uma por usuário/servidor/porta. Sem janela, usa só o que já está
/// guardado (senha no cofre, chave do servidor confirmada); com janela, pergunta o que faltar.
/// </summary>
public static class RemoteConnections
{
    private static readonly ConcurrentDictionary<string, SftpFileSystem> Cache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> SessionSecrets = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim ConnectGate = new(1, 1);

    /// <summary>Favoritos e chaves de servidor; a interface troca pela própria lista ao iniciar.</summary>
    public static IRemoteSettings Settings { get; set; } = new FileRemoteSettings(AppPaths.SettingsFile);

    /// <summary>Tempo máximo para abrir a conexão.</summary>
    public static TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Favorito da mesma conexão do endereço (usuário, servidor e porta), se houver.</summary>
    public static RemoteFavorite? FindFavorite(IEnumerable<RemoteFavorite> favorites, RemoteAddress address)
    {
        foreach (var favorite in favorites)
        {
            if (RemoteAddress.TryParse(favorite.Address, out var favAddress, out _) && favAddress.SameEndpoint(address))
            {
                return favorite;
            }
        }
        return null;
    }

    /// <summary>
    /// Conexão para o endereço, sem perguntar nada: reaproveita a aberta ou abre com a senha da sessão ou do cofre.
    /// </summary>
    /// <exception cref="RemoteAccessException">Falta senha, chave do servidor não confirmada, recusa ou servidor fora do ar.</exception>
    public static SftpFileSystem Get(RemoteAddress address)
    {
        if (Cache.TryGetValue(address.EndpointKey, out var cached)) return cached;
        ConnectGate.Wait();
        try
        {
            if (Cache.TryGetValue(address.EndpointKey, out cached)) return cached;
            var fs = ConnectCore(address, FindSecret(address));
            Cache[address.EndpointKey] = fs;
            return fs;
        }
        finally
        {
            ConnectGate.Release();
        }
    }

    /// <summary>
    /// Abre (ou reaproveita) a conexão perguntando ao usuário o que faltar: confirmação da chave de um servidor novo
    /// e senha. A senha confirmada fica na memória até o OmniSync fechar e, se o usuário pedir, no cofre.
    /// </summary>
    /// <param name="address">Endereço remoto.</param>
    /// <param name="prompt">Perguntas ao usuário; <c>null</c> equivale a <see cref="Get"/>.</param>
    /// <param name="forceNew">Fecha a conexão aberta e conecta de novo (Testar conexão).</param>
    /// <exception cref="RemoteAccessException">Falha que o usuário não resolveu (cancelou, chave alterada, servidor fora do ar).</exception>
    public static async Task<SftpFileSystem> ConnectAsync(RemoteAddress address, IRemotePrompt? prompt, bool forceNew = false)
    {
        if (forceNew) Disconnect(address);
        else if (Cache.TryGetValue(address.EndpointKey, out var cached)) return cached;

        var secret = FindSecret(address);
        bool storeSecret = false;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await ConnectGate.WaitAsync().ConfigureAwait(false);
                SftpFileSystem fs;
                try
                {
                    var current = secret;
                    fs = await Task.Run(() => ConnectCore(address, current)).ConfigureAwait(false);
                    if (Cache.TryRemove(address.EndpointKey, out var old)) old.Dispose();
                    Cache[address.EndpointKey] = fs;
                }
                finally
                {
                    ConnectGate.Release();
                }

                if (secret != null)
                {
                    SessionSecrets[address.EndpointKey] = secret;
                    if (storeSecret && !CredentialStore.Current.Write(CredentialStore.KeyFor(address.EndpointKey), secret))
                    {
                        FileLogService.Write($"[AVISO] A senha de {address.EndpointKey} não pôde ser guardada no {CredentialStore.Current.DisplayName}.");
                    }
                }
                return fs;
            }
            catch (RemoteAccessException ex) when (prompt != null && attempt < 6)
            {
                switch (ex.Problem)
                {
                    case RemoteAccessProblem.UnknownHostKey:
                        if (!await prompt.ConfirmHostKeyAsync(address, ex.Fingerprint!).ConfigureAwait(false)) throw;
                        Settings.SaveHostKey(address.HostPortKey, ex.Fingerprint!);
                        break;
                    case RemoteAccessProblem.MissingSecret:
                    case RemoteAccessProblem.AuthenticationFailed:
                        if (ex.Problem == RemoteAccessProblem.AuthenticationFailed) ForgetSecret(address, fromStore: false);
                        var answer = await prompt.AskSecretAsync(
                            address, ex.IsPassphrase, ex.Problem == RemoteAccessProblem.AuthenticationFailed, CredentialStore.Current.IsAvailable).ConfigureAwait(false);
                        if (answer == null) throw;
                        secret = answer.Secret;
                        storeSecret = answer.Store;
                        break;
                    default:
                        throw;
                }
            }
        }
    }

    /// <summary>Fecha a conexão aberta com o endereço, se houver.</summary>
    public static void Disconnect(RemoteAddress address)
    {
        if (Cache.TryRemove(address.EndpointKey, out var fs)) fs.Dispose();
    }

    /// <summary>Fecha todas as conexões (ao sair).</summary>
    public static void DisconnectAll()
    {
        foreach (var key in Cache.Keys.ToList())
        {
            if (Cache.TryRemove(key, out var fs)) fs.Dispose();
        }
    }

    /// <summary>Esquece a senha da conexão: na memória e, com <paramref name="fromStore"/>, também no cofre.</summary>
    public static void ForgetSecret(RemoteAddress address, bool fromStore)
    {
        SessionSecrets.TryRemove(address.EndpointKey, out _);
        if (fromStore) CredentialStore.Current.Delete(CredentialStore.KeyFor(address.EndpointKey));
    }

    /// <summary>Guarda a senha no cofre (favorito salvo com senha).</summary>
    public static bool StoreSecret(RemoteAddress address, string secret)
    {
        SessionSecrets[address.EndpointKey] = secret;
        return CredentialStore.Current.Write(CredentialStore.KeyFor(address.EndpointKey), secret);
    }

    /// <summary>Indica se há senha guardada no cofre para a conexão.</summary>
    public static bool HasStoredSecret(RemoteAddress address) =>
        CredentialStore.Current.Read(CredentialStore.KeyFor(address.EndpointKey)) != null;

    private static string? FindSecret(RemoteAddress address)
    {
        if (SessionSecrets.TryGetValue(address.EndpointKey, out var secret)) return secret;
        return CredentialStore.Current.Read(CredentialStore.KeyFor(address.EndpointKey));
    }

    /// <summary>Abre a conexão com o segredo informado, sem perguntar nada.</summary>
    private static SftpFileSystem ConnectCore(RemoteAddress address, string? secret)
    {
        var favorite = Settings.FindFavorite(address);
        var methods = new List<AuthenticationMethod>();
        var keyFile = favorite?.KeyFile;
        bool usesKey = !string.IsNullOrWhiteSpace(keyFile);
        if (usesKey)
        {
            keyFile = PathHelper.ExpandUserPath(keyFile);
            if (!File.Exists(keyFile))
            {
                throw new RemoteAccessException(RemoteAccessProblem.KeyFileProblem, address, $"O arquivo de chave {keyFile} não existe.");
            }
            PrivateKeyFile key;
            try
            {
                key = string.IsNullOrEmpty(secret) ? new PrivateKeyFile(keyFile) : new PrivateKeyFile(keyFile, secret);
            }
            catch (SshPassPhraseNullOrEmptyException ex)
            {
                throw new RemoteAccessException(RemoteAccessProblem.MissingSecret, address,
                    $"A chave {keyFile} tem frase-senha, e ela não está guardada.", isPassphrase: true, inner: ex);
            }
            catch (SshException ex) when (!string.IsNullOrEmpty(secret))
            {
                throw new RemoteAccessException(RemoteAccessProblem.AuthenticationFailed, address,
                    $"A frase-senha da chave {keyFile} não confere.", isPassphrase: true, inner: ex);
            }
            catch (Exception ex) when (ex is SshException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                throw new RemoteAccessException(RemoteAccessProblem.KeyFileProblem, address,
                    $"O arquivo de chave {keyFile} não pôde ser lido: {ErrorMessages.AfterColon(ex.Message)}", inner: ex);
            }
            methods.Add(new PrivateKeyAuthenticationMethod(address.User, key));
        }
        else
        {
            if (secret == null)
            {
                throw new RemoteAccessException(RemoteAccessProblem.MissingSecret, address,
                    $"Não há senha guardada para {address.EndpointKey}. Abra o OmniSync e conecte uma vez para informar a senha.");
            }
            methods.Add(new PasswordAuthenticationMethod(address.User, secret));
            // Vários NAS só aceitam a senha pelo método "keyboard-interactive"
            var keyboard = new KeyboardInteractiveAuthenticationMethod(address.User);
            keyboard.AuthenticationPrompt += (_, e) =>
            {
                foreach (var p in e.Prompts) p.Response = secret;
            };
            methods.Add(keyboard);
        }

        var info = new ConnectionInfo(address.Host, address.Port, address.User, methods.ToArray()) { Timeout = ConnectTimeout };
        var client = new SftpClient(info)
        {
            OperationTimeout = TimeSpan.FromMinutes(2),
            KeepAliveInterval = TimeSpan.FromSeconds(30)
        };

        var known = Settings.GetHostKey(address.HostPortKey);
        string? presented = null;
        client.HostKeyReceived += (_, e) =>
        {
            presented = e.FingerPrintSHA256;
            e.CanTrust = known != null && string.Equals(known, presented, StringComparison.Ordinal);
        };

        try
        {
            client.Connect();
            var endpoint = address.WithPath("/");
            var confirmed = presented;
            // Canal de comandos para o SHA-256 no servidor: mesmas credenciais e mesma chave de servidor já conferida
            SshClient ShellFactory()
            {
                var shell = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
                shell.HostKeyReceived += (_, e) => e.CanTrust = string.Equals(confirmed, e.FingerPrintSHA256, StringComparison.Ordinal);
                return shell;
            }
            bool PreferReadBack() => Settings.FindFavorite(address)?.VerifyByReading == true;
            return new SftpFileSystem(client, endpoint, ShellFactory, PreferReadBack);
        }
        catch (Exception ex) when (presented != null && !string.Equals(known, presented, StringComparison.Ordinal))
        {
            client.Dispose();
            if (known == null)
            {
                throw new RemoteAccessException(RemoteAccessProblem.UnknownHostKey, address,
                    $"Primeira conexão com {address.HostPortKey}: a chave do servidor ainda não foi confirmada. Abra o OmniSync e conecte uma vez.",
                    presented, inner: ex);
            }
            throw new RemoteAccessException(RemoteAccessProblem.ChangedHostKey, address,
                $"A chave do servidor {address.HostPortKey} mudou desde a confirmação (agora SHA256:{presented}). A conexão foi recusada: " +
                "pode ser outro servidor no lugar do verdadeiro. Se o servidor foi reinstalado, esqueça a chave antiga no favorito.",
                presented, inner: ex);
        }
        catch (SshAuthenticationException ex)
        {
            client.Dispose();
            throw new RemoteAccessException(RemoteAccessProblem.AuthenticationFailed, address,
                usesKey ? $"O servidor recusou a chave de {address.EndpointKey}." : $"O servidor recusou o usuário ou a senha de {address.EndpointKey}.",
                isPassphrase: false, inner: ex);
        }
        catch (Exception ex) when (ex is SshException or SocketException or IOException or TimeoutException or ProxyException)
        {
            client.Dispose();
            throw new RemoteAccessException(RemoteAccessProblem.Unreachable, address,
                $"Não foi possível conectar a {address.HostPortKey}: {ErrorMessages.AfterColon(ex.Message)}", inner: ex);
        }
    }
}
