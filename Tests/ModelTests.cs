using System;
using OmniSync.Models;
using OmniSync.ViewModels;
using Xunit;

namespace OmniSync.Tests;

/// <summary>
/// Testes das regras de apresentação dos modelos.
/// </summary>
public class ModelTests
{
    private static SyncItem Conflict(DateTime src, DateTime dst) => new()
    {
        RelativePath = "a.txt",
        SourceFullPath = "/s/a.txt",
        DestinationFullPath = "/d/a.txt",
        SourceModifiedUtc = src,
        DestinationModifiedUtc = dst,
        ActionType = SyncActionType.Conflict
    };

    /// <summary>
    /// A direção do conflito indica o lado modificado mais recentemente.
    /// </summary>
    [Fact]
    public void ConflictDirection_ShouldReflectNewestSide()
    {
        var now = DateTime.UtcNow;
        Assert.Equal(ConflictDirection.SourceNewer, Conflict(now, now.AddMinutes(-5)).ConflictDirection);
        Assert.Equal(ConflictDirection.DestinationNewer, Conflict(now.AddMinutes(-5), now).ConflictDirection);
        Assert.Equal(ConflictDirection.SameTime, Conflict(now, now.AddSeconds(1)).ConflictDirection);
    }

    /// <summary>
    /// O índice do seletor da grade e a decisão permanecem sincronizados nos dois sentidos.
    /// </summary>
    [Fact]
    public void ResolutionIndex_ShouldMapBothWays()
    {
        var item = Conflict(DateTime.UtcNow, DateTime.UtcNow);
        item.ResolutionIndex = 1;
        Assert.Equal(ConflictResolution.Replace, item.Resolution);
        item.Resolution = ConflictResolution.Ignore;
        Assert.Equal(2, item.ResolutionIndex);
    }

    /// <summary>
    /// O alerta de remoção em massa dispara com 20% ou mais do destino (mínimo de 10 órfãos) ou 1.000 órfãos.
    /// </summary>
    [Theory]
    [InlineData(5, 10, false)]
    [InlineData(10, 50, true)]
    [InlineData(10, 51, false)]
    [InlineData(1000, 1_000_000, true)]
    [InlineData(0, 0, false)]
    public void PlanSummary_IsMassOrphan(int orphans, int destFiles, bool expected)
    {
        var summary = new SyncPlanSummary { DestinationPath = "/d", OrphanCount = orphans, DestinationFileCount = destFiles };
        Assert.Equal(expected, summary.IsMassOrphan);
    }

    /// <summary>
    /// A severidade do log é deduzida do prefixo.
    /// </summary>
    [Theory]
    [InlineData("[ERRO] falhou", LogLevel.Error)]
    [InlineData("Aviso: pasta ilegível", LogLevel.Warning)]
    [InlineData("[NOVO] Copiando: a.txt", LogLevel.Info)]
    public void LogEntry_InferLevel(string message, LogLevel expected)
    {
        Assert.Equal(expected, LogEntry.InferLevel(message));
    }
}
