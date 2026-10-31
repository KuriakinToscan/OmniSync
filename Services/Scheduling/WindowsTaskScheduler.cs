using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using OmniSync.Models;

namespace OmniSync.Services.Scheduling;

/// <summary>
/// Agendamentos no Agendador de Tarefas do Windows, pasta <c>\OmniSync</c>, um item por tarefa (nomeado pelo id).
/// A definição vai em XML para <c>schtasks.exe /Create /XML</c>, que aceita opções que a linha de comando simples
/// não tem: executar na bateria, executar assim que possível após um horário perdido e não iniciar duas vezes.
/// A tarefa roda como o próprio usuário, só com a sessão aberta (sem pedir senha, sem privilégio elevado).
/// </summary>
public sealed class WindowsTaskScheduler : ISystemScheduler
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>Pasta do Agendador de Tarefas usada pelo OmniSync.</summary>
    public const string FolderName = "OmniSync";

    /// <summary>Nome completo do item da tarefa no Agendador de Tarefas.</summary>
    /// <param name="taskId">Identificador da tarefa.</param>
    public static string TaskName(Guid taskId) => $"\\{FolderName}\\{taskId:D}";

    /// <inheritdoc />
    public string? Register(SyncProfile profile, SyncSchedule schedule, string executable)
    {
        var xmlPath = Path.Combine(Path.GetTempPath(), $"omnisync-agenda-{profile.Id:N}.xml");
        try
        {
            var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
            WriteXml(BuildTaskXml(profile.Name, profile.Id, schedule, executable, user, DateTime.Now), xmlPath);
            var (code, output) = SystemScheduler.Run("schtasks.exe",
                new[] { "/Create", "/TN", TaskName(profile.Id), "/XML", xmlPath, "/F" }, OemEncoding());
            return code == 0 ? null : $"O Agendador de Tarefas recusou o agendamento: {SystemScheduler.FirstLine(output)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Não foi possível preparar o agendamento: {ErrorMessages.Translate(ex)}";
        }
        finally
        {
            PathHelper.TryDeleteFile(xmlPath);
        }
    }

    /// <inheritdoc />
    public string? Unregister(Guid taskId)
    {
        if (!Exists(taskId)) return null;
        var (code, output) = SystemScheduler.Run("schtasks.exe", new[] { "/Delete", "/TN", TaskName(taskId), "/F" }, OemEncoding());
        return code == 0 ? null : $"Não foi possível remover o agendamento: {SystemScheduler.FirstLine(output)}";
    }

    /// <inheritdoc />
    public bool Exists(Guid taskId) =>
        SystemScheduler.Run("schtasks.exe", new[] { "/Query", "/TN", TaskName(taskId) }, OemEncoding()).ExitCode == 0;

    /// <summary>
    /// Monta a definição da tarefa agendada (esquema do Agendador de Tarefas 1.2).
    /// </summary>
    /// <param name="taskName">Nome da tarefa do OmniSync (descrição do item).</param>
    /// <param name="taskId">Identificador da tarefa.</param>
    /// <param name="schedule">Agendamento.</param>
    /// <param name="executable">Executável a chamar.</param>
    /// <param name="userId">Usuário (DOMÍNIO\usuário) que executa.</param>
    /// <param name="now">Momento de referência (hora local), para a data inicial do gatilho.</param>
    public static XDocument BuildTaskXml(string taskName, Guid taskId, SyncSchedule schedule, string executable, string userId, DateTime now)
    {
        var start = now.Date.AddHours(schedule.Hour).AddMinutes(schedule.Minute)
            .ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

        XElement trigger = schedule.Frequency switch
        {
            ScheduleFrequency.AtLogon => new XElement(Ns + "LogonTrigger",
                new XElement(Ns + "Enabled", "true"),
                new XElement(Ns + "UserId", userId),
                new XElement(Ns + "Delay", $"PT{SyncSchedule.LogonDelayMinutes}M")),
            ScheduleFrequency.Weekly => new XElement(Ns + "CalendarTrigger",
                new XElement(Ns + "StartBoundary", start),
                new XElement(Ns + "Enabled", "true"),
                new XElement(Ns + "ScheduleByWeek",
                    new XElement(Ns + "DaysOfWeek",
                        schedule.Days.Distinct().OrderBy(d => ((int)d + 6) % 7).Select(d => new XElement(Ns + d.ToString()))),
                    new XElement(Ns + "WeeksInterval", "1"))),
            _ => new XElement(Ns + "CalendarTrigger",
                new XElement(Ns + "StartBoundary", start),
                new XElement(Ns + "Enabled", "true"),
                new XElement(Ns + "ScheduleByDay", new XElement(Ns + "DaysInterval", "1")))
        };

        return new XDocument(
            new XDeclaration("1.0", "UTF-16", null),
            new XElement(Ns + "Task", new XAttribute("version", "1.2"),
                new XElement(Ns + "RegistrationInfo",
                    new XElement(Ns + "Description", $"OmniSync: {taskName}. {schedule.Describe()}."),
                    new XElement(Ns + "URI", TaskName(taskId))),
                new XElement(Ns + "Triggers", trigger),
                new XElement(Ns + "Principals",
                    new XElement(Ns + "Principal", new XAttribute("id", "Author"),
                        new XElement(Ns + "UserId", userId),
                        new XElement(Ns + "LogonType", "InteractiveToken"),
                        new XElement(Ns + "RunLevel", "LeastPrivilege"))),
                new XElement(Ns + "Settings",
                    new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(Ns + "DisallowStartIfOnBatteries", "false"),
                    new XElement(Ns + "StopIfGoingOnBatteries", "false"),
                    new XElement(Ns + "AllowHardTerminate", "true"),
                    new XElement(Ns + "StartWhenAvailable", "true"),
                    new XElement(Ns + "RunOnlyIfNetworkAvailable", "false"),
                    new XElement(Ns + "IdleSettings",
                        new XElement(Ns + "StopOnIdleEnd", "false"),
                        new XElement(Ns + "RestartOnIdle", "false")),
                    new XElement(Ns + "AllowStartOnDemand", "true"),
                    new XElement(Ns + "Enabled", "true"),
                    new XElement(Ns + "Hidden", "false"),
                    new XElement(Ns + "RunOnlyIfIdle", "false"),
                    new XElement(Ns + "WakeToRun", "false"),
                    new XElement(Ns + "ExecutionTimeLimit", "PT72H"),
                    new XElement(Ns + "Priority", "7")),
                new XElement(Ns + "Actions", new XAttribute("Context", "Author"),
                    new XElement(Ns + "Exec",
                        new XElement(Ns + "Command", executable),
                        new XElement(Ns + "Arguments", string.Join(' ', schedule.BuildArguments(taskId))),
                        new XElement(Ns + "WorkingDirectory", Path.GetDirectoryName(executable) ?? string.Empty)))));
    }

    /// <summary>
    /// Grava o XML em UTF-16 com BOM, a codificação que o <c>schtasks.exe</c> espera.
    /// </summary>
    private static void WriteXml(XDocument document, string path)
    {
        var settings = new XmlWriterSettings { Encoding = new UnicodeEncoding(false, true), Indent = true };
        using var writer = XmlWriter.Create(path, settings);
        document.Save(writer);
    }

    private static Encoding? OemEncoding()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding((int)GetOEMCP());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();
}
