using OmniSync.Services.Remote;

namespace OmniSync.Services.FileSystems;

/// <summary>
/// Escolhe o sistema de arquivos pelo caminho: <c>sftp://</c> usa a conexão SFTP (aberta sem perguntar nada,
/// <see cref="RemoteConnections.Get"/>); o resto é local (disco ou pasta de rede SMB).
/// </summary>
public static class FileSystemProvider
{
    /// <summary>Sistema de arquivos do caminho.</summary>
    /// <exception cref="System.ArgumentException">Endereço <c>sftp://</c> inválido.</exception>
    /// <exception cref="RemoteAccessException">Conexão remota que não pôde ser aberta.</exception>
    public static IFileSystem For(string path) =>
        RemoteAddress.IsRemote(path) ? RemoteConnections.Get(RemoteAddress.Parse(path)) : LocalFileSystem.Instance;
}
