using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;

namespace OmniSync;

/// <summary>
/// Ponto de entrada principal da aplicação multiplataforma OmniSync.
/// Configura os tratadores globais de exceções não capturadas e inicializa o subsistema gráfico Avalonia UI.
/// </summary>
public sealed class Program
{
    /// <summary>
    /// Método de inicialização do processo. Deve ser executado em modo <see cref="STAThreadAttribute"/>
    /// para compatibilidade com diálogos nativos do Windows Shell (COM) e servidores gráficos Linux (X11/Wayland).
    /// </summary>
    /// <param name="args">Argumentos de linha de comando (suporta <c>--self-test</c> para validação automatizada).</param>
    [STAThread]
    public static void Main(string[] args)
    {
        // Captura exceções fatais em threads de domínio para diagnóstico seguro em crash.log
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            LogCrash("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        };

        // Evita que Tasks órfãs com exceção não observada derrubem o processo em segundo plano
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        try
        {
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            LogCrash("Main Catch", ex);
            throw;
        }
    }

    /// <summary>
    /// Registra detalhes de falhas inesperadas no arquivo <c>crash.log</c> junto ao diretório do executável.
    /// </summary>
    /// <param name="source">Identificador da origem da exceção.</param>
    /// <param name="ex">Objeto da exceção capturada.</param>
    public static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var msg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}]\n{ex}\n\n";
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"), msg);
        }
        catch
        {
            // Ignora falhas de escrita caso o diretório base esteja em mídia somente leitura
        }
    }

    /// <summary>
    /// Constrói e configura o pipeline do Avalonia UI (detecção automática de plataforma Windows/Linux,
    /// fonte Inter integrada e rastreamento de logs).
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
