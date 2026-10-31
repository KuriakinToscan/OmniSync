using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace OmniSync.Cli;

/// <summary>
/// Saída de texto do modo de linha de comando.
/// No Windows o executável é um aplicativo de janela (sem console próprio): a saída vai para o pipe ou arquivo
/// de quem chamou, ou para o console do terminal pai, com a página de código desse console (acentos corretos).
/// Sem console nem redirecionamento (ex.: Agendador de Tarefas) a saída é descartada e só o log diário fica.
/// </summary>
public static class ConsoleHost
{
    private const int StdOutputHandle = -11;
    private const int AttachParentProcess = -1;

    /// <summary>
    /// Abre a saída de texto para o modo de linha de comando.
    /// </summary>
    /// <returns>Escritor de texto pronto para uso (nunca <c>null</c>).</returns>
    public static TextWriter OpenOutput()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Console.Out;
        }

        try
        {
            var handle = GetStdHandle(StdOutputHandle);
            bool redirected = handle != IntPtr.Zero && handle != new IntPtr(-1);
            if (!redirected && !AttachConsole(AttachParentProcess))
            {
                return TextWriter.Null;
            }

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            uint codePage = GetConsoleOutputCP();
            if (codePage == 0) codePage = GetOEMCP();
            Encoding encoding;
            try
            {
                encoding = codePage == 65001 ? new UTF8Encoding(false) : Encoding.GetEncoding((int)codePage);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                encoding = new UTF8Encoding(false);
            }

            var writer = new StreamWriter(Console.OpenStandardOutput(), encoding) { AutoFlush = true };
            // Começa em linha nova: no console do terminal pai o prompt já pode ter sido impresso
            if (!redirected) writer.WriteLine();
            return writer;
        }
        catch (Exception ex) when (ex is IOException or EntryPointNotFoundException or DllNotFoundException)
        {
            return TextWriter.Null;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleOutputCP();

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();
}
