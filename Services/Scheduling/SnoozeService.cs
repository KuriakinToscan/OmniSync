using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace OmniSync.Services.Scheduling;

/// <summary>
/// O que é preciso para repetir uma execução agendada mais tarde (botão "Adiar" do aviso).
/// </summary>
/// <param name="TaskId">Identificador da tarefa.</param>
/// <param name="TaskName">Nome da tarefa (para a descrição no agendador e no log).</param>
/// <param name="Arguments">Argumentos da execução a repetir (os mesmos da execução agendada que avisou).</param>
/// <param name="StateDirectory">Pasta onde fica o registro do adiamento pendente.</param>
public sealed record SnoozeRequest(Guid TaskId, string TaskName, IReadOnlyList<string> Arguments, string StateDirectory);

/// <summary>
/// Adiamento pendente de uma tarefa: nome do disparo único no agendador e horário previsto (hora local).
/// </summary>
/// <param name="Name">Nome do disparo único (item do Agendador de Tarefas ou unit transitória do systemd).</param>
/// <param name="DueLocal">Horário previsto, em hora local.</param>
public sealed record SnoozeState(string Name, DateTime DueLocal);

/// <summary>
/// Adiamento do aviso de uma execução agendada: registra um disparo único no agendador do sistema que repete a
/// análise (ou a sincronização) mais tarde; a execução repetida só avisa de novo se ainda houver o que avisar.
/// Cada tarefa tem no máximo um adiamento pendente (arquivo <c>snooze/&lt;id&gt;.txt</c> na pasta de dados):
/// adiar de novo substitui o anterior, e uma execução do agendamento normal cancela o pendente.
/// </summary>
public sealed class SnoozeService
{
    /// <summary>Intervalos oferecidos no botão "Adiar".</summary>
    public static readonly TimeSpan[] Options = { TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(4) };

    /// <summary>
    /// Folga para distinguir a execução do próprio adiamento (que começa no horário previsto ou depois, se o
    /// computador estava desligado) de uma execução do agendamento normal que chega antes dele.
    /// </summary>
    public static readonly TimeSpan DueTolerance = TimeSpan.FromMinutes(1);

    private readonly ISystemScheduler _scheduler;
    private readonly string _stateDirectory;

    /// <summary>
    /// Cria o serviço.
    /// </summary>
    /// <param name="scheduler">Agendador do sistema.</param>
    /// <param name="stateDirectory">Pasta dos registros de adiamento.</param>
    public SnoozeService(ISystemScheduler scheduler, string stateDirectory)
    {
        _scheduler = scheduler;
        _stateDirectory = stateDirectory;
    }

    /// <summary>Pasta padrão dos registros de adiamento.</summary>
    public static string DefaultDirectory => Path.Combine(AppPaths.DataDirectory, "snooze");

    /// <summary>
    /// Nome do disparo único: id da tarefa e horário previsto, para não colidir com o disparo em execução
    /// (adiar de novo a partir do aviso de um adiamento).
    /// </summary>
    /// <param name="taskId">Identificador da tarefa.</param>
    /// <param name="due">Horário previsto.</param>
    public static string BuildName(Guid taskId, DateTime due) =>
        $"{taskId:D}-adiado-{due.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Texto do intervalo para o menu e o log ("15 minutos", "1 hora", "4 horas").
    /// </summary>
    /// <param name="delay">Intervalo.</param>
    public static string Describe(TimeSpan delay) =>
        delay.TotalMinutes < 60
            ? $"{delay.TotalMinutes:0} minutos"
            : PtText.Count((int)delay.TotalHours, "hora", "horas");

    /// <summary>
    /// Adia por um intervalo: cancela o adiamento pendente da tarefa, se houver, e registra um disparo único em
    /// <paramref name="now"/> + <paramref name="delay"/>.
    /// </summary>
    /// <param name="request">Execução a repetir.</param>
    /// <param name="executable">Executável que o agendador vai chamar.</param>
    /// <param name="now">Momento atual (hora local).</param>
    /// <param name="delay">Intervalo escolhido.</param>
    /// <returns>Mensagem de erro em português, ou <c>null</c> em caso de sucesso.</returns>
    public string? Snooze(SnoozeRequest request, string executable, DateTime now, TimeSpan delay) =>
        Register(request, executable, TruncateToSecond(now + delay), $"por {Describe(delay)}");

    /// <summary>
    /// Adia até um horário escolhido no relógio ("Escolher horário…"): a próxima ocorrência desse horário
    /// (hoje, se ainda não passou; senão amanhã), ver <see cref="NextOccurrence"/>.
    /// </summary>
    /// <param name="request">Execução a repetir.</param>
    /// <param name="executable">Executável que o agendador vai chamar.</param>
    /// <param name="now">Momento atual (hora local).</param>
    /// <param name="timeOfDay">Horário escolhido (horas e minutos).</param>
    /// <returns>Mensagem de erro em português, ou <c>null</c> em caso de sucesso.</returns>
    public string? SnoozeUntil(SnoozeRequest request, string executable, DateTime now, TimeSpan timeOfDay) =>
        Register(request, executable, NextOccurrence(now, timeOfDay), "para horário escolhido");

