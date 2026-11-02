using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using OmniSync.Models;

namespace OmniSync.Services.Scheduling;

/// <summary>
/// Agendamentos no Linux como timers do systemd do usuário (<c>~/.config/systemd/user/omnisync-&lt;id&gt;.timer</c>
/// e <c>.service</c>). <c>Persistent=true</c> executa um horário perdido assim que o computador volta a ligar.
/// </summary>
public sealed class SystemdTimerScheduler : ISystemScheduler
{
    /// <summary>Pasta das units do usuário.</summary>
    public static string UnitDirectory
    {
        get
        {
            var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrWhiteSpace(config))
            {
                config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            }
            return Path.Combine(config, "systemd", "user");
        }
    }

    /// <summary>Nome base das units da tarefa.</summary>
    /// <param name="taskId">Identificador da tarefa.</param>
    public static string UnitName(Guid taskId) => $"omnisync-{taskId:D}";

    /// <inheritdoc />
    public string? Register(SyncProfile profile, SyncSchedule schedule, string executable)
    {
        var name = UnitName(profile.Id);
        try
        {
            Directory.CreateDirectory(UnitDirectory);
            File.WriteAllText(Path.Combine(UnitDirectory, name + ".service"), BuildServiceUnit(profile.Name, profile.Id, schedule, executable));
            File.WriteAllText(Path.Combine(UnitDirectory, name + ".timer"), BuildTimerUnit(profile.Name, profile.Id, schedule));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Não foi possível gravar o agendamento: {ErrorMessages.Translate(ex)}";
        }

        SystemScheduler.Run("systemctl", new[] { "--user", "daemon-reload" });
        var (code, output) = SystemScheduler.Run("systemctl", new[] { "--user", "enable", "--now", name + ".timer" });
        return code == 0 ? null : $"O systemd recusou o agendamento: {SystemScheduler.FirstLine(output)}";
    }

    /// <inheritdoc />
    public string? Unregister(Guid taskId)
    {
        var name = UnitName(taskId);
        if (!Exists(taskId)) return null;
        SystemScheduler.Run("systemctl", new[] { "--user", "disable", "--now", name + ".timer" });
        PathHelper.TryDeleteFile(Path.Combine(UnitDirectory, name + ".timer"));
        PathHelper.TryDeleteFile(Path.Combine(UnitDirectory, name + ".service"));
        SystemScheduler.Run("systemctl", new[] { "--user", "daemon-reload" });
        return Exists(taskId) ? "Não foi possível remover o agendamento (arquivos do systemd em uso)." : null;
    }

    /// <inheritdoc />
    public bool Exists(Guid taskId) => File.Exists(Path.Combine(UnitDirectory, UnitName(taskId) + ".timer"));

    /// <inheritdoc />
    public string? RegisterOnce(string name, string description, IReadOnlyList<string> arguments, string executable, DateTime when)
    {
        var (code, output) = SystemScheduler.Run("systemd-run", BuildOnceArguments(name, description, arguments, executable, when));
        return code == 0 ? null : $"O systemd recusou o adiamento: {SystemScheduler.FirstLine(output)}";
    }

    /// <inheritdoc />
    public string? UnregisterOnce(string name)
    {
        var unit = OnceUnitName(name);
        // Parar só o timer: se o serviço do adiamento já estiver rodando, ele termina normalmente
        var (code, output) = SystemScheduler.Run("systemctl", new[] { "--user", "stop", unit + ".timer" });
        if (code != 0 && !output.Contains("not loaded", StringComparison.OrdinalIgnoreCase))
        {
            return $"Não foi possível remover o adiamento: {SystemScheduler.FirstLine(output)}";
        }
        return null;
    }

    /// <summary>Nome da unit transitória de um disparo único.</summary>
    /// <param name="name">Nome do disparo.</param>
    public static string OnceUnitName(string name) => $"omnisync-{name}";

    /// <summary>
    /// Argumentos do <c>systemd-run</c> para um disparo único: timer transitório do usuário, no horário exato,
    /// descartado após executar (não sobrevive a um reinício do computador).
    /// </summary>
    public static IReadOnlyList<string> BuildOnceArguments(string name, string description, IReadOnlyList<string> arguments, string executable, DateTime when)
    {
        var args = new List<string>
        {
            "--user",
            $"--unit={OnceUnitName(name)}",
            $"--description={OneLine(description)}",
            $"--on-calendar={when.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}",
            "--timer-property=AccuracySec=1s",
            "--collect",
            "--",
            executable
        };
        args.AddRange(arguments);
        return args;
    }

    /// <summary>
    /// Unit de serviço que executa a tarefa uma vez.
    /// </summary>
    public static string BuildServiceUnit(string taskName, Guid taskId, SyncSchedule schedule, string executable)
    {
        var args = string.Join(' ', schedule.BuildArguments(taskId).Select(Quote));
        return "[Unit]\n" +
               $"Description=OmniSync: {OneLine(taskName)}\n\n" +
               "[Service]\n" +
               "Type=oneshot\n" +
               $"ExecStart={Quote(executable)} {args}\n";
    }

    /// <summary>
    /// Unit de timer com o calendário da tarefa.
    /// </summary>
    public static string BuildTimerUnit(string taskName, Guid taskId, SyncSchedule schedule)
    {
        var when = schedule.Frequency switch
        {
            ScheduleFrequency.AtLogon => $"OnStartupSec={SyncSchedule.LogonDelayMinutes}min\n",
            ScheduleFrequency.Weekly =>
                $"OnCalendar={string.Join(',', schedule.Days.Distinct().OrderBy(d => ((int)d + 6) % 7).Select(d => d.ToString()[..3]))} " +
                $"*-*-* {schedule.Hour:00}:{schedule.Minute:00}:00\nPersistent=true\n",
            _ => $"OnCalendar=*-*-* {schedule.Hour:00}:{schedule.Minute:00}:00\nPersistent=true\n"
        };
        return "[Unit]\n" +
               $"Description=Agendamento OmniSync: {OneLine(taskName)}\n\n" +
               "[Timer]\n" +
               when +
               $"Unit={UnitName(taskId)}.service\n\n" +
               "[Install]\n" +
               "WantedBy=timers.target\n";
    }

    /// <summary>Aspas no estilo do systemd, com <c>%</c> escapado (especificadores).</summary>
    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("%", "%%") + "\"";

    private static string OneLine(string text) => text.Replace('\n', ' ').Replace('\r', ' ').Replace("%", "%%");
}
