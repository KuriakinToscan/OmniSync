using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmniSync.Models;
using OmniSync.Services;

namespace OmniSync.ViewModels;

/// <summary>
/// ViewModel principal da aplicação OmniSync.
/// Coordena a seleção de diretórios de Origem e Destino, a varredura diferencial (Dry-Run),
/// a execução da sincronização, a filtragem da prévia de arquivos, o log em tempo real
/// e a restauração de arquivos da pasta <c>lixo/</c>.
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    /// <summary>
    /// Limite máximo de linhas mantidas na memória visual do Log de Operações para preservar desempenho.
    /// </summary>
    private const int MaxLogEntries = 500;

    private readonly ISyncEngine _syncEngine;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Delegate injetado pela View para abertura do seletor nativo de pastas do sistema operacional.
    /// </summary>
    public Func<string, Task<string?>>? PickFolderAsync { get; set; }

    /// <summary>
    /// Delegate injetado pela View para abertura do modal de decisão de conflitos.
    /// </summary>
    public Func<SyncItem, Task<(ConflictResolution resolution, bool applyToAll)>>? AskConflictResolutionAsync { get; set; }

    /// <summary>
    /// Caminho do diretório de Origem selecionado pelo usuário.
    /// </summary>
    [ObservableProperty]
    private string _sourcePath = string.Empty;

    /// <summary>
    /// Caminho do diretório de Destino (drive local ou compartilhamento NAS/UNC).
    /// </summary>
    [ObservableProperty]
    private string _destinationPath = string.Empty;

    /// <summary>
    /// Indica se uma varredura diferencial (Dry-Run) está em andamento.
    /// </summary>
    [ObservableProperty]
    private bool _isScanning = false;

    /// <summary>
    /// Indica se a execução física da sincronização (ou restauração) está em andamento.
    /// </summary>
    [ObservableProperty]
    private bool _isSyncing = false;

    /// <summary>
    /// Quando ativo, compara o hash criptográfico SHA-256 de arquivos com mesmo tamanho durante a análise.
    /// </summary>
    [ObservableProperty]
    private bool _enableDeepHashCheck = false;

    /// <summary>
    /// Quando ativo (padrão), garante que a sincronização ocorra dentro de uma subpasta no destino
    /// com o mesmo nome da pasta de origem (ex.: Origem <c>E:\Fotos</c> + Destino <c>G:\</c> -> <c>G:\Fotos</c>),
    /// evitando que outras pastas existentes no drive de destino sejam tratadas como órfãs.
    /// </summary>
    [ObservableProperty]
    private bool _useSourceFolderNameInDestination = true;

    /// <summary>
    /// Mensagem exibida no rodapé da janela principal informando o estado geral da aplicação.
    /// </summary>
    [ObservableProperty]
    private string _statusMessage = "Pronto. Selecione os diretórios e clique em '1. Analisar Diferenças'.";

    /// <summary>
    /// Percentual global de progresso da operação atual (0 a 100).
    /// </summary>
    [ObservableProperty]
    private double _overallProgress = 0;

    /// <summary>
    /// Texto descritivo da operação ou arquivo em processamento no painel da barra de progresso.
    /// </summary>
    [ObservableProperty]
    private string _currentOperationText = string.Empty;

    /// <summary>
    /// Velocidade de transferência atual formatada (ex.: <c>110,2 MB/s</c>).
    /// </summary>
    [ObservableProperty]
    private string _speedFormatted = "—";

    /// <summary>
    /// Tempo restante estimado formatado (ex.: <c>01:45</c>).
    /// </summary>
    [ObservableProperty]
    private string _etaFormatted = "—";

    /// <summary>
    /// Contador textual de arquivos processados sobre o total previsto (ex.: <c>42 / 120 arquivos</c>).
    /// </summary>
    [ObservableProperty]
    private string _processedFilesText = "—";

    /// <summary>
    /// Total de arquivos novos (presentes apenas na Origem) identificados na última análise.
    /// </summary>
    [ObservableProperty]
    private int _newCount = 0;

    /// <summary>
    /// Total de arquivos idênticos (que serão ignorados) identificados na última análise.
    /// </summary>
    [ObservableProperty]
    private int _identicalCount = 0;

    /// <summary>
    /// Total de arquivos conflitantes (mesmo caminho relativo, mas diferentes) identificados na última análise.
    /// </summary>
    [ObservableProperty]
    private int _conflictCount = 0;

    /// <summary>
    /// Total de arquivos órfãos (presentes apenas no Destino, que serão movidos para <c>lixo/</c>).
    /// </summary>
    [ObservableProperty]
    private int _trashCount = 0;

    /// <summary>
    /// Volume total de dados a copiar nos arquivos novos, formatado em B/KB/MB/GB.
    /// </summary>
    [ObservableProperty]
    private string _newBytesFormatted = "0 B";

    /// <summary>
    /// Filtro atualmente ativo na aba de Prévia de Arquivos (<c>Todos</c>, <c>Novos</c>, <c>Conflitos</c>, <c>Lixo</c>, <c>Identicos</c>).
    /// </summary>
    [ObservableProperty]
    private string _activeFilter = "Todos";

    /// <summary>
    /// Lista imutável exibida pelo <c>DataGrid</c> correspondente ao filtro selecionado.
    /// Substituída atomicamente para evitar milhares de eventos individuais de coleção na UI.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<SyncItem> _filteredItems = Array.Empty<SyncItem>();

    /// <summary>
    /// Indica se qualquer operação assíncrona (varredura ou sincronização) está em execução.
    /// </summary>
    public bool IsBusy => IsScanning || IsSyncing;

    /// <summary>
    /// Indica se o botão "1. Analisar Diferenças (Dry-Run)" pode ser acionado.
    /// </summary>
    public bool CanScan => !IsBusy && !string.IsNullOrWhiteSpace(SourcePath) && !string.IsNullOrWhiteSpace(DestinationPath);

    /// <summary>
    /// Indica se o botão "2. Iniciar Sincronização" pode ser acionado (requer itens pendentes de ação).
    /// </summary>
    public bool CanSync => !IsBusy && Items.Any(i => i.ActionType != SyncActionType.Identical && !i.IsProcessed);

    /// <summary>
    /// Indica se os botões de acesso rápido às pastas <c>destino/lixo/</c> e <c>destino/old/</c> estão habilitados.
    /// </summary>
    public bool CanOpenDestinationFolders => !string.IsNullOrWhiteSpace(DestinationPath);

    /// <summary>
    /// Indica se o botão "Restaurar de lixo/" pode ser acionado.
    /// </summary>
    public bool CanRestoreTrash => !IsBusy && !string.IsNullOrWhiteSpace(DestinationPath);

    /// <summary>
    /// Lista completa de todos os itens retornados pela última varredura diferencial.
    /// </summary>
    public List<SyncItem> Items { get; } = new();

    /// <summary>
    /// Coleção observável de mensagens exibidas na aba "Registro de Operações (Log)".
    /// </summary>
    public ObservableCollection<string> Logs { get; } = new();

    /// <summary>
    /// Inicializa o <see cref="MainViewModel"/> com uma implementação específica de <see cref="ISyncEngine"/>.
    /// </summary>
    /// <param name="syncEngine">Instância do motor de sincronização.</param>
    public MainViewModel(ISyncEngine syncEngine)
    {
        _syncEngine = syncEngine;
    }

    /// <summary>
    /// Construtor padrão que instancia o motor de produção <see cref="SyncEngine"/>.
    /// </summary>
    public MainViewModel() : this(new SyncEngine())
    {
    }

    /// <summary>
    /// Adiciona uma entrada com carimbo de hora ao topo do Registro de Operações (Log) de forma thread-safe.
    /// </summary>
    /// <param name="message">Texto da mensagem a ser registrada.</param>
    public void AddLog(string message)
    {
        var formatted = $"[{DateTime.Now:HH:mm:ss}] {message}";

        void InsertOnUi()
        {
            Logs.Insert(0, formatted);
            while (Logs.Count > MaxLogEntries)
            {
                Logs.RemoveAt(Logs.Count - 1);
            }
        }

        try
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                InsertOnUi();
            }
            else
            {
                Dispatcher.UIThread.Post(InsertOnUi);
            }
        }
        catch
        {
            InsertOnUi();
        }
    }

    partial void OnSourcePathChanged(string value) => NotifyCommands();

    partial void OnDestinationPathChanged(string value) => NotifyCommands();

    partial void OnIsScanningChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBusy));
        NotifyCommands();
    }

    partial void OnIsSyncingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBusy));
        NotifyCommands();
    }

    /// <summary>
    /// Atualiza o estado de habilitação (<c>CanExecute</c>) de todos os comandos vinculados à interface.
    /// </summary>
    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanScan));
        OnPropertyChanged(nameof(CanSync));
        OnPropertyChanged(nameof(CanOpenDestinationFolders));
        OnPropertyChanged(nameof(CanRestoreTrash));
        ScanCommand.NotifyCanExecuteChanged();
        StartSyncCommand.NotifyCanExecuteChanged();
        OpenTrashFolderCommand.NotifyCanExecuteChanged();
        OpenOldFolderCommand.NotifyCanExecuteChanged();
        RestoreTrashCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Calcula o caminho efetivo do diretório de destino.
    /// Quando <see cref="UseSourceFolderNameInDestination"/> está ativo (ou quando o destino é a raiz de um disco, ex.: <c>G:\</c>),
    /// anexa automaticamente o nome da pasta de origem caso ainda não conste no final do caminho de destino.
    /// </summary>
    /// <param name="sourcePath">Caminho da pasta de Origem.</param>
    /// <param name="destPath">Caminho da pasta de Destino informada.</param>
    /// <returns>Caminho de destino resolvido com segurança.</returns>
    public string ResolveEffectiveDestinationPath(string sourcePath, string destPath)
    {
        if (string.IsNullOrWhiteSpace(destPath)) return string.Empty;
        destPath = destPath.Trim();
        if (string.IsNullOrWhiteSpace(sourcePath)) return destPath;

        var trimmedSource = sourcePath.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var sourceFolderName = Path.GetFileName(trimmedSource);

        if (string.IsNullOrWhiteSpace(sourceFolderName))
        {
            return destPath;
        }

        var trimmedDest = destPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var destFolderName = Path.GetFileName(trimmedDest);
        var destRoot = Path.GetPathRoot(destPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        bool isDestDriveRoot = string.IsNullOrEmpty(destFolderName) ||
                               string.Equals(trimmedDest, destRoot, StringComparison.OrdinalIgnoreCase);

        if (UseSourceFolderNameInDestination || isDestDriveRoot)
        {
            if (!string.Equals(sourceFolderName, destFolderName, StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(destPath, sourceFolderName);
            }
        }

        return destPath;
    }

    /// <summary>
    /// Abre o seletor de pastas para escolha do diretório de Origem e atualiza a subpasta correspondente no Destino.
    /// </summary>
    [RelayCommand]
    private async Task BrowseSourceAsync()
    {
        try
        {
            if (PickFolderAsync != null)
            {
                var previousSourceFolder = string.IsNullOrWhiteSpace(SourcePath)
                    ? null
                    : Path.GetFileName(SourcePath.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

                var folder = await PickFolderAsync(SourcePath);
                if (!string.IsNullOrEmpty(folder))
                {
                    SourcePath = folder;

                    if (!string.IsNullOrWhiteSpace(DestinationPath) && UseSourceFolderNameInDestination)
                    {
                        var baseDest = DestinationPath.Trim();
                        var destName = Path.GetFileName(baseDest.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                        if (!string.IsNullOrEmpty(previousSourceFolder) &&
                            string.Equals(destName, previousSourceFolder, StringComparison.OrdinalIgnoreCase))
                        {
                            var parent = Path.GetDirectoryName(baseDest.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                            if (!string.IsNullOrEmpty(parent))
                            {
                                baseDest = parent;
                            }
                        }
                        DestinationPath = ResolveEffectiveDestinationPath(SourcePath, baseDest);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erro ao selecionar pasta de origem: {ex.Message}";
            AddLog($"[ERRO] {ex.Message}");
        }
    }

    /// <summary>
    /// Abre o seletor de pastas para escolha do diretório de Destino.
    /// </summary>
    [RelayCommand]
    private async Task BrowseDestinationAsync()
    {
        try
        {
            if (PickFolderAsync != null)
            {
                var folder = await PickFolderAsync(DestinationPath);
                if (!string.IsNullOrEmpty(folder))
                {
                    DestinationPath = ResolveEffectiveDestinationPath(SourcePath, folder);
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erro ao selecionar pasta de destino: {ex.Message}";
            AddLog($"[ERRO] {ex.Message}");
        }
    }

    /// <summary>
    /// Executa a Etapa 1: Varredura Diferencial (Dry-Run) entre Origem e Destino sem modificar nenhum arquivo no disco.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        if (string.IsNullOrWhiteSpace(SourcePath) || string.IsNullOrWhiteSpace(DestinationPath))
        {
            StatusMessage = "Por favor, selecione os caminhos de Origem e Destino.";
            return;
        }

        var effectiveDest = ResolveEffectiveDestinationPath(SourcePath, DestinationPath);
        if (!string.Equals(effectiveDest, DestinationPath.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            DestinationPath = effectiveDest;
            AddLog($"[INFO] Destino ajustado automaticamente para a subpasta da origem: {effectiveDest}");
        }

        IsScanning = true;
        StatusMessage = "Analisando diretórios e identificando diferenças...";
        OverallProgress = 0;
        CurrentOperationText = "Varredura diferencial em andamento...";
        _cts = new CancellationTokenSource();

        var logger = new Progress<string>(AddLog);

        try
        {
            Items.Clear();
            FilteredItems = Array.Empty<SyncItem>();

            var scanned = await _syncEngine.ScanDirectoriesAsync(
                SourcePath.Trim(),
                DestinationPath.Trim(),
                EnableDeepHashCheck,
                logger,
                _cts.Token);

            Items.AddRange(scanned);

            UpdateCounts();
            ApplyFilter(ActiveFilter);

            StatusMessage = $"Análise concluída: {NewCount} novos, {ConflictCount} conflitos, {TrashCount} órfãos p/ lixo, {IdenticalCount} idênticos.";
            CurrentOperationText = "Pronto para sincronizar.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Varredura cancelada pelo usuário.";
            CurrentOperationText = "Cancelado.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erro durante a varredura: {ex.Message}";
            AddLog($"[ERRO] {ex.Message}");
        }
        finally
        {
            IsScanning = false;
            NotifyCommands();
        }
    }

    /// <summary>
    /// Executa a Etapa 2: Sincronização física dos arquivos conforme o plano listado na prévia.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSync))]
    private async Task StartSyncAsync()
    {
        if (!Items.Any()) return;

        IsSyncing = true;
        StatusMessage = "Sincronização em andamento...";
        OverallProgress = 0;
        _cts = new CancellationTokenSource();

        var progress = new Progress<SyncProgressReport>(report =>
        {
            OverallProgress = report.OverallProgressPercent;
            SpeedFormatted = report.SpeedFormatted;
            EtaFormatted = report.EtaFormatted;
            ProcessedFilesText = $"{report.ProcessedFiles} / {report.TotalFiles} arquivos";
            CurrentOperationText = !string.IsNullOrEmpty(report.CurrentFileName)
                ? $"Processando: {report.CurrentFileName}"
                : "Sincronização concluída.";
        });

        var logger = new Progress<string>(AddLog);

        ConflictResolution? rememberedChoice = null;

        // Handler local que invoca o modal de conflito ou reaproveita a decisão quando "Aplicar a todos" foi marcado
        async Task<ConflictResolution> ResolveConflictHandler(SyncItem item)
        {
            if (rememberedChoice.HasValue)
            {
                return rememberedChoice.Value;
            }

            if (AskConflictResolutionAsync != null)
            {
                var (choice, applyToAll) = await AskConflictResolutionAsync(item);
                if (applyToAll)
                {
                    rememberedChoice = choice;
                }
                return choice;
            }

            return ConflictResolution.Ignore;
        }

        try
        {
            await _syncEngine.ExecuteSyncAsync(
                Items,
                SourcePath.Trim(),
                DestinationPath.Trim(),
                ResolveConflictHandler,
                progress,
                logger,
                _cts.Token);

            StatusMessage = "Sincronização concluída com sucesso!";
            CurrentOperationText = "Operações finalizadas.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Sincronização interrompida pelo usuário.";
            CurrentOperationText = "Cancelado.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erro durante a sincronização: {ex.Message}";
            AddLog($"[ERRO CRÍTICO] {ex.Message}");
        }
        finally
        {
            IsSyncing = false;
            UpdateCounts();
            ApplyFilter(ActiveFilter);
            NotifyCommands();
        }
    }

    /// <summary>
    /// Solicita o cancelamento cooperativo da varredura ou sincronização em andamento.
    /// </summary>
    [RelayCommand]
    private void Cancel()
    {
        try
        {
            _cts?.Cancel();
            StatusMessage = "Solicitação de cancelamento enviada...";
        }
        catch { }
    }

    /// <summary>
    /// Altera o filtro ativo da grade de Prévia de Arquivos.
    /// </summary>
    /// <param name="filter">Identificador do filtro (<c>Todos</c>, <c>Novos</c>, <c>Conflitos</c>, <c>Lixo</c>, <c>Identicos</c>).</param>
    [RelayCommand]
    private void SetFilter(string filter)
    {
        ActiveFilter = filter;
        ApplyFilter(filter);
    }

    /// <summary>
    /// Filtra a lista principal <see cref="Items"/> e atualiza atomicamente <see cref="FilteredItems"/>.
    /// </summary>
    private void ApplyFilter(string filter)
    {
        IEnumerable<SyncItem> source = filter switch
        {
            "Novos" => Items.Where(i => i.ActionType == SyncActionType.New),
            "Conflitos" => Items.Where(i => i.ActionType == SyncActionType.Conflict),
            "Lixo" => Items.Where(i => i.ActionType == SyncActionType.OrphanTrash),
            "Identicos" => Items.Where(i => i.ActionType == SyncActionType.Identical),
            _ => Items
        };

        FilteredItems = source.ToList();
    }

    /// <summary>
    /// Recalcula os totalizadores exibidos nos cartões de resumo (Novos, Idênticos, Conflitos, Órfãos e Bytes a copiar).
    /// </summary>
    private void UpdateCounts()
    {
        NewCount = Items.Count(i => i.ActionType == SyncActionType.New);
        IdenticalCount = Items.Count(i => i.ActionType == SyncActionType.Identical);
        ConflictCount = Items.Count(i => i.ActionType == SyncActionType.Conflict);
        TrashCount = Items.Count(i => i.ActionType == SyncActionType.OrphanTrash);

        long newBytes = Items.Where(i => i.ActionType == SyncActionType.New).Sum(i => i.SourceSize);
        NewBytesFormatted = SyncItem.FormatBytes(newBytes);
    }

    /// <summary>
    /// Define em lote a resolução de todos os conflitos listados como <see cref="ConflictResolution.Replace"/>.
    /// </summary>
    [RelayCommand]
    private void MarkAllConflictsAsReplace()
    {
        foreach (var item in Items.Where(i => i.ActionType == SyncActionType.Conflict))
        {
            item.Resolution = ConflictResolution.Replace;
        }
        AddLog("Todos os conflitos foram marcados para 'Substituir (old/)'.");
    }

    /// <summary>
    /// Define em lote a resolução de todos os conflitos listados como <see cref="ConflictResolution.Ignore"/>.
    /// </summary>
    [RelayCommand]
    private void MarkAllConflictsAsIgnore()
    {
        foreach (var item in Items.Where(i => i.ActionType == SyncActionType.Conflict))
        {
            item.Resolution = ConflictResolution.Ignore;
        }
        AddLog("Todos os conflitos foram marcados para 'Ignorar'.");
    }

    /// <summary>
    /// Restaura os arquivos contidos na pasta <c>lixo/</c> de volta para seus caminhos relativos originais no destino.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRestoreTrash))]
    private async Task RestoreTrashAsync()
    {
        if (string.IsNullOrWhiteSpace(DestinationPath)) return;

        IsSyncing = true;
        StatusMessage = "Restaurando arquivos da pasta lixo/...";
        CurrentOperationText = "Restaurando arquivos...";

        try
        {
            int restoredFiles = 0;
            int organizedFolders = 0;

            await Task.Run(() =>
            {
                var dest = DestinationPath.Trim();
                var candidateTrashDirs = new List<string>();

                var directTrash = Path.Combine(dest, "lixo");
                if (Directory.Exists(directTrash))
                {
                    candidateTrashDirs.Add(directTrash);
                }

                var parentDir = Path.GetDirectoryName(dest.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!string.IsNullOrEmpty(parentDir))
                {
                    var parentTrash = Path.Combine(parentDir, "lixo");
                    if (Directory.Exists(parentTrash) && !candidateTrashDirs.Contains(parentTrash, StringComparer.OrdinalIgnoreCase))
                    {
                        candidateTrashDirs.Add(parentTrash);
                    }
                }

                foreach (var trashRoot in candidateTrashDirs)
                {
                    var targetBase = Path.GetDirectoryName(trashRoot)!;
                    AddLog($"[RESTAURAÇÃO] Restaurando itens de '{trashRoot}' para '{targetBase}'...");

                    foreach (var file in Directory.EnumerateFiles(trashRoot, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                    {
                        var rel = Path.GetRelativePath(trashRoot, file);
                        var targetPath = Path.Combine(targetBase, rel);
                        try
                        {
                            var targetDir = Path.GetDirectoryName(targetPath);
                            if (!string.IsNullOrEmpty(targetDir))
                            {
                                Directory.CreateDirectory(targetDir);
                            }

                            if (!File.Exists(targetPath))
                            {
                                File.Move(file, targetPath);
                            }
                            else
                            {
                                File.Delete(file);
                            }
                            restoredFiles++;
                        }
                        catch (Exception ex)
                        {
                            AddLog($"[AVISO] Não foi possível restaurar '{rel}': {ex.Message}");
                        }
                    }

                    try
                    {
                        Directory.Delete(trashRoot, recursive: true);
                    }
                    catch { }
                }

                // Se subpastas da origem tiverem sido copiadas na raiz pai do destino, organiza-as dentro da subpasta correta
                if (!string.IsNullOrWhiteSpace(SourcePath) && Directory.Exists(SourcePath.Trim()))
                {
                    var effectiveDest = ResolveEffectiveDestinationPath(SourcePath, dest);
                    var parentOfEffective = Path.GetDirectoryName(effectiveDest.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (!string.IsNullOrEmpty(parentOfEffective) &&
                        !string.Equals(parentOfEffective, effectiveDest, StringComparison.OrdinalIgnoreCase) &&
                        Directory.Exists(parentOfEffective))
                    {
                        Directory.CreateDirectory(effectiveDest);
                        foreach (var srcSubDir in Directory.GetDirectories(SourcePath.Trim()))
                        {
                            var subName = Path.GetFileName(srcSubDir);
                            var misplacedInParent = Path.Combine(parentOfEffective, subName);
                            var correctInSubfolder = Path.Combine(effectiveDest, subName);
                            if (Directory.Exists(misplacedInParent) && !Directory.Exists(correctInSubfolder))
                            {
                                try
                                {
                                    Directory.Move(misplacedInParent, correctInSubfolder);
                                    organizedFolders++;
                                    AddLog($"[ORGANIZAÇÃO] Pasta '{subName}' movida para '{effectiveDest}'.");
                                }
                                catch { }
                            }
                        }
                    }
                }
            });

            if (!string.IsNullOrWhiteSpace(SourcePath))
            {
                DestinationPath = ResolveEffectiveDestinationPath(SourcePath, DestinationPath);
            }

            StatusMessage = $"Restauração concluída: {restoredFiles} arquivos restaurados de lixo/ e {organizedFolders} pastas organizadas.";
            CurrentOperationText = "Restauração concluída.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erro ao restaurar lixo/: {ex.Message}";
            AddLog($"[ERRO] {ex.Message}");
        }
        finally
        {
            IsSyncing = false;
            NotifyCommands();
        }
    }

    /// <summary>
    /// Abre a pasta <c>destino/lixo/</c> no gerenciador de arquivos do sistema operacional.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenDestinationFolders))]
    private void OpenTrashFolder() => OpenFolderIfExists("lixo");

    /// <summary>
    /// Abre a pasta <c>destino/old/</c> no gerenciador de arquivos do sistema operacional.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenDestinationFolders))]
    private void OpenOldFolder() => OpenFolderIfExists("old");

    /// <summary>
    /// Garante a criação da subpasta solicitada (<c>lixo</c> ou <c>old</c>) dentro do destino e a abre
    /// utilizando o explorador nativo (<c>explorer.exe</c> no Windows, <c>xdg-open</c> no Linux, <c>open</c> no macOS).
    /// </summary>
    private void OpenFolderIfExists(string subFolder)
    {
        if (string.IsNullOrWhiteSpace(DestinationPath))
        {
            StatusMessage = "Por favor, selecione primeiro o diretório de destino.";
            AddLog("[AVISO] Selecione o diretório de destino antes de abrir as pastas lixo/ ou old/.");
            return;
        }

        try
        {
            var effectiveDest = ResolveEffectiveDestinationPath(SourcePath, DestinationPath);
            var fullPath = Path.GetFullPath(Path.Combine(effectiveDest, subFolder));
            if (!Directory.Exists(fullPath))
            {
                Directory.CreateDirectory(fullPath);
            }

            var executable = OperatingSystem.IsWindows()
                ? "explorer.exe"
                : OperatingSystem.IsLinux() ? "xdg-open" : "open";

            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = $"\"{fullPath}\"",
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            AddLog($"[ERRO] Não foi possível abrir a pasta: {ex.Message}");
        }
    }
}
