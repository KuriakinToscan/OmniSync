using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OmniSync.Models;
using OmniSync.ViewModels;

namespace OmniSync.Views;

/// <summary>
/// Janela principal da aplicação OmniSync.
/// Conecta os serviços de interface (seletor nativo de pastas do sistema operacional e diálogo modal de conflitos)
/// aos delegates expostos pelo <see cref="MainViewModel"/>.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// Inicializa os componentes gráficos definidos em <c>MainWindow.axaml</c>.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Associa os callbacks de interação visual (<see cref="MainViewModel.PickFolderAsync"/> e
    /// <see cref="MainViewModel.AskConflictResolutionAsync"/>) sempre que o DataContext da janela é definido.
    /// </summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainViewModel vm)
        {
            vm.PickFolderAsync = PickFolderAsync;
            vm.AskConflictResolutionAsync = AskConflictResolutionAsync;
            vm.ConfirmSyncAsync = ConfirmSyncAsync;
            vm.PickSaveFileAsync = PickSaveFileAsync;
            vm.SelectTrashEntriesAsync = SelectTrashEntriesAsync;
            vm.ConfirmAsync = (title, message, confirmText) => MessageDialog.ShowAsync(this, title, message, confirmText);
        }
    }

    /// <summary>
    /// Largura abaixo da qual a árvore de pastas é ocultada para preservar a lista de arquivos.
    /// </summary>
    private const double CompactWidth = 1240;

    /// <summary>
    /// Alterna a classe <c>compact</c> conforme a largura da janela.
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ClientSizeProperty)
        {
            Classes.Set("compact", ClientSize.Width < CompactWidth);
        }
    }

    /// <summary>
    /// Salva os pares e as preferências do usuário ao fechar a janela.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (DataContext is MainViewModel vm)
        {
            vm.SaveSettings();
        }
    }

    /// <summary>
    /// Exibe o diálogo de confirmação do plano de sincronização.
    /// </summary>
    private async Task<bool> ConfirmSyncAsync(SyncPlanSummary summary)
    {
        var dialogVm = new ConfirmSyncViewModel(summary);
        var dialog = new ConfirmSyncWindow { DataContext = dialogVm };
        await dialog.ShowDialog(this);
        return dialogVm.Result;
    }

    /// <summary>
    /// Exibe o diálogo de restauração seletiva de lixo/.
    /// </summary>
    private async Task<IReadOnlyList<TrashEntry>?> SelectTrashEntriesAsync(IReadOnlyList<TrashEntry> entries)
    {
        var dialogVm = new RestoreViewModel(entries);
        var dialog = new RestoreWindow { DataContext = dialogVm };
        await dialog.ShowDialog(this);
        return dialogVm.Result;
    }

    /// <summary>
    /// Abre o seletor nativo para salvar o relatório CSV.
    /// </summary>
    /// <param name="suggestedName">Nome de arquivo sugerido.</param>
    /// <returns>Caminho local escolhido, ou <c>null</c> se cancelado.</returns>
    private async Task<string?> PickSaveFileAsync(string suggestedName)
    {
        var topLevel = GetTopLevel(this);
        if (topLevel?.StorageProvider == null) return null;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Exportar relatório",
            SuggestedFileName = suggestedName,
            DefaultExtension = "csv",
            ShowOverwritePrompt = true,
            FileTypeChoices = new[]
            {
                new FilePickerFileType("CSV (separado por ponto e vírgula)") { Patterns = new[] { "*.csv" } }
            }
        });
        return file?.TryGetLocalPath();
    }

    /// <summary>
    /// Abre o seletor nativo de diretórios do sistema operacional (Windows Explorer / GTK / KDE)
    /// com tratamento resiliente para unidades de rede (NAS/UNC) e pastas virtuais do Shell.
    /// </summary>
    /// <param name="initialPath">Caminho inicial sugerido para abertura do seletor.</param>
    /// <returns>Caminho local ou UNC selecionado pelo usuário, ou <c>null</c> se cancelado/inválido.</returns>
    private async Task<string?> PickFolderAsync(string? initialPath)
    {
        try
        {
            var topLevel = GetTopLevel(this);
            if (topLevel?.StorageProvider == null) return null;

            var options = new FolderPickerOpenOptions
            {
                Title = "Selecionar Diretório",
                AllowMultiple = false
            };

            // Tenta posicionar o diálogo na pasta atual (ou na pasta pai, caso a subpasta de destino ainda não tenha sido criada)
            if (!string.IsNullOrWhiteSpace(initialPath))
            {
                try
                {
                    var candidatePath = initialPath;
                    if (!Directory.Exists(candidatePath))
                    {
                        var parent = Path.GetDirectoryName(candidatePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                        if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
                        {
                            candidatePath = parent;
                        }
                    }

                    if (Directory.Exists(candidatePath))
                    {
                        var folder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(candidatePath);
                        if (folder != null)
                        {
                            options.SuggestedStartLocation = folder;
                        }
                    }
                }
                catch
                {
                    // Ignora silenciosamente caso o caminho inicial seja um NAS desconectado ou inacessível
                }
            }

            var results = await topLevel.StorageProvider.OpenFolderPickerAsync(options);
            if (results != null && results.Count > 0)
            {
                var selected = results[0];

                string? localPath = null;
                try
                {
                    localPath = selected.TryGetLocalPath();
                }
                catch { }

                if (string.IsNullOrWhiteSpace(localPath))
                {
                    try
                    {
                        localPath = selected.Path?.LocalPath;
                    }
                    catch { }
                }

                // Filtra itens virtuais do Windows Explorer (ex.: GUIDs de Rede "::{" sem pasta compartilhada)
                if (!string.IsNullOrWhiteSpace(localPath) && !localPath.StartsWith("::{", StringComparison.Ordinal))
                {
                    return localPath;
                }

                if (DataContext is MainViewModel vmWarn)
                {
                    vmWarn.StatusMessage = "Selecione uma pasta compartilhada específica (ex: \\\\servidor\\pasta) e não o ícone raiz da Rede.";
                    vmWarn.AddLog("[AVISO] Item virtual de rede selecionado. Abra o servidor e escolha uma pasta compartilhada (ou digite o caminho \\\\servidor\\pasta).");
                }
            }
        }
        catch (Exception ex)
        {
            if (DataContext is MainViewModel vmErr)
            {
                vmErr.StatusMessage = "Não foi possível selecionar este item. Se for NAS, abra uma pasta compartilhada (\\\\servidor\\pasta).";
                vmErr.AddLog($"[AVISO] Seletor de pastas: {ex.Message}");
            }
        }

        return null;
    }

    /// <summary>
    /// Exibe na thread de interface (UIThread) a janela modal de resolução de conflito (<see cref="ConflictDialogWindow"/>)
    /// e retorna a decisão escolhida pelo usuário.
    /// </summary>
    /// <param name="item">Item em conflito a ser decidido.</param>
    /// <returns>Tupla contendo a resolução escolhida e se ela deve ser aplicada aos demais conflitos.</returns>
    private async Task<(ConflictResolution resolution, bool applyToAll)> AskConflictResolutionAsync(SyncItem item)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var dialogVm = new ConflictDialogViewModel(item);
            var dialogWindow = new ConflictDialogWindow
            {
                DataContext = dialogVm
            };

            var result = await dialogWindow.ShowDialog<ConflictResolution>(this);
            if (result == ConflictResolution.AskUser)
            {
                // Caso o usuário feche a janela pelo botão 'X', assume Ignorar por segurança
                result = ConflictResolution.Ignore;
            }

            return (result, dialogVm.ApplyToAllRemaining);
        });
    }
}
