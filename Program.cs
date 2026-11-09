using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using OmniSync.Cli;
using OmniSync.Services;
using OmniSync.Services.Remote;

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
    /// <param name="args">
    /// Argumentos de linha de comando: <c>--self-test</c> para validação automatizada, ou os comandos do modo
    /// sem janela (<c>--listar</c>, <c>--analisar</c>, <c>--sincronizar</c>; ver <see cref="CliCommand"/>).
    /// </param>
    /// <returns>Código de saída (no modo de linha de comando, um <see cref="CliExitCode"/>).</returns>
    [STAThread]
    public static int Main(string[] args)
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

        if (CliCommand.IsCliInvocation(args))
        {
            return RunCli(args);
        }

        try
        {
            int exitCode = BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
            FileLogService.Close();
            return exitCode;
        }
        catch (Exception ex)
        {
            LogCrash("Main Catch", ex);
            throw;
        }
    }

    /// <summary>
    /// Executa o modo de linha de comando, sem abrir a janela.
    /// </summary>
    /// <param name="args">Argumentos do processo.</param>
    /// <returns>Código de saída.</returns>
    private static int RunCli(string[] args)
    {
        var output = ConsoleHost.OpenOutput();
        try
        {
            if (!CliCommand.TryParse(args, out var command, out var error))
            {
                output.WriteLine(error);
                output.WriteLine("Use --ajuda para ver as opções.");
                return (int)CliExitCode.InvalidInput;
            }

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                // Primeiro Ctrl+C cancela com segurança; o processo termina sozinho em seguida
                e.Cancel = true;
                cts.Cancel();
            };

            var runner = new CliRunner(new SyncEngine(), output, AppPaths.SettingsFile);
            var code = runner.RunAsync(command!, cts.Token).GetAwaiter().GetResult();
            RemoteConnections.DisconnectAll();
            FileLogService.Write($"[CLI] Código de saída: {(int)code} ({code})");
            if (runner.Notification != null)
            {
                ShowNotification(runner.Notification);
            }
            return (int)code;
        }
        catch (Exception ex)
        {
            LogCrash("CLI", ex);
            output.WriteLine($"Erro inesperado: {ex.Message}");
            return (int)CliExitCode.UnexpectedError;
        }
        finally
        {
            output.Flush();
            FileLogService.Close();
        }
    }

    /// <summary>
    /// Exibe a janela de aviso de uma execução agendada e espera o usuário fechá-la (ou o tempo esgotar).
    /// Sem sessão gráfica (ex.: timer do systemd sem DISPLAY), o aviso fica só no log.
    /// </summary>
    /// <param name="notification">Conteúdo do aviso.</param>
    private static void ShowNotification(CliNotification notification)
    {
        FileLogService.Write($"[AVISO AO USUÁRIO] {notification.Title}: {notification.Message}");
        try
        {
            App.PendingNotification = notification;
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(Array.Empty<string>());
        }
        catch (Exception ex)
        {
            FileLogService.Write($"[AVISO] Não foi possível exibir o aviso na tela: {ex.Message}");
        }
    }

    /// <summary>
    /// Registra detalhes de falhas inesperadas no arquivo <c>crash.log</c> da pasta de dados do usuário
    /// (<see cref="AppPaths.CrashLogFile"/>), que é sempre gravável, ao contrário da pasta do executável.
    /// </summary>
    /// <param name="source">Identificador da origem da exceção.</param>
    /// <param name="ex">Objeto da exceção capturada.</param>
    public static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var msg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}]\n{ex}\n\n";
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.AppendAllText(AppPaths.CrashLogFile, msg);
            FileLogService.Write($"[ERRO CRÍTICO] [{source}] {ex?.Message}");
        }
        catch
        {
            // Ignora falhas de escrita para não mascarar a exceção original
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
