using System;
using System.IO;

namespace OmniSync.Services;

/// <summary>
/// Traduz erros do sistema operacional para mensagens em português com orientação de ação.
/// O Windows devolve o texto no idioma do sistema (muitas vezes em inglês) e o Linux sempre em inglês;
/// a mensagem original continua no log, para diagnóstico.
/// </summary>
public static class ErrorMessages
{
    // Códigos Win32 (parte baixa do HRESULT 0x8007xxxx)
    private const int WinFileNotFound = 2;
    private const int WinPathNotFound = 3;
    private const int WinAccessDenied = 5;
    private const int WinWriteProtect = 19;
    private const int WinNotReady = 21;
    private const int WinSharingViolation = 32;
    private const int WinLockViolation = 33;
    private const int WinHandleDiskFull = 39;
    private const int WinBadNetPath = 53;
    private const int WinUnexpectedNetError = 59;
    private const int WinNetNameDeleted = 64;
    private const int WinFileExists = 80;
    private const int WinDiskFull = 112;
    private const int WinSemTimeout = 121;
    private const int WinAlreadyExists = 183;
    private const int WinFilenameTooLong = 206;

    // errno do Linux/macOS (o .NET o coloca em HResult nas exceções de E/S fora do Windows)
    private const int ErrNoEnt = 2;
    private const int ErrAccess = 13;
    private const int ErrExist = 17;
    private const int ErrNoSpace = 28;
    private const int ErrReadOnlyFs = 30;
    private const int ErrNameTooLong = 36;
    private const int ErrBusy = 16;
    private const int ErrHostDown = 112;
    private const int ErrTimedOut = 110;

    /// <summary>
    /// Mensagem em português para a exceção, no sistema operacional atual.
    /// </summary>
    /// <param name="ex">Exceção capturada.</param>
    public static string Translate(Exception ex) => Translate(ex, OperatingSystem.IsWindows());

    /// <summary>
    /// Mensagem em português para a exceção, interpretando o código como Windows ou Unix.
    /// </summary>
    /// <param name="ex">Exceção capturada.</param>
    /// <param name="windows"><c>true</c> para interpretar o HRESULT como código Win32.</param>
    public static string Translate(Exception ex, bool windows)
    {
        string? byCode = windows ? FromWin32(ex.HResult) : FromErrno(ex.HResult);
        if (byCode != null) return byCode;

        return ex switch
        {
            UnauthorizedAccessException => AccessDenied,
            PathTooLongException => PathTooLong,
            FileNotFoundException => NotFound,
            DirectoryNotFoundException => PathNotFound,
            DriveNotFoundException => DriveNotFound,
            OperationCanceledException => "Operação cancelada.",
            _ => ex.Message
        };
    }

    /// <summary>
    /// Texto para o log: a mensagem traduzida e, se for diferente, a original do sistema entre colchetes.
    /// </summary>
    /// <param name="ex">Exceção capturada.</param>
    public static string ForLog(Exception ex)
    {
        var translated = Translate(ex);
        return string.Equals(translated, ex.Message, StringComparison.Ordinal)
            ? translated
            : $"{translated} [sistema: {ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}]";
    }

    private const string FileInUse = "Arquivo em uso por outro programa. Feche-o e tente novamente.";
    private const string AccessDenied = "Acesso negado. Verifique as permissões da pasta ou se o arquivo está marcado como somente leitura.";
    private const string DiskFull = "Espaço insuficiente no disco de destino.";
    private const string PathTooLong = "Caminho longo demais para o sistema. Encurte nomes de pastas ou ative caminhos longos no Windows.";
    private const string NotFound = "Arquivo não encontrado. Ele pode ter sido movido ou apagado depois da análise; analise novamente.";
    private const string PathNotFound = "Pasta não encontrada. Ela pode ter sido movida ou apagada depois da análise; analise novamente.";
    private const string DriveNotFound = "Unidade não encontrada. Verifique se o disco ou pendrive está conectado.";
    private const string Network = "Falha na conexão de rede com a pasta compartilhada. Verifique a rede e tente novamente.";
    private const string ReadOnlyMedia = "O disco está protegido contra gravação.";
    private const string NotReady = "O dispositivo não está pronto. Verifique se o disco está conectado.";
    private const string AlreadyExists = "Já existe um arquivo com esse nome no local de gravação.";

    private static string? FromWin32(int hresult)
    {
        // Só HRESULTs do tipo 0x8007xxxx (FACILITY_WIN32) ou o código Win32 puro
        int code = (hresult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000) ? hresult & 0xFFFF
            : hresult is > 0 and < 0x10000 ? hresult
            : -1;
        return code switch
        {
            WinSharingViolation or WinLockViolation => FileInUse,
            WinAccessDenied => AccessDenied,
            WinHandleDiskFull or WinDiskFull => DiskFull,
            WinFilenameTooLong => PathTooLong,
            WinFileNotFound => NotFound,
            WinPathNotFound => PathNotFound,
            WinBadNetPath or WinUnexpectedNetError or WinNetNameDeleted or WinSemTimeout => Network,
            WinWriteProtect => ReadOnlyMedia,
            WinNotReady => NotReady,
            WinFileExists or WinAlreadyExists => AlreadyExists,
            _ => null
        };
    }

    private static string? FromErrno(int errno) => errno switch
    {
        ErrBusy => FileInUse,
        ErrAccess => AccessDenied,
        ErrNoSpace => DiskFull,
        ErrNameTooLong => PathTooLong,
        ErrNoEnt => NotFound,
        ErrReadOnlyFs => ReadOnlyMedia,
        ErrExist => AlreadyExists,
        ErrHostDown or ErrTimedOut => Network,
        _ => null
    };
}
