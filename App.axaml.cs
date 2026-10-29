using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using OmniSync.Models;
using OmniSync.Services;
using OmniSync.ViewModels;
using OmniSync.Views;

namespace OmniSync;

/// <summary>
/// Classe central da aplicação Avalonia UI.
/// Gerencia o ciclo de vida da janela principal, interceptação de erros na thread de interface
/// e o modo de autoteste integrado (<c>--self-test</c>).
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Carrega os recursos e estilos XAML definidos em <c>App.axaml</c>.
    /// </summary>
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Executado após a inicialização do framework gráfico.
    /// Instancia <see cref="MainWindow"/> com seu <see cref="MainViewModel"/> e configura proteções da UI.
    /// </summary>
    public override void OnFrameworkInitializationCompleted()
    {
        // Intercepta exceções lançadas no Dispatcher da interface para evitar fechamento abrupto do aplicativo
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Program.LogCrash("Dispatcher.UIThread.UnhandledException", e.Exception);
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime dt &&
                dt.MainWindow?.DataContext is MainViewModel mvm)
            {
                mvm.StatusMessage = $"Erro tratado: {e.Exception.Message}";
                mvm.AddLog($"[ERRO UI] {e.Exception.Message}");
            }
            e.Handled = true;
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            bool isSelfTest = desktop.Args != null && desktop.Args.Contains("--self-test");
            var vm = new MainViewModel(new SyncEngine(), loadSettings: !isSelfTest);
            var mainWin = new MainWindow
            {
                DataContext = vm,
            };
            desktop.MainWindow = mainWin;

            // Modo de verificação automatizada de ponta a ponta (--self-test)
            if (isSelfTest)
            {
                mainWin.Opened += async (_, _) =>
                {
                    var testRoot = Path.Combine(Path.GetTempPath(), "OmniSyncSelfTest_" + Guid.NewGuid().ToString("N"));
                    var selfTestLog = Path.Combine(AppPaths.DataDirectory, "selftest.log");
                    Directory.CreateDirectory(AppPaths.DataDirectory);
                    try
                    {
                        var src = Path.Combine(testRoot, "Origem");
                        var dstParent = Path.Combine(testRoot, "Destino");
                        var dstSub = Path.Combine(dstParent, "Origem");
                        Directory.CreateDirectory(src);
                        Directory.CreateDirectory(dstSub);

                        await File.WriteAllTextAsync(Path.Combine(src, "novo.txt"), "novo");
                        await File.WriteAllTextAsync(Path.Combine(src, "conflito.txt"), "versao_nova");
                        await File.WriteAllTextAsync(Path.Combine(dstSub, "conflito.txt"), "versao_antiga");
                        await File.WriteAllTextAsync(Path.Combine(dstSub, "orfao.txt"), "orfao");

                        vm.SourcePath = src;
                        vm.DestinationPath = dstParent;
                        vm.AskConflictResolutionAsync = _ => Task.FromResult((ConflictResolution.Replace, true));
                        vm.ConfirmSyncAsync = _ => Task.FromResult(true);

                        await vm.ScanCommand.ExecuteAsync(null);
                        await vm.StartSyncCommand.ExecuteAsync(null);

                        bool ok = vm.ErrorCount == 0 &&
                                  File.Exists(Path.Combine(dstSub, "novo.txt")) &&
                                  File.ReadAllText(Path.Combine(dstSub, "conflito.txt")) == "versao_nova" &&
                                  File.Exists(Path.Combine(dstSub, "lixo", "orfao.txt"));
                        File.WriteAllText(
                            selfTestLog,
                            $"{(ok ? "SELF_TEST_OK" : "SELF_TEST_FAIL")}: Dest={vm.DestinationPath}, New={vm.NewCount}, Conflict={vm.ConflictCount}, Trash={vm.TrashCount}, Status={vm.StatusMessage}");
                    }
                    catch (Exception ex)
                    {
                        Program.LogCrash("SelfTest", ex);
                        File.WriteAllText(selfTestLog, "SELF_TEST_FAIL: " + ex);
                    }
                    finally
                    {
                        try
                        {
                            if (Directory.Exists(testRoot))
                            {
                                Directory.Delete(testRoot, recursive: true);
                            }
                        }
                        catch { }
                        desktop.Shutdown(0);
                    }
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
