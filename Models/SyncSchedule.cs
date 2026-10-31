using System;
using System.Collections.Generic;
using System.Linq;
using OmniSync.Cli;

namespace OmniSync.Models;

/// <summary>
/// O que a execução agendada faz.
/// </summary>
public enum ScheduleMode
{
    /// <summary>Analisa e sincroniza sem perguntas.</summary>
    Sync,
    /// <summary>Só analisa e avisa quando há alterações pendentes.</summary>
    AnalyzeAndNotify
}

/// <summary>
/// Quando a execução agendada acontece.
/// </summary>
public enum ScheduleFrequency
{
    /// <summary>Todo dia no horário escolhido.</summary>
    Daily,
    /// <summary>Nos dias da semana marcados, no horário escolhido.</summary>
    Weekly,
    /// <summary>Alguns minutos depois de o usuário entrar no sistema.</summary>
    AtLogon
}

/// <summary>
/// Agendamento de uma tarefa, registrado no agendador do sistema (Agendador de Tarefas ou timer do systemd).
/// Persistido dentro da tarefa em <c>settings.json</c>; ausente equivale a desligado.
/// </summary>
public sealed class SyncSchedule
{
    /// <summary>Atraso da execução ao entrar no sistema, para não disputar o início da sessão.</summary>
    public const int LogonDelayMinutes = 5;

    /// <summary>Se o agendamento está ligado.</summary>
    public bool Enabled { get; set; }

    /// <summary>Sincronizar ou só analisar e avisar.</summary>
    public ScheduleMode Mode { get; set; } = ScheduleMode.Sync;

    /// <summary>Frequência.</summary>
    public ScheduleFrequency Frequency { get; set; } = ScheduleFrequency.Daily;

    /// <summary>Hora do dia (0–23).</summary>
    public int Hour { get; set; } = 18;

    /// <summary>Minuto (0–59).</summary>
    public int Minute { get; set; }

    /// <summary>Dias da semana, para <see cref="ScheduleFrequency.Weekly"/>.</summary>
    public List<DayOfWeek> Days { get; set; } = new() { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday };

    /// <summary>Política para conflitos sem decisão.</summary>
    public CliConflictPolicy ConflictPolicy { get; set; } = CliConflictPolicy.Keep;

    /// <summary>
    /// Executável registrado no agendador na última gravação. Usado para detectar agendamento que aponta para um
    /// executável apagado ou movido.
    /// </summary>
    public string? RegisteredExecutable { get; set; }

    /// <summary>
    /// Indica se a configuração é executável: semanal precisa de pelo menos um dia; hora e minuto válidos.
    /// </summary>
    public bool IsValid =>
        Hour is >= 0 and <= 23 && Minute is >= 0 and <= 59 &&
        (Frequency != ScheduleFrequency.Weekly || Days.Count > 0);

    /// <summary>
    /// Argumentos da linha de comando que o agendador passa ao executável.
    /// </summary>
    /// <param name="taskId">Identificador da tarefa (o nome pode mudar; o id não).</param>
    public IReadOnlyList<string> BuildArguments(Guid taskId)
    {
        var args = new List<string>
        {
            Mode == ScheduleMode.Sync ? "--sincronizar" : "--analisar",
            taskId.ToString(),
            "--agendado"
        };
        if (Mode == ScheduleMode.Sync)
        {
            args.Add("--conflitos");
            args.Add(ConflictPolicy == CliConflictPolicy.Keep ? "manter" : "origem-mais-nova");
        }
        return args;
    }

    /// <summary>
    /// Próxima execução a partir de <paramref name="now"/> (hora local), ou <c>null</c> para "ao entrar".
    /// </summary>
    /// <param name="now">Momento de referência, em hora local.</param>
    public DateTime? NextRun(DateTime now)
    {
        if (!Enabled || !IsValid || Frequency == ScheduleFrequency.AtLogon) return null;
        var candidate = now.Date.AddHours(Hour).AddMinutes(Minute);
        for (int i = 0; i < 8; i++)
        {
            var day = candidate.AddDays(i);
            if (day <= now) continue;
            if (Frequency == ScheduleFrequency.Daily || Days.Contains(day.DayOfWeek)) return day;
        }
        return null;
    }

    /// <summary>
    /// Descrição curta em português (ex.: "Todo dia às 18:00", "Seg, Qua e Sex às 07:30").
    /// </summary>
    public string Describe()
    {
        var time = $"{Hour:00}:{Minute:00}";
        var action = Mode == ScheduleMode.Sync ? "sincroniza" : "analisa e avisa";
        return Frequency switch
        {
            ScheduleFrequency.Daily => $"Todo dia às {time}, {action}",
            ScheduleFrequency.Weekly => $"{JoinDays(Days)} às {time}, {action}",
            _ => $"Ao entrar no sistema (após {LogonDelayMinutes} min), {action}"
        };
    }

    /// <summary>Cópia independente (para editar sem afetar a tarefa salva).</summary>
    public SyncSchedule Clone() => new()
    {
        Enabled = Enabled,
        Mode = Mode,
        Frequency = Frequency,
        Hour = Hour,
        Minute = Minute,
        Days = Days.ToList(),
        ConflictPolicy = ConflictPolicy,
        RegisteredExecutable = RegisteredExecutable
    };

    /// <summary>Abreviação do dia da semana em português.</summary>
    /// <param name="day">Dia.</param>
    public static string ShortName(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "Seg",
        DayOfWeek.Tuesday => "Ter",
        DayOfWeek.Wednesday => "Qua",
        DayOfWeek.Thursday => "Qui",
        DayOfWeek.Friday => "Sex",
        DayOfWeek.Saturday => "Sáb",
        _ => "Dom"
    };

    private static string JoinDays(IEnumerable<DayOfWeek> days)
    {
        // Semana começando na segunda, como no calendário brasileiro de trabalho
        var ordered = days.Distinct().OrderBy(d => ((int)d + 6) % 7).Select(ShortName).ToList();
        return ordered.Count switch
        {
            0 => "Nenhum dia",
            1 => ordered[0],
            _ => string.Join(", ", ordered.Take(ordered.Count - 1)) + " e " + ordered[^1]
        };
    }
}
