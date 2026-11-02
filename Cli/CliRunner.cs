using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OmniSync.Models;
using OmniSync.Services;
using OmniSync.Services.Scheduling;

namespace OmniSync.Cli;

/// <summary>
/// Executa os comandos de linha de comando sobre as tarefas salvas, sem interface e sem perguntas.
/// Conflitos sem decisão seguem a <see cref="CliConflictPolicy"/>; remoção em massa aborta antes de alterar o destino.
/// Toda linha do motor vai para o log diário; o console recebe o resumo, avisos e erros.
/// </summary>
public sealed class CliRunner
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(5);

    private readonly ISyncEngine _engine;
    private readonly TextWriter _output;
    private readonly string _settingsPath;
    private readonly string _historyDirectory;
    private readonly string _lockDirectory;
    private readonly string _snoozeDirectory;
    private readonly ISystemScheduler _scheduler;

    /// <summary>
    /// Cria o executor.
    /// </summary>
    /// <param name="engine">Motor de sincronização.</param>
    /// <param name="output">Saída de texto (console ou, nos testes, um <see cref="StringWriter"/>).</param>
    /// <param name="settingsPath">Arquivo de configurações com as tarefas salvas.</param>
    /// <param name="scheduler">Agendador do sistema, para cancelar adiamentos (padrão: o da plataforma).</param>
    public CliRunner(ISyncEngine engine, TextWriter output, string settingsPath, ISystemScheduler? scheduler = null)
    {
        _scheduler = scheduler ?? SystemScheduler.Current;
        _engine = engine;
        _output = TextWriter.Synchronized(output);
        _settingsPath = settingsPath;
        // Histórico e travas ficam ao lado do settings.json (a pasta de dados; nos testes, uma pasta temporária)
        var dataDirectory = Path.GetDirectoryName(Path.GetFullPath(settingsPath))!;
        _historyDirectory = Path.Combine(dataDirectory, "history");
        _lockDirectory = Path.Combine(dataDirectory, "locks");
        _snoozeDirectory = Path.Combine(dataDirectory, "snooze");
    }

    /// <summary>
    /// Aviso a exibir ao fim de uma execução agendada, ou <c>null</c> quando não há o que avisar
    /// (ou a execução não foi agendada).
    /// </summary>
    public CliNotification? Notification { get; private set; }

    /// <summary>
    /// Executa o comando.
    /// </summary>
    /// <param name="command">Comando interpretado.</param>
    /// <param name="cancellationToken">Cancelamento (Ctrl+C).</param>
    /// <returns>Código de saída do processo.</returns>
    public async Task<CliExitCode> RunAsync(CliCommand command, CancellationToken cancellationToken)
    {
        try
        {
            switch (command.Action)
            {
                case CliAction.Help:
                    _output.WriteLine(CliCommand.HelpText);
                    return CliExitCode.Success;
                case CliAction.List:
                    return ListTasks();
                default:
                    return await AnalyzeOrRunAsync(command, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            Report("Interrompido. Nenhum arquivo parcial ficou no destino.", alwaysShow: true);
            return CliExitCode.Cancelled;
        }
        catch (Exception ex)
        {
            Report($"[ERRO CRÍTICO] {ErrorMessages.ForLog(ex)}", alwaysShow: true);
            Notify(command, "OmniSync: erro na execução agendada", $"{ErrorMessages.Translate(ex)} Detalhes no log.", problem: true);
            return CliExitCode.UnexpectedError;
        }
    }

    private CliExitCode ListTasks()
    {
        var settings = SettingsService.Load(_settingsPath);
        if (settings.Profiles.Count == 0)
        {
            _output.WriteLine("Nenhuma tarefa salva. Crie uma pela interface.");
            return CliExitCode.Success;
        }

        _output.WriteLine(PtText.Count(settings.Profiles.Count, "tarefa salva:", "tarefas salvas:"));
        foreach (var p in settings.Profiles)
        {
            var dest = PathHelper.ResolveEffectiveDestination(PathHelper.ExpandUserPath(p.SourcePath), PathHelper.ExpandUserPath(p.DestinationPath), p.UseSourceFolderNameInDestination);
            _output.WriteLine();
            _output.WriteLine($"  {p.Name}");
            _output.WriteLine($"    Origem:  {p.SourcePath}");
            _output.WriteLine($"    Destino: {dest}");
            _output.WriteLine($"    Última:  {DescribeLastRun(p)}");
        }
        return CliExitCode.Success;
    }

    private async Task<CliExitCode> AnalyzeOrRunAsync(CliCommand command, CancellationToken cancellationToken)
    {
        var settings = SettingsService.Load(_settingsPath);
        var profile = FindProfile(settings.Profiles, command.TaskName!, out var lookupError);
        if (profile == null)
        {
            _output.WriteLine(lookupError);
            Notify(command, "Agendamento do OmniSync não executado", lookupError!, problem: true);
            return CliExitCode.InvalidInput;
        }

        if (command.Scheduled)
        {
            // Execução agendada: o adiamento pendente desta tarefa perde o sentido (ou é ele mesmo executando)
            new SnoozeService(_scheduler, _snoozeDirectory).OnScheduledRun(profile.Id, DateTime.Now);
        }

        bool run = command.Action == CliAction.Run;
        var started = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        RunRecord NewRecord(string outcome, string summary) => new()
        {
            StartedUtc = started,
            Duration = stopwatch.Elapsed,
            Scheduled = command.Scheduled,
            AnalyzeOnly = !run,
            Outcome = outcome,
            Summary = summary
        };

        string? invalid = null;
        var source = PathHelper.ExpandUserPath(profile.SourcePath);
        var dest = string.Empty;
        if (string.IsNullOrWhiteSpace(profile.SourcePath) || string.IsNullOrWhiteSpace(profile.DestinationPath))
        {
            invalid = $"A tarefa \"{profile.Name}\" não tem origem e destino definidos. Complete-a pela interface.";
        }
        else
        {
            dest = PathHelper.ResolveEffectiveDestination(source, PathHelper.ExpandUserPath(profile.DestinationPath), profile.UseSourceFolderNameInDestination);
            invalid = PathHelper.DescribePairProblem(source, dest)
                      ?? (Directory.Exists(source) ? null : $"A origem não existe ou não está acessível: {source}");
        }
        if (invalid != null)
        {
            _output.WriteLine(invalid);
            RunHistory.Append(profile.Id, NewRecord("erro", invalid), _historyDirectory);
            Notify(command, $"{profile.Name}: não executada", invalid, problem: true);
            return CliExitCode.InvalidInput;
        }

        // Só a sincronização precisa de exclusividade; analisar apenas lê
        using var taskLock = run ? TaskLock.TryAcquire(profile.Id, _lockDirectory) : null;
        if (run && taskLock == null)
        {
            const string busy = "A tarefa já está sendo sincronizada (pela interface ou por outro agendamento). Nada foi feito.";
            Report($"[AVISO] {busy}", alwaysShow: true);
            RunHistory.Append(profile.Id, NewRecord("ocupado", busy), _historyDirectory);
            return CliExitCode.AlreadyRunning;
        }

        Report($"[CLI] {(run ? "Sincronizando" : "Analisando")} a tarefa \"{profile.Name}\"{(command.Scheduled ? " (agendada)" : "")}: {source} -> {dest}", alwaysShow: true);

        var logger = new CliLogger(this, command.Verbose);
        var options = new ScanOptions
        {
            UseDeepHash = profile.EnableDeepHashCheck,
            TolerateDstShift = profile.TolerateDstShift,
            ExcludePatterns = ScanOptions.ParsePatterns(profile.ExcludePatterns)
        };
        var scan = await _engine.ScanDirectoriesAsync(source, dest, options, logger, cancellationToken);
        var items = scan.Items;
        foreach (var warning in scan.Warnings)
        {
            Report($"Aviso: {warning}", alwaysShow: true);
        }

        int replaceCount = ApplyConflictPolicy(items, command.ConflictPolicy);
        WritePlanSummary(items, replaceCount, command.ConflictPolicy);

        int news = items.Count(i => i.IsNew);
        int conflicts = items.Count(i => i.IsConflict);
        int orphans = items.Count(i => i.IsOrphan);
        int pending = news + conflicts + orphans;
        string pendingText = $"{PtText.Count(news, "novo", "novos")}, {PtText.Count(conflicts, "conflito", "conflitos")}, {orphans:N0} para lixo/";

        if (!run)
        {
            bool ok = await WriteReportAsync(command.ReportPath, items);
            var record = NewRecord(ok ? "ok" : "falhas", pending == 0 ? "Nada a sincronizar" : pendingText);
            record.PendingChanges = pending;
            RunHistory.Append(profile.Id, record, _historyDirectory);
            if (pending > 0)
            {
                Notify(command, $"{profile.Name}: há alterações para sincronizar",
                    $"{pendingText}. Abra o OmniSync para revisar e sincronizar.", problem: false,
                    snooze: BuildSnooze(command, profile));
            }
            return ok ? CliExitCode.Success : CliExitCode.Failures;
        }

        if (SyncSafety.IsMassOrphan(orphans, scan.DestinationFileCount))
        {
            var message = $"Sincronização abortada: {PtText.Count(orphans, "arquivo iria", "arquivos iriam")} para lixo/ " +
                          $"de {scan.DestinationFileCount:N0} no destino (remoção em massa). Nada foi alterado. " +
                          "Confira o destino da tarefa e, se estiver certo, sincronize pela interface.";
            Report($"[ERRO] {message}", alwaysShow: true);
            await WriteReportAsync(command.ReportPath, items);
            var aborted = NewRecord("abortado", "Abortada: remoção em massa, nada alterado");
            aborted.PendingChanges = pending;
            RunHistory.Append(profile.Id, aborted, _historyDirectory);
            Notify(command, $"{profile.Name}: sincronização abortada", message, problem: true);
            return CliExitCode.MassRemovalAborted;
        }

        SyncResult? result = null;
        bool cancelled = false;
        try
        {
            result = await _engine.ExecuteSyncAsync(
                items,
                dest,
                _ => Task.FromResult(ConflictResolution.Ignore),
                null,
                new CliProgress(this),
                logger,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        stopwatch.Stop();

        RecordRun(profile.Id, result, cancelled);
        bool reportOk = await WriteReportAsync(command.ReportPath, items);

        if (cancelled)
        {
            RunHistory.Append(profile.Id, NewRecord("interrompido", "Interrompida; nenhum arquivo parcial no destino"), _historyDirectory);
            Report("Interrompido. Nenhum arquivo parcial ficou no destino.", alwaysShow: true);
            return CliExitCode.Cancelled;
        }

        var done = NewRecord(result!.HasFailures ? "falhas" : "ok", result.Summary);
        done.Copied = result.Copied;
        done.Replaced = result.Replaced;
        done.Trashed = result.Trashed;
        done.Failed = result.Failed;
        RunHistory.Append(profile.Id, done, _historyDirectory);

        _output.WriteLine();
        _output.WriteLine($"Resultado em {SyncEngine.FormatElapsed(stopwatch.Elapsed)}: {result.Summary}.");
        foreach (var failed in result.FailedItems.Take(20))
        {
            _output.WriteLine($"  Falhou: {failed.RelativePath} ({failed.StatusMessage})");
        }
        if (result.Failed > 20)
        {
            _output.WriteLine($"  … e mais {result.Failed - 20:N0} (detalhes no log).");
        }
        _output.WriteLine($"Log: {FileLogService.CurrentLogFile}");

        if (result.HasFailures)
        {
            Notify(command, $"{profile.Name}: sincronizada com {PtText.Count(result.Failed, "falha", "falhas")}",
                $"{result.Summary}. Abra o OmniSync para ver os arquivos que falharam.", problem: true,
                snooze: BuildSnooze(command, profile));
        }
        else if (result.Copied + result.Replaced + result.Trashed > 0)
        {
            Notify(command, $"{profile.Name}: sincronizada", result.Summary + ".", problem: false);
        }

        return result.HasFailures || !reportOk ? CliExitCode.Failures : CliExitCode.Success;
    }

    /// <summary>
    /// Prepara o aviso exibido ao fim de uma execução agendada (ignorado fora do agendamento).
    /// </summary>
    private void Notify(CliCommand command, string title, string message, bool problem, SnoozeRequest? snooze = null)
    {
        if (command.Scheduled)
        {
            Notification = new CliNotification(title, message, problem, snooze);
        }
    }

    /// <summary>
    /// Execução a repetir pelo botão "Adiar": a mesma ação e política de conflitos, pelo id da tarefa, como agendada.
    /// </summary>
    private SnoozeRequest BuildSnooze(CliCommand command, SyncProfile profile)
    {
        var args = new List<string>
        {
            command.Action == CliAction.Run ? "--sincronizar" : "--analisar",
            profile.Id.ToString("D"),
            "--agendado"
        };
        if (command.Action == CliAction.Run)
        {
            args.Add("--conflitos");
            args.Add(command.ConflictPolicy == CliConflictPolicy.Keep ? "manter" : "origem-mais-nova");
        }
        return new SnoozeRequest(profile.Id, profile.Name, args, _snoozeDirectory);
    }

    /// <summary>
    /// Define a decisão dos conflitos ainda sem decisão, já que não há ninguém para perguntar.
    /// </summary>
    /// <param name="items">Itens da análise.</param>
    /// <param name="policy">Política escolhida.</param>
    /// <returns>Quantidade de conflitos que serão substituídos.</returns>
    public static int ApplyConflictPolicy(IEnumerable<SyncItem> items, CliConflictPolicy policy)
    {
        int replace = 0;
        foreach (var item in items.Where(i => i.IsConflict && i.Resolution == ConflictResolution.AskUser))
        {
            bool replaceIt = policy == CliConflictPolicy.ReplaceWhenSourceNewer &&
                             item.ConflictDirection == ConflictDirection.SourceNewer;
            item.Resolution = replaceIt ? ConflictResolution.Replace : ConflictResolution.Ignore;
            if (replaceIt) replace++;
        }
        return replace;
    }

    /// <summary>
    /// Encontra a tarefa pelo nome (ignorando maiúsculas e acentos) ou pelo identificador.
    /// </summary>
    /// <param name="profiles">Tarefas salvas.</param>
    /// <param name="query">Nome ou identificador informado.</param>
    /// <param name="error">Mensagem quando nenhuma ou mais de uma tarefa corresponde.</param>
    /// <returns>A tarefa encontrada, ou <c>null</c>.</returns>
    public static SyncProfile? FindProfile(IReadOnlyList<SyncProfile> profiles, string query, out string? error)
    {
        error = null;
        var trimmed = query.Trim();
        if (Guid.TryParse(trimmed, out var id))
        {
            var byId = profiles.FirstOrDefault(p => p.Id == id);
            if (byId != null) return byId;
        }

        var matches = profiles.Where(p => string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
        {
            var folded = TextSearch.Fold(trimmed);
            matches = profiles.Where(p => TextSearch.Fold(p.Name) == folded).ToList();
        }

        if (matches.Count == 1) return matches[0];

        if (matches.Count == 0)
        {
            error = profiles.Count == 0
                ? $"Tarefa \"{trimmed}\" não encontrada: não há tarefas salvas. Crie uma pela interface."
                : $"Tarefa \"{trimmed}\" não encontrada. Tarefas salvas: {string.Join(", ", profiles.Select(p => $"\"{p.Name}\""))}.";
        }
        else
        {
            error = $"Há {matches.Count} tarefas chamadas \"{trimmed}\". Renomeie uma delas pela interface ou use o identificador: " +
                    string.Join(", ", matches.Select(p => p.Id.ToString())) + ".";
        }
        return null;
    }

    private void WritePlanSummary(IReadOnlyList<SyncItem> items, int replaceCount, CliConflictPolicy policy)
    {
        var news = items.Where(i => i.IsNew).ToList();
        var conflicts = items.Where(i => i.IsConflict).ToList();
        int orphans = items.Count(i => i.IsOrphan);
        int identical = items.Count(i => i.IsIdentical);

        _output.WriteLine();
        _output.WriteLine("Plano:");
        _output.WriteLine($"  Novos:      {news.Count,8:N0}  ({SyncItem.FormatBytes(news.Sum(i => i.SourceSize))})");
        _output.WriteLine($"  Conflitos:  {conflicts.Count,8:N0}  ({PtText.Count(replaceCount, "substituído", "substituídos")}, " +
                          $"{PtText.Count(conflicts.Count - replaceCount, "mantido", "mantidos")} no destino; " +
                          $"política: {(policy == CliConflictPolicy.Keep ? "manter" : "origem-mais-nova")})");
        _output.WriteLine($"  Para lixo/: {orphans,8:N0}");
        _output.WriteLine($"  Idênticos:  {identical,8:N0}");
    }

    private async Task<bool> WriteReportAsync(string? path, IEnumerable<SyncItem> items)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        try
        {
            var full = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            await ReportExporter.WriteCsvAsync(full, items);
            Report($"Relatório gravado em {full}", alwaysShow: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Report($"[ERRO] Não foi possível gravar o relatório: {ErrorMessages.ForLog(ex)}", alwaysShow: true);
            return false;
        }
    }

    /// <summary>
    /// Registra a execução na tarefa, relendo as configurações para não desfazer mudanças feitas pela interface.
    /// </summary>
    private void RecordRun(Guid profileId, SyncResult? result, bool cancelled)
    {
        var settings = SettingsService.Load(_settingsPath);
        var profile = settings.Profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile == null) return;
        profile.LastRunUtc = DateTime.UtcNow;
        profile.LastOutcome = cancelled ? ProfileRunOutcome.Cancelled
            : result?.HasFailures == true ? ProfileRunOutcome.Failures
            : ProfileRunOutcome.Success;
        profile.LastFailedCount = result?.Failed ?? 0;
        SettingsService.Save(settings, _settingsPath);
    }

    private static string DescribeLastRun(SyncProfile p)
    {
        if (!p.LastRunUtc.HasValue || p.LastOutcome == ProfileRunOutcome.None) return "nunca sincronizada";
        var when = p.LastRunUtc.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
        return p.LastOutcome switch
        {
            ProfileRunOutcome.Success => $"{when}, sem falhas",
            ProfileRunOutcome.Failures => $"{when}, {PtText.Count(p.LastFailedCount, "falha", "falhas")}",
            ProfileRunOutcome.Cancelled => $"{when}, interrompida",
            _ => when
        };
    }

    /// <summary>
    /// Grava a linha no log diário e, se for o caso, no console.
    /// </summary>
    private void Report(string message, bool alwaysShow)
    {
        FileLogService.Write(message);
        if (alwaysShow)
        {
            _output.WriteLine(message);
        }
    }

    /// <summary>
    /// Registro do motor: tudo no log diário; no console, avisos e erros (ou tudo, com <c>--detalhado</c>).
    /// </summary>
    private sealed class CliLogger : IProgress<string>
    {
        private readonly CliRunner _runner;
        private readonly bool _verbose;

        public CliLogger(CliRunner runner, bool verbose)
        {
            _runner = runner;
            _verbose = verbose;
        }

        public void Report(string value) => _runner.Report(value, _verbose || IsNotable(value));

        private static bool IsNotable(string line) =>
            line.StartsWith("[ERRO", StringComparison.Ordinal) ||
            line.StartsWith("[AVISO", StringComparison.Ordinal) ||
            line.StartsWith("Aviso", StringComparison.Ordinal);
    }

    /// <summary>
    /// Progresso no console a cada <see cref="ProgressInterval"/>, para execuções longas.
    /// </summary>
    private sealed class CliProgress : IProgress<SyncProgressReport>
    {
        private readonly CliRunner _runner;
        private readonly Stopwatch _sinceLast = Stopwatch.StartNew();

        public CliProgress(CliRunner runner) => _runner = runner;

        public void Report(SyncProgressReport value)
        {
            lock (_sinceLast)
            {
                if (_sinceLast.Elapsed < ProgressInterval) return;
                _sinceLast.Restart();
            }
            _runner._output.WriteLine(
                $"  {Math.Min(value.OverallProgressPercent, 99):0}% · Arquivos: {value.ProcessedFiles:N0} de {value.TotalFiles:N0} · " +
                $"Volume: {SyncItem.FormatBytes(value.ProcessedBytes)} de {SyncItem.FormatBytes(value.TotalBytes)}");
        }
    }
}

/// <summary>
/// Aviso ao usuário ao fim de uma execução agendada.
/// </summary>
/// <param name="Title">Título (ex.: "Campo: sincronizada").</param>
/// <param name="Message">Texto do aviso.</param>
/// <param name="IsProblem">Se é um problema (falhas, abortado, erro) e merece destaque.</param>
/// <param name="Snooze">
/// Execução a repetir quando o usuário escolhe "Adiar", ou <c>null</c> quando repetir não mudaria o resultado
/// (sincronizada, remoção em massa abortada, tarefa inválida).
/// </param>
public sealed record CliNotification(string Title, string Message, bool IsProblem, SnoozeRequest? Snooze = null);
