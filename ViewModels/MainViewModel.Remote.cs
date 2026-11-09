using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OmniSync.Models;
using OmniSync.Services;
using OmniSync.Services.FileSystems;
using OmniSync.Services.Remote;

namespace OmniSync.ViewModels;

/// <summary>
/// Destinos remotos na interface: favoritos, chaves de servidor confirmadas e conexão antes de analisar,
/// sincronizar ou restaurar (com as perguntas de senha e de chave do servidor).
/// </summary>
public partial class MainViewModel
{
    /// <summary>Perguntas ao usuário durante a conexão (janela de senha e de chave do servidor), injetadas pela View.</summary>
    public IRemotePrompt? RemotePrompt { get; set; }

    /// <summary>Favoritos salvos.</summary>
    public IReadOnlyList<RemoteFavorite> Favorites => _settings.Favorites;

    /// <summary>Chaves de servidor confirmadas (<c>servidor:porta</c> → SHA-256).</summary>
    public IReadOnlyDictionary<string, string> KnownHostKeys => _settings.KnownHostKeys;

    /// <summary>Liga as conexões remotas às configurações desta janela (favoritos e chaves confirmadas).</summary>
    private void InitializeRemote()
    {
        if (_persistSettings) RemoteConnections.Settings = new ViewModelRemoteSettings(this);
    }

    /// <summary>Substitui a lista de favoritos e grava.</summary>
    /// <param name="favorites">Lista nova.</param>
    public void SaveFavorites(IEnumerable<RemoteFavorite> favorites)
    {
        _settings.Favorites = favorites.ToList();
        OnPropertyChanged(nameof(Favorites));
        SaveSettings();
    }

    /// <summary>Esquece a chave confirmada de um servidor (servidor reinstalado) e grava.</summary>
    /// <param name="hostPortKey"><c>servidor:porta</c>.</param>
    public void ForgetHostKey(string hostPortKey)
    {
        if (_settings.KnownHostKeys.Remove(hostPortKey)) SaveSettings();
    }

    /// <summary>Preenche a origem ou o destino com o endereço de um favorito.</summary>
    /// <param name="favorite">Favorito escolhido.</param>
    /// <param name="isSource"><c>true</c> para a origem.</param>
    public void UseFavorite(RemoteFavorite favorite, bool isSource)
    {
        if (isSource) SourcePath = favorite.Address;
        else DestinationPath = favorite.Address;
    }

