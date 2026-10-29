using System;
using System.IO;
using System.Linq;
using OmniSync.Models;
using OmniSync.Services;
using OmniSync.ViewModels;
using OmniSync.Views;
using Xunit;

namespace OmniSync.Tests;

/// <summary>
/// Testes dos pares salvos (persistência e migração) e das regras da nova interface
/// (árvore de pastas, barra proporcional, textos da lista).
/// </summary>
public class ProfileAndLayoutTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OmniSyncProfileTests_" + Guid.NewGuid().ToString("N"));

    public ProfileAndLayoutTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static SyncItem Item(string relativePath, SyncActionType type) => new()
    {
        RelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar),
        SourceFullPath = type == SyncActionType.OrphanTrash ? null : "/s/" + relativePath,
        DestinationFullPath = "/d/" + relativePath,
        ActionType = type
    };

    /// <summary>
    /// O settings.json da versão 1.1 (um par em campos soltos) vira um par salvo, ativo, com as mesmas opções.
    /// </summary>
    [Fact]
    public void Load_ShouldMigrateLegacySinglePair()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """
            {
              "SourcePath": "E:\\Fotos",
              "DestinationPath": "G:\\Fotos",
              "UseSourceFolderNameInDestination": false,
              "EnableDeepHashCheck": true,
              "TolerateDstShift": true,
              "ExcludePatterns": "*.tmp"
            }
            """);

        var settings = SettingsService.Load(path);

        var profile = Assert.Single(settings.Profiles);
        Assert.Equal("Fotos", profile.Name);
        Assert.Equal("E:\\Fotos", profile.SourcePath);
        Assert.Equal("G:\\Fotos", profile.DestinationPath);
        Assert.False(profile.UseSourceFolderNameInDestination);
        Assert.True(profile.EnableDeepHashCheck);
        Assert.True(profile.TolerateDstShift);
        Assert.Equal("*.tmp", profile.ExcludePatterns);
        Assert.Equal(profile.Id, settings.ActiveProfileId);
        Assert.Null(settings.SourcePath);
    }

    /// <summary>
    /// Gravar e reler preserva os pares, o par ativo e o resultado da última execução; os campos antigos não são regravados.
    /// </summary>
    [Fact]
    public void SaveAndLoad_ShouldRoundTripProfiles()
    {
        var path = Path.Combine(_dir, "settings.json");
        var a = new SyncProfile { Name = "Fotos", SourcePath = "/a", DestinationPath = "/b" };
        var b = new SyncProfile
        {
            Name = "Docs", SourcePath = "/c", DestinationPath = "/d",
            LastRunUtc = new DateTime(2026, 10, 28, 18, 42, 0, DateTimeKind.Utc),
            LastOutcome = ProfileRunOutcome.Failures, LastFailedCount = 2
        };
        SettingsService.Save(new AppSettings { Profiles = { a, b }, ActiveProfileId = b.Id }, path);

        var json = File.ReadAllText(path);
        Assert.DoesNotContain("\"SourcePath\": null", json);
        Assert.Contains("\"Failures\"", json);

        var loaded = SettingsService.Load(path);
        Assert.Equal(new[] { "Fotos", "Docs" }, loaded.Profiles.Select(p => p.Name));
        Assert.Equal(b.Id, loaded.ActiveProfileId);
        var docs = loaded.Profiles[1];
        Assert.Equal(ProfileRunOutcome.Failures, docs.LastOutcome);
        Assert.Equal(2, docs.LastFailedCount);
        Assert.Equal(b.LastRunUtc, docs.LastRunUtc);
    }

    /// <summary>
    /// JSON inválido não impede a abertura: volta ao padrão sem pares.
    /// </summary>
    [Fact]
    public void Load_InvalidJson_ShouldFallBackToDefaults()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ isto não é json");

        var settings = SettingsService.Load(path);

        Assert.Empty(settings.Profiles);
        Assert.Null(settings.ActiveProfileId);
    }

    /// <summary>
    /// A normalização corrige ids repetidos, nomes vazios e par ativo inexistente (edição manual do arquivo).
    /// </summary>
    [Fact]
    public void Normalize_ShouldRepairDuplicateIdsBlankNamesAndDanglingActive()
    {
        var id = Guid.NewGuid();
        var settings = new AppSettings
        {
            Profiles =
            {
                new SyncProfile { Id = id, Name = "", SourcePath = "/dados/Campo" },
                new SyncProfile { Id = id, Name = "  Outro  " }
            },
            ActiveProfileId = Guid.NewGuid()
        };

        SettingsService.Normalize(settings);

        Assert.NotEqual(settings.Profiles[0].Id, settings.Profiles[1].Id);
        Assert.Equal("Campo", settings.Profiles[0].Name);
        Assert.Equal("Outro", settings.Profiles[1].Name);
        Assert.Equal(settings.Profiles[0].Id, settings.ActiveProfileId);
    }

    /// <summary>
    /// O nome sugerido vem da última pasta da origem; raízes de disco e vazios têm alternativa.
    /// </summary>
    [Theory]
    [InlineData("/home/u/Levantamentos", "Levantamentos")]
    [InlineData("/home/u/Levantamentos/", "Levantamentos")]
    [InlineData("", "Novo par")]
    [InlineData(null, "Novo par")]
    public void SuggestName_ShouldUseLastFolder(string? source, string expected)
    {
        Assert.Equal(expected, SyncProfile.SuggestName(source));
    }

    /// <summary>
    /// A linha secundária do par reflete o resultado da última execução.
    /// </summary>
    [Fact]
    public void ProfileItem_ShouldDescribeLastRun()
    {
        var profile = new SyncProfile { Name = "X", SourcePath = "/a", DestinationPath = "/b" };
        var item = new ProfileItemViewModel(profile);
        Assert.Equal(ProfileStatusKind.Idle, item.Status);
        Assert.Equal("Nunca sincronizado", item.Subtitle);

        profile.LastRunUtc = DateTime.UtcNow;
        profile.LastOutcome = ProfileRunOutcome.Failures;
        profile.LastFailedCount = 3;
        item.RefreshFromLastRun();
        Assert.Equal(ProfileStatusKind.Problem, item.Status);
        Assert.StartsWith("3 falhas ·", item.Subtitle);

        profile.LastFailedCount = 1;
        item.RefreshFromLastRun();
        Assert.StartsWith("1 falha ·", item.Subtitle);

        item.ShowPlanReady(10, 2);
        Assert.Equal(ProfileStatusKind.PlanReady, item.Status);
        Assert.Contains("2 sem decisão", item.Subtitle);

        item.Name = "Renomeado";
        Assert.Equal("Renomeado", profile.Name);
    }

    /// <summary>
    /// Datas recentes aparecem como "hoje"/"ontem"; as demais, por extenso.
    /// </summary>
    [Fact]
    public void FormatWhen_ShouldUseRelativeDays()
    {
        var now = new DateTime(2026, 10, 29, 9, 0, 0);
        Assert.Equal("hoje, 08:15", ProfileItemViewModel.FormatWhen(new DateTime(2026, 10, 29, 8, 15, 0), now));
        Assert.Equal("ontem, 18:42", ProfileItemViewModel.FormatWhen(new DateTime(2026, 10, 28, 18, 42, 0), now));
        Assert.Equal("12/10/2026", ProfileItemViewModel.FormatWhen(new DateTime(2026, 10, 12, 10, 0, 0), now));
    }

    /// <summary>
    /// A árvore agrega alterações e conflitos por pasta, ignora idênticos e ordena por nome.
    /// </summary>
    [Fact]
    public void FolderTree_ShouldAggregateChangesPerFolder()
    {
        var items = new[]
        {
            Item("raiz.txt", SyncActionType.New),
            Item("B/x.txt", SyncActionType.New),
            Item("A/Tabelas/p.xlsx", SyncActionType.Conflict),
            Item("A/Tabelas/q.csv", SyncActionType.OrphanTrash),
            Item("A/Fotos/f.jpg", SyncActionType.New),
            Item("C/igual.txt", SyncActionType.Identical)
        };

        var root = FolderTreeBuilder.Build(items, "Origem");

        Assert.Equal(5, root.ChangeCount);
        Assert.Equal(1, root.ConflictCount);
        Assert.Equal(new[] { "A", "B" }, root.Children.Select(c => c.Name));
        var a = root.Children[0];
        Assert.Equal(3, a.ChangeCount);
        Assert.Equal(1, a.ConflictCount);
        Assert.Equal(new[] { "Fotos", "Tabelas" }, a.Children.Select(c => c.Name));
        Assert.Equal(2, a.Children[1].ChangeCount);
    }

    /// <summary>
    /// Um nó contém os itens da própria pasta e das subpastas, mas não de pastas com prefixo parecido.
    /// </summary>
    [Fact]
    public void FolderNode_Contains_ShouldMatchFolderAndDescendantsOnly()
    {
        var root = FolderTreeBuilder.Build(new[]
        {
            Item("A/x.txt", SyncActionType.New),
            Item("A/sub/y.txt", SyncActionType.New),
            Item("AB/z.txt", SyncActionType.New)
        }, "Origem");
        var a = root.Children.Single(c => c.Name == "A");

        Assert.True(a.Contains(Item("A/x.txt", SyncActionType.New)));
        Assert.True(a.Contains(Item("A/sub/y.txt", SyncActionType.New)));
        Assert.False(a.Contains(Item("AB/z.txt", SyncActionType.New)));
        Assert.False(a.Contains(Item("raiz.txt", SyncActionType.New)));
        Assert.True(root.Contains(Item("raiz.txt", SyncActionType.New)));
    }

    /// <summary>
    /// A barra proporcional respeita a largura total, os espaços e a largura mínima dos segmentos pequenos.
    /// </summary>
    [Fact]
    public void ProportionBar_ShouldKeepSmallSegmentsVisible()
    {
        var widths = ProportionBar.ComputeWidths(new[] { 1000, 1, 0 }, 300, 2, 6);

        Assert.Equal(0, widths[2]);
        Assert.True(widths[1] >= 6);
        Assert.Equal(298, widths[0] + widths[1], 6);

        Assert.All(ProportionBar.ComputeWidths(new[] { 0, 0, 0 }, 300, 2, 6), w => Assert.Equal(0, w));
    }

    /// <summary>
    /// Na lista, conflitos mostram a decisão; os demais, o verbo da ação.
    /// </summary>
    [Fact]
    public void ActionSummary_ShouldShowDecisionForConflicts()
    {
        var conflict = Item("a.txt", SyncActionType.Conflict);
        Assert.Equal("Perguntar", conflict.ActionSummary);
        Assert.True(conflict.NeedsDecision);

        conflict.Resolution = ConflictResolution.Replace;
        Assert.Equal("Substituir", conflict.ActionSummary);
        Assert.False(conflict.NeedsDecision);

        var orphan = Item("b.txt", SyncActionType.OrphanTrash);
        Assert.Equal("Para lixo/", orphan.ActionSummary);
        Assert.False(orphan.NeedsDecision);
    }
}
