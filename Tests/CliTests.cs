using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OmniSync.Cli;
using OmniSync.Models;
using OmniSync.Services;
using Xunit;

namespace OmniSync.Tests;

/// <summary>
/// Testes do modo de linha de comando: interpretação dos argumentos, busca da tarefa, regras sem perguntas
/// (conflitos e remoção em massa) e execução de ponta a ponta sobre pastas temporárias.
/// </summary>
public class CliTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 30, 9, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "OmniSyncCliTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static SyncItem Conflict(string path, DateTime dstTime) => new()
    {
        RelativePath = path,
        SourceFullPath = "/s/" + path,
        DestinationFullPath = "/d/" + path,
        ActionType = SyncActionType.Conflict,
        SourceSize = 10,
        DestinationSize = 20,
        SourceModifiedUtc = T0,
        DestinationModifiedUtc = dstTime
    };

    /// <summary>
    /// Só <c>--self-test</c> (ou nenhum argumento) abre a janela; qualquer outro argumento vai para o modo de linha de comando.
    /// </summary>
    [Fact]
    public void IsCliInvocation_ShouldKeepSelfTestAndNoArgsInInterface()
    {
        Assert.False(CliCommand.IsCliInvocation(Array.Empty<string>()));
        Assert.False(CliCommand.IsCliInvocation(new[] { "--self-test" }));
        Assert.True(CliCommand.IsCliInvocation(new[] { "--listar" }));
        Assert.True(CliCommand.IsCliInvocation(new[] { "--sincronisar", "X" }));
    }

    /// <summary>
    /// Comandos e opções em português e em inglês, com valores entre aspas.
    /// </summary>
    [Fact]
    public void TryParse_ShouldReadCommandAndOptions()
    {
        Assert.True(CliCommand.TryParse(new[] { "--sincronizar", "Volume RS", "--conflitos", "origem-mais-nova", "--relatorio", "r.csv", "--detalhado" },
            out var cmd, out _));
        Assert.Equal(new CliCommand(CliAction.Run, "Volume RS", CliConflictPolicy.ReplaceWhenSourceNewer, "r.csv", true), cmd);

        Assert.True(CliCommand.TryParse(new[] { "--analyze", "Volume" }, out cmd, out _));
        Assert.Equal(CliAction.Analyze, cmd!.Action);
        Assert.Equal(CliConflictPolicy.Keep, cmd.ConflictPolicy);

        Assert.True(CliCommand.TryParse(new[] { "--LISTAR" }, out cmd, out _));
        Assert.Equal(CliAction.List, cmd!.Action);

        Assert.True(CliCommand.TryParse(new[] { "--sincronizar", "X", "--ajuda" }, out cmd, out _));
        Assert.Equal(CliAction.Help, cmd!.Action);
    }

    /// <summary>
    /// Argumentos incompletos ou desconhecidos dão mensagem em português e nenhum comando.
    /// </summary>
    [Theory]
    [InlineData(new[] { "--sincronizar" }, "Falta o nome da tarefa")]
    [InlineData(new[] { "--sincronizar", "--detalhado" }, "Falta o nome da tarefa")]
    [InlineData(new[] { "--analisar", "A", "--sincronizar", "B" }, "só um comando")]
    [InlineData(new[] { "--sincronizar", "A", "--conflitos", "substituir" }, "Valor inválido para --conflitos")]
    [InlineData(new[] { "--sincronizar", "A", "--relatorio" }, "Falta o caminho")]
    [InlineData(new[] { "--sincronisar", "A" }, "Argumento desconhecido")]
    [InlineData(new[] { "--detalhado" }, "Nenhum comando")]
    public void TryParse_ShouldRejectInvalidArguments(string[] args, string expected)
    {
        Assert.False(CliCommand.TryParse(args, out var cmd, out var error));
        Assert.Null(cmd);
        Assert.Contains(expected, error);
    }

    /// <summary>
    /// A tarefa é encontrada pelo nome sem diferenciar maiúsculas e acentos, ou pelo identificador; nome repetido é recusado.
    /// </summary>
    [Fact]
    public void FindProfile_ShouldMatchNameIgnoringCaseAndAccents()
    {
        var a = new SyncProfile { Name = "Levantamento Área 1" };
        var b = new SyncProfile { Name = "Volume" };
        var c = new SyncProfile { Name = "volume" };
        var profiles = new[] { a, b, c };

        Assert.Same(a, CliRunner.FindProfile(profiles, "levantamento area 1", out _));
        Assert.Same(c, CliRunner.FindProfile(profiles, c.Id.ToString(), out _));
        // Igualdade exata (sem diferenciar maiúsculas) vence a comparação sem acentos, mas aqui há duas
        Assert.Null(CliRunner.FindProfile(profiles, "VOLUME", out var error));
        Assert.Contains("Há 2 tarefas", error);
        Assert.Null(CliRunner.FindProfile(profiles, "Outra", out error));
        Assert.Contains("\"Volume\"", error);
    }

    /// <summary>
    /// Sem ninguém para perguntar: o padrão mantém o destino; "origem-mais-nova" só substitui quando a origem é mais nova.
    /// Decisões já tomadas não mudam.
    /// </summary>
    [Fact]
    public void ApplyConflictPolicy_ShouldNeverReplaceNewerDestination()
    {
        SyncItem[] Items() => new[]
        {
            Conflict("origem-nova.txt", T0.AddHours(-2)),
            Conflict("destino-novo.txt", T0.AddHours(2)),
            Conflict("mesmo-horario.txt", T0),
            new SyncItem
            {
                RelativePath = "decidido.txt", SourceFullPath = "/s/x", DestinationFullPath = "/d/x",
                ActionType = SyncActionType.Conflict, SourceModifiedUtc = T0, DestinationModifiedUtc = T0.AddHours(5),
                Resolution = ConflictResolution.Replace
            }
        };

        var keep = Items();
        Assert.Equal(0, CliRunner.ApplyConflictPolicy(keep, CliConflictPolicy.Keep));
        Assert.All(keep.Take(3), i => Assert.Equal(ConflictResolution.Ignore, i.Resolution));
        Assert.Equal(ConflictResolution.Replace, keep[3].Resolution);

        var newer = Items();
        Assert.Equal(1, CliRunner.ApplyConflictPolicy(newer, CliConflictPolicy.ReplaceWhenSourceNewer));
        Assert.Equal(ConflictResolution.Replace, newer[0].Resolution);
        Assert.Equal(ConflictResolution.Ignore, newer[1].Resolution);
        Assert.Equal(ConflictResolution.Ignore, newer[2].Resolution);
    }

    /// <summary>
    /// Ponta a ponta: copia novos, mantém o conflito (padrão), move o órfão para lixo/, grava o CSV e registra a execução na tarefa.
    /// </summary>
    [Fact]
    public async Task Run_ShouldSyncAndRecordLastRun()
    {
        var (settingsPath, src, dst) = CreateTask(orphans: 1);
        var output = new StringWriter();
        var report = Path.Combine(_root, "relatorios", "volume.csv");
        var runner = new CliRunner(new SyncEngine(), output, settingsPath);

        var code = await runner.RunAsync(new CliCommand(CliAction.Run, "volume", ReportPath: report), CancellationToken.None);

        Assert.Equal(CliExitCode.Success, code);
        var effective = Path.Combine(dst, "Volume");
        Assert.Equal("novo", File.ReadAllText(Path.Combine(effective, "novo.txt")));
        Assert.Equal("versao_destino_longa", File.ReadAllText(Path.Combine(effective, "conflito.txt")));
        Assert.True(File.Exists(Path.Combine(effective, "lixo", "orfao0.txt")));
        Assert.Contains(";novo;", File.ReadAllText(report));
        Assert.Contains("1 copiado, 0 substituídos, 1 movido p/ lixo/, 1 mantido no destino", output.ToString());

        var saved = SettingsService.Load(settingsPath).Profiles.Single();
        Assert.Equal(ProfileRunOutcome.Success, saved.LastOutcome);
        Assert.NotNull(saved.LastRunUtc);
    }

    /// <summary>
    /// Analisar não altera nada no destino nem registra execução.
    /// </summary>
    [Fact]
    public async Task Analyze_ShouldNotTouchDestination()
    {
        var (settingsPath, _, dst) = CreateTask(orphans: 1);
        var output = new StringWriter();
        var runner = new CliRunner(new SyncEngine(), output, settingsPath);

        var code = await runner.RunAsync(new CliCommand(CliAction.Analyze, "Volume"), CancellationToken.None);

        Assert.Equal(CliExitCode.Success, code);
        Assert.False(File.Exists(Path.Combine(dst, "Volume", "novo.txt")));
        Assert.True(File.Exists(Path.Combine(dst, "Volume", "orfao0.txt")));
        Assert.Contains("Novos:", output.ToString());
        Assert.Null(SettingsService.Load(settingsPath).Profiles.Single().LastRunUtc);
    }

    /// <summary>
    /// Remoção em massa aborta antes de alterar o destino, com código 2.
    /// </summary>
    [Fact]
    public async Task Run_ShouldAbortOnMassOrphanWithoutChanges()
    {
        var (settingsPath, _, dst) = CreateTask(orphans: 12);
        var output = new StringWriter();
        var runner = new CliRunner(new SyncEngine(), output, settingsPath);

        var code = await runner.RunAsync(new CliCommand(CliAction.Run, "Volume"), CancellationToken.None);

        Assert.Equal(CliExitCode.MassRemovalAborted, code);
        Assert.Contains("remoção em massa", output.ToString());
        Assert.False(File.Exists(Path.Combine(dst, "Volume", "novo.txt")));
        Assert.False(Directory.Exists(Path.Combine(dst, "Volume", "lixo")));
    }

    /// <summary>
    /// Tarefa inexistente ou com origem ausente dá código 3 sem tocar em nada.
    /// </summary>
    [Fact]
    public async Task Run_ShouldRejectUnknownTaskAndMissingSource()
    {
        var (settingsPath, src, _) = CreateTask(orphans: 0);
        var runner = new CliRunner(new SyncEngine(), new StringWriter(), settingsPath);

        Assert.Equal(CliExitCode.InvalidInput, await runner.RunAsync(new CliCommand(CliAction.Run, "Outra"), CancellationToken.None));

        Directory.Delete(src, recursive: true);
        var output = new StringWriter();
        runner = new CliRunner(new SyncEngine(), output, settingsPath);
        Assert.Equal(CliExitCode.InvalidInput, await runner.RunAsync(new CliCommand(CliAction.Run, "Volume"), CancellationToken.None));
        Assert.Contains("A origem não existe", output.ToString());
    }

    /// <summary>
    /// Lista as tarefas com o destino efetivo.
    /// </summary>
    [Fact]
    public async Task List_ShouldShowTasksWithEffectiveDestination()
    {
        var (settingsPath, _, dst) = CreateTask(orphans: 0);
        var output = new StringWriter();
        var runner = new CliRunner(new SyncEngine(), output, settingsPath);

        Assert.Equal(CliExitCode.Success, await runner.RunAsync(new CliCommand(CliAction.List), CancellationToken.None));
        Assert.Contains("1 tarefa salva:", output.ToString());
        Assert.Contains(Path.Combine(dst, "Volume"), output.ToString());
        Assert.Contains("nunca sincronizada", output.ToString());
    }

    /// <summary>
    /// Cria origem "Volume" (um novo e um conflito), destino com a subpasta "Volume" (conflito com destino mais novo
    /// e <paramref name="orphans"/> órfãos) e o arquivo de configurações com a tarefa.
    /// </summary>
    private (string SettingsPath, string Source, string Destination) CreateTask(int orphans)
    {
        var src = Path.Combine(_root, "Origem", "Volume");
        var dst = Path.Combine(_root, "Backup");
        var effective = Path.Combine(dst, "Volume");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(effective);

        File.WriteAllText(Path.Combine(src, "novo.txt"), "novo");
        File.WriteAllText(Path.Combine(src, "conflito.txt"), "versao_origem");
        File.WriteAllText(Path.Combine(effective, "conflito.txt"), "versao_destino_longa");
        File.SetLastWriteTimeUtc(Path.Combine(src, "conflito.txt"), T0);
        File.SetLastWriteTimeUtc(Path.Combine(effective, "conflito.txt"), T0.AddDays(1));
        for (int i = 0; i < orphans; i++)
        {
            File.WriteAllText(Path.Combine(effective, $"orfao{i}.txt"), "o");
        }

        var settingsPath = Path.Combine(_root, "settings.json");
        var settings = new AppSettings();
        settings.Profiles.Add(new SyncProfile
        {
            Name = "Volume",
            SourcePath = src,
            DestinationPath = dst,
            UseSourceFolderNameInDestination = true
        });
        SettingsService.Save(settings, settingsPath);
        return (settingsPath, src, dst);
    }
}