    /// <summary>
    /// Abre as conexões dos lados remotos da tarefa, perguntando o que faltar (senha, chave de servidor novo).
    /// </summary>
    /// <param name="paths">Caminhos a conferir (os locais são ignorados).</param>
    /// <returns><c>true</c> se todas as conexões abriram; senão a mensagem já foi mostrada.</returns>
    private async Task<bool> EnsureRemoteConnectedAsync(params string[] paths)
    {
        foreach (var path in paths.Where(RemoteAddress.IsRemote).Distinct(StringComparer.Ordinal))
        {
            if (!RemoteAddress.TryParse(path, out var address, out var parseError))
            {
                StatusMessage = $"Endereço remoto inválido: {parseError}";
                return false;
            }
            try
            {
                CurrentOperationText = $"Conectando a {address.HostPortKey}...";
                StatusMessage = $"Conectando a {address.Root}...";
                await RemoteConnections.ConnectAsync(address, RemotePrompt);
                AddLog($"[REMOTO] Conectado a {address.Root}.");
            }
            catch (RemoteAccessException ex)
            {
                StatusMessage = ex.Message;
                AddLog($"[ERRO] {ex.Message}");
                if (NotifyAsync != null && ex.Problem is not (RemoteAccessProblem.MissingSecret or RemoteAccessProblem.AuthenticationFailed or RemoteAccessProblem.UnknownHostKey))
                {
                    // Cancelar a senha ou recusar a chave foi escolha do usuário; o resto merece a janela de aviso
                    await NotifyAsync("Conexão remota", ex.Message);
                }
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Testa um endereço: conecta (perguntando o que faltar), lista a pasta, grava, renomeia e apaga um arquivo de teste.
    /// </summary>
    /// <param name="address">Endereço a testar.</param>
    /// <param name="prompt">Perguntas sobre a janela que pediu o teste; <c>null</c> usa as da janela principal.</param>
    /// <returns>Mensagem com o resultado e se deu certo.</returns>
    public async Task<(bool ok, string message)> TestRemoteAsync(RemoteAddress address, IRemotePrompt? prompt = null)
    {
        try
        {
            var fs = await RemoteConnections.ConnectAsync(address, prompt ?? RemotePrompt, forceNew: true);
            return await Task.Run(() =>
            {
                var folder = address.ToString();
                if (!fs.DirectoryExists(folder))
                {
                    return (false, $"Conectou, mas a pasta {address.Path} não existe no servidor.");
                }
                int count = fs.ListDirectory(folder).Count;
                var probe = fs.Combine(folder, ".omnisync-teste-" + Guid.NewGuid().ToString("N")[..8]);
                var renamed = probe + "-ok";
                string check;
                try
                {
                    using (var stream = fs.CreateNew(probe, 2, readBack: false))
                    {
                        stream.Write("ok"u8);
                    }
                    check = DescribeServerCheck(fs, probe);
                    fs.MoveFile(probe, renamed);
                    fs.DeleteFile(renamed);
                }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
                {
                    PathHelper.TryDeleteFile(fs, probe);
                    PathHelper.TryDeleteFile(fs, renamed);
                    return (false, $"Conectou e listou a pasta ({PtText.Count(count, "item", "itens")}), mas não conseguiu gravar e renomear: {ErrorMessages.AfterColon(ex.Message)}");
                }
                return (true, $"Conexão OK: a pasta {address.Path} tem {PtText.Count(count, "item", "itens")}, e gravar, renomear e apagar funcionaram. {check}");
            });
        }
        catch (RemoteAccessException ex)
        {
            return (false, ex.Message);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return (false, ErrorMessages.Translate(ex));
        }
    }

    /// <summary>
    /// Confere o SHA-256 calculado pelo servidor sobre o arquivo de teste e descreve a conferência que as cópias terão.
    /// </summary>
    private static string DescribeServerCheck(SftpFileSystem fs, string probe)
    {
        var tool = fs.ServerHashCommand;
        if (tool == null)
        {
            return "Conferência das cópias: só pelo tamanho, porque o servidor não executa comandos de SHA-256" +
                   (fs.CopyCheck == CopyCheck.ReadBack ? " (o favorito pede releitura pela rede, então elas terão SHA-256)." : ".");
        }
        var hash = fs.ComputeSha256OnServer(probe);
        if (hash == null)
        {
            return $"O servidor tem {tool}, mas não calculou o SHA-256 do arquivo de teste (o caminho no shell deve ser diferente do SFTP). " +
                   "Conferência das cópias: só pelo tamanho.";
        }
        if (!hash.AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData("ok"u8)))
        {
            return $"Atenção: o SHA-256 que o servidor calculou ({tool}) não confere com o arquivo de teste. As cópias para este servidor vão falhar na conferência.";
        }
        return $"Conferência das cópias: SHA-256 calculado pelo servidor ({tool}).";
    }

    /// <summary>Configurações remotas sobre as da janela (gravadas junto com as tarefas).</summary>
    private sealed class ViewModelRemoteSettings : IRemoteSettings
    {
        private readonly MainViewModel _owner;

        public ViewModelRemoteSettings(MainViewModel owner) => _owner = owner;

        public RemoteFavorite? FindFavorite(RemoteAddress address) => RemoteConnections.FindFavorite(_owner._settings.Favorites, address);

        public string? GetHostKey(string hostPortKey) =>
            _owner._settings.KnownHostKeys.TryGetValue(hostPortKey, out var fingerprint) ? fingerprint : null;

        public void SaveHostKey(string hostPortKey, string fingerprint)
        {
            // Chamado na thread da interface (depois da confirmação); a gravação junta as tarefas abertas
            _owner._settings.KnownHostKeys[hostPortKey] = fingerprint;
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) _owner.SaveSettings();
            else Avalonia.Threading.Dispatcher.UIThread.Post(_owner.SaveSettings);
        }
    }
}
