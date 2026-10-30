using System;
using System.IO;
using System.Linq;
using OmniSync.Models;
using OmniSync.Services;
using OmniSync.ViewModels;
using Xunit;

namespace OmniSync.Tests;

/// <summary>
/// Testes da fase "decisões seguras": decisões mantidas entre análises, destino efetivo sem subpasta gravada no campo,
/// detecção imediata de origem/destino inválidos e confirmação das substituições de versões mais novas.
/// </summary>
public class SafeDecisionTests
{
    private static readonly DateTime T0 = new(2026, 10, 30, 9, 0, 0, DateTimeKind.Utc);

    private static SyncItem Conflict(string path, ConflictResolution resolution = ConflictResolution.AskUser, long srcSize = 10, DateTime? dstTime = null) => new()
    {
        RelativePath = path,
        SourceFullPath = "/s/" + path,
        DestinationFullPath = "/d/" + path,
        ActionType = SyncActionType.Conflict,
        SourceSize = srcSize,
        DestinationSize = 20,
        SourceModifiedUtc = T0,
        DestinationModifiedUtc = dstTime ?? T0.AddHours(-1),
        Resolution = resolution
    };

    /// <summary>
    /// Decisões tomadas voltam na nova análise do mesmo par quando os arquivos não mudaram.
    /// </summary>
    [Fact]
    public void DecisionMemory_ShouldRestoreDecisionsForUnchangedFiles()
    {
        var memory = new DecisionMemory();
        var key = DecisionMemory.PairKey("/dados/Lev", "/backup/Lev");
        memory.Capture(key, new[]
        {
            Conflict("a.txt", ConflictResolution.Replace),
            Conflict("b.txt", ConflictResolution.Ignore),
            Conflict("c.txt")
        });
        Assert.Equal(2, memory.Count);

        var rescan = new[] { Conflict("a.txt"), Conflict("b.txt"), Conflict("c.txt") };
        Assert.Equal(2, memory.Apply(key, rescan));
        Assert.Equal(ConflictResolution.Replace, rescan[0].Resolution);
        Assert.Equal(ConflictResolution.Ignore, rescan[1].Resolution);
        Assert.Equal(ConflictResolution.AskUser, rescan[2].Resolution);
    }

    /// <summary>
    /// Se qualquer lado do arquivo mudou, ou se o par é outro, a decisão não volta.
    /// </summary>
    [Fact]
    public void DecisionMemory_ShouldNotRestoreWhenFileOrPairChanged()
    {
        var memory = new DecisionMemory();
        var key = DecisionMemory.PairKey("/dados/Lev", "/backup/Lev");
        memory.Capture(key, new[] { Conflict("a.txt", ConflictResolution.Replace), Conflict("b.txt", ConflictResolution.Replace) });

        var rescan = new[] { Conflict("a.txt", srcSize: 11), Conflict("b.txt", dstTime: T0.AddMinutes(5)) };
        Assert.Equal(0, memory.Apply(key, rescan));
        Assert.All(rescan, i => Assert.Equal(ConflictResolution.AskUser, i.Resolution));

        var otherPair = new[] { Conflict("a.txt") };
        Assert.Equal(0, memory.Apply(DecisionMemory.PairKey("/dados/Lev", "/outro/Lev"), otherPair));
    }

    /// <summary>
    /// Itens já processados (sincronizados) não são guardados.
    /// </summary>
    [Fact]
    public void DecisionMemory_ShouldIgnoreProcessedItems()
    {
        var memory = new DecisionMemory();
        var done = Conflict("a.txt", ConflictResolution.Replace);
        done.IsProcessed = true;
        memory.Capture("k", new[] { done });
        Assert.Equal(0, memory.Count);
    }

