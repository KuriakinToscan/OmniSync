using System;
using System.IO;
using System.Text;

namespace OmniSync.Services;

/// <summary>
/// Registro persistente e auditável das operações em arquivo diário (<c>omnisync-yyyyMMdd.log</c>).
/// Seguro para chamadas concorrentes. Falhas de gravação desativam o registro em arquivo sem afetar a sincronização.
/// </summary>
public static class FileLogService
{
    private static readonly object Gate = new();
    private static StreamWriter? _writer;
    private static DateTime _writerDate;
    private static bool _disabled;

    /// <summary>
    /// Caminho do arquivo de log do dia corrente.
    /// </summary>
    public static string CurrentLogFile => Path.Combine(AppPaths.LogsDirectory, $"omnisync-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>
    /// Acrescenta uma linha ao log do dia, com data e hora completas.
    /// </summary>
    /// <param name="message">Texto a registrar.</param>
    public static void Write(string message)
    {
        lock (Gate)
        {
            if (_disabled) return;
            try
            {
                var today = DateTime.Today;
                if (_writer == null || _writerDate != today)
                {
                    _writer?.Dispose();
                    Directory.CreateDirectory(AppPaths.LogsDirectory);
                    _writer = new StreamWriter(CurrentLogFile, append: true, new UTF8Encoding(false)) { AutoFlush = true };
                    _writerDate = today;
                }
                _writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _disabled = true;
                _writer?.Dispose();
                _writer = null;
            }
        }
    }

    /// <summary>
    /// Fecha o arquivo de log (chamado no encerramento do aplicativo).
    /// </summary>
    public static void Close()
    {
        lock (Gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