    /// <summary>
    /// Próxima ocorrência de um horário do dia: hoje, se ainda está à frente de <paramref name="now"/>; senão,
    /// amanhã. O mesmo minuto de agora conta como passado (vira amanhã), para não disparar na hora.
    /// </summary>
    /// <param name="now">Momento atual (hora local).</param>
    /// <param name="timeOfDay">Horário (horas e minutos; segundos ignorados).</param>
    public static DateTime NextOccurrence(DateTime now, TimeSpan timeOfDay)
    {
        var today = now.Date + new TimeSpan(timeOfDay.Hours, timeOfDay.Minutes, 0);
        var currentMinute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Kind);
        return today > currentMinute ? today : today.AddDays(1);
    }

    /// <summary>
    /// Cancela o adiamento pendente e registra o disparo único em <paramref name="due"/>.
    /// </summary>
    private string? Register(SnoozeRequest request, string executable, DateTime due, string how)
    {
        Cancel(request.TaskId);
        var name = BuildName(request.TaskId, due);
        var error = _scheduler.RegisterOnce(name, $"OmniSync: {request.TaskName} (adiado)", request.Arguments, executable, due);
        if (error != null)
        {
            FileLogService.Write($"[AGENDA] Adiamento da tarefa \"{request.TaskName}\" não registrado: {error}");
            return error;
        }
        try
        {
            Directory.CreateDirectory(_stateDirectory);
            File.WriteAllText(StatePath(request.TaskId),
                name + "\n" + due.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // O disparo já está no agendador; sem o registro, só não será cancelado automaticamente
            FileLogService.Write($"[AVISO] Registro do adiamento não gravado: {ErrorMessages.Translate(ex)}");
        }
        FileLogService.Write($"[AGENDA] Tarefa \"{request.TaskName}\" adiada {how}, até {due:dd/MM HH:mm}.");
        return null;
    }

    /// <summary>
    /// Lê o adiamento pendente da tarefa, ou <c>null</c> se não houver (ou o registro estiver ilegível).
    /// </summary>
    /// <param name="taskId">Identificador da tarefa.</param>
    public SnoozeState? Read(Guid taskId)
    {
        try
        {
            var path = StatePath(taskId);
            if (!File.Exists(path)) return null;
            var lines = File.ReadAllLines(path);
            if (lines.Length < 2 || string.IsNullOrWhiteSpace(lines[0])) return null;
            if (!DateTime.TryParseExact(lines[1].Trim(), "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var due)) return null;
            return new SnoozeState(lines[0].Trim(), due);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Cancela o adiamento pendente da tarefa, se houver (tarefa excluída, agendamento desligado ou novo adiamento).
    /// </summary>
    /// <param name="taskId">Identificador da tarefa.</param>
    /// <returns>Mensagem de erro em português, ou <c>null</c> em caso de sucesso (inclusive quando não havia).</returns>
    public string? Cancel(Guid taskId)
    {
        var state = Read(taskId);
        string? error = null;
        if (state != null)
        {
            error = _scheduler.UnregisterOnce(state.Name);
            FileLogService.Write(error == null
                ? $"[AGENDA] Adiamento previsto para {state.DueLocal:dd/MM HH:mm} cancelado."
                : $"[AGENDA] Adiamento previsto para {state.DueLocal:dd/MM HH:mm} não cancelado: {error}");
        }
        PathHelper.TryDeleteFile(StatePath(taskId));
        return error;
    }

    /// <summary>
    /// Chamado no início de toda execução agendada da tarefa. Se o adiamento pendente ainda está no futuro, quem
    /// executa é o agendamento normal, e o adiamento é cancelado (a análise atual já o substitui). Se o horário já
    /// chegou, esta é a execução do próprio adiamento: só o registro é apagado (o disparo único se desfaz sozinho).
    /// </summary>
    /// <param name="taskId">Identificador da tarefa.</param>
    /// <param name="now">Momento atual (hora local).</param>
    public void OnScheduledRun(Guid taskId, DateTime now)
    {
        var state = Read(taskId);
        if (state == null) return;
        if (state.DueLocal > now + DueTolerance)
        {
            Cancel(taskId);
        }
        else
        {
            PathHelper.TryDeleteFile(StatePath(taskId));
        }
    }

    private string StatePath(Guid taskId) => Path.Combine(_stateDirectory, $"{taskId:D}.txt");

    private static DateTime TruncateToSecond(DateTime value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, value.Kind);
}
