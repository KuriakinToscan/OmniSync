using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using OmniSync.Cli;
using OmniSync.Models;
using OmniSync.Services;
using OmniSync.Services.Scheduling;
using OmniSync.ViewModels;
using Xunit;

namespace OmniSync.Tests;

/// <summary>
/// Testes da v1.5: agendamento (regras, XML do Agendador de Tarefas, units do systemd), histórico, trava por tarefa,
/// execução agendada pela linha de comando e instalação na pasta fixa.
/// </summary>
public class ScheduleTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 30, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "OmniSyncScheduleTests_" + Guid.NewGuid().ToString("N"));

    public ScheduleTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ------------------------------------------------------------------ SyncSchedule

    /// <summary>
    /// Diário: hoje, se o horário ainda não passou; senão, amanhã.
    /// </summary>
    [Fact]
    public void NextRun_Daily_ShouldPickTodayOrTomorrow()
    {
        var s = new SyncSchedule { Enabled = true, Frequency = ScheduleFrequency.Daily, Hour = 18, Minute = 30 };
        Assert.Equal(new DateTime(2026, 10, 31, 18, 30, 0), s.NextRun(new DateTime(2026, 10, 31, 9, 0, 0)));
        Assert.Equal(new DateTime(2026, 11, 1, 18, 30, 0), s.NextRun(new DateTime(2026, 10, 31, 18, 30, 0)));
    }

    /// <summary>
    /// Semanal: o próximo dia marcado; desligado ou "ao entrar" não têm horário.
    /// </summary>
    [Fact]
    public void NextRun_Weekly_ShouldPickNextMarkedDay()
    {
        // 31/10/2026 é sábado
        var s = new SyncSchedule
        {
            Enabled = true,
            Frequency = ScheduleFrequency.Weekly,
            Hour = 7,
            Days = new List<DayOfWeek> { DayOfWeek.Wednesday, DayOfWeek.Monday }
        };
        Assert.Equal(new DateTime(2026, 11, 2, 7, 0, 0), s.NextRun(new DateTime(2026, 10, 31, 12, 0, 0)));

        s.Enabled = false;
        Assert.Null(s.NextRun(new DateTime(2026, 10, 31, 12, 0, 0)));
        s.Enabled = true;
        s.Frequency = ScheduleFrequency.AtLogon;
        Assert.Null(s.NextRun(new DateTime(2026, 10, 31, 12, 0, 0)));
    }

    /// <summary>
    /// Semanal sem dia é inválido; descrições em português, com a semana começando na segunda.
    /// </summary>
    [Fact]
    public void IsValidAndDescribe_ShouldFollowRules()
    {
        var s = new SyncSchedule { Enabled = true, Frequency = ScheduleFrequency.Weekly, Days = new List<DayOfWeek>() };
        Assert.False(s.IsValid);

        s.Days = new List<DayOfWeek> { DayOfWeek.Sunday, DayOfWeek.Friday, DayOfWeek.Monday };
        s.Hour = 7;
        s.Minute = 5;
        Assert.True(s.IsValid);
        Assert.Equal("Seg, Sex e Dom às 07:05, sincroniza", s.Describe());

        s.Frequency = ScheduleFrequency.Daily;
        s.Mode = ScheduleMode.AnalyzeAndNotify;
        Assert.Equal("Todo dia às 07:05, analisa e avisa", s.Describe());

        s.Frequency = ScheduleFrequency.AtLogon;
        Assert.StartsWith("Ao entrar no sistema", s.Describe());
    }

    /// <summary>
    /// Os argumentos usam o id da tarefa (renomear não quebra o agendamento) e são aceitos pelo interpretador da CLI.
    /// </summary>
    [Fact]
    public void BuildArguments_ShouldUseIdAndParseBack()
    {
        var sync = new SyncSchedule { ConflictPolicy = CliConflictPolicy.ReplaceWhenSourceNewer };
        var args = sync.BuildArguments(Id);
        Assert.Equal(new[] { "--sincronizar", Id.ToString(), "--agendado", "--conflitos", "origem-mais-nova" }, args);
        Assert.True(CliCommand.TryParse(args, out var command, out _));
        Assert.Equal(CliAction.Run, command!.Action);
        Assert.True(command.Scheduled);
        Assert.Equal(CliConflictPolicy.ReplaceWhenSourceNewer, command.ConflictPolicy);

        var analyze = new SyncSchedule { Mode = ScheduleMode.AnalyzeAndNotify }.BuildArguments(Id);
        Assert.Equal(new[] { "--analisar", Id.ToString(), "--agendado" }, analyze);
    }

    /// <summary>
    /// "--agendado" não vale com --listar.
    /// </summary>
    [Fact]
    public void TryParse_ShouldRejectScheduledWithList()
    {
        Assert.False(CliCommand.TryParse(new[] { "--listar", "--agendado" }, out _, out var error));
        Assert.Contains("--agendado", error);
    }

    /// <summary>
    /// O agendamento é salvo dentro da tarefa no settings.json e relido igual.
    /// </summary>
    [Fact]
    public void Schedule_ShouldRoundTripInSettings()
    {
        var path = Path.Combine(_root, "settings.json");
        var settings = new AppSettings();
        settings.Profiles.Add(new SyncProfile
        {
            Id = Id,
            Name = "Volume",
            Schedule = new SyncSchedule
            {
                Enabled = true,
                Frequency = ScheduleFrequency.Weekly,
                Days = new List<DayOfWeek> { DayOfWeek.Tuesday },
                Hour = 6,
                ConflictPolicy = CliConflictPolicy.ReplaceWhenSourceNewer,
                RegisteredExecutable = @"C:\Users\x\AppData\Local\Programs\OmniSync\OmniSync.exe"
            }
        });
        SettingsService.Save(settings, path);

        var loaded = SettingsService.Load(path).Profiles.Single().Schedule!;
        Assert.True(loaded.Enabled);
        Assert.Equal(ScheduleFrequency.Weekly, loaded.Frequency);
        Assert.Equal(new[] { DayOfWeek.Tuesday }, loaded.Days);
        Assert.Equal(6, loaded.Hour);
        Assert.Equal(CliConflictPolicy.ReplaceWhenSourceNewer, loaded.ConflictPolicy);
        Assert.EndsWith("OmniSync.exe", loaded.RegisteredExecutable);
    }

    /// <summary>
    /// Próxima execução em texto: hoje, amanhã ou dia da semana com data.
    /// </summary>
    [Fact]
    public void FormatNextRun_ShouldBeRelative()
    {
        var now = new DateTime(2026, 10, 31, 9, 0, 0);
        Assert.Equal("hoje às 18:00", MainViewModel.FormatNextRun(new DateTime(2026, 10, 31, 18, 0, 0), now));
        Assert.Equal("amanhã às 07:30", MainViewModel.FormatNextRun(new DateTime(2026, 11, 1, 7, 30, 0), now));
        Assert.Equal("seg 02/11 às 18:00", MainViewModel.FormatNextRun(new DateTime(2026, 11, 2, 18, 0, 0), now));
    }

    /// <summary>
    /// Horário digitado: formatos aceitos.
    /// </summary>
    [Theory]
    [InlineData("18:30", 18, 30)]
    [InlineData("1830", 18, 30)]
    [InlineData("730", 7, 30)]
    [InlineData("7:05", 7, 5)]
    [InlineData("7h05", 7, 5)]
    [InlineData("7H", 7, 0)]
    [InlineData("7", 7, 0)]
    [InlineData(" 00:00 ", 0, 0)]
    [InlineData("23:59", 23, 59)]
    public void TryParseTime_ShouldAcceptCommonFormats(string text, int hour, int minute)
    {
        Assert.True(SyncSchedule.TryParseTime(text, out var time));
        Assert.Equal(new TimeSpan(hour, minute, 0), time);
    }

    /// <summary>
    /// Horários impossíveis ou texto qualquer são recusados.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("24:00")]
    [InlineData("12:60")]
    [InlineData("abc")]
    [InlineData("1:2:3")]
    [InlineData("12345")]
    [InlineData("-1")]
    public void TryParseTime_ShouldRejectInvalid(string text)
    {
        Assert.False(SyncSchedule.TryParseTime(text, out _));
    }

    /// <summary>
    /// %VARIÁVEL% é expandida; variável inexistente fica como está; vazio vira vazio.
    /// </summary>
    [Fact]
    public void ExpandUserPath_ShouldExpandEnvironmentVariables()
    {
        Environment.SetEnvironmentVariable("OMNISYNC_TESTE_DIR", "/dados/teste");
        Assert.Equal("/dados/teste/Campo", PathHelper.ExpandUserPath("  %OMNISYNC_TESTE_DIR%/Campo "));
        Assert.Equal("%NAO_EXISTE_OMNI%/x", PathHelper.ExpandUserPath("%NAO_EXISTE_OMNI%/x"));
        Assert.Equal(string.Empty, PathHelper.ExpandUserPath("   "));
        if (!OperatingSystem.IsWindows())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.Equal(home + "/Fotos", PathHelper.ExpandUserPath("~/Fotos"));
            Assert.Equal(home + "/Fotos", PathHelper.ExpandUserPath("$HOME/Fotos"));
        }
    }

    // ------------------------------------------------------------------ Agendador do Windows e systemd

    /// <summary>
    /// XML do Agendador de Tarefas: gatilho semanal, roda na bateria, uma instância por vez, sem privilégio elevado,
    /// e a ação chama o executável instalado com os argumentos da tarefa.
    /// </summary>
    [Fact]
    public void WindowsTaskXml_ShouldContainTriggerSettingsAndAction()
    {
        var schedule = new SyncSchedule
        {
            Enabled = true,
            Frequency = ScheduleFrequency.Weekly,
            Hour = 18,
            Minute = 30,
            Days = new List<DayOfWeek> { DayOfWeek.Friday, DayOfWeek.Monday }
        };
        const string exe = @"C:\Users\João Silva\AppData\Local\Programs\OmniSync\OmniSync.exe";
        var xml = WindowsTaskScheduler.BuildTaskXml("Fotos & campo", Id, schedule, exe, @"PC\joao", new DateTime(2026, 10, 31, 9, 0, 0));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var root = xml.Root!;

        string Value(params string[] path) =>
            path.Aggregate((XElement)root, (e, name) => e.Element(ns + name)!).Value;

        Assert.Equal("2026-10-31T18:30:00", Value("Triggers", "CalendarTrigger", "StartBoundary"));
        var days = root.Descendants(ns + "DaysOfWeek").Single().Elements().Select(e => e.Name.LocalName).ToArray();
        Assert.Equal(new[] { "Monday", "Friday" }, days);
        Assert.Equal("false", Value("Settings", "DisallowStartIfOnBatteries"));
        Assert.Equal("false", Value("Settings", "StopIfGoingOnBatteries"));
        Assert.Equal("true", Value("Settings", "StartWhenAvailable"));
        Assert.Equal("IgnoreNew", Value("Settings", "MultipleInstancesPolicy"));
        Assert.Equal("LeastPrivilege", Value("Principals", "Principal", "RunLevel"));
        Assert.Equal("InteractiveToken", Value("Principals", "Principal", "LogonType"));
        Assert.Equal(exe, Value("Actions", "Exec", "Command"));
        Assert.Equal($"--sincronizar {Id} --agendado --conflitos manter", Value("Actions", "Exec", "Arguments"));
        Assert.Equal(@"\OmniSync\" + Id, Value("RegistrationInfo", "URI"));
        Assert.Contains("Fotos & campo", Value("RegistrationInfo", "Description"));
    }

    /// <summary>
    /// "Ao entrar no sistema" vira gatilho de logon com atraso de 5 minutos.
    /// </summary>
    [Fact]
    public void WindowsTaskXml_AtLogon_ShouldUseLogonTriggerWithDelay()
    {
        var schedule = new SyncSchedule { Enabled = true, Frequency = ScheduleFrequency.AtLogon, Mode = ScheduleMode.AnalyzeAndNotify };
        var xml = WindowsTaskScheduler.BuildTaskXml("Volume", Id, schedule, @"C:\x\OmniSync.exe", @"PC\u", DateTime.Now);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var trigger = xml.Root!.Element(ns + "Triggers")!.Element(ns + "LogonTrigger")!;
        Assert.Equal("PT5M", trigger.Element(ns + "Delay")!.Value);
        Assert.Equal(@"PC\u", trigger.Element(ns + "UserId")!.Value);
        Assert.Equal($"--analisar {Id} --agendado", xml.Root.Descendants(ns + "Arguments").Single().Value);
    }

    /// <summary>
    /// Units do systemd: calendário semanal persistente e caminho com espaço e % entre aspas e escapado.
    /// </summary>
    [Fact]
    public void SystemdUnits_ShouldQuoteAndSchedule()
    {
        var schedule = new SyncSchedule
        {
            Enabled = true,
            Frequency = ScheduleFrequency.Weekly,
            Hour = 7,
            Minute = 5,
            Days = new List<DayOfWeek> { DayOfWeek.Wednesday, DayOfWeek.Monday }
        };
        var service = SystemdTimerScheduler.BuildServiceUnit("Fotos 100%", Id, schedule, "/home/ana/Meus Programas/OmniSync");
        Assert.Contains("ExecStart=\"/home/ana/Meus Programas/OmniSync\" \"--sincronizar\"", service);
        Assert.Contains("Description=OmniSync: Fotos 100%%", service);

        var timer = SystemdTimerScheduler.BuildTimerUnit("Fotos", Id, schedule);
        Assert.Contains("OnCalendar=Mon,Wed *-*-* 07:05:00", timer);
        Assert.Contains("Persistent=true", timer);
        Assert.Contains($"Unit=omnisync-{Id}.service", timer);

        schedule.Frequency = ScheduleFrequency.AtLogon;
        Assert.Contains("OnStartupSec=5min", SystemdTimerScheduler.BuildTimerUnit("Fotos", Id, schedule));
    }

    /// <summary>
    /// Integração real com o Agendador de Tarefas (só no Windows): registra, confere e remove uma tarefa de teste.
    /// </summary>
    [Fact]
    public void WindowsTaskScheduler_ShouldRegisterQueryAndDelete()
    {
        if (!OperatingSystem.IsWindows()) return;
        var scheduler = new WindowsTaskScheduler();
        var id = Guid.NewGuid();
        var profile = new SyncProfile { Id = id, Name = "Teste automatizado OmniSync" };
        var schedule = new SyncSchedule { Enabled = true, Frequency = ScheduleFrequency.Daily, Hour = 3, Minute = 17 };
        var exe = Path.Combine(_root, "Pasta com espaço", "OmniSync.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "falso");
        try
        {
            Assert.Null(scheduler.Register(profile, schedule, exe));
            Assert.True(scheduler.Exists(id));
            // Gravar de novo substitui sem erro
            schedule.Frequency = ScheduleFrequency.Weekly;
            Assert.Null(scheduler.Register(profile, schedule, exe));
        }
        finally
        {
            Assert.Null(scheduler.Unregister(id));
        }
        Assert.False(scheduler.Exists(id));
        // Remover o que não existe não é erro
        Assert.Null(scheduler.Unregister(id));
    }

    /// <summary>
    /// Saúde do agendamento: executável apagado ou entrada ausente no agendador são problemas; desligado não.
    /// </summary>
    [Fact]
    public void ScheduleHealth_ShouldDetectMissingExecutableAndEntry()
    {
        var profile = new SyncProfile { Id = Id, Schedule = new SyncSchedule { Enabled = true, RegisteredExecutable = "/x/OmniSync" } };
        var scheduler = new FakeScheduler();

        Assert.Contains("não existe mais", ScheduleHealth.Check(profile, scheduler, _ => false));
        Assert.Contains("não está mais", ScheduleHealth.Check(profile, scheduler, _ => true));
        scheduler.Registered.Add(Id);
        Assert.Null(ScheduleHealth.Check(profile, scheduler, _ => true));
        profile.Schedule.Enabled = false;
        Assert.Null(ScheduleHealth.Check(profile, scheduler, _ => false));
    }

    // ------------------------------------------------------------------ Histórico e trava

    /// <summary>
    /// O histórico guarda as 30 execuções mais recentes, mais nova primeiro.
    /// </summary>
    [Fact]
    public void RunHistory_ShouldKeepMostRecent()
    {
        for (int i = 0; i < RunHistory.MaxRecords + 5; i++)
        {
            RunHistory.Append(Id, new RunRecord { StartedUtc = T0.AddMinutes(i), Summary = $"r{i}" }, _root);
        }
        var records = RunHistory.Load(Id, _root);
        Assert.Equal(RunHistory.MaxRecords, records.Count);
        Assert.Equal($"r{RunHistory.MaxRecords + 4}", records[0].Summary);
        Assert.Equal("r5", records[^1].Summary);

        RunHistory.Delete(Id, _root);
        Assert.Empty(RunHistory.Load(Id, _root));
    }

    /// <summary>
    /// Arquivo de histórico corrompido é ignorado (lista vazia) e substituído na próxima gravação.
    /// </summary>
    [Fact]
    public void RunHistory_ShouldIgnoreCorruptFile()
    {
        File.WriteAllText(Path.Combine(_root, $"{Id:D}.json"), "{ isto não é json");
        Assert.Empty(RunHistory.Load(Id, _root));
        RunHistory.Append(Id, new RunRecord { StartedUtc = T0, Summary = "ok" }, _root);
        Assert.Single(RunHistory.Load(Id, _root));
    }

    /// <summary>
    /// Só uma execução por tarefa: a segunda trava falha até a primeira ser liberada; tarefas diferentes não se bloqueiam.
    /// </summary>
    [Fact]
    public void TaskLock_ShouldBeExclusivePerTask()
    {
        using (var first = TaskLock.TryAcquire(Id, _root))
        {
            Assert.NotNull(first);
            Assert.Null(TaskLock.TryAcquire(Id, _root));
            using var other = TaskLock.TryAcquire(Guid.NewGuid(), _root);
            Assert.NotNull(other);
        }
        using var again = TaskLock.TryAcquire(Id, _root);
        Assert.NotNull(again);
    }

    // ------------------------------------------------------------------ Execução agendada (CLI)

    /// <summary>
    /// Sincronização agendada: grava o histórico como agendada e prepara o aviso (houve alteração).
    /// </summary>
    [Fact]
    public async Task ScheduledRun_ShouldRecordHistoryAndNotify()
    {
        var (settingsPath, id) = CreateTask(orphans: 0);
        var runner = new CliRunner(new SyncEngine(), new StringWriter(), settingsPath);

        var code = await runner.RunAsync(new CliCommand(CliAction.Run, id.ToString(), Scheduled: true), CancellationToken.None);

        Assert.Equal(CliExitCode.Success, code);
        var record = RunHistory.Load(id, Path.Combine(_root, "history")).Single();
        Assert.True(record.Scheduled);
        Assert.False(record.AnalyzeOnly);
        Assert.Equal("ok", record.Outcome);
        Assert.Equal(1, record.Copied);
        Assert.NotNull(runner.Notification);
        Assert.False(runner.Notification!.IsProblem);
    }

    /// <summary>
    /// Execução manual pela linha de comando não mostra aviso (o usuário está vendo o console).
    /// </summary>
    [Fact]
    public async Task ManualRun_ShouldNotNotify()
    {
        var (settingsPath, _) = CreateTask(orphans: 0);
        var runner = new CliRunner(new SyncEngine(), new StringWriter(), settingsPath);
        await runner.RunAsync(new CliCommand(CliAction.Run, "Volume"), CancellationToken.None);
        Assert.Null(runner.Notification);
    }

    /// <summary>
    /// Análise agendada com alterações pendentes avisa e registra a quantidade, sem tocar no destino.
    /// </summary>
    [Fact]
    public async Task ScheduledAnalyze_ShouldNotifyPendingChanges()
    {
        var (settingsPath, id) = CreateTask(orphans: 1);
        var runner = new CliRunner(new SyncEngine(), new StringWriter(), settingsPath);

        var code = await runner.RunAsync(new CliCommand(CliAction.Analyze, id.ToString(), Scheduled: true), CancellationToken.None);

        Assert.Equal(CliExitCode.Success, code);
        var record = RunHistory.Load(id, Path.Combine(_root, "history")).Single();
        Assert.True(record.AnalyzeOnly);
        Assert.Equal(2, record.PendingChanges);
        Assert.Contains("alterações", runner.Notification!.Title);
        Assert.False(File.Exists(Path.Combine(_root, "Backup", "Volume", "novo.txt")));
    }

    /// <summary>
    /// Se a tarefa já está sendo sincronizada (trava ocupada), a execução agendada sai com código 6 sem alterar nada.
    /// </summary>
    [Fact]
    public async Task ScheduledRun_ShouldReturnAlreadyRunningWhenLocked()
    {
        var (settingsPath, id) = CreateTask(orphans: 0);
        using var held = TaskLock.TryAcquire(id, Path.Combine(_root, "locks"));
        Assert.NotNull(held);
        var runner = new CliRunner(new SyncEngine(), new StringWriter(), settingsPath);

        var code = await runner.RunAsync(new CliCommand(CliAction.Run, id.ToString(), Scheduled: true), CancellationToken.None);

        Assert.Equal(CliExitCode.AlreadyRunning, code);
        Assert.Equal("ocupado", RunHistory.Load(id, Path.Combine(_root, "history")).Single().Outcome);
        Assert.False(File.Exists(Path.Combine(_root, "Backup", "Volume", "novo.txt")));
    }

    /// <summary>
    /// Remoção em massa numa execução agendada aborta, registra "abortado" e avisa como problema.
    /// </summary>
    [Fact]
    public async Task ScheduledRun_MassOrphan_ShouldNotifyProblem()
    {
        var (settingsPath, id) = CreateTask(orphans: 12);
        var runner = new CliRunner(new SyncEngine(), new StringWriter(), settingsPath);

        var code = await runner.RunAsync(new CliCommand(CliAction.Run, id.ToString(), Scheduled: true), CancellationToken.None);

        Assert.Equal(CliExitCode.MassRemovalAborted, code);
        Assert.Equal("abortado", RunHistory.Load(id, Path.Combine(_root, "history")).Single().Outcome);
        Assert.True(runner.Notification!.IsProblem);
    }

    // ------------------------------------------------------------------ Instalação

    /// <summary>
    /// Situação da instalação: ausente, igual, diferente e em execução a partir da pasta fixa.
    /// </summary>
    [Fact]
    public void AppInstaller_GetState_ShouldCompareContent()
    {
        var current = Path.Combine(_root, "Downloads", AppInstaller.ExecutableName);
        var installDir = Path.Combine(_root, "Programs", "OmniSync");
        var installed = Path.Combine(installDir, AppInstaller.ExecutableName);
        Directory.CreateDirectory(Path.GetDirectoryName(current)!);
        File.WriteAllText(current, "versao 1.5");

        Assert.Equal(InstallState.NotApplicable, AppInstaller.GetState(null, installed));
        Assert.Equal(InstallState.NotInstalled, AppInstaller.GetState(current, installed));

        Assert.Null(AppInstaller.Install(current, installDir, desktopShortcut: false, startMenuShortcut: false));
        Assert.Equal("versao 1.5", File.ReadAllText(installed));
        Assert.Equal(InstallState.InstalledSame, AppInstaller.GetState(current, installed));
        Assert.Equal(InstallState.RunningInstalled, AppInstaller.GetState(installed, installed));

        File.WriteAllText(current, "versao 1.6");
        Assert.Equal(InstallState.InstalledDifferent, AppInstaller.GetState(current, installed));
        // Atualizar substitui a versão instalada e não deixa temporário para trás
        Assert.Null(AppInstaller.Install(current, installDir, false, false));
        Assert.Equal("versao 1.6", File.ReadAllText(installed));
        Assert.Single(Directory.GetFiles(installDir));
    }

    /// <summary>
    /// Atalho do Linux: entrada .desktop com o executável entre aspas e o ícone.
    /// </summary>
    [Fact]
    public void DesktopEntry_ShouldQuoteExecutable()
    {
        var entry = ShortcutService.BuildDesktopEntry("/home/ana/.local/share/Programs/OmniSync/OmniSync", "/x/omnisync.png");
        Assert.Contains("Exec=\"/home/ana/.local/share/Programs/OmniSync/OmniSync\"", entry);
        Assert.Contains("Icon=/x/omnisync.png", entry);
        Assert.Contains("Type=Application", entry);
    }

    // ------------------------------------------------------------------ Apoio

    private (string SettingsPath, Guid Id) CreateTask(int orphans)
    {
        var src = Path.Combine(_root, "Origem", "Volume");
        var effective = Path.Combine(_root, "Backup", "Volume");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(effective);
        File.WriteAllText(Path.Combine(src, "novo.txt"), "novo");
        for (int i = 0; i < orphans; i++)
        {
            File.WriteAllText(Path.Combine(effective, $"orfao{i}.txt"), "o");
        }

        var settingsPath = Path.Combine(_root, "settings.json");
        var settings = new AppSettings();
        var profile = new SyncProfile
        {
            Name = "Volume",
            SourcePath = src,
            DestinationPath = Path.Combine(_root, "Backup"),
            UseSourceFolderNameInDestination = true
        };
        settings.Profiles.Add(profile);
        SettingsService.Save(settings, settingsPath);
        return (settingsPath, profile.Id);
    }

    private sealed class FakeScheduler : ISystemScheduler
    {
        public HashSet<Guid> Registered { get; } = new();
        public string? Register(SyncProfile profile, SyncSchedule schedule, string executable) { Registered.Add(profile.Id); return null; }
        public string? Unregister(Guid taskId) { Registered.Remove(taskId); return null; }
        public bool Exists(Guid taskId) => Registered.Contains(taskId);
    }
}