    /// <summary>
    /// Destinos gravados pelas versões anteriores com a subpasta da origem perdem a subpasta;
    /// o destino efetivo continua o mesmo com a opção ligada.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Users\u\OmniTeste\Levantamentos", @"C:\Users\u\OmniTeste\Backup\Levantamentos", @"C:\Users\u\OmniTeste\Backup")]
    [InlineData(@"E:\Fotos", @"G:\Fotos", @"G:\")]
    [InlineData(@"E:\Fotos", @"\\nas\backup\fotos\", @"\\nas\backup")]
    [InlineData("/home/u/Fotos", "/mnt/nas/Fotos", "/mnt/nas")]
    [InlineData("/home/u/Fotos", "/Fotos", "/")]
    [InlineData(@"E:\Fotos", @"\\nas\Fotos", @"\\nas\Fotos")]
    [InlineData(@"E:\Fotos", @"G:\Backup", @"G:\Backup")]
    [InlineData("", @"G:\Fotos", @"G:\Fotos")]
    public void StripSourceFolderFromDestination_ShouldRecoverChosenFolder(string source, string dest, string expected)
    {
        Assert.Equal(expected, SettingsService.StripSourceFolderFromDestination(source, dest));
    }

    /// <summary>
    /// A migração só remove a subpasta quando a opção está ligada (com ela desligada, o destino é literal).
    /// </summary>
    [Fact]
    public void Normalize_ShouldStripBakedSubfolderOnlyWhenOptionIsOn()
    {
        var settings = new AppSettings
        {
            Profiles =
            {
                new SyncProfile { Name = "A", SourcePath = "/dados/Lev", DestinationPath = "/backup/Lev", UseSourceFolderNameInDestination = true },
                new SyncProfile { Name = "B", SourcePath = "/dados/Lev", DestinationPath = "/backup/Lev", UseSourceFolderNameInDestination = false }
            }
        };

        SettingsService.Normalize(settings);

        Assert.Equal("/backup", settings.Profiles[0].DestinationPath);
        Assert.Equal("/backup/Lev", settings.Profiles[1].DestinationPath);
    }

    /// <summary>
    /// O destino efetivo acrescenta a subpasta só com a opção ligada e não a duplica.
    /// </summary>
    [Fact]
    public void ResolveEffectiveDestination_ShouldAppendSourceFolderOnce()
    {
        var source = Path.Combine(Path.GetTempPath(), "Lev");
        var backup = Path.Combine(Path.GetTempPath(), "Backup");
        var expected = Path.Combine(backup, "Lev");

        Assert.Equal(expected, PathHelper.ResolveEffectiveDestination(source, backup, useSourceFolderName: true));
        Assert.Equal(expected, PathHelper.ResolveEffectiveDestination(source, expected, useSourceFolderName: true));
        Assert.Equal(backup, PathHelper.ResolveEffectiveDestination(source, backup, useSourceFolderName: false));
        Assert.Equal(string.Empty, PathHelper.ResolveEffectiveDestination(source, " ", useSourceFolderName: true));
    }

    /// <summary>
    /// Origem e destino iguais ou aninhados são apontados na hora da escolha; pares válidos ou incompletos, não.
    /// </summary>
    [Fact]
    public void DescribePairProblem_ShouldFlagSameAndNestedFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), "par");
        var source = Path.Combine(root, "Lev");

        Assert.Contains("mesma pasta", PathHelper.DescribePairProblem(source, source + Path.DirectorySeparatorChar));
        Assert.Contains("Destino está dentro da Origem", PathHelper.DescribePairProblem(source, Path.Combine(source, "copia")));
        Assert.Contains("Origem está dentro do Destino", PathHelper.DescribePairProblem(source, root));
        Assert.Null(PathHelper.DescribePairProblem(source, Path.Combine(root, "Backup", "Lev")));
        Assert.Null(PathHelper.DescribePairProblem(source, ""));
    }

    /// <summary>
    /// Substituir uma versão mais nova do destino exige marcar a confirmação própria na revisão.
    /// </summary>
    [Fact]
    public void ConfirmSync_ShouldRequireAcknowledgingDestinationNewerReplacements()
    {
        var summary = new SyncPlanSummary
        {
            DestinationPath = "/d",
            ReplaceCount = 1,
            DestinationNewerReplacements = new[] { "destino_mais_novo.txt" }
        };
        var vm = new ConfirmSyncViewModel(summary);

        Assert.False(vm.CanConfirm);
        Assert.Contains("destino_mais_novo.txt", vm.DestinationNewerListText);
        vm.DestinationNewerAcknowledged = true;
        Assert.True(vm.CanConfirm);

        var plain = new ConfirmSyncViewModel(new SyncPlanSummary { DestinationPath = "/d", ReplaceCount = 1 });
        Assert.True(plain.CanConfirm);
    }

    /// <summary>
    /// A lista de substituições arriscadas mostra no máximo 8 nomes e resume o restante.
    /// </summary>
    [Fact]
    public void ConfirmSync_ShouldSummarizeLongReplacementLists()
    {
        var vm = new ConfirmSyncViewModel(new SyncPlanSummary
        {
            DestinationPath = "/d",
            DestinationNewerReplacements = Enumerable.Range(1, 11).Select(i => $"f{i}.txt").ToList()
        });

        Assert.Contains("e mais 3", vm.DestinationNewerListText);
        Assert.DoesNotContain("f9.txt", vm.DestinationNewerListText);
    }
}
