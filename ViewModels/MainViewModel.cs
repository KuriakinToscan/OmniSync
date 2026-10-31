using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmniSync.Models;
using OmniSync.Services;

namespace OmniSync.ViewModels;

/// <summary>
/// ViewModel principal da aplicação OmniSync.
/// Coordena as tarefas de sincronização salvas, a varredura diferencial (Dry-Run), a navegação pelo plano
/// (árvore de pastas, filtros, painel de detalhes), a confirmação e execução da sincronização, a tela de
/// resultado e a restauração de arquivos da pasta <c>lixo/</c>.
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    /// <summary>
    /// Limite máximo de linhas mantidas na memória visual do registro (o log completo vai para arquivo).
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
    private readonly AppSettings _settings;
    private CancellationTokenSource? _cts;
    private readonly bool _persistSettings;
    private bool _suppressInvalidation;
    private int _lastDestinationFileCount;
    private int _lastSourceFileCount;
    private IReadOnlyList<string> _lastScanWarnings = Array.Empty<string>();
    private readonly DecisionMemory _decisionMemory = new();
    private string? _planPairKey;

    // Contadores da execução em andamento, por fase
    private int _runCopyTotal, _runCopyDone, _runReplaceTotal, _runReplaceDone, _runTrashTotal, _runTrashDone;
    private long _runCopyBytes;

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
    /// Delegate injetado pela View para perguntas simples de confirmação (título, mensagem, rótulo do botão).
    /// Quando não definido, a resposta é "sim".
    /// </summary>
    public Func<string, string, string, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>
    /// Delegate injetado pela View para avisos com um único botão (título, mensagem).
    /// Quando não definido, o aviso vai só para a mensagem de estado.
    /// </summary>
    public Func<string, string, Task>? NotifyAsync { get; set; }

    /// <summary>
    /// Delegate injetado pela View para escolher onde salvar o relatório CSV.
    /// </summary>
    public Func<string, Task<string?>>? PickSaveFileAsync { get; set; }

    /// <summary>
    /// Delegate injetado pela View para o usuário selecionar quais arquivos de <c>lixo/</c> restaurar.
    /// Retorna <c>null</c> se cancelado.
    /// </summary>
    public Func<IReadOnlyList<TrashEntry>, Task<IReadOnlyList<TrashEntry>?>>? SelectTrashEntriesAsync { get; set; }

    // ------------------------------------------------------------------
    // Tarefas salvas
    // ------------------------------------------------------------------

    /// <summary>
    /// Tarefas de sincronização salvas, exibidas na barra lateral.
    /// </summary>
    public ObservableCollection<ProfileItemViewModel> Profiles { get; } = new();

    /// <summary>
    /// Tarefa ativa. Trocar de tarefa descarta o plano carregado.
    /// </summary>
    [ObservableProperty]
    private ProfileItemViewModel? _selectedProfile;

    /// <summary>
    /// Nome da tarefa ativa (editável no cabeçalho).
    /// </summary>
    [ObservableProperty]
    private string _profileName = string.Empty;

    // ------------------------------------------------------------------
    // Configuração da tarefa ativa
    // ------------------------------------------------------------------

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

    // ------------------------------------------------------------------
    // Estado de execução
    // ------------------------------------------------------------------

    /// <summary>
    /// Indica se uma varredura diferencial (Dry-Run) está em andamento.
    /// </summary>
    [ObservableProperty]
    private bool _isScanning = false;

    /// <summary>
    /// Indica se uma operação que altera o destino (sincronização ou restauração) está em andamento.
    /// </summary>
    [ObservableProperty]
    private bool _isSyncing = false;

    /// <summary>
    /// Indica se a sincronização em si está em andamento (tela de execução).
    /// </summary>
    [ObservableProperty]
    private bool _isRunningSync = false;

    /// <summary>
    /// Indica se a tela de resultado está visível.
    /// </summary>
    [ObservableProperty]
    private bool _isShowingResult = false;

    /// <summary>
    /// Indica que o usuário voltou à configuração com um plano ainda carregado ("Alterar").
    /// O plano só é descartado se algum caminho ou opção mudar; até lá, "Voltar ao plano" o recupera intacto.
    /// </summary>
    [ObservableProperty]
    private bool _isEditingSetup;

    /// <summary>
    /// Balanço da última sincronização da sessão.
    /// </summary>
    [ObservableProperty]
    private SyncResult? _lastResult;

    /// <summary>
    /// Indica se a última sincronização foi cancelada pelo usuário.
    /// </summary>
    [ObservableProperty]
    private bool _lastRunCancelled;

    /// <summary>
    /// Mensagem de estado geral da aplicação.
    /// </summary>
    [ObservableProperty]
    private string _statusMessage = "Pronto. Escolha a origem e o destino e clique em 'Analisar'.";

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
    /// Texto descritivo da operação ou arquivo em processamento.
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
    /// Volume processado sobre o total previsto (ex.: <c>1,6 GB de 3,4 GB</c>).
    /// </summary>
    [ObservableProperty]
    private string _processedBytesText = "—";

    /// <summary>
    /// Percentual formatado para a tela de execução.
    /// </summary>
    [ObservableProperty]
    private string _progressPercentText = "0%";

    /// <summary>
    /// Indica que a execução está parada aguardando a decisão de um conflito.
    /// </summary>
    [ObservableProperty]
    private bool _isWaitingForDecision;

    /// <summary>
    /// Arquivo cujo conflito aguarda decisão.
    /// </summary>
    [ObservableProperty]
    private string _waitingDecisionFile = string.Empty;

    /// <summary>Progresso da fase de cópia (ex.: <c>630 de 1.248</c>).</summary>
    [ObservableProperty]
    private string _copyPhaseText = "—";

    /// <summary>Progresso da fase de substituição.</summary>
    [ObservableProperty]
    private string _replacePhaseText = "—";

    /// <summary>Progresso da fase de movimentação para <c>lixo/</c>.</summary>
    [ObservableProperty]
    private string _trashPhaseText = "—";

    // ------------------------------------------------------------------
    // Resumo do plano
    // ------------------------------------------------------------------

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

    /// <summary>Conflitos pendentes marcados para substituir.</summary>
    [ObservableProperty]
    private int _replaceDecisionCount;

    /// <summary>Conflitos pendentes marcados para manter o destino.</summary>
    [ObservableProperty]
    private int _keepDecisionCount;

    /// <summary>Conflitos pendentes ainda sem decisão.</summary>
    [ObservableProperty]
    private int _askDecisionCount;

    /// <summary>Indica se há conflitos sem decisão.</summary>
    public bool HasPendingDecisions => AskDecisionCount > 0;

    /// <summary>Título do resumo (ex.: <c>1.397 alterações planejadas</c>).</summary>
    [ObservableProperty]
    private string _planHeadline = string.Empty;

    /// <summary>Linha secundária do resumo (volume e idênticos).</summary>
    [ObservableProperty]
    private string _planSubline = string.Empty;

    /// <summary>Descrição dos arquivos indexados na origem.</summary>
    [ObservableProperty]
    private string _sourceStatsText = string.Empty;

    /// <summary>Descrição dos arquivos indexados no destino.</summary>
    [ObservableProperty]
    private string _destinationStatsText = string.Empty;

    // ------------------------------------------------------------------
    // Navegação no plano
    // ------------------------------------------------------------------

    /// <summary>
    /// Filtro de categoria ativo na lista.
    /// </summary>
    [ObservableProperty]
    private ItemFilter _activeFilter = ItemFilter.Changes;

    /// <summary>
    /// Texto de busca aplicado ao caminho relativo (sem diferenciar maiúsculas nem acentos).
    /// </summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>
    /// Lista exibida pela grade, conforme filtro, pasta e busca.
    /// Substituída atomicamente para evitar milhares de eventos individuais de coleção na UI.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<SyncItem> _filteredItems = Array.Empty<SyncItem>();

    /// <summary>
    /// Raízes da árvore "Pastas com alterações" (um único nó raiz).
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<FolderNode> _folderRoots = Array.Empty<FolderNode>();

    /// <summary>
    /// Pasta selecionada na árvore; filtra a lista.
    /// </summary>
    [ObservableProperty]
    private FolderNode? _selectedFolder;

    /// <summary>
    /// Arquivo selecionado na lista; exibido no painel de detalhes.
    /// </summary>
    [ObservableProperty]
    private SyncItem? _selectedItem;

    /// <summary>
    /// Na tela de execução, mostra só avisos e erros no registro.
    /// </summary>
    [ObservableProperty]
    private bool _showOnlyProblems;

    // ------------------------------------------------------------------
    // Propriedades derivadas
    // ------------------------------------------------------------------

    /// <summary>
    /// Indica se qualquer operação assíncrona (varredura ou sincronização) está em execução.
    /// </summary>
    public bool IsBusy => IsScanning || IsSyncing;

    /// <summary>
    /// Indica se há resultado de análise carregado.
    /// </summary>
    public bool HasItems => Items.Count > 0;

    /// <summary>Tela de configuração da tarefa (sem plano carregado, ou editando com o plano guardado).</summary>
    public bool IsSetupScreen => !IsRunningSync && !IsShowingResult && (Items.Count == 0 || IsEditingSetup);

    /// <summary>Tela do plano analisado.</summary>
    public bool IsPlanScreen => !IsRunningSync && !IsShowingResult && Items.Count > 0 && !IsEditingSetup;

    /// <summary>Indica se a configuração foi aberta a partir de um plano que ainda pode ser retomado.</summary>
    public bool CanReturnToPlan => IsEditingSetup && Items.Count > 0 && !IsBusy;

    /// <summary>
    /// Destino efetivo: a pasta escolhida, mais a subpasta com o nome da origem quando a opção está marcada.
    /// </summary>
    public string EffectiveDestinationPath =>
        PathHelper.ResolveEffectiveDestination(SourcePath, DestinationPath, UseSourceFolderNameInDestination);

    /// <summary>
    /// Linha "Sincroniza em: …" sob o destino, exibida quando o destino efetivo difere da pasta escolhida.
    /// </summary>
    public string EffectiveDestinationHint
    {
        get
        {
            var effective = EffectiveDestinationPath;
            return string.IsNullOrWhiteSpace(effective) || string.Equals(effective, DestinationPath.Trim(), StringComparison.Ordinal)
                ? string.Empty
                : $"Sincroniza em: {effective}";
        }
    }

    /// <summary>Indica se a linha "Sincroniza em: …" deve ser exibida.</summary>
    public bool HasEffectiveDestinationHint => EffectiveDestinationHint.Length > 0;

    /// <summary>
    /// Problema do par Origem/Destino detectado na hora da escolha (mesma pasta ou uma dentro da outra).
    /// </summary>
    public string? PairProblem => PathHelper.DescribePairProblem(SourcePath, EffectiveDestinationPath);

    /// <summary>Indica se há problema no par Origem/Destino.</summary>
    public bool HasPairProblem => PairProblem != null;

    /// <summary>Tela de execução.</summary>
    public bool IsRunScreen => IsRunningSync;

    /// <summary>Tela de resultado.</summary>
    public bool IsResultScreen => !IsRunningSync && IsShowingResult;

    /// <summary>Indica se há arquivo selecionado (painel de detalhes visível).</summary>
    public bool HasSelectedItem => SelectedItem != null;

    /// <summary>Total de alterações planejadas (chip "Todas").</summary>
    public int ChangeCount => NewCount + ConflictCount + TrashCount;

    /// <summary>
    /// Indica se a lista está restrita por categoria, pasta ou busca (diferente da visão "Todas").
    /// </summary>
    public bool HasListRefinement =>
        ActiveFilter != ItemFilter.Changes ||
        !string.IsNullOrWhiteSpace(SearchText) ||
        (SelectedFolder != null && SelectedFolder.RelativePath.Length > 0);

    /// <summary>
    /// Descrição do que restringe a lista (ex.: <c>Mostrando: conflitos · pasta Fotos · busca "img"</c>).
    /// </summary>
    public string ListRefinementText
    {
        get
        {
            var parts = new List<string>();
            var category = ActiveFilter switch
            {
                ItemFilter.New => "novos",
                ItemFilter.Conflicts => "conflitos",
                ItemFilter.Orphans => "para lixo/",
                ItemFilter.Identical => "idênticos",
                ItemFilter.Errors => "erros",
                ItemFilter.All => "tudo",
                _ => "todas as alterações"
            };
            parts.Add(category);
            if (SelectedFolder != null && SelectedFolder.RelativePath.Length > 0)
            {
                parts.Add($"pasta {SelectedFolder.RelativePath}");
            }
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                parts.Add($"busca \"{SearchText.Trim()}\"");
            }
            return $"Mostrando {FilteredItems.Count:N0}: " + string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// Indica se a mensagem de estado vazio deve ser exibida sobre a grade.
    /// </summary>
    public bool IsEmptyStateVisible => !IsBusy && Items.Count > 0 && FilteredItems.Count == 0;

    /// <summary>
    /// Texto do estado vazio da grade, conforme o contexto.
    /// </summary>
    public string EmptyStateText => ActiveFilter == ItemFilter.Changes && string.IsNullOrWhiteSpace(SearchText) && (SelectedFolder == null || SelectedFolder.RelativePath.Length == 0)
        ? "Origem e destino já estão iguais. Não há nada a sincronizar."
        : "Nenhum arquivo corresponde ao filtro, à pasta ou à busca.";

    /// <summary>
    /// Indica se o botão "Analisar" pode ser acionado.
    /// </summary>
    public bool CanScan => !IsBusy && !string.IsNullOrWhiteSpace(SourcePath) && !string.IsNullOrWhiteSpace(DestinationPath) && !HasPairProblem;

    /// <summary>
    /// Texto de ajuda sob o botão "Analisar" quando ele está desabilitado.
    /// </summary>
    public string ScanHint => string.IsNullOrWhiteSpace(SourcePath)
        ? "Escolha a origem para analisar."
        : string.IsNullOrWhiteSpace(DestinationPath)
            ? "Escolha o destino para analisar."
            : HasPairProblem
                ? "Corrija a origem ou o destino para analisar."
                : StatusMessage;

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
    /// Indica se é possível trocar de tarefa, criar ou remover tarefas.
    /// </summary>
    public bool CanManageProfiles => !IsBusy;

    /// <summary>
    /// Resumo da barra inferior do plano.
    /// </summary>
    public string PlanFooterText
    {
        get
        {
            var parts = new List<string> { $"Copiar {NewCount:N0}" };
            if (ConflictCount > 0)
            {
                parts.Add($"substituir {ReplaceDecisionCount:N0}");
                parts.Add($"manter {KeepDecisionCount:N0}");
                if (AskDecisionCount > 0) parts.Add($"{AskDecisionCount:N0} sem decisão");
            }
            parts.Add($"mover {TrashCount:N0} para lixo/");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Chips com as opções ativas da tarefa, exibidos no plano.</summary>
    public IReadOnlyList<string> ActiveOptionChips
    {
        get
        {
            var chips = new List<string>();
            if (UseSourceFolderNameInDestination) chips.Add("Subpasta da origem");
            if (EnableDeepHashCheck) chips.Add("SHA-256");
            if (TolerateDstShift) chips.Add("Tolerar 1 h");
            int patterns = ParseExcludePatterns().Count;
            if (patterns > 0) chips.Add(patterns == 1 ? "Ignorar 1 padrão" : $"Ignorar {patterns} padrões");
            return chips;
        }
    }

    /// <summary>Texto do link de avisos da análise.</summary>
    public string ScanWarningsLinkText => ScanWarnings.Count == 1 ? "1 aviso da análise" : $"{ScanWarnings.Count} avisos da análise";

    /// <summary>Título da tela de resultado.</summary>
    public string ResultTitle => LastResult == null
        ? string.Empty
        : LastRunCancelled
            ? "Sincronização interrompida"
            : LastResult.HasFailures
                ? (LastResult.Failed == 1 ? "Concluído com 1 falha" : $"Concluído com {LastResult.Failed} falhas")
                : "Sincronização concluída";

    /// <summary>Linha secundária da tela de resultado.</summary>
    public string ResultSubtitle => LastResult == null
        ? string.Empty
        : $"{SyncItem.FormatBytes(LastResult.BytesCopied)} gravados em {FormatDuration(LastResult.Duration)} · nenhuma cópia parcial ficou no destino";

    /// <summary>Indica que a última execução teve falhas ou foi interrompida.</summary>
    public bool ResultHasProblems => LastResult != null && (LastResult.HasFailures || LastRunCancelled);

    /// <summary>Indica que a última execução terminou sem falhas.</summary>
    public bool ResultIsClean => LastResult != null && !LastResult.HasFailures && !LastRunCancelled;

    /// <summary>Itens com falha na última execução.</summary>
    public IReadOnlyList<SyncItem> FailedItems => LastResult?.FailedItems ?? (IReadOnlyList<SyncItem>)Array.Empty<SyncItem>();

    /// <summary>Indica se há falhas a listar.</summary>
    public bool HasFailedItems => FailedItems.Count > 0;

    /// <summary>Rótulo do botão de nova tentativa.</summary>
    public string RetryText => LastRunCancelled ? "Continuar sincronização" : FailedItems.Count == 1 ? "Tentar a falha de novo" : $"Tentar as {FailedItems.Count} de novo";

    /// <summary>Registro exibido na tela de execução (completo ou só avisos e erros).</summary>
    public IReadOnlyList<LogEntry> ActivityLogs => ShowOnlyProblems ? ProblemLogs : Logs;

    /// <summary>Quantidade de avisos e erros no registro.</summary>
    public int ProblemLogCount => ProblemLogs.Count;

    // ------------------------------------------------------------------
    // Coleções
    // ------------------------------------------------------------------

    /// <summary>
    /// Lista completa de todos os itens retornados pela última varredura diferencial.
    /// </summary>
    public List<SyncItem> Items { get; } = new();

    /// <summary>
    /// Registro de operações (mais recentes no topo).
    /// </summary>
    public ObservableCollection<LogEntry> Logs { get; } = new();

    /// <summary>Nenhuma linha no registro desta sessão (a janela Registro mostra uma explicação).</summary>
    public bool HasNoSessionLogs => Logs.Count == 0;

    /// <summary>
    /// Avisos e erros do registro (mais recentes no topo).
    /// </summary>
    public ObservableCollection<LogEntry> ProblemLogs { get; } = new();

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
    /// <param name="loadSettings">Se <c>true</c>, restaura e passa a gravar as tarefas salvas (desative em testes automatizados).</param>
    public MainViewModel(ISyncEngine syncEngine, bool loadSettings = true)
    {
        _syncEngine = syncEngine;
        _logQueue = new UiBatchQueue<LogEntry>(AppendLogs, UiBatchInterval);
        _itemUpdateQueue = new UiBatchQueue<SyncItemUpdate>(ApplyItemUpdates, UiBatchInterval);
        ScanWarnings.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasScanWarnings));
            OnPropertyChanged(nameof(ScanWarningsLinkText));
        };
        _persistSettings = loadSettings;
        _settings = loadSettings ? SettingsService.Load() : new AppSettings();
        InitializeScheduleEditor();
        Logs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoSessionLogs));

        foreach (var profile in _settings.Profiles)
        {
            Profiles.Add(new ProfileItemViewModel(profile));
        }
        if (Profiles.Count == 0)
        {
            Profiles.Add(CreateProfileItem());
        }

        RefreshRunsFromHistory();
        SelectedProfile = Profiles.FirstOrDefault(p => p.Profile.Id == _settings.ActiveProfileId) ?? Profiles[0];
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

    // ------------------------------------------------------------------
    // Persistência e tarefas
    // ------------------------------------------------------------------

    /// <summary>
    /// Grava as tarefas salvas e a tarefa ativa.
    /// </summary>
    public void SaveSettings()
    {
        WriteBackToProfile();
        if (!_persistSettings) return;
        _settings.Profiles = Profiles.Select(p => p.Profile).ToList();
        _settings.ActiveProfileId = SelectedProfile?.Profile.Id;
        SettingsService.Save(_settings);
    }

    private static ProfileItemViewModel CreateProfileItem() =>
        new(new SyncProfile { Name = SyncProfile.DefaultName, ExcludePatterns = SettingsService.DefaultExcludePatterns });

    /// <summary>
    /// Copia a configuração atual da tela para a tarefa ativa.
    /// </summary>
    private void WriteBackToProfile()
    {
        if (SelectedProfile == null || _suppressInvalidation) return;
        var p = SelectedProfile.Profile;
        p.SourcePath = SourcePath;
        p.DestinationPath = DestinationPath;
        p.UseSourceFolderNameInDestination = UseSourceFolderNameInDestination;
        p.EnableDeepHashCheck = EnableDeepHashCheck;
        p.TolerateDstShift = TolerateDstShift;
        p.ExcludePatterns = ExcludePatternsText;
    }

    partial void OnSelectedProfileChanged(ProfileItemViewModel? oldValue, ProfileItemViewModel? newValue)
    {
        if (oldValue != null && !ReferenceEquals(oldValue, newValue))
        {
            oldValue.RefreshFromLastRun();
        }
        if (newValue == null) return;

        var p = newValue.Profile;
        _suppressInvalidation = true;
        try
        {
            ProfileName = p.Name;
            SourcePath = p.SourcePath;
            DestinationPath = p.DestinationPath;
            UseSourceFolderNameInDestination = p.UseSourceFolderNameInDestination;
            EnableDeepHashCheck = p.EnableDeepHashCheck;
            TolerateDstShift = p.TolerateDstShift;
            ExcludePatternsText = p.ExcludePatterns;
        }
        finally
        {
            _suppressInvalidation = false;
        }

        LoadScheduleEditor(p);
        ClearPlan();
        IsShowingResult = false;
        LastResult = null;
        LastRunCancelled = false;
        StatusMessage = HasPaths ? "Pronto para analisar." : "Escolha a origem e o destino.";
        NotifyCommands();
    }

    private bool HasPaths => !string.IsNullOrWhiteSpace(SourcePath) && !string.IsNullOrWhiteSpace(DestinationPath);

    partial void OnProfileNameChanged(string value)
    {
        if (SelectedProfile != null && !string.IsNullOrWhiteSpace(value))
        {
            SelectedProfile.Name = value.Trim();
        }
    }

    /// <summary>
    /// Cria uma tarefa vazia e a seleciona.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanManageProfiles))]
    private void NewProfile()
    {
        SaveSettings();
        var item = CreateProfileItem();
        Profiles.Add(item);
        SelectedProfile = item;
    }

    /// <summary>
    /// Remove uma tarefa salva (só a configuração; nenhum arquivo é tocado), após confirmação.
    /// </summary>
    /// <param name="item">Tarefa a remover; quando nula, a tarefa ativa.</param>
    [RelayCommand(CanExecute = nameof(CanManageProfiles))]
    private async Task DeleteProfileAsync(ProfileItemViewModel? item)
    {
        item ??= SelectedProfile;
        if (item == null) return;

        bool scheduled = item.Profile.Schedule?.Enabled == true;
        if (ConfirmAsync != null &&
            !await ConfirmAsync(
                "Excluir tarefa",
                $"Excluir a tarefa \"{item.Name}\"? Só a configuração salva é apagada; nenhum arquivo da origem ou do destino é alterado." +
                (scheduled ? " O agendamento desta tarefa também é removido." : string.Empty),
                "Excluir"))
        {
            return;
        }

        int index = Profiles.IndexOf(item);
        bool wasSelected = ReferenceEquals(item, SelectedProfile);
        Profiles.Remove(item);
        if (Profiles.Count == 0)
        {
            Profiles.Add(CreateProfileItem());
        }
        if (wasSelected)
        {
            SelectedProfile = Profiles[Math.Clamp(index, 0, Profiles.Count - 1)];
        }
        SaveSettings();
        await RemoveScheduleAndHistoryAsync(item.Profile);
    }

    // ------------------------------------------------------------------
    // Reações a mudanças
    // ------------------------------------------------------------------

    private void AppendLogs(IReadOnlyList<LogEntry> entries)
    {
        // Mais recentes no topo; insere o lote de uma vez e apara o excedente
        bool problems = false;
        for (int i = 0; i < entries.Count; i++)
        {
            Logs.Insert(0, entries[i]);
            if (entries[i].Level != LogLevel.Info)
            {
                ProblemLogs.Insert(0, entries[i]);
                problems = true;
            }
        }
        while (Logs.Count > MaxLogEntries)
        {
            Logs.RemoveAt(Logs.Count - 1);
        }
        while (ProblemLogs.Count > MaxLogEntries)
        {
            ProblemLogs.RemoveAt(ProblemLogs.Count - 1);
        }
        if (problems)
        {
            OnPropertyChanged(nameof(ProblemLogCount));
        }
    }

    private void ApplyItemUpdates(IReadOnlyList<SyncItemUpdate> updates)
    {
        foreach (var update in updates)
        {
            bool wasProcessed = update.Item.IsProcessed;
            update.Apply();
            if (!wasProcessed && update.IsProcessed)
            {
                switch (update.Item.ActionType)
                {
                    case SyncActionType.New: _runCopyDone++; break;
                    case SyncActionType.Conflict: _runReplaceDone++; break;
                    case SyncActionType.OrphanTrash: _runTrashDone++; break;
                }
            }
        }
        UpdatePhaseTexts();
    }

    private void UpdatePhaseTexts()
    {
        CopyPhaseText = _runCopyTotal == 0 ? "Nada a copiar" : $"{_runCopyDone:N0} de {_runCopyTotal:N0} · {SyncItem.FormatBytes(_runCopyBytes)}";
        ReplacePhaseText = _runReplaceTotal == 0 ? "Nenhum conflito" : $"{_runReplaceDone:N0} de {_runReplaceTotal:N0} decididos";
        TrashPhaseText = _runTrashTotal == 0 ? "Nada a mover" : $"{_runTrashDone:N0} de {_runTrashTotal:N0}";
    }

    partial void OnSourcePathChanged(string? oldValue, string newValue)
    {
        // Nome automático enquanto o usuário não renomear a tarefa
        if (!_suppressInvalidation && SelectedProfile != null &&
            (SyncProfile.IsDefaultName(ProfileName) || ProfileName == SyncProfile.SuggestName(oldValue)))
        {
            ProfileName = SyncProfile.SuggestName(newValue);
        }
        NotifyDestination();
        InvalidatePlan();
        RefreshSetupStatus();
    }

    /// <summary>
    /// Expande variáveis de ambiente (<c>%USERPROFILE%</c>, <c>~</c>) nos campos de origem e destino. Chamado ao sair
    /// do campo e antes de analisar, para que a tarefa salve e use o caminho real.
    /// </summary>
    public void ExpandPaths()
    {
        var source = PathHelper.ExpandUserPath(SourcePath);
        if (source != SourcePath) SourcePath = source;
        var dest = PathHelper.ExpandUserPath(DestinationPath);
        if (dest != DestinationPath) DestinationPath = dest;
    }

    /// <summary>
    /// Atualiza o destino efetivo e o aviso de origem/destino inválidos exibidos na configuração.
    /// </summary>
    private void NotifyDestination()
    {
        OnPropertyChanged(nameof(EffectiveDestinationPath));
        OnPropertyChanged(nameof(EffectiveDestinationHint));
        OnPropertyChanged(nameof(HasEffectiveDestinationHint));
        OnPropertyChanged(nameof(PairProblem));
        OnPropertyChanged(nameof(HasPairProblem));
    }

    /// <summary>
    /// Mantém a mensagem da tela de configuração coerente com os caminhos preenchidos.
    /// </summary>
    private void RefreshSetupStatus()
    {
        if (!_suppressInvalidation && !IsBusy && Items.Count == 0)
        {
            StatusMessage = HasPaths ? "Pronto para analisar." : "Escolha a origem e o destino.";
        }
        OnPropertyChanged(nameof(ScanHint));
    }

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(ScanHint));

    partial void OnDestinationPathChanged(string value)
    {
        NotifyDestination();
        InvalidatePlan();
        RefreshSetupStatus();
    }

    partial void OnEnableDeepHashCheckChanged(bool value) => InvalidatePlan();

    partial void OnTolerateDstShiftChanged(bool value) => InvalidatePlan();

    partial void OnExcludePatternsTextChanged(string value) => InvalidatePlan();

    partial void OnUseSourceFolderNameInDestinationChanged(bool value)
    {
        NotifyDestination();
        InvalidatePlan();
        RefreshSetupStatus();
    }

    partial void OnIsEditingSetupChanged(bool value) => NotifyScreens();

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

    partial void OnIsRunningSyncChanged(bool value) => NotifyScreens();

    partial void OnIsShowingResultChanged(bool value) => NotifyScreens();

    partial void OnLastResultChanged(SyncResult? value) => NotifyResult();

    partial void OnLastRunCancelledChanged(bool value) => NotifyResult();

    partial void OnActiveFilterChanged(ItemFilter value) => ApplyFilter();

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
        OnPropertyChanged(nameof(HasSearchText));
        ClearSearchCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Indica se há texto na busca (mostra o botão de limpar).
    /// </summary>
    public bool HasSearchText => !string.IsNullOrEmpty(SearchText);

    /// <summary>
    /// Limpa a busca (botão X da caixa de busca ou Esc dentro dela).
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSearchText))]
    private void ClearSearch() => SearchText = string.Empty;

    partial void OnSelectedFolderChanged(FolderNode? value) => ApplyFilter();

    partial void OnSelectedItemChanged(SyncItem? value) => OnPropertyChanged(nameof(HasSelectedItem));

    partial void OnShowOnlyProblemsChanged(bool value) => OnPropertyChanged(nameof(ActivityLogs));

    partial void OnFilteredItemsChanged(IReadOnlyList<SyncItem> value)
    {
        OnPropertyChanged(nameof(HasListRefinement));
        OnPropertyChanged(nameof(ListRefinementText));
        OnPropertyChanged(nameof(IsEmptyStateVisible));
        OnPropertyChanged(nameof(EmptyStateText));
    }

    partial void OnConflictCountChanged(int value) => OnPropertyChanged(nameof(CanEditConflicts));

    partial void OnAskDecisionCountChanged(int value) => OnPropertyChanged(nameof(HasPendingDecisions));

    private void NotifyScreens()
    {
        OnPropertyChanged(nameof(CanReturnToPlan));
        OnPropertyChanged(nameof(IsSetupScreen));
        OnPropertyChanged(nameof(IsPlanScreen));
        OnPropertyChanged(nameof(IsRunScreen));
        OnPropertyChanged(nameof(IsResultScreen));
    }

    private void NotifyResult()
    {
        OnPropertyChanged(nameof(ResultTitle));
        OnPropertyChanged(nameof(ResultSubtitle));
        OnPropertyChanged(nameof(ResultHasProblems));
        OnPropertyChanged(nameof(ResultIsClean));
        OnPropertyChanged(nameof(FailedItems));
        OnPropertyChanged(nameof(HasFailedItems));
        OnPropertyChanged(nameof(RetryText));
    }

    /// <summary>
    /// Descarta o plano carregado quando caminhos ou opções mudam, para que a execução nunca use
    /// uma análise feita com outra configuração.
    /// </summary>
    private void InvalidatePlan()
    {
        if (_suppressInvalidation) return;

        WriteBackToProfile();
        if (!IsBusy && (Items.Count > 0 || IsShowingResult))
        {
            ClearPlan();
            IsShowingResult = false;
            StatusMessage = "Configuração alterada. Analise novamente antes de sincronizar.";
        }
        if (!IsBusy && SelectedProfile != null && Items.Count == 0)
        {
            SelectedProfile.RefreshFromLastRun();
        }
        OnPropertyChanged(nameof(ActiveOptionChips));
        NotifyCommands();
    }

    private void ClearPlan()
    {
        if (Items.Count > 0 && _planPairKey != null)
        {
            _decisionMemory.Capture(_planPairKey, Items);
        }
        _planPairKey = null;
        IsEditingSetup = false;
        foreach (var item in Items.Where(i => i.IsConflict))
        {
            item.PropertyChanged -= OnConflictItemChanged;
        }
        Items.Clear();
        ScanWarnings.Clear();
        _lastScanWarnings = Array.Empty<string>();
        _lastDestinationFileCount = 0;
        _lastSourceFileCount = 0;
        SelectedItem = null;
        FolderRoots = Array.Empty<FolderNode>();
        SelectedFolder = null;
        ActiveFilter = ItemFilter.Changes;
        UpdateCounts();
        ApplyFilter();
        NotifyScreens();
    }

    private void OnConflictItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SyncItem.Resolution) && !IsRunningSync)
        {
            UpdateDecisionCounts();
        }
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
        OnPropertyChanged(nameof(CanManageProfiles));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsEmptyStateVisible));
        OnPropertyChanged(nameof(EmptyStateText));
        ScanCommand.NotifyCanExecuteChanged();
        StartSyncCommand.NotifyCanExecuteChanged();
        OpenTrashFolderCommand.NotifyCanExecuteChanged();
        OpenOldFolderCommand.NotifyCanExecuteChanged();
        RestoreTrashCommand.NotifyCanExecuteChanged();
        ExportReportCommand.NotifyCanExecuteChanged();
        NewProfileCommand.NotifyCanExecuteChanged();
        DeleteProfileCommand.NotifyCanExecuteChanged();
        EditSetupCommand.NotifyCanExecuteChanged();
        SaveScheduleCommand.NotifyCanExecuteChanged();
        FixScheduleCommand.NotifyCanExecuteChanged();
    }

    // ------------------------------------------------------------------
    // Origem e destino
    // ------------------------------------------------------------------

    /// <summary>
    /// Calcula o caminho efetivo do diretório de destino.
    /// Quando <see cref="UseSourceFolderNameInDestination"/> está ativo (ou quando o destino é a raiz de um disco, ex.: <c>G:\</c>),
    /// anexa automaticamente o nome da pasta de origem caso ainda não conste no final do caminho de destino.
    /// </summary>
    /// <param name="sourcePath">Caminho da pasta de Origem.</param>
    /// <param name="destPath">Caminho da pasta de Destino informada.</param>
    /// <returns>Caminho de destino resolvido com segurança.</returns>
    public string ResolveEffectiveDestinationPath(string sourcePath, string destPath) =>
        PathHelper.ResolveEffectiveDestination(sourcePath, destPath, UseSourceFolderNameInDestination);

    /// <summary>
    /// Abre o seletor de pastas para escolha do diretório de Origem.
    /// </summary>
    [RelayCommand]
    private async Task BrowseSourceAsync()
    {
        try
        {
            if (PickFolderAsync != null)
            {
                var folder = await PickFolderAsync(SourcePath);
                if (!string.IsNullOrEmpty(folder))
                {
                    SourcePath = folder;
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erro ao selecionar pasta de origem: {ErrorMessages.Translate(ex)}";
            AddLog($"[ERRO] {ErrorMessages.ForLog(ex)}");
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
                    DestinationPath = folder;
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erro ao selecionar pasta de destino: {ErrorMessages.Translate(ex)}";
            AddLog($"[ERRO] {ErrorMessages.ForLog(ex)}");
        }
    }

    /// <summary>
    /// Volta para a tela de configuração da tarefa sem descartar o plano: ele só é descartado se algum caminho ou
    /// opção mudar, e as decisões de conflito já tomadas são reaplicadas na próxima análise.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanManageProfiles))]
    private void EditSetup()
    {
        IsShowingResult = false;
        IsEditingSetup = Items.Count > 0;
        StatusMessage = IsEditingSetup
            ? "Nada mudou ainda: \"Voltar ao plano\" retoma o plano como estava."
            : "Ajuste as pastas ou as opções e analise novamente.";
        NotifyCommands();
    }

    /// <summary>
    /// Sai da configuração e retoma o plano guardado, com as decisões como estavam.
    /// </summary>
    [RelayCommand]
    private void ReturnToPlan()
    {
        if (Items.Count == 0) return;
        IsEditingSetup = false;
        StatusMessage = "Plano retomado.";
    }

    /// <summary>
    /// Converte o texto de exclusões em lista de padrões.
    /// </summary>
    private IReadOnlyList<string> ParseExcludePatterns()
    {
        return ScanOptions.ParsePatterns(ExcludePatternsText);
    }

    // ------------------------------------------------------------------
    // Análise
    // ------------------------------------------------------------------

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

        ExpandPaths();
        var effectiveDest = EffectiveDestinationPath;

        SaveSettings();
        ClearPlan();
        IsShowingResult = false;
        LastResult = null;
        LastRunCancelled = false;

        IsScanning = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Analisando diretórios e identificando diferenças...";
        OverallProgress = 0;
        CurrentOperationText = "Lendo origem e destino...";
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
                effectiveDest,
                options,
                new InlineProgress<string>(AddLog),
                _cts!.Token);

            Items.AddRange(scan.Items);
            Items.Sort((a, b) => NaturalStringComparer.Instance.Compare(a.RelativePath, b.RelativePath));
            _planPairKey = DecisionMemory.PairKey(SourcePath, effectiveDest);
            int restored = _decisionMemory.Apply(_planPairKey, Items);
            if (restored > 0)
            {
                AddLog(restored == 1
                    ? "1 decisão de conflito da análise anterior foi mantida (o arquivo não mudou)."
                    : $"{restored} decisões de conflito da análise anterior foram mantidas (os arquivos não mudaram).");
            }
            foreach (var item in Items.Where(i => i.IsConflict))
            {
                item.PropertyChanged += OnConflictItemChanged;
            }
            _lastDestinationFileCount = scan.DestinationFileCount;
            _lastSourceFileCount = scan.SourceFileCount;
            _lastScanWarnings = scan.Warnings.ToList();
            foreach (var warning in scan.Warnings)
            {
                ScanWarnings.Add(warning);
            }

            BuildFolderTree();
            UpdateCounts();
            ApplyFilter();
            // Nenhum conflito é selecionado sozinho: o painel de decisão só abre no arquivo que o usuário escolher
            SelectedItem = null;

            StatusMessage = $"Análise concluída: {NewCount} novos, {ConflictCount} conflitos, {TrashCount} órfãos p/ lixo/, {IdenticalCount} idênticos" +
                            (scan.Warnings.Count > 0 ? $" · {PtText.Count(scan.Warnings.Count, "aviso", "avisos")}." : ".");
            CurrentOperationText = "Pronto para sincronizar.";
            SelectedProfile?.ShowPlanReady(NewCount + ConflictCount + TrashCount, AskDecisionCount);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Análise cancelada.";
            CurrentOperationText = "Cancelado.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erro durante a análise: {ErrorMessages.Translate(ex)}";
            AddLog($"[ERRO] {ErrorMessages.ForLog(ex)}");
        }
        finally
        {
            _logQueue.Flush();
            IsProgressIndeterminate = false;
            IsScanning = false;
            NotifyScreens();
            NotifyCommands();
        }
    }

    private void BuildFolderTree()
    {
        var rootName = SyncProfile.SuggestName(SourcePath);
        var root = FolderTreeBuilder.Build(Items, rootName);
        FolderRoots = new[] { root };
        SelectedFolder = root;
    }

    // ------------------------------------------------------------------
    // Sincronização
    // ------------------------------------------------------------------

    /// <summary>
    /// Monta o resumo do plano atual para confirmação.
    /// </summary>
    public SyncPlanSummary BuildPlanSummary()
    {
        var pending = Items.Where(i => !i.IsProcessed).ToList();
        var conflicts = pending.Where(i => i.IsConflict).ToList();
        return new SyncPlanSummary
        {
            SourcePath = SourcePath.Trim(),
            DestinationPath = EffectiveDestinationPath,
            DestinationNewerReplacements = conflicts
                .Where(i => i.Resolution == ConflictResolution.Replace && i.IsDestinationNewer)
                .Select(i => i.RelativePath)
                .ToList(),
            NewCount = pending.Count(i => i.IsNew),
            NewBytes = pending.Where(i => i.IsNew).Sum(i => i.SourceSize),
            ReplaceCount = conflicts.Count(i => i.Resolution == ConflictResolution.Replace),
            ReplaceBytes = conflicts.Where(i => i.Resolution == ConflictResolution.Replace).Sum(i => i.SourceSize),
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

        // Mesma trava do agendador: impede que a interface e uma execução agendada sincronizem a tarefa ao mesmo tempo
        using var runLock = _persistSettings && SelectedProfile != null ? TaskLock.TryAcquire(SelectedProfile.Profile.Id) : null;
        if (_persistSettings && SelectedProfile != null && runLock == null)
        {
            const string busy = "Esta tarefa está sendo sincronizada agora por um agendamento. Aguarde terminar e analise de novo.";
            StatusMessage = busy;
            AddLog($"[AVISO] {busy}");
            if (NotifyAsync != null) await NotifyAsync("Tarefa em execução", busy);
            return;
        }

        var pending = Items.Where(i => !i.IsProcessed && !i.IsIdentical).ToList();
        _runCopyTotal = pending.Count(i => i.IsNew);
        _runCopyBytes = pending.Where(i => i.IsNew).Sum(i => i.SourceSize);
        _runReplaceTotal = pending.Count(i => i.IsConflict);
        _runTrashTotal = pending.Count(i => i.IsOrphan);
        _runCopyDone = _runReplaceDone = _runTrashDone = 0;
        UpdatePhaseTexts();

        Logs.Clear();
        ProblemLogs.Clear();
        OnPropertyChanged(nameof(ProblemLogCount));
        ShowOnlyProblems = false;

        IsSyncing = true;
        IsRunningSync = true;
        IsShowingResult = false;
        LastRunCancelled = false;
        StatusMessage = "Sincronização em andamento...";
        OverallProgress = 0;
        ProgressPercentText = "0%";
        ProcessedBytesText = "Volume: —";
        ProcessedFilesText = "Arquivos: —";
        SpeedFormatted = "—";
        EtaFormatted = "—";
        CurrentOperationText = "Preparando...";
        SelectedProfile?.ShowRunning(0);
        ResetCts();

        var runStopwatch = Stopwatch.StartNew();
        var progress = new Progress<SyncProgressReport>(report =>
        {
            // 100% só quando todos os arquivos terminaram (um conflito aguardando decisão não pode aparecer como concluído)
            double percent = CapPercent(report.OverallProgressPercent, report.ProcessedFiles, report.TotalFiles);
            OverallProgress = percent;
            ProgressPercentText = $"{percent:0}%";
            SpeedFormatted = report.SpeedFormatted;
            EtaFormatted = report.EtaFormatted;
            ProcessedFilesText = $"Arquivos: {report.ProcessedFiles:N0} de {report.TotalFiles:N0}";
            ProcessedBytesText = $"Volume: {SyncItem.FormatBytes(report.ProcessedBytes)} de {SyncItem.FormatBytes(report.TotalBytes)}";
            CurrentOperationText = !string.IsNullOrEmpty(report.CurrentFileName)
                ? report.CurrentFileName
                : "Finalizando...";
            SelectedProfile?.ShowRunning(percent);
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
                CurrentOperationText = item.RelativePath;
                WaitingDecisionFile = item.RelativePath;
                IsWaitingForDecision = true;
                (ConflictResolution choice, bool applyToAll) answer;
                try
                {
                    answer = await AskConflictResolutionAsync(item);
                }
                finally
                {
                    IsWaitingForDecision = false;
                    WaitingDecisionFile = string.Empty;
                }
                var (choice, applyToAll) = answer;
                if (applyToAll)
                {
                    rememberedChoice = choice;
                }
                return choice;
            }

            return ConflictResolution.Ignore;
        }

        SyncResult? result = null;
        bool cancelled = false;
        try
        {
            result = await _syncEngine.ExecuteSyncAsync(
                Items,
                EffectiveDestinationPath,
                ResolveConflictHandler,
                _itemUpdateQueue,
                progress,
                new InlineProgress<string>(AddLog),
                _cts!.Token);

            StatusMessage = result.HasFailures
                ? $"Concluída com {PtText.Count(result.Failed, "falha", "falhas")}: {result.Summary}."
                : $"Sincronização concluída sem falhas: {result.Summary}.";
            CurrentOperationText = "Operações finalizadas.";
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            StatusMessage = "Sincronização interrompida pelo usuário. Nenhum arquivo parcial foi deixado no destino.";
            CurrentOperationText = "Cancelado.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erro durante a sincronização: {ErrorMessages.Translate(ex)}";
            AddLog($"[ERRO CRÍTICO] {ErrorMessages.ForLog(ex)}");
        }
        finally
        {
            _itemUpdateQueue.Flush();
            _logQueue.Flush();
            IsSyncing = false;
            UpdateCounts();
            ApplyFilter();
        }

        // Balanço exibido mesmo quando a execução foi interrompida: monta-se a partir do estado dos itens
        runStopwatch.Stop();
        if (result == null)
        {
            result = BuildResultFromItems(pending);
            result.Duration = runStopwatch.Elapsed;
        }
        LastRunCancelled = cancelled;
        LastResult = result;
        RecordRunOnProfile(result, cancelled);
        IsShowingResult = true;
        IsRunningSync = false;
        NotifyCommands();
    }

    /// <summary>
    /// Reconstrói um balanço aproximado a partir dos itens quando o motor não devolveu resultado (cancelamento ou erro).
    /// </summary>
    private static SyncResult BuildResultFromItems(IReadOnlyList<SyncItem> attempted)
    {
        var result = new SyncResult();
        foreach (var item in attempted)
        {
            if (item.HasError)
            {
                result.FailedItems.Add(item);
                continue;
            }
            if (!item.IsProcessed) continue;
            switch (item.ActionType)
            {
                case SyncActionType.New:
                    result.Copied++;
                    result.BytesCopied += item.SourceSize;
                    break;
                case SyncActionType.Conflict when item.Resolution == ConflictResolution.Replace:
                    result.Replaced++;
                    result.BytesCopied += item.SourceSize;
                    break;
                case SyncActionType.Conflict:
                    result.Ignored++;
                    break;
                case SyncActionType.OrphanTrash:
                    result.Trashed++;
                    break;
            }
        }
        return result;
    }

    private void RecordRunOnProfile(SyncResult result, bool cancelled)
    {
        if (SelectedProfile == null) return;
        var p = SelectedProfile.Profile;
        p.LastRunUtc = DateTime.UtcNow;
        p.LastOutcome = cancelled ? ProfileRunOutcome.Cancelled : result.HasFailures ? ProfileRunOutcome.Failures : ProfileRunOutcome.Success;
        p.LastFailedCount = result.Failed;
        AppendManualRunToHistory(p, result, cancelled);
        SelectedProfile.RefreshFromLastRun();
        SaveSettings();
        RefreshScheduleStatus();
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
    /// Sai da tela de resultado e volta ao plano (com o estado de cada item após a execução).
    /// </summary>
    [RelayCommand]
    private void BackToPlan()
    {
        IsShowingResult = false;
        if (Items.Count > 0)
        {
            ActiveFilter = ErrorCount > 0 ? ItemFilter.Errors : ItemFilter.Changes;
        }
    }

    // ------------------------------------------------------------------
    // Filtros e decisões
    // ------------------------------------------------------------------

    /// <summary>
    /// Fecha o painel de detalhes.
    /// </summary>
    [RelayCommand]
    private void ClearSelection() => SelectedItem = null;

    /// <summary>
    /// Alterna o registro da tela de execução entre "tudo" e "só avisos e erros".
    /// </summary>
    /// <param name="onlyProblems"><c>true</c> para mostrar só avisos e erros.</param>
    [RelayCommand]
    private void SetShowOnlyProblems(bool onlyProblems) => ShowOnlyProblems = onlyProblems;

    /// <summary>
    /// Volta à visão "Todas": sem filtro de categoria, na pasta raiz e sem busca.
    /// </summary>
    [RelayCommand]
    private void ClearListRefinement()
    {
        SearchText = string.Empty;
        SelectedFolder = FolderRoots.FirstOrDefault();
        ActiveFilter = ItemFilter.Changes;
    }

    /// <summary>
    /// Alterna o filtro de categoria: clicar de novo no filtro ativo volta para "Todas".
    /// </summary>
    /// <param name="filter">Filtro a aplicar.</param>
    [RelayCommand]
    private void SetFilter(ItemFilter filter)
    {
        ActiveFilter = ActiveFilter == filter && filter != ItemFilter.Changes ? ItemFilter.Changes : filter;
    }

    /// <summary>
    /// Filtra <see cref="Items"/> por categoria, pasta e busca, e atualiza atomicamente <see cref="FilteredItems"/>.
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
            ItemFilter.Changes => Items.Where(i => !i.IsIdentical || i.HasError),
            _ => Items
        };

        var folder = SelectedFolder;
        if (folder != null && folder.RelativePath.Length > 0)
        {
            source = source.Where(folder.Contains);
        }

        var search = TextSearch.Fold(SearchText?.Trim());
        if (search.Length > 0)
        {
            source = source.Where(i => TextSearch.ContainsFolded(i.RelativePath, search));
        }

        var list = source.ToList();
        int prefix = folder == null || folder.RelativePath.Length == 0 ? 0 : folder.RelativePath.Length + 1;
        foreach (var item in list)
        {
            item.DisplayPath = prefix > 0 && item.RelativePath.Length > prefix ? item.RelativePath[prefix..] : item.RelativePath;
        }
        FilteredItems = list;
        if (SelectedItem != null && !FilteredItems.Contains(SelectedItem))
        {
            SelectedItem = null;
        }
    }

    /// <summary>
    /// Recalcula os totalizadores do resumo.
    /// </summary>
    private void UpdateCounts()
    {
        NewCount = Items.Count(i => i.IsNew);
        IdenticalCount = Items.Count(i => i.IsIdentical);
        ConflictCount = Items.Count(i => i.IsConflict);
        TrashCount = Items.Count(i => i.IsOrphan);
        ErrorCount = Items.Count(i => i.HasError);
        var newBytes = Items.Where(i => i.IsNew).Sum(i => i.SourceSize);
        NewBytesFormatted = SyncItem.FormatBytes(newBytes);

        int changes = ChangeCount;
        OnPropertyChanged(nameof(ChangeCount));
        long transfer = newBytes + Items.Where(i => i.IsConflict).Sum(i => i.SourceSize);
        PlanHeadline = changes == 0
            ? "Nada a sincronizar"
            : changes == 1 ? "1 alteração planejada" : $"{changes:N0} alterações planejadas";
        PlanSubline = $"Até {SyncItem.FormatBytes(transfer)} a transferir · {IdenticalCount:N0} idênticos não serão tocados";
        SourceStatsText = _lastSourceFileCount > 0 ? $"{_lastSourceFileCount:N0} arquivos indexados" : string.Empty;
        DestinationStatsText = _lastDestinationFileCount > 0 ? $"{_lastDestinationFileCount:N0} arquivos indexados" : string.Empty;
        UpdateDecisionCounts();
    }

    private void UpdateDecisionCounts()
    {
        var pending = Items.Where(i => i.IsConflict && !i.IsProcessed).ToList();
        ReplaceDecisionCount = pending.Count(i => i.Resolution == ConflictResolution.Replace);
        KeepDecisionCount = pending.Count(i => i.Resolution == ConflictResolution.Ignore);
        AskDecisionCount = pending.Count(i => i.Resolution == ConflictResolution.AskUser);
        OnPropertyChanged(nameof(PlanFooterText));
        if (Items.Count > 0 && !IsBusy && !IsShowingResult)
        {
            SelectedProfile?.ShowPlanReady(NewCount + ConflictCount + TrashCount, AskDecisionCount);
        }
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
        AddLog("Todos os conflitos foram marcados para 'Manter destino'.");
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
        AddLog($"Política 'mais recente vence' aplicada: {replace} para substituir, {ignore} para manter.");
    }

    // ------------------------------------------------------------------
    // Restauração, relatório e pastas
    // ------------------------------------------------------------------

    /// <summary>
    /// Lista o conteúdo de <c>lixo/</c>, permite ao usuário escolher os arquivos e os devolve ao local original
    /// sem sobrescrever nem excluir nada.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRestoreTrash))]
    private async Task RestoreTrashAsync()
    {
        if (string.IsNullOrWhiteSpace(DestinationPath)) return;

        var dest = EffectiveDestinationPath;
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
            StatusMessage = $"Restaurando {PtText.Count(selected.Count, "arquivo", "arquivos")}...";
            var result = await _restoreService.RestoreAsync(dest, selected, new InlineProgress<string>(AddLog), _cts.Token);

            // Os arquivos do destino mudaram: a análise anterior deixa de valer
            ClearPlan();
            IsShowingResult = false;
            StatusMessage = DescribeRestoreResult(result.Restored, result.KeptInTrash, result.Failed);
            CurrentOperationText = "Restauração concluída.";
            if (NotifyAsync != null)
            {
                await NotifyAsync("Restauração concluída", StatusMessage);
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Restauração cancelada.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erro ao restaurar lixo/: {ErrorMessages.Translate(ex)}";
            AddLog($"[ERRO] {ErrorMessages.ForLog(ex)}");
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
            StatusMessage = $"Erro ao exportar relatório: {ErrorMessages.Translate(ex)}";
            AddLog($"[ERRO] Exportação de relatório: {ErrorMessages.ForLog(ex)}");
        }
    }

    /// <summary>
    /// Abre a pasta <c>destino/lixo/</c> no gerenciador de arquivos do sistema operacional.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenDestinationFolders))]
    private Task OpenTrashFolderAsync() => OpenDestinationSubfolderAsync(PathHelper.TrashFolderName);

    /// <summary>
    /// Abre a pasta <c>destino/old/</c> no gerenciador de arquivos do sistema operacional.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenDestinationFolders))]
    private Task OpenOldFolderAsync() => OpenDestinationSubfolderAsync(PathHelper.OldFolderName);

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
    /// Nome do gerenciador de arquivos do sistema, para os rótulos dos botões.
    /// </summary>
    public static string FileManagerName => OperatingSystem.IsWindows() ? "Explorer" : OperatingSystem.IsMacOS() ? "Finder" : "gerenciador de arquivos";

    /// <summary>Rótulo dos botões que abrem origem e destino no gerenciador de arquivos.</summary>
    public string OpenInFileManagerText => $"Abrir no {FileManagerName}";

    /// <summary>
    /// Abre a pasta de origem ou de destino no gerenciador de arquivos. Para o destino, abre o destino efetivo;
    /// se ele ainda não existe (primeira sincronização), abre a pasta escolhida e avisa.
    /// </summary>
    /// <param name="side"><c>source</c> para a origem; qualquer outro valor para o destino.</param>
    [RelayCommand]
    private async Task OpenSideFolderAsync(string? side)
    {
        bool isSource = side == "source";
        var path = (isSource ? SourcePath : EffectiveDestinationPath).Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusMessage = isSource ? "Escolha a origem primeiro." : "Escolha o destino primeiro.";
            return;
        }
        if (Directory.Exists(path))
        {
            OpenInFileManager(path);
            return;
        }

        var chosen = DestinationPath.Trim();
        if (!isSource && Directory.Exists(chosen))
        {
            // Primeira sincronização: a subpasta com o nome da origem ainda não foi criada
            OpenInFileManager(chosen);
            StatusMessage = $"A subpasta {path} ainda não existe; ela é criada na primeira sincronização. Foi aberta a pasta escolhida.";
            return;
        }

        StatusMessage = $"A pasta {path} não existe ou não está acessível.";
        if (NotifyAsync != null)
        {
            await NotifyAsync("Pasta não encontrada", StatusMessage);
        }
    }

    /// <summary>
    /// Abre, no gerenciador de arquivos, a pasta que contém o arquivo selecionado.
    /// </summary>
    /// <param name="side"><c>source</c> para a origem; qualquer outro valor para o destino.</param>
    [RelayCommand]
    private void OpenItemLocation(string? side)
    {
        var item = SelectedItem;
        if (item == null) return;
        var path = side == "source" ? item.SourceFullPath : item.DestinationFullPath;
        if (string.IsNullOrWhiteSpace(path)) return;

        var folder = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            StatusMessage = "A pasta deste arquivo não existe (ainda).";
            return;
        }
        OpenInFileManager(folder);
    }

    /// <summary>
    /// Abre a subpasta reservada (<c>lixo</c> ou <c>old</c>) do destino efetivo. Não cria a pasta: se ela ainda não
    /// existe, avisa que será criada pela primeira sincronização que precisar dela.
    /// </summary>
    private async Task OpenDestinationSubfolderAsync(string subFolder)
    {
        if (string.IsNullOrWhiteSpace(DestinationPath))
        {
            StatusMessage = "Por favor, selecione primeiro o diretório de destino.";
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(Path.Combine(EffectiveDestinationPath, subFolder));
            if (Directory.Exists(fullPath))
            {
                OpenInFileManager(fullPath);
                return;
            }

            var purpose = subFolder == PathHelper.TrashFolderName
                ? "Ela é criada na primeira sincronização que mover para lá arquivos que existem só no destino."
                : "Ela é criada na primeira sincronização que substituir um arquivo, para guardar a versão anterior.";
            var message = $"A pasta {subFolder}/ ainda não existe em {EffectiveDestinationPath}. {purpose}";
            StatusMessage = message;
            if (NotifyAsync != null)
            {
                await NotifyAsync($"A pasta {subFolder}/ ainda não existe", message);
            }
        }
        catch (Exception ex)
        {
            AddLog($"[ERRO] Não foi possível abrir a pasta: {ErrorMessages.ForLog(ex)}");
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
            AddLog($"[ERRO] Não foi possível abrir a pasta: {ErrorMessages.ForLog(ex)}");
        }
    }

    /// <summary>
    /// Limita o percentual a 99% enquanto houver arquivos por terminar.
    /// </summary>
    /// <param name="percent">Percentual calculado pelo motor (por bytes).</param>
    /// <param name="processedFiles">Arquivos concluídos.</param>
    /// <param name="totalFiles">Total de arquivos da execução.</param>
    public static double CapPercent(double percent, int processedFiles, int totalFiles) =>
        processedFiles < totalFiles ? Math.Min(percent, 99) : percent;

    /// <summary>
    /// Escolhe a forma singular ou plural conforme a quantidade ("1 arquivo", "2 arquivos").
    /// </summary>
    public static string Plural(int count, string singular, string plural) => PtText.Count(count, singular, plural);

    /// <summary>
    /// Texto do resultado da restauração de lixo/, com concordância de número.
    /// </summary>
    /// <param name="restored">Arquivos devolvidos ao destino.</param>
    /// <param name="keptInTrash">Arquivos mantidos em lixo/ porque já existiam no destino.</param>
    /// <param name="failed">Falhas.</param>
    public static string DescribeRestoreResult(int restored, int keptInTrash, int failed)
    {
        var parts = new List<string> { Plural(restored, "arquivo restaurado", "arquivos restaurados") };
        if (keptInTrash > 0)
        {
            parts.Add(Plural(keptInTrash, "mantido", "mantidos") + " em lixo/ (já " + (keptInTrash == 1 ? "existia" : "existiam") + " no destino)");
        }
        if (failed > 0)
        {
            parts.Add(Plural(failed, "falha", "falhas"));
        }
        return string.Join(", ", parts) + ". Analise novamente para ver o estado atual.";
    }

    /// <summary>
    /// Formata uma duração curta (ex.: <c>1 min 04 s</c>, <c>12 s</c>, <c>2 h 05 min</c>).
    /// </summary>
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1) return $"{(int)duration.TotalHours} h {duration.Minutes:D2} min";
        if (duration.TotalMinutes >= 1) return $"{(int)duration.TotalMinutes} min {duration.Seconds:D2} s";
        return $"{Math.Max(0, (int)Math.Round(duration.TotalSeconds))} s";
    }
}
