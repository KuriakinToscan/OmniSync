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
/// a confirmação e execução da sincronização, a filtragem e busca da prévia, o log em tempo real
/// e a restauração de arquivos da pasta <c>lixo/</c>.
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    /// <summary>
    /// Limite máximo de linhas mantidas na memória visual do Log de Operações (o log completo vai para arquivo).
    /// </summary>
    private const int MaxLogEntries = 1000;

    /// <summary>
    /// Intervalo de entrega em lote das atualizações de itens e de log para a interface.
    /// </summary>
    private static readonly TimeSpan UiBatchInterval = TimeSpan.FromMilliseconds(150);

    private readonly ISyncEngine _syncEngine;
    private readonly RestoreService _restoreService = new();
    private readonly UiBatchQueue<LogEntry> _logQueue;
    private readonly UiBatchQueue<SyncItemUpdate> _itemUpdateQueue;
    private CancellationTokenSource? _cts;
    private readonly bool _persistSettings;
    private bool _suppressInvalidation;
    private int _lastDestinationFileCount;
    private IReadOnlyList<string> _lastScanWarnings = Array.Empty<string>();

    /// <summary>
    /// Delegate injetado pela View para abertura do seletor nativo de pastas do sistema operacional.
    /// </summary>
    public Func<string, Task<string?>>? PickFolderAsync { get; set; }

    /// <summary>
    /// Delegate injetado pela View para abertura do modal de decisão de conflitos.
    /// </summary>
    public Func<SyncItem, Task<(ConflictResolution resolution, bool applyToAll)>>? AskConflictResolutionAsync { get; set; }

    /// <summary>
    /// Delegate injetado pela View para confirmar o plano antes da execução. Retorna <c>true</c> para prosseguir.
    /// Quando não definido, a execução prossegue sem confirmação (modo automatizado).
    /// </summary>
    public Func<SyncPlanSummary, Task<bool>>? ConfirmSyncAsync { get; set; }

    /// <summary>
    /// Delegate injetado pela View para escolher onde salvar o relatório CSV.
    /// </summary>
    public Func<string, Task<string?>>? PickSaveFileAsync { get; set; }

    /// <summary>
    /// Delegate injetado pela View para o usuário selecionar quais arquivos de <c>lixo/</c> restaurar.
    /// Retorna <c>null</c> se cancelado.
    /// </summary>
    public Func<IReadOnlyList<TrashEntry>, Task<IReadOnlyList<TrashEntry>?>>? SelectTrashEntriesAsync { get; set; }

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
    /// Quando ativo, diferença de exatamente 1 hora entre datas é considerada igual (horário de verão em FAT/exFAT).
    /// </summary>
    [ObservableProperty]
    private bool _tolerateDstShift = false;

    /// <summary>
    /// Padrões de exclusão separados por ponto e vírgula (ex.: <c>Thumbs.db; ~$*</c>).
    /// </summary>
    [ObservableProperty]
    private string _excludePatternsText = SettingsService.DefaultExcludePatterns;

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
    private string _statusMessage = "Pronto. Selecione os diretórios e clique em 'Analisar'.";

    /// <summary>
    /// Percentual global de progresso da operação atual (0 a 100).
    /// </summary>
    [ObservableProperty]
    private double _overallProgress = 0;

    /// <summary>
    /// Indica progresso indeterminado (varredura, restauração).
    /// </summary>
    [ObservableProperty]
    private bool _isProgressIndeterminate = false;

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
    /// Total de itens que falharam na última execução.
    /// </summary>
    [ObservableProperty]
    private int _errorCount = 0;

    /// <summary>
    /// Volume total de dados a copiar nos arquivos novos, formatado em B/KB/MB/GB.
    /// </summary>
    [ObservableProperty]
    private string _newBytesFormatted = "0 B";

    /// <summary>
    /// Filtro atualmente ativo na grade de prévia.
    /// </summary>
    [ObservableProperty]
    private ItemFilter _activeFilter = ItemFilter.All;

    /// <summary>
    /// Texto de busca aplicado ao caminho relativo (sem diferenciar maiúsculas).
    /// </summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>
    /// Lista imutável exibida pelo <c>DataGrid</c> correspondente ao filtro e à busca.
    /// Substituída atomicamente para evitar milhares de eventos individuais de coleção na UI.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<SyncItem> _filteredItems = Array.Empty<SyncItem>();

    /// <summary>
    /// Indica se qualquer operação assíncrona (varredura ou sincronização) está em execução.
    /// </summary>
    public bool IsBusy => IsScanning || IsSyncing;

    /// <summary>
    /// Indica se há resultado de análise carregado.
    /// </summary>
    public bool HasItems => Items.Count > 0;

    /// <summary>
    /// Indica se a mensagem de estado vazio deve ser exibida sobre a grade.
    /// </summary>
    public bool IsEmptyStateVisible => !IsBusy && FilteredItems.Count == 0;

    /// <summary>
    /// Texto do estado vazio da grade, conforme o contexto.
    /// </summary>
    public string EmptyStateText => Items.Count == 0
        ? "Nenhuma análise carregada. Escolha a origem e o destino e clique em Analisar (F5)."
        : "Nenhum item corresponde ao filtro ou à busca.";

    /// <summary>
    /// Indica se o botão "Analisar" pode ser acionado.
    /// </summary>
    public bool CanScan => !IsBusy && !string.IsNullOrWhiteSpace(SourcePath) && !string.IsNullOrWhiteSpace(DestinationPath);

    /// <summary>
    /// Indica se o botão "Sincronizar" pode ser acionado (requer itens pendentes de ação).
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
    /// Indica se há itens para exportar em relatório.
    /// </summary>
    public bool CanExportReport => !IsBusy && Items.Count > 0;

    /// <summary>
    /// Indica se há conflitos para as ações em lote.
    /// </summary>
    public bool CanEditConflicts => !IsBusy && ConflictCount > 0;

    /// <summary>
    /// Lista completa de todos os itens retornados pela última varredura diferencial.
    /// </summary>
    public List<SyncItem> Items { get; } = new();

    /// <summary>
    /// Coleção observável de mensagens exibidas na aba "Registro de Operações".
    /// </summary>
    public ObservableCollection<LogEntry> Logs { get; } = new();

    /// <summary>
    /// Avisos da última varredura.
    /// </summary>
    public ObservableCollection<string> ScanWarnings { get; } = new();

    /// <summary>
    /// Indica se a última varredura gerou avisos.
    /// </summary>
    public bool HasScanWarnings => ScanWarnings.Count > 0;

    /// <summary>
    /// Inicializa o <see cref="MainViewModel"/> com uma implementação específica de <see cref="ISyncEngine"/>.
    /// Deve ser instanciado na thread de UI.
    /// </summary>
    /// <param name="syncEngine">Instância do motor de sincronização.</param>
    /// <param name="loadSettings">Se <c>true</c>, restaura e passa a gravar as preferências do usuário (desative em testes automatizados).</param>
    public MainViewModel(ISyncEngine syncEngine, bool loadSettings = true)
    {
        _syncEngine = syncEngine;
        _logQueue = new UiBatchQueue<LogEntry>(AppendLogs, UiBatchInterval);
        _itemUpdateQueue = new UiBatchQueue<SyncItemUpdate>(ApplyItemUpdates, UiBatchInterval);
        ScanWarnings.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasScanWarnings));
        _persistSettings = loadSettings;

        if (loadSettings)
        {
            ApplySettings(SettingsService.Load());
        }
    }

    /// <summary>
    /// Construtor padrão que instancia o motor de produção <see cref="SyncEngine"/>.
    /// </summary>
    public MainViewModel() : this(new SyncEngine())
    {
    }

    /// <summary>
    /// Registra uma mensagem no log em arquivo e enfileira para exibição. Pode ser chamado de qualquer thread.
    /// </summary>
    /// <param name="message">Texto da mensagem a ser registrada.</param>
    public void AddLog(string message)
    {
        FileLogService.Write(message);
        _logQueue.Report(new LogEntry(DateTime.Now, LogEntry.InferLevel(message), message));
    }

    /// <summary>
    /// Salva as preferências atuais do usuário.
    /// </summary>
    public void SaveSettings()
    {
        if (!_persistSettings) return;
        SettingsService.Save(new AppSettings
        {
            SourcePath = SourcePath,
            DestinationPath = DestinationPath,
            UseSourceFolderNameInDestination = UseSourceFolderNameInDestination,
            EnableDeepHashCheck = EnableDeepHashCheck,
            TolerateDstShift = TolerateDstShift,
            ExcludePatterns = ExcludePatternsText
        });
    }

    private void ApplySettings(AppSettings settings)
    {
        _suppressInvalidation = true;
        try
        {
            SourcePath = settings.SourcePath;
            DestinationPath = settings.DestinationPath;
            UseSourceFolderNameInDestination = settings.UseSourceFolderNameInDestination;
            EnableDeepHashCheck = settings.EnableDeepHashCheck;
            TolerateDstShift = settings.TolerateDstShift;
            ExcludePatternsText = settings.ExcludePatterns;
        }
        finally
        {
            _suppressInvalidation = false;
        }
    }

    private void AppendLogs(IReadOnlyList<LogEntry> entries)
    {
        // Mais recentes no topo; insere o lote de uma vez e apara o excedente
        for (int i = 0; i < entries.Count; i++)
        {
            Logs.Insert(0, entries[i]);
        }
        while (Logs.Count > MaxLogEntries)
        {
            Logs.RemoveAt(Logs.Count - 1);
        }
    }

    private void ApplyItemUpdates(IReadOnlyList<SyncItemUpdate> updates)
    {
        foreach (var update in updates)
        {
            update.Apply();
        }
    }

    partial void OnSourcePathChanged(string value) => InvalidatePlan();

    partial void OnDestinationPathChanged(string value) => InvalidatePlan();

    partial void OnEnableDeepHashCheckChanged(bool value) => InvalidatePlan();

    partial void OnTolerateDstShiftChanged(bool value) => InvalidatePlan();

    partial void OnExcludePatternsTextChanged(string value) => InvalidatePlan();

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

    partial void OnActiveFilterChanged(ItemFilter value) => ApplyFilter();

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnFilteredItemsChanged(IReadOnlyList<SyncItem> value)
    {
        OnPropertyChanged(nameof(IsEmptyStateVisible));
        OnPropertyChanged(nameof(EmptyStateText));
    }

    partial void OnConflictCountChanged(int value) => OnPropertyChanged(nameof(CanEditConflicts));

    /// <summary>
    /// Descarta o plano carregado quando caminhos ou opções mudam, para que a execução nunca use
    /// uma análise feita com outra configuração.
    /// </summary>
    private void InvalidatePlan()
    {
        if (!_suppressInvalidation && !IsBusy && Items.Count > 0)
        {
            ClearPlan();
            StatusMessage = "Configuração alterada. Execute a análise novamente antes de sincronizar.";
        }
        NotifyCommands();
    }

    private void ClearPlan()
    {
        Items.Clear();
        ScanWarnings.Clear();
        _lastScanWarnings = Array.Empty<string>();
        _lastDestinationFileCount = 0;
        UpdateCounts();
        ApplyFilter();
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
        OnPropertyChanged(nameof(CanExportReport));
        OnPropertyChanged(nameof(CanEditConflicts));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsEmptyStateVisible));
        OnPropertyChanged(nameof(EmptyStateText));
        ScanCommand.NotifyCanExecuteChanged();
        StartSyncCommand.NotifyCanExecuteChanged();
        OpenTrashFolderCommand.NotifyCanExecuteChanged();
        OpenOldFolderCommand.NotifyCanExecuteChanged();
        RestoreTrashCommand.NotifyCanExecuteChanged();
        ExportReportCommand.NotifyCanExecuteChanged();
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
    /// Converte o texto de exclusões em lista de padrões.
    /// </summary>
    private IReadOnlyList<string> ParseExcludePatterns()
    {
        return (ExcludePatternsText ?? string.Empty)
            .Split(new[] { ';', ',', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
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
        if (!string.Equals(effectiveDest, DestinationPath.Trim(), StringComparison.Ordinal))
        {
            DestinationPath = effectiveDest;
            AddLog($"[INFO] Destino ajustado automaticamente para a subpasta da origem: {effectiveDest}");
        }

        SaveSettings();
        ClearPlan();

        IsScanning = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Analisando diretórios e identificando diferenças...";
        OverallProgress = 0;
        CurrentOperationText = "Varredura diferencial em andamento...";
        ResetCts();

        var options = new ScanOptions
        {
            UseDeepHash = EnableDeepHashCheck,
            TolerateDstShift = TolerateDstShift,
            ExcludePatterns = ParseExcludePatterns()
        };

        try
        {
            var scan = await _syncEngine.ScanDirectoriesAsync(
                SourcePath.Trim(),
                DestinationPath.Trim(),
                options,
                new InlineProgress<string>(AddLog),
                _cts!.Token);

            Items.AddRange(scan.Items);
            _lastDestinationFileCount = scan.DestinationFileCount;
            _lastScanWarnings = scan.Warnings.ToList();
            foreach (var warning in scan.Warnings)
            {
                ScanWarnings.Add(warning);
            }

            UpdateCounts();
            ApplyFilter();

            StatusMessage = $"Análise concluída: {NewCount} novos, {ConflictCount} conflitos, {TrashCount} órfãos p/ lixo/, {IdenticalCount} idênticos" +
                            (scan.Warnings.Count > 0 ? $" · {scan.Warnings.Count} aviso(s)." : ".");
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
            _logQueue.Flush();
            IsProgressIndeterminate = false;
            IsScanning = false;
            NotifyCommands();
        }
    }

    /// <summary>
    /// Monta o resumo do plano atual para confirmação.
    /// </summary>
    public SyncPlanSummary BuildPlanSummary()
    {
        var pending = Items.Where(i => !i.IsProcessed).ToList();
        var conflicts = pending.Where(i => i.IsConflict).ToList();
        return new SyncPlanSummary
        {
            DestinationPath = DestinationPath.Trim(),
            NewCount = pending.Count(i => i.IsNew),
            NewBytes = pending.Where(i => i.IsNew).Sum(i => i.SourceSize),
            ReplaceCount = conflicts.Count(i => i.Resolution == ConflictResolution.Replace),
            IgnoreCount = conflicts.Count(i => i.Resolution == ConflictResolution.Ignore),
            AskCount = conflicts.Count(i => i.Resolution == ConflictResolution.AskUser),
            OrphanCount = pending.Count(i => i.IsOrphan),
            DestinationFileCount = _lastDestinationFileCount,
            Warnings = _lastScanWarnings
        };
    }

    /// <summary>
    /// Executa a Etapa 2: confirma o plano com o usuário e realiza a sincronização física.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSync))]
    private async Task StartSyncAsync()
    {
        if (!Items.Any()) return;

        var summary = BuildPlanSummary();
        if (ConfirmSyncAsync != null && !await ConfirmSyncAsync(summary))
        {
            StatusMessage = "Sincronização não iniciada.";
            return;
        }

        IsSyncing = true;
        StatusMessage = "Sincronização em andamento...";
        OverallProgress = 0;
        ResetCts();

        var progress = new Progress<SyncProgressReport>(report =>
        {
            OverallProgress = report.OverallProgressPercent;
            SpeedFormatted = report.SpeedFormatted;
            EtaFormatted = report.EtaFormatted;
            ProcessedFilesText = $"{report.ProcessedFiles} / {report.TotalFiles} arquivos";
            CurrentOperationText = !string.IsNullOrEmpty(report.CurrentFileName)
                ? $"Processando: {report.CurrentFileName}"
                : "Finalizando...";
        });

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
            var result = await _syncEngine.ExecuteSyncAsync(
                Items,
                DestinationPath.Trim(),
                ResolveConflictHandler,
                _itemUpdateQueue,
                progress,
                new InlineProgress<string>(AddLog),
                _cts!.Token);

            StatusMessage = result.HasFailures
                ? $"Concluída com {result.Failed} falha(s): {result.Summary}. Use o filtro 'Erros' para ver os itens."
                : $"Sincronização concluída sem falhas: {result.Summary}.";
            CurrentOperationText = "Operações finalizadas.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Sincronização interrompida pelo usuário. Nenhum arquivo parcial foi deixado no destino.";
            CurrentOperationText = "Cancelado.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erro durante a sincronização: {ex.Message}";
            AddLog($"[ERRO CRÍTICO] {ex.Message}");
        }
        finally
        {
            _itemUpdateQueue.Flush();
            _logQueue.Flush();
            IsSyncing = false;
            UpdateCounts();
            ApplyFilter();
            NotifyCommands();
        }
    }

    /// <summary>
    /// Solicita o cancelamento cooperativo da varredura ou sincronização em andamento.
    /// </summary>
    [RelayCommand]
    private void Cancel()
    {
        if (_cts == null || _cts.IsCancellationRequested) return;
        _cts.Cancel();
        StatusMessage = "Solicitação de cancelamento enviada...";
    }

    private void ResetCts()
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
    }

    /// <summary>
    /// Altera o filtro ativo da grade de prévia.
    /// </summary>
    /// <param name="filter">Filtro a aplicar.</param>
    [RelayCommand]
    private void SetFilter(ItemFilter filter)
    {
        ActiveFilter = filter;
    }

    /// <summary>
    /// Filtra a lista principal <see cref="Items"/> pelo filtro ativo e pela busca, e atualiza atomicamente <see cref="FilteredItems"/>.
    /// </summary>
    private void ApplyFilter()
    {
        IEnumerable<SyncItem> source = ActiveFilter switch
        {
            ItemFilter.New => Items.Where(i => i.IsNew),
            ItemFilter.Conflicts => Items.Where(i => i.IsConflict),
            ItemFilter.Orphans => Items.Where(i => i.IsOrphan),
            ItemFilter.Identical => Items.Where(i => i.IsIdentical),
            ItemFilter.Errors => Items.Where(i => i.HasError),
            _ => Items
        };

        var search = SearchText?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            source = source.Where(i => i.RelativePath.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        FilteredItems = source.ToList();
    }

    /// <summary>
    /// Recalcula os totalizadores exibidos nos cartões de resumo.
    /// </summary>
    private void UpdateCounts()
    {
        NewCount = Items.Count(i => i.IsNew);
        IdenticalCount = Items.Count(i => i.IsIdentical);
        ConflictCount = Items.Count(i => i.IsConflict);
        TrashCount = Items.Count(i => i.IsOrphan);
        ErrorCount = Items.Count(i => i.HasError);
        NewBytesFormatted = SyncItem.FormatBytes(Items.Where(i => i.IsNew).Sum(i => i.SourceSize));
    }

    private IEnumerable<SyncItem> PendingConflicts => Items.Where(i => i.IsConflict && !i.IsProcessed);

    /// <summary>
    /// Define em lote a resolução de todos os conflitos pendentes como <see cref="ConflictResolution.Replace"/>.
    /// </summary>
    [RelayCommand]
    private void MarkAllConflictsAsReplace()
    {
        foreach (var item in PendingConflicts)
        {
            item.Resolution = ConflictResolution.Replace;
        }
        AddLog("Todos os conflitos foram marcados para 'Substituir (anterior em old/)'.");
    }

    /// <summary>
    /// Define em lote a resolução de todos os conflitos pendentes como <see cref="ConflictResolution.Ignore"/>.
    /// </summary>
    [RelayCommand]
    private void MarkAllConflictsAsIgnore()
    {
        foreach (var item in PendingConflicts)
        {
            item.Resolution = ConflictResolution.Ignore;
        }
        AddLog("Todos os conflitos foram marcados para 'Ignorar'.");
    }

    /// <summary>
    /// Política "mais recente vence": substitui quando a origem é mais nova; mantém o destino quando ele é mais novo
    /// ou quando as datas coincidem.
    /// </summary>
    [RelayCommand]
    private void MarkConflictsNewestWins()
    {
        int replace = 0, ignore = 0;
        foreach (var item in PendingConflicts)
        {
            if (item.ConflictDirection == ConflictDirection.SourceNewer)
            {
                item.Resolution = ConflictResolution.Replace;
                replace++;
            }
            else
            {
                item.Resolution = ConflictResolution.Ignore;
                ignore++;
            }
        }
        AddLog($"Política 'mais recente vence' aplicada: {replace} para substituir, {ignore} para ignorar.");
    }

    /// <summary>
    /// Lista o conteúdo de <c>lixo/</c>, permite ao usuário escolher os arquivos e os devolve ao local original
    /// sem sobrescrever nem excluir nada.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRestoreTrash))]
    private async Task RestoreTrashAsync()
    {
        if (string.IsNullOrWhiteSpace(DestinationPath)) return;

        var dest = ResolveEffectiveDestinationPath(SourcePath, DestinationPath);
        IsSyncing = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Lendo a pasta lixo/...";
        CurrentOperationText = "Restauração";
        ResetCts();

        try
        {
            var entries = await _restoreService.ListTrashAsync(dest, _cts!.Token);
            if (entries.Count == 0)
            {
                StatusMessage = $"A pasta lixo/ de '{dest}' está vazia ou não existe.";
                return;
            }

            IReadOnlyList<TrashEntry>? selected = entries;
            if (SelectTrashEntriesAsync != null)
            {
                IsProgressIndeterminate = false;
                selected = await SelectTrashEntriesAsync(entries);
            }

            if (selected == null || selected.Count == 0)
            {
                StatusMessage = "Restauração cancelada.";
                return;
            }

            IsProgressIndeterminate = true;
            StatusMessage = $"Restaurando {selected.Count} arquivo(s)...";
            var result = await _restoreService.RestoreAsync(dest, selected, new InlineProgress<string>(AddLog), _cts.Token);

            // Os arquivos do destino mudaram: a análise anterior deixa de valer
            ClearPlan();
            StatusMessage = $"Restauração concluída: {result.Restored} restaurados, {result.KeptInTrash} mantidos em lixo/ (já existiam no destino), {result.Failed} falhas. Analise novamente.";
            CurrentOperationText = "Restauração concluída.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Restauração cancelada.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erro ao restaurar lixo/: {ex.Message}";
            AddLog($"[ERRO] {ex.Message}");
        }
        finally
        {
            _logQueue.Flush();
            IsProgressIndeterminate = false;
            IsSyncing = false;
            NotifyCommands();
        }
    }

    /// <summary>
    /// Exporta a análise/resultado atual em CSV.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExportReport))]
    private async Task ExportReportAsync()
    {
        if (PickSaveFileAsync == null) return;
        try
        {
            var path = await PickSaveFileAsync($"omnisync-relatorio-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            if (string.IsNullOrWhiteSpace(path)) return;

            await ReportExporter.WriteCsvAsync(path, Items);
            StatusMessage = $"Relatório exportado: {path}";
            AddLog($"Relatório CSV exportado para '{path}' ({Items.Count} itens).");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erro ao exportar relatório: {ex.Message}";
            AddLog($"[ERRO] Exportação de relatório: {ex.Message}");
        }
    }

    /// <summary>
    /// Abre a pasta <c>destino/lixo/</c> no gerenciador de arquivos do sistema operacional.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenDestinationFolders))]
    private void OpenTrashFolder() => OpenDestinationSubfolder(PathHelper.TrashFolderName);

    /// <summary>
    /// Abre a pasta <c>destino/old/</c> no gerenciador de arquivos do sistema operacional.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenDestinationFolders))]
    private void OpenOldFolder() => OpenDestinationSubfolder(PathHelper.OldFolderName);

    /// <summary>
    /// Abre a pasta de logs do aplicativo.
    /// </summary>
    [RelayCommand]
    private void OpenLogsFolder()
    {
        Directory.CreateDirectory(AppPaths.LogsDirectory);
        OpenInFileManager(AppPaths.LogsDirectory);
    }

    /// <summary>
    /// Garante a criação da subpasta solicitada (<c>lixo</c> ou <c>old</c>) dentro do destino e a abre.
    /// </summary>
    private void OpenDestinationSubfolder(string subFolder)
    {
        if (string.IsNullOrWhiteSpace(DestinationPath))
        {
            StatusMessage = "Por favor, selecione primeiro o diretório de destino.";
            return;
        }

        try
        {
            var effectiveDest = ResolveEffectiveDestinationPath(SourcePath, DestinationPath);
            var fullPath = Path.GetFullPath(Path.Combine(effectiveDest, subFolder));
            Directory.CreateDirectory(fullPath);
            OpenInFileManager(fullPath);
        }
        catch (Exception ex)
        {
            AddLog($"[ERRO] Não foi possível abrir a pasta: {ex.Message}");
        }
    }

    /// <summary>
    /// Abre uma pasta no explorador nativo (<c>explorer.exe</c> no Windows, <c>xdg-open</c> no Linux, <c>open</c> no macOS).
    /// </summary>
    private void OpenInFileManager(string fullPath)
    {
        try
        {
            var executable = OperatingSystem.IsWindows()
                ? "explorer.exe"
                : OperatingSystem.IsLinux() ? "xdg-open" : "open";

            var psi = new ProcessStartInfo { FileName = executable, UseShellExecute = false };
            psi.ArgumentList.Add(fullPath);
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            AddLog($"[ERRO] Não foi possível abrir a pasta: {ex.Message}");
        }
    }
}
