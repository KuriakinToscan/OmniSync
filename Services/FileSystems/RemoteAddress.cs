using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace OmniSync.Services.FileSystems;

/// <summary>
/// Endereço SFTP no formato <c>sftp://usuario@servidor[:porta]/caminho/absoluto</c>.
/// A forma canônica (<see cref="ToString"/>) tem servidor em minúsculas, porta só quando difere de 22, caminho
/// começando por <c>/</c>, sem barra final (exceto a raiz) e sem segmentos vazios.
/// </summary>
public sealed record RemoteAddress
{
    /// <summary>Prefixo dos endereços SFTP.</summary>
    public const string Scheme = "sftp://";

    /// <summary>Porta padrão do SSH.</summary>
    public const int DefaultPort = 22;

    /// <summary>Usuário da conexão.</summary>
    public string User { get; }

    /// <summary>Nome ou IP do servidor, em minúsculas (IPv6 sem colchetes).</summary>
    public string Host { get; }

    /// <summary>Porta TCP.</summary>
    public int Port { get; }

    /// <summary>Caminho absoluto no servidor, com <c>/</c>.</summary>
    public string Path { get; }

    private RemoteAddress(string user, string host, int port, string path)
    {
        User = user;
        Host = host;
        Port = port;
        Path = path;
    }

    /// <summary>Indica se o texto é um endereço SFTP (começa com <c>sftp://</c>).</summary>
    public static bool IsRemote([NotNullWhen(true)] string? text) =>
        text != null && text.TrimStart().StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>Cria um endereço a partir das partes, validando e normalizando.</summary>
    /// <exception cref="ArgumentException">Parte inválida.</exception>
    public static RemoteAddress Create(string user, string host, int port, string path)
    {
        if (string.IsNullOrWhiteSpace(user)) throw new ArgumentException("Informe o usuário: sftp://usuario@servidor/pasta.");
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Informe o servidor: sftp://usuario@servidor/pasta.");
        if (port is < 1 or > 65535) throw new ArgumentException("A porta deve estar entre 1 e 65535.");
        return new RemoteAddress(user.Trim(), host.Trim().Trim('[', ']').ToLowerInvariant(), port, NormalizePath(path));
    }

    /// <summary>Lê um endereço <c>sftp://</c>.</summary>
    /// <exception cref="ArgumentException">Texto que não é um endereço SFTP válido (mensagem para o usuário).</exception>
    public static RemoteAddress Parse(string text)
    {
        if (!TryParse(text, out var address, out var error)) throw new ArgumentException(error);
        return address;
    }

    /// <summary>Tenta ler um endereço <c>sftp://</c>.</summary>
    /// <param name="text">Texto digitado.</param>
    /// <param name="address">Endereço lido.</param>
    /// <param name="error">Motivo da recusa, para o usuário.</param>
    public static bool TryParse(string? text, [NotNullWhen(true)] out RemoteAddress? address, [NotNullWhen(false)] out string? error)
    {
        address = null;
        if (!IsRemote(text))
        {
            error = "O endereço remoto deve começar com sftp://.";
            return false;
        }

        var rest = text.Trim()[Scheme.Length..];
        int slash = rest.IndexOf('/');
        var authority = slash < 0 ? rest : rest[..slash];
        var path = slash < 0 ? "/" : rest[slash..];

        int at = authority.LastIndexOf('@');
        if (at <= 0)
        {
            error = "Informe o usuário: sftp://usuario@servidor/pasta.";
            return false;
        }
        var user = Uri.UnescapeDataString(authority[..at]);
        var hostPort = authority[(at + 1)..];

        string host;
        int port = DefaultPort;
        string? portText = null;
        if (hostPort.StartsWith('['))
        {
            int close = hostPort.IndexOf(']');
            if (close < 0)
            {
                error = "Servidor IPv6 sem o colchete de fechamento.";
                return false;
            }
            host = hostPort[1..close];
            var after = hostPort[(close + 1)..];
            if (after.StartsWith(':')) portText = after[1..];
            else if (after.Length > 0)
            {
                error = "Servidor inválido.";
                return false;
            }
        }
        else
        {
            int colon = hostPort.LastIndexOf(':');
            host = colon < 0 ? hostPort : hostPort[..colon];
            if (colon >= 0) portText = hostPort[(colon + 1)..];
        }

        if (portText != null && (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535))
        {
            error = "A porta deve ser um número entre 1 e 65535.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(host))
        {
            error = "Informe o servidor: sftp://usuario@servidor/pasta.";
            return false;
        }

        try
        {
            address = new RemoteAddress(user, host.ToLowerInvariant(), port, NormalizePath(path));
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
        error = null;
        return true;
    }

    /// <summary>
    /// Normaliza um caminho remoto: começa por <c>/</c>, aceita <c>\</c> como separador, remove segmentos vazios e
    /// <c>.</c>, e recusa <c>..</c>.
    /// </summary>
    /// <exception cref="ArgumentException">Caminho com <c>..</c>.</exception>
    public static string NormalizePath(string? path)
    {
        var segments = new List<string>();
        foreach (var segment in (path ?? string.Empty).Replace('\\', '/').Split('/'))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..") throw new ArgumentException("O caminho remoto não pode ter '..'. Use o caminho completo da pasta.");
            segments.Add(segment);
        }
        return "/" + string.Join('/', segments);
    }

    /// <summary>Identificação da conexão (usuário, servidor e porta), usada no cofre e no cache de conexões.</summary>
    public string EndpointKey => $"{User}@{HostPortKey}";

    /// <summary>Identificação do servidor (servidor e porta), usada para guardar a chave do servidor.</summary>
    public string HostPortKey => $"{(Host.Contains(':') ? "[" + Host + "]" : Host)}:{Port.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Raiz canônica do endereço, sem o caminho: <c>sftp://usuario@servidor[:porta]</c>.</summary>
    public string Root =>
        Scheme + User + "@" + (Host.Contains(':') ? "[" + Host + "]" : Host) +
        (Port == DefaultPort ? string.Empty : ":" + Port.ToString(CultureInfo.InvariantCulture));

    /// <summary>Último segmento do caminho (nome da pasta ou do arquivo); vazio na raiz.</summary>
    public string Leaf => Path == "/" ? string.Empty : Path[(Path.LastIndexOf('/') + 1)..];

    /// <summary>Mesmo servidor e usuário, com outro caminho.</summary>
    public RemoteAddress WithPath(string path) => new(User, Host, Port, NormalizePath(path));

    /// <summary>Indica se dois endereços usam a mesma conexão (usuário, servidor e porta).</summary>
    public bool SameEndpoint(RemoteAddress other) => string.Equals(EndpointKey, other.EndpointKey, StringComparison.Ordinal);

    /// <summary>Forma canônica do endereço.</summary>
    public override string ToString() => Root + Path;
}
