using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmniSync.Cli;
using OmniSync.Models;
using OmniSync.Services;
using OmniSync.Services.Scheduling;

namespace OmniSync.ViewModels;

/// <summary>
/// Dia da semana marcável no cartão de agendamento.
/// </summary>
public partial class ScheduleDayOption : ObservableObject
{
    /// <summary>Dia.</summary>
    public DayOfWeek Day { get; }

    /// <summary>Abreviação exibida ("Seg", "Ter"...).</summary>
    public string Label { get; }

    /// <summary>Se o dia está marcado.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Cria a opção.</summary>
    public ScheduleDayOption(DayOfWeek day)
    {
        Day = day;
        Label = SyncSchedule.ShortName(day);
    }
}

/// <summary>
/// Parte do <see cref="MainViewModel"/> que cuida do agendamento da tarefa ativa, da instalação na pasta fixa
/// (exigida pelo agendamento) e do histórico de execuções.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Execuções exibidas no cartão de agendamento.</summary>
    public const int VisibleHistoryCount = 5;

    private bool _loadingSchedule;
    private readonly Dictionary<Guid, string> _scheduleProblems = new();

    /// <summary>Agendador do sistema (substituível em testes).</summary>
    public ISystemScheduler Scheduler { get; set; } = SystemScheduler.Current;

    /// <summary>Situação da instalação (substituível em testes).</summary>
    public Func<InstallState> InstallStateProvider { get; set; } = AppInstaller.GetState;

    /// <summary>Executável que o agendador chama (substituível em testes).</summary>
    public Func<string> InstalledExecutableProvider { get; set; } = () => AppInstaller.InstalledExecutable;

    /// <summary>
    /// Delegate injetado pela View: oferece instalar (ou atualizar) na pasta fixa, com o motivo informado.
    /// Retorna <c>true</c> se instalou.
    /// </summary>
    public Func<InstallState, string, Task<bool>>? EnsureInstalledAsync { get; set; }

    /// <summary>Dias da semana do agendamento semanal, de segunda a domingo.</summary>
    public ObservableCollection<ScheduleDayOption> ScheduleDays { get; } = new(
        new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday }
            .Select(d => new ScheduleDayOption(d)));

    /// <summary>Agendamento ligado (no editor; vale após "Salvar agendamento").</summary>
    [ObservableProperty]
    private bool _scheduleEnabled;

    /// <summary>0 = sincronizar; 1 = só analisar e avisar.</summary>
    [ObservableProperty]
    private int _scheduleModeIndex;

    /// <summary>0 = todo dia; 1 = dias da semana; 2 = ao entrar no sistema.</summary>
    [ObservableProperty]
    private int _scheduleFrequencyIndex;

    /// <summary>Horário da execução (último horário válido digitado).</summary>
    private TimeSpan _scheduleTime = new(18, 0, 0);

    /// <summary>Horário como digitado no campo (aceita 18:30, 1830, 7h05, 7).</summary>
    [ObservableProperty]
    private string _scheduleTimeText = "18:00";

    /// <summary>O texto do horário não é um horário válido.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveScheduleCommand))]
    private bool _scheduleTimeInvalid;

    /// <summary>Em conflito, substituir quando a origem for mais nova (senão, manter o destino).</summary>
    [ObservableProperty]
    private bool _scheduleReplaceNewer;

    /// <summary>Há alterações no editor ainda não salvas no agendador.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveScheduleCommand))]
    private bool _scheduleDirty;

    /// <summary>Gravação no agendador em andamento.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveScheduleCommand), nameof(FixScheduleCommand))]
    private bool _isSavingSchedule;

    /// <summary>Estado do agendamento salvo (próxima execução, última agendada).</summary>
    [ObservableProperty]
    private string _scheduleStatusText = string.Empty;

    /// <summary>Erro da última gravação, ou vazio.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScheduleError))]
    private string _scheduleError = string.Empty;

    /// <summary>Problema detectado no agendamento salvo da tarefa ativa (executável apagado, entrada removida), ou vazio.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScheduleWarning))]
    [NotifyCanExecuteChangedFor(nameof(FixScheduleCommand))]
    private string _scheduleWarning = string.Empty;

    /// <summary>Últimas execuções da tarefa ativa (manuais e agendadas), mais nova primeiro.</summary>
    public ObservableCollection<string> RecentRuns { get; } = new();

    /// <summary>Há execuções no histórico.</summary>
    public bool HasRecentRuns => RecentRuns.Count > 0;

    /// <summary>Mostra os dias da semana.</summary>
    public bool ScheduleIsWeekly => ScheduleFrequencyIndex == 1;

    /// <summary>Mostra o horário (não se aplica a "ao entrar no sistema").</summary>
    public bool ScheduleHasTime => ScheduleFrequencyIndex != 2;

    /// <summary>Mostra a política de conflitos (só ao sincronizar).</summary>
    public bool ScheduleIsSync => ScheduleModeIndex == 0;

    /// <summary>Há erro de gravação.</summary>
    public bool HasScheduleError => !string.IsNullOrEmpty(ScheduleError);

    /// <summary>Há problema no agendamento salvo.</summary>
    public bool HasScheduleWarning => !string.IsNullOrEmpty(ScheduleWarning);

    /// <summary>Se a oferta de instalação ao abrir foi recusada com "Não perguntar de novo".</summary>
    public bool InstallOfferDismissed => _settings.InstallOfferDismissed;

    /// <summary>Grava a recusa da oferta de instalação ao abrir.</summary>
    public void DismissInstallOffer()
    {
        _settings.InstallOfferDismissed = true;
        SaveSettings();
    }

    private void InitializeScheduleEditor()
    {
        foreach (var day in ScheduleDays)
        {
            day.PropertyChanged += OnScheduleDayChanged;
        }
        RecentRuns.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRecentRuns));
    }

    private void OnScheduleDayChanged(object? sender, PropertyChangedEventArgs e) => MarkScheduleDirty();

    partial void OnScheduleEnabledChanged(bool value) => MarkScheduleDirty();

    partial void OnScheduleModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(ScheduleIsSync));
        MarkScheduleDirty();
    }

    partial void OnScheduleFrequencyIndexChanged(int value)
    {
        OnPropertyChanged(nameof(ScheduleIsWeekly));
        OnPropertyChanged(nameof(ScheduleHasTime));
        MarkScheduleDirty();
    }

    partial void OnScheduleTimeTextChanged(string value)
    {
        if (SyncSchedule.TryParseTime(value, out var time))
        {
            _scheduleTime = time;
            ScheduleTimeInvalid = false;
        }
        else
        {
            ScheduleTimeInvalid = true;
        }
        MarkScheduleDirty();
    }

    /// <summary>
    /// Reescreve o horário digitado no formato HH:MM (ao sair do campo), quando válido.
    /// </summary>
    public void NormalizeScheduleTimeText()
    {
        if (ScheduleTimeInvalid) return;
        var normalized = $"{_scheduleTime.Hours:00}:{_scheduleTime.Minutes:00}";
        if (ScheduleTimeText == normalized) return;
        bool wasDirty = ScheduleDirty;
        _loadingSchedule = true;
        try
        {
            ScheduleTimeText = normalized;
        }
        finally
        {
            _loadingSchedule = false;
        }
        ScheduleDirty = wasDirty;
    }

    partial void OnScheduleReplaceNewerChanged(bool value) => MarkScheduleDirty();

    private void MarkScheduleDirty()
    {
        if (_loadingSchedule) return;
        ScheduleDirty = true;
        ScheduleError = string.Empty;
        SaveScheduleCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Carrega o agendamento salvo da tarefa ativa no editor e atualiza estado e histórico.
    /// </summary>
    private void LoadScheduleEditor(SyncProfile profile)
    {
        var schedule = profile.Schedule ?? new SyncSchedule();
        _loadingSchedule = true;
        try
        {
            ScheduleEnabled = schedule.Enabled;
            ScheduleModeIndex = schedule.Mode == ScheduleMode.Sync ? 0 : 1;
            ScheduleFrequencyIndex = schedule.Frequency switch
            {
                ScheduleFrequency.Daily => 0,
                ScheduleFrequency.Weekly => 1,
                _ => 2
            };
            ScheduleTimeText = $"{schedule.Hour:00}:{schedule.Minute:00}";
            ScheduleReplaceNewer = schedule.ConflictPolicy == CliConflictPolicy.ReplaceWhenSourceNewer;
            foreach (var day in ScheduleDays)
            {
                day.IsSelected = schedule.Days.Contains(day.Day);
            }
        }
        finally
        {
            _loadingSchedule = false;
        }
        ScheduleDirty = false;
        ScheduleError = string.Empty;
        ScheduleWarning = _scheduleProblems.TryGetValue(profile.Id, out var problem) ? problem : string.Empty;
        RefreshScheduleStatus();
    }

    /// <summary>
    /// Monta o agendamento a partir do editor.
    /// </summary>
    private SyncSchedule BuildScheduleFromEditor()
    {
        var time = _scheduleTime;
        return new SyncSchedule
        {
            Enabled = ScheduleEnabled,
            Mode = ScheduleModeIndex == 0 ? ScheduleMode.Sync : ScheduleMode.AnalyzeAndNotify,
            Frequency = ScheduleFrequencyIndex switch
            {
                0 => ScheduleFrequency.Daily,
                1 => ScheduleFrequency.Weekly,
                _ => ScheduleFrequency.AtLogon
            },
            Hour = time.Hours,
            Minute = time.Minutes,
            Days = ScheduleDays.Where(d => d.IsSelected).Select(d => d.Day).ToList(),
            ConflictPolicy = ScheduleReplaceNewer ? CliConflictPolicy.ReplaceWhenSourceNewer : CliConflictPolicy.Keep
        };
    }

    /// <summary>
    /// Atualiza a linha de estado (próxima e última execução agendada) e a lista de execuções recentes da tarefa ativa.
    /// </summary>
    private void RefreshScheduleStatus()
    {
        RecentRuns.Clear();
        if (SelectedProfile == null)
        {
            ScheduleStatusText = string.Empty;
            return;
        }

        var profile = SelectedProfile.Profile;
        var history = _persistSettings ? RunHistory.Load(profile.Id) : new List<RunRecord>();
        foreach (var record in history.Take(VisibleHistoryCount))
        {
            RecentRuns.Add(record.Display);
        }

        var schedule = profile.Schedule;
        if (schedule is not { Enabled: true })
        {
            ScheduleStatusText = "Desligado. A tarefa só roda quando você clica em Analisar.";
            return;
        }

        var parts = new List<string> { schedule.Describe() + "." };
        var next = schedule.NextRun(DateTime.Now);
        if (next.HasValue)
        {
            parts.Add($"Próxima: {FormatNextRun(next.Value, DateTime.Now)}.");
        }
        var lastScheduled = history.FirstOrDefault(r => r.Scheduled);
        if (lastScheduled != null)
        {
            parts.Add($"Última agendada: {ProfileItemViewModel.FormatWhen(lastScheduled.StartedUtc.ToLocalTime(), DateTime.Now)}, {lastScheduled.Summary}.");
        }
        ScheduleStatusText = string.Join(" ", parts);
    }

    /// <summary>
    /// Formata a próxima execução: "hoje às 18:00", "amanhã às 07:30" ou "sex 07/11 às 18:00".
    /// </summary>
    /// <param name="next">Próxima execução (hora local).</param>
    /// <param name="now">Agora (hora local).</param>
    public static string FormatNextRun(DateTime next, DateTime now)
    {
        var time = next.ToString("HH:mm");
        if (next.Date == now.Date) return $"hoje às {time}";
        if (next.Date == now.Date.AddDays(1)) return $"amanhã às {time}";
        return $"{SyncSchedule.ShortName(next.DayOfWeek).ToLowerInvariant()} {next:dd/MM} às {time}";
    }

    private bool CanSaveSchedule() => ScheduleDirty && !IsSavingSchedule && !IsBusy && !(ScheduleEnabled && ScheduleTimeInvalid && ScheduleHasTime);

    /// <summary>
    /// Grava o agendamento do editor: registra no agendador do sistema (ligado) ou remove (desligado).
    /// Ligar exige o OmniSync instalado na pasta fixa; se não estiver, oferece a instalação.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveSchedule))]
    private async Task SaveScheduleAsync()
    {
        if (SelectedProfile == null) return;
        SaveSettings();
        var item = SelectedProfile;
        var profile = item.Profile;
        var schedule = BuildScheduleFromEditor();
        ScheduleError = string.Empty;

        if (schedule.Enabled)
        {
            if (!HasPaths)
            {
                ScheduleError = "Escolha a origem e o destino antes de agendar.";
                return;
            }
            if (!schedule.IsValid)
            {
                ScheduleError = "Marque pelo menos um dia da semana.";
                return;
            }
        }

        IsSavingSchedule = true;
        try
        {
            if (schedule.Enabled)
            {
                var exe = await ResolveScheduleExecutableAsync();
                if (exe == null) return;
                var error = await Task.Run(() => Scheduler.Register(profile, schedule, exe));
                if (error != null)
                {
                    ScheduleError = error;
                    AddLog($"[ERRO] Agendamento da tarefa \"{profile.Name}\" não gravado: {error}");
                    return;
                }
                schedule.RegisteredExecutable = exe;
                AddLog($"[AGENDA] Tarefa \"{profile.Name}\": {schedule.Describe()}.");
            }
            else
            {
                var error = await Task.Run(() => Scheduler.Unregister(profile.Id));
                if (error != null)
                {
                    ScheduleError = error;
                    AddLog($"[ERRO] Agendamento da tarefa \"{profile.Name}\" não removido: {error}");
                    return;
                }
                AddLog($"[AGENDA] Agendamento da tarefa \"{profile.Name}\" desligado.");
            }

            profile.Schedule = schedule;
            _scheduleProblems.Remove(profile.Id);
            item.RefreshSchedule();
            SaveSettings();
            if (ReferenceEquals(item, SelectedProfile))
            {
                LoadScheduleEditor(profile);
            }
        }
        finally
        {
            IsSavingSchedule = false;
        }
    }

    /// <summary>
    /// Devolve o executável instalado, oferecendo a instalação quando ainda não existe ou é de outra versão.
    /// </summary>
    /// <returns>Caminho do executável, ou <c>null</c> se o usuário não instalou (a mensagem fica em <see cref="ScheduleError"/>).</returns>
    private async Task<string?> ResolveScheduleExecutableAsync()
    {
        var state = InstallStateProvider();
        switch (state)
        {
            case InstallState.NotApplicable:
                ScheduleError = "O agendamento precisa do OmniSync publicado (OmniSync.exe); esta execução é de desenvolvimento.";
                return null;
            case InstallState.RunningInstalled:
            case InstallState.InstalledSame:
                return InstalledExecutableProvider();
        }

        const string reason = "O agendamento chama o OmniSync instalado numa pasta fixa, que não muda quando você baixa uma nova versão.";
        if (EnsureInstalledAsync == null || !await EnsureInstalledAsync(state, reason))
        {
            ScheduleError = "Agendamento não gravado: o OmniSync precisa estar instalado na pasta fixa.";
            return null;
        }
        return InstalledExecutableProvider();
    }

    private bool CanFixSchedule() => HasScheduleWarning && !IsSavingSchedule && !IsBusy;

    /// <summary>
    /// Registra de novo o agendamento salvo da tarefa ativa (após o executável ser apagado ou a entrada removida).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanFixSchedule))]
    private async Task FixScheduleAsync()
    {
        if (SelectedProfile == null) return;
        await FixSchedulesAsync(new[] { SelectedProfile });
    }

    /// <summary>
    /// Verifica os agendamentos ligados de todas as tarefas (executável existe, entrada no agendador existe).
    /// </summary>
    /// <returns>Tarefas com problema e o motivo de cada uma.</returns>
    public async Task<IReadOnlyList<(ProfileItemViewModel Item, string Problem)>> VerifySchedulesAsync()
    {
        var scheduled = Profiles.Where(p => p.Profile.Schedule?.Enabled == true).ToList();
        var problems = new List<(ProfileItemViewModel, string)>();
        if (scheduled.Count == 0) return problems;

        var scheduler = Scheduler;
        var results = await Task.Run(() => scheduled.Select(p => (p, ScheduleHealth.Check(p.Profile, scheduler))).ToList());
        _scheduleProblems.Clear();
        foreach (var (item, problem) in results)
        {
            if (problem == null) continue;
            _scheduleProblems[item.Profile.Id] = $"O agendamento não vai rodar: {problem}.";
            problems.Add((item, problem));
            AddLog($"[AVISO] Agendamento da tarefa \"{item.Name}\": {problem}.");
        }
        if (SelectedProfile != null)
        {
            ScheduleWarning = _scheduleProblems.TryGetValue(SelectedProfile.Profile.Id, out var current) ? current : string.Empty;
        }
        return problems;
    }

    /// <summary>
    /// Registra de novo os agendamentos salvos das tarefas indicadas, apontando para o executável instalado.
    /// </summary>
    /// <param name="items">Tarefas a corrigir.</param>
    /// <returns>Quantidade corrigida.</returns>
    public async Task<int> FixSchedulesAsync(IEnumerable<ProfileItemViewModel> items)
    {
        var list = items.Where(i => i.Profile.Schedule?.Enabled == true).ToList();
        if (list.Count == 0) return 0;
        IsSavingSchedule = true;
        int fixedCount = 0;
        try
        {
            ScheduleError = string.Empty;
            var exe = await ResolveScheduleExecutableAsync();
            if (exe == null) return 0;
            foreach (var item in list)
            {
                var schedule = item.Profile.Schedule!;
                var error = await Task.Run(() => Scheduler.Register(item.Profile, schedule, exe));
                if (error != null)
                {
                    AddLog($"[ERRO] Agendamento da tarefa \"{item.Name}\" não corrigido: {error}");
                    if (ReferenceEquals(item, SelectedProfile)) ScheduleError = error;
                    continue;
                }
                schedule.RegisteredExecutable = exe;
                _scheduleProblems.Remove(item.Profile.Id);
                AddLog($"[AGENDA] Agendamento da tarefa \"{item.Name}\" registrado de novo.");
                fixedCount++;
            }
            SaveSettings();
            if (SelectedProfile != null && !_scheduleProblems.ContainsKey(SelectedProfile.Profile.Id))
            {
                ScheduleWarning = string.Empty;
            }
        }
        finally
        {
            IsSavingSchedule = false;
        }
        return fixedCount;
    }

    /// <summary>
    /// Remove o agendamento e o histórico de uma tarefa excluída. Falhas vão para o registro.
    /// </summary>
    private async Task RemoveScheduleAndHistoryAsync(SyncProfile profile)
    {
        _scheduleProblems.Remove(profile.Id);
        if (!_persistSettings) return;
        if (profile.Schedule?.Enabled == true)
        {
            var error = await Task.Run(() => Scheduler.Unregister(profile.Id));
            AddLog(error == null
                ? $"[AGENDA] Agendamento da tarefa excluída \"{profile.Name}\" removido."
                : $"[ERRO] Agendamento da tarefa excluída \"{profile.Name}\" não removido: {error}");
        }
        RunHistory.Delete(profile.Id);
    }

    /// <summary>
    /// Grava no histórico uma execução feita pela interface.
    /// </summary>
    private void AppendManualRunToHistory(SyncProfile profile, SyncResult result, bool cancelled)
    {
        if (!_persistSettings) return;
        var record = new RunRecord
        {
            StartedUtc = DateTime.UtcNow - result.Duration,
            Duration = result.Duration,
            Scheduled = false,
            Outcome = cancelled ? "interrompido" : result.HasFailures ? "falhas" : "ok",
            Copied = result.Copied,
            Replaced = result.Replaced,
            Trashed = result.Trashed,
            Failed = result.Failed,
            Summary = cancelled ? "Interrompida; nenhum arquivo parcial no destino" : result.Summary
        };
        RunHistory.Append(profile.Id, record);
    }

    /// <summary>
    /// Traz para as tarefas as execuções feitas fora da interface (agendador ou linha de comando) desde a última
    /// leitura. Chamado quando a janela volta a ficar ativa, para que a gravação seguinte do <c>settings.json</c> não
    /// desfaça o "Sincronizado hoje" registrado por elas.
    /// </summary>
    public void RefreshRunsFromHistory()
    {
        if (!_persistSettings) return;
        foreach (var item in Profiles)
        {
            var profile = item.Profile;
            var latest = RunHistory.Load(profile.Id)
                .FirstOrDefault(r => !r.AnalyzeOnly && r.Outcome is "ok" or "falhas" or "interrompido");
            if (latest == null) continue;
            var finishedUtc = latest.StartedUtc + latest.Duration;
            if (profile.LastRunUtc.HasValue && finishedUtc <= profile.LastRunUtc.Value.AddSeconds(1)) continue;

            profile.LastRunUtc = finishedUtc;
            profile.LastOutcome = latest.Outcome switch
            {
                "ok" => ProfileRunOutcome.Success,
                "falhas" => ProfileRunOutcome.Failures,
                _ => ProfileRunOutcome.Cancelled
            };
            profile.LastFailedCount = latest.Failed;
            if (item.Status is not (ProfileStatusKind.Running or ProfileStatusKind.PlanReady))
            {
                item.RefreshFromLastRun();
            }
        }
        if (!IsBusy)
        {
            RefreshScheduleStatus();
        }
    }
}
