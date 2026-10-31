using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OmniSync.Models;
using OmniSync.Services;
using Xunit;

namespace OmniSync.Tests;

/// <summary>
/// Testes da camada <see cref="Services.FileSystems.IFileSystem"/>: o motor e a restauração funcionam inteiros sobre
/// um sistema de arquivos em memória, sem tocar no disco, com as mesmas garantias de <c>lixo/</c>, <c>old/</c> e
/// cópia conferida.
/// </summary>
public class FileSystemLayerTests
{
    private static readonly DateTime OldTime = new(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime NewTime = new(2026, 3, 5, 9, 30, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "OmniSyncMemFs_" + Guid.NewGuid().ToString("N"));
    private string Src => Path.Combine(_root, "Origem");
    private string Dst => Path.Combine(_root, "Destino");

    private (MemoryFileSystem Source, MemoryFileSystem Dest) Seed()
    {
        var source = new MemoryFileSystem();
        var dest = new MemoryFileSystem();
        source.Seed(Path.Combine(Src, "novo.txt"), "novo", NewTime);
        source.Seed(Path.Combine(Src, "sub", "igual.txt"), "igual", OldTime);
        source.Seed(Path.Combine(Src, "conflito.txt"), "versao_nova", NewTime);
        dest.Seed(Path.Combine(Dst, "sub", "igual.txt"), "igual", OldTime);
        dest.Seed(Path.Combine(Dst, "conflito.txt"), "versao_antiga", OldTime);
        dest.Seed(Path.Combine(Dst, "pasta", "orfao.txt"), "orfao", OldTime);
        return (source, dest);
    }

    /// <summary>
    /// Análise e sincronização completas em memória: novo copiado com a data preservada, conflito substituído com
    /// a versão anterior em <c>old/</c>, órfão em <c>lixo/</c>, pasta vazia removida e nada criado no disco.
    /// </summary>
    [Fact]
    public async Task ScanAndSync_RunEntirelyThroughTheLayer()
    {
        var (source, dest) = Seed();
        var engine = new SyncEngine(source, dest) { VerifyCopies = true };

        var scan = await engine.ScanDirectoriesAsync(Src, Dst, new ScanOptions(), null, CancellationToken.None);
        Assert.Equal(SyncActionType.New, scan.Items.Single(i => i.RelativePath == "novo.txt").ActionType);
        Assert.Equal(SyncActionType.Identical, scan.Items.Single(i => i.RelativePath == Path.Combine("sub", "igual.txt")).ActionType);
        Assert.Equal(SyncActionType.Conflict, scan.Items.Single(i => i.RelativePath == "conflito.txt").ActionType);
        Assert.Equal(SyncActionType.OrphanTrash, scan.Items.Single(i => i.RelativePath == Path.Combine("pasta", "orfao.txt")).ActionType);

        var result = await engine.ExecuteSyncAsync(
            scan.Items, Dst, _ => Task.FromResult(ConflictResolution.Replace), null, null, null, CancellationToken.None);

        Assert.Equal(0, result.Failed);
        Assert.Equal(1, result.Copied);
        Assert.Equal(1, result.Replaced);
        Assert.Equal(1, result.Trashed);
        Assert.Equal("novo", dest.Read(Path.Combine(Dst, "novo.txt")));
        Assert.Equal(NewTime, dest.GetFile(Path.Combine(Dst, "novo.txt"))!.Value.LastWriteUtc);
        Assert.Equal("versao_nova", dest.Read(Path.Combine(Dst, "conflito.txt")));
        Assert.Equal("versao_antiga", dest.Read(Path.Combine(Dst, "old", "conflito.txt")));
        Assert.Equal("orfao", dest.Read(Path.Combine(Dst, "lixo", "pasta", "orfao.txt")));
        Assert.False(dest.DirectoryExists(Path.Combine(Dst, "pasta")));
        Assert.DoesNotContain(dest.AllFiles(), f => f.EndsWith(PathHelper.TempSuffix, StringComparison.Ordinal));
        Assert.Equal("versao_nova", source.Read(Path.Combine(Src, "conflito.txt")));
        Assert.True(source.Calls > 0 && dest.Calls > 0);
        Assert.False(Directory.Exists(_root));

        // Reanálise: tudo idêntico
        var again = await engine.ScanDirectoriesAsync(Src, Dst, new ScanOptions(), null, CancellationToken.None);
        Assert.All(again.Items, i => Assert.Equal(SyncActionType.Identical, i.ActionType));
    }

    /// <summary>
    /// A comparação por SHA-256 na análise lê os dois lados pela camada.
    /// </summary>
    [Fact]
    public async Task DeepHashScan_ReadsThroughTheLayer()
    {
        var source = new MemoryFileSystem();
        var dest = new MemoryFileSystem();
        source.Seed(Path.Combine(Src, "a.txt"), "abc", NewTime);
        dest.Seed(Path.Combine(Dst, "a.txt"), "xyz", NewTime);
        var engine = new SyncEngine(source, dest);

        var scan = await engine.ScanDirectoriesAsync(Src, Dst, new ScanOptions { UseDeepHash = true }, null, CancellationToken.None);

        Assert.Equal(SyncActionType.Conflict, scan.Items.Single().ActionType);
        Assert.False(Directory.Exists(_root));
    }

    /// <summary>
    /// A restauração de <c>lixo/</c> lista e devolve pela camada, sem sobrescrever o que já existe.
    /// </summary>
    [Fact]
    public async Task Restore_RunsThroughTheLayer()
    {
        var dest = new MemoryFileSystem();
        dest.Seed(Path.Combine(Dst, "lixo", "pasta", "volta.txt"), "volta", OldTime);
        dest.Seed(Path.Combine(Dst, "lixo", "fica.txt"), "do lixo", OldTime);
        dest.Seed(Path.Combine(Dst, "fica.txt"), "atual", NewTime);
        var service = new RestoreService(dest);

        var entries = await service.ListTrashAsync(Dst, CancellationToken.None);
        Assert.Equal(2, entries.Count);
        var result = await service.RestoreAsync(Dst, entries, null, CancellationToken.None);

        Assert.Equal(1, result.Restored);
        Assert.Equal(1, result.KeptInTrash);
        Assert.Equal("volta", dest.Read(Path.Combine(Dst, "pasta", "volta.txt")));
        Assert.Equal("atual", dest.Read(Path.Combine(Dst, "fica.txt")));
        Assert.Equal("do lixo", dest.Read(Path.Combine(Dst, "lixo", "fica.txt")));
        Assert.False(dest.DirectoryExists(Path.Combine(Dst, "lixo", "pasta")));
        Assert.False(Directory.Exists(_root));
    }
}
