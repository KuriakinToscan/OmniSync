using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OmniSync.Models;
using OmniSync.Services;
using OmniSync.ViewModels;
using Xunit;

namespace OmniSync.Tests;

/// <summary>
/// Suíte de testes automatizados de integração do <see cref="SyncEngine"/>.
/// Verifica todas as regras de negócio de sincronização utilizando diretórios temporários isolados.
/// </summary>
public class SyncEngineTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _sourceDir;
    private readonly string _destDir;
    private readonly SyncEngine _engine;

    /// <summary>
    /// Prepara um ambiente de diretórios temporários limpos (Origem e Destino) antes de cada teste.
    /// </summary>
    public SyncEngineTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "OmniSyncTests_" + Guid.NewGuid().ToString("N"));
        _sourceDir = Path.Combine(_testRoot, "Origem");
        _destDir = Path.Combine(_testRoot, "Destino");

        Directory.CreateDirectory(_sourceDir);
        Directory.CreateDirectory(_destDir);

        _engine = new SyncEngine();
    }

    /// <summary>
    /// Remove recursivamente os diretórios temporários criados após a execução de cada teste.
    /// </summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, recursive: true);
            }
        }
        catch { }
    }

    /// <summary>
    /// Executa a varredura entre as pastas de teste.
    /// </summary>
    private async Task<List<SyncItem>> ScanAsync(ScanOptions? options = null)
    {
        var result = await _engine.ScanDirectoriesAsync(_sourceDir, _destDir, options ?? new ScanOptions(), null, CancellationToken.None);
        return result.Items;
    }

    /// <summary>
    /// Executa a sincronização respondendo a todos os conflitos com a decisão informada.
    /// </summary>
    private Task<SyncResult> ExecuteAsync(
        List<SyncItem> items,
        ConflictResolution resolution,
        IProgress<SyncProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return _engine.ExecuteSyncAsync(items, _destDir, _ => Task.FromResult(resolution), null, progress, null, cancellationToken);
    }

    /// <summary>
    /// Verifica se arquivos presentes exclusivamente na Origem são classificados como <see cref="SyncActionType.New"/>.
    /// </summary>
    [Fact]
    public async Task Scan_ShouldIdentifyNewFiles_WhenOnlyInSource()
    {
        var filePath = Path.Combine(_sourceDir, "novo_arquivo.txt");
        await File.WriteAllTextAsync(filePath, "conteudo novo");

        var items = await ScanAsync();

        var item = Assert.Single(items);
        Assert.Equal("novo_arquivo.txt", item.RelativePath);
        Assert.Equal(SyncActionType.New, item.ActionType);
    }

    /// <summary>
    /// Verifica se arquivos com mesmo tamanho e timestamp em ambos os diretórios são classificados como <see cref="SyncActionType.Identical"/>.
    /// </summary>
    [Fact]
    public async Task Scan_ShouldIdentifyIdenticalFiles_WhenSameSizeAndTime()
    {
        var srcPath = Path.Combine(_sourceDir, "identico.txt");
        var dstPath = Path.Combine(_destDir, "identico.txt");

        await File.WriteAllTextAsync(srcPath, "mesmo conteudo");
        await File.WriteAllTextAsync(dstPath, "mesmo conteudo");

        var now = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(srcPath, now);
        File.SetLastWriteTimeUtc(dstPath, now);

        var items = await ScanAsync();

        var item = Assert.Single(items);
        Assert.Equal("identico.txt", item.RelativePath);
        Assert.Equal(SyncActionType.Identical, item.ActionType);
    }

    /// <summary>
    /// Verifica se arquivos presentes apenas no Destino (ausentes na Origem) são classificados como <see cref="SyncActionType.OrphanTrash"/>.
    /// </summary>
    [Fact]
    public async Task Scan_ShouldIdentifyOrphanFiles_WhenOnlyInDestination()
    {
        var dstPath = Path.Combine(_destDir, "orfao.txt");
        await File.WriteAllTextAsync(dstPath, "so no destino");

        var items = await ScanAsync();

        var item = Assert.Single(items);
        Assert.Equal("orfao.txt", item.RelativePath);
        Assert.Equal(SyncActionType.OrphanTrash, item.ActionType);
    }

    /// <summary>
    /// Garante que as subpastas reservadas <c>destino/lixo/</c> e <c>destino/old/</c> sejam ignoradas durante a varredura.
    /// </summary>
    [Fact]
    public async Task Scan_ShouldIgnoreLixoAndOldFoldersInDestination()
    {
        var lixoDir = Path.Combine(_destDir, "lixo", "sub");
        var oldDir = Path.Combine(_destDir, "old", "sub");
        Directory.CreateDirectory(lixoDir);
        Directory.CreateDirectory(oldDir);

        await File.WriteAllTextAsync(Path.Combine(lixoDir, "antigo_deletado.txt"), "lixo");
        await File.WriteAllTextAsync(Path.Combine(oldDir, "versao_anterior.txt"), "old");

        var items = await ScanAsync();

        Assert.Empty(items);
    }

    /// <summary>
    /// Verifica se arquivos com mesmo nome relativo e tamanhos diferentes são classificados como <see cref="SyncActionType.Conflict"/>.
    /// </summary>
    [Fact]
    public async Task Scan_ShouldDetectConflict_WhenContentDiffers()
    {
        var srcPath = Path.Combine(_sourceDir, "documento.txt");
        var dstPath = Path.Combine(_destDir, "documento.txt");

        await File.WriteAllTextAsync(srcPath, "versao 2 atualizada");
        await File.WriteAllTextAsync(dstPath, "versao 1 antiga");

        var items = await ScanAsync();

        var item = Assert.Single(items);
        Assert.Equal("documento.txt", item.RelativePath);
        Assert.Equal(SyncActionType.Conflict, item.ActionType);
    }

    /// <summary>
    /// Verifica se a verificação profunda por SHA-256 detecta conflitos mesmo quando os dois arquivos possuem exatamente o mesmo tamanho em bytes.
    /// </summary>
    [Fact]
    public async Task Scan_ShouldDetectConflictViaSha256_WhenSizeMatchesButHashDiffers()
    {
        var srcPath = Path.Combine(_sourceDir, "dados.bin");
        var dstPath = Path.Combine(_destDir, "dados.bin");

        await File.WriteAllBytesAsync(srcPath, new byte[] { 1, 2, 3, 4 });
        await File.WriteAllBytesAsync(dstPath, new byte[] { 1, 2, 3, 9 });

        var items = await ScanAsync(new ScanOptions { UseDeepHash = true });

        var item = Assert.Single(items);
        Assert.Equal("dados.bin", item.RelativePath);
        Assert.Equal(SyncActionType.Conflict, item.ActionType);
    }

    /// <summary>
    /// Verifica se a execução da sincronização copia arquivos novos preservando sua estrutura de subpastas.
    /// </summary>
    [Fact]
    public async Task ExecuteSync_ShouldCopyNewFiles()
    {
        var srcPath = Path.Combine(_sourceDir, "sub", "novo.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(srcPath)!);
        await File.WriteAllTextAsync(srcPath, "conteudo do novo");

        var items = await ScanAsync();

        await ExecuteAsync(items, ConflictResolution.Ignore);

        var targetFile = Path.Combine(_destDir, "sub", "novo.txt");
        Assert.True(File.Exists(targetFile));
        Assert.Equal("conteudo do novo", await File.ReadAllTextAsync(targetFile));
    }

    /// <summary>
    /// Verifica se arquivos órfãos no destino são movidos para <c>destino/lixo/</c> preservando sua hierarquia relativa.
    /// </summary>
    [Fact]
    public async Task ExecuteSync_ShouldMoveOrphansToLixoFolder()
    {
        var dstPath = Path.Combine(_destDir, "subpasta", "deve_ir_pro_lixo.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);
        await File.WriteAllTextAsync(dstPath, "adeus");

        var items = await ScanAsync();

        await ExecuteAsync(items, ConflictResolution.Ignore);

        Assert.False(File.Exists(dstPath));

        var trashPath = Path.Combine(_destDir, "lixo", "subpasta", "deve_ir_pro_lixo.txt");
        Assert.True(File.Exists(trashPath));
        Assert.Equal("adeus", await File.ReadAllTextAsync(trashPath));
    }

    /// <summary>
    /// Verifica se ao escolher <see cref="ConflictResolution.Replace"/>, o arquivo antigo do destino é movido para <c>destino/old/</c>
    /// e substituído pela versão nova da origem.
    /// </summary>
    [Fact]
    public async Task ExecuteSync_WhenConflictReplace_MovesOldToOldFolderAndCopiesNew()
    {
        var srcPath = Path.Combine(_sourceDir, "conflito.txt");
        var dstPath = Path.Combine(_destDir, "conflito.txt");

        await File.WriteAllTextAsync(srcPath, "novo conteudo origem");
        await File.WriteAllTextAsync(dstPath, "antigo conteudo destino");

        var items = await ScanAsync();

        await ExecuteAsync(items, ConflictResolution.Replace);

        Assert.True(File.Exists(dstPath));
        Assert.Equal("novo conteudo origem", await File.ReadAllTextAsync(dstPath));

        var oldFile = Directory.EnumerateFiles(Path.Combine(_destDir, "old"), "conflito*.txt").FirstOrDefault();
        Assert.NotNull(oldFile);
        Assert.Equal("antigo conteudo destino", await File.ReadAllTextAsync(oldFile));
    }

    /// <summary>
    /// Verifica se ao escolher <see cref="ConflictResolution.Ignore"/>, o arquivo existente no destino permanece intacto
    /// e nada é movido para <c>destino/old/</c>.
    /// </summary>
    [Fact]
    public async Task ExecuteSync_WhenConflictIgnore_KeepsDestinationIntact()
    {
        var srcPath = Path.Combine(_sourceDir, "conflito.txt");
        var dstPath = Path.Combine(_destDir, "conflito.txt");

        await File.WriteAllTextAsync(srcPath, "novo conteudo origem");
        await File.WriteAllTextAsync(dstPath, "antigo conteudo destino");

        var items = await ScanAsync();

        await ExecuteAsync(items, ConflictResolution.Ignore);

        Assert.True(File.Exists(dstPath));
        Assert.Equal("antigo conteudo destino", await File.ReadAllTextAsync(dstPath));
        Assert.False(Directory.Exists(Path.Combine(_destDir, "old")) && Directory.EnumerateFiles(Path.Combine(_destDir, "old")).Any());
    }

    /// <summary>
    /// Destino dentro da Origem causaria recursão (a origem passaria a conter as próprias cópias): deve ser bloqueado.
    /// </summary>
    [Fact]
    public async Task Scan_ShouldReject_WhenDestinationIsInsideSource()
    {
        var nestedDest = Path.Combine(_sourceDir, "backup");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _engine.ScanDirectoriesAsync(_sourceDir, nestedDest, new ScanOptions(), null, CancellationToken.None));
    }

    /// <summary>
    /// Origem dentro do Destino faria todo o restante do destino virar órfão: deve ser bloqueado.
    /// </summary>
    [Fact]
    public async Task Scan_ShouldReject_WhenSourceIsInsideDestination()
    {
        var nestedSource = Path.Combine(_destDir, "dados");
        Directory.CreateDirectory(nestedSource);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _engine.ScanDirectoriesAsync(nestedSource, _destDir, new ScanOptions(), null, CancellationToken.None));
    }

    /// <summary>
    /// Pastas "lixo" e "old" na raiz da Origem colidiriam com as pastas reservadas do destino:
    /// são ignoradas com aviso. Pastas com esses nomes em subníveis continuam sincronizadas.
    /// </summary>
    [Fact]
    public async Task Scan_ShouldSkipReservedNamesAtSourceRoot_AndWarn()
    {
        Directory.CreateDirectory(Path.Combine(_sourceDir, "lixo"));
        Directory.CreateDirectory(Path.Combine(_sourceDir, "sub", "old"));
        await File.WriteAllTextAsync(Path.Combine(_sourceDir, "lixo", "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(_sourceDir, "sub", "old", "b.txt"), "b");

        var result = await _engine.ScanDirectoriesAsync(_sourceDir, _destDir, new ScanOptions(), null, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(Path.Combine("sub", "old", "b.txt"), item.RelativePath);
        Assert.Contains(result.Warnings, w => w.Contains("'lixo'"));
    }

    /// <summary>
    /// Em plataformas que diferenciam maiúsculas (Linux), arquivos que diferem só na caixa são itens distintos
    /// e geram aviso; antes, um sobrescrevia o outro no índice e nunca era copiado.
    /// </summary>
    [Fact]
    public async Task Scan_ShouldKeepFilesDifferingOnlyByCase_OnCaseSensitivePlatforms()
    {
        if (!PathHelper.IsPlatformCaseSensitive)
        {
            return;
        }

        await File.WriteAllTextAsync(Path.Combine(_sourceDir, "Foto.JPG"), "1");
        await File.WriteAllTextAsync(Path.Combine(_sourceDir, "foto.jpg"), "2");

        var result = await _engine.ScanDirectoriesAsync(_sourceDir, _destDir, new ScanOptions(), null, CancellationToken.None);

        Assert.Equal(2, result.Items.Count(i => i.ActionType == SyncActionType.New));
        Assert.Contains(result.Warnings, w => w.Contains("maiúsculas"));
    }

    /// <summary>
    /// Padrões de exclusão valem para os dois lados: o arquivo excluído não é copiado nem vira órfão.
    /// </summary>
    [Fact]
    public async Task Scan_ShouldApplyExcludePatterns_OnBothSides()
    {
        await File.WriteAllTextAsync(Path.Combine(_sourceDir, "Thumbs.db"), "x");
        await File.WriteAllTextAsync(Path.Combine(_sourceDir, "~$relatorio.docx"), "x");
        await File.WriteAllTextAsync(Path.Combine(_sourceDir, "relatorio.docx"), "x");
        await File.WriteAllTextAsync(Path.Combine(_destDir, "thumbs.db"), "y");

        var items = await ScanAsync(new ScanOptions { ExcludePatterns = new[] { "Thumbs.db", "~$*" } });

        var item = Assert.Single(items);
        Assert.Equal("relatorio.docx", item.RelativePath);
    }

    /// <summary>
    /// Com a tolerância de horário de verão ativa, diferença exata de 1 h é tratada como idêntica.
    /// </summary>
    [Fact]
    public async Task Scan_ShouldTreatOneHourShiftAsIdentical_WhenDstToleranceEnabled()
    {
        var srcPath = Path.Combine(_sourceDir, "fat.txt");
        var dstPath = Path.Combine(_destDir, "fat.txt");
        await File.WriteAllTextAsync(srcPath, "mesmo");
        await File.WriteAllTextAsync(dstPath, "mesmo");
        var now = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(srcPath, now);
        File.SetLastWriteTimeUtc(dstPath, now.AddHours(1));

        Assert.Equal(SyncActionType.Conflict, Assert.Single(await ScanAsync()).ActionType);
        Assert.Equal(SyncActionType.Identical, Assert.Single(await ScanAsync(new ScanOptions { TolerateDstShift = true })).ActionType);
    }

    /// <summary>
    /// Após copiar, a data de modificação é preservada e uma nova análise classifica o arquivo como idêntico.
    /// </summary>
    [Fact]
    public async Task ExecuteSync_ThenRescan_ShouldReportIdentical()
    {
        await File.WriteAllTextAsync(Path.Combine(_sourceDir, "a.txt"), "conteudo");
        var items = await ScanAsync();
        var result = await ExecuteAsync(items, ConflictResolution.Ignore);

        Assert.Equal(1, result.Copied);
        Assert.False(result.HasFailures);
        Assert.Equal(SyncActionType.Identical, Assert.Single(await ScanAsync()).ActionType);
    }

    /// <summary>
    /// Se um arquivo surgir no destino entre a análise e a execução, ele nunca é sobrescrito: o item falha com erro.
    /// </summary>
    [Fact]
    public async Task ExecuteSync_ShouldNotOverwrite_WhenFileAppearsAfterScan()
    {
        await File.WriteAllTextAsync(Path.Combine(_sourceDir, "novo.txt"), "origem");
        var items = await ScanAsync();

        var dstPath = Path.Combine(_destDir, "novo.txt");
        await File.WriteAllTextAsync(dstPath, "criado depois da análise");

        var result = await ExecuteAsync(items, ConflictResolution.Ignore);

        Assert.Equal(1, result.Failed);
        Assert.True(items[0].HasError);
        Assert.Equal("criado depois da análise", await File.ReadAllTextAsync(dstPath));
        Assert.Contains("surgiu no destino", items[0].StatusMessage);
    }

    /// <summary>
    /// Cancelar no meio de uma cópia não deixa arquivo truncado nem temporário no destino.
    /// </summary>
    [Fact]
    public async Task ExecuteSync_WhenCancelledMidCopy_ShouldLeaveNoPartialFile()
    {
        var big = new byte[20 * 1024 * 1024];
        new Random(42).NextBytes(big);
        await File.WriteAllBytesAsync(Path.Combine(_sourceDir, "grande.bin"), big);
        var items = await ScanAsync();

        using var cts = new CancellationTokenSource();
        var progress = new InlineProgress<SyncProgressReport>(r =>
        {
            if (r.ProcessedBytes > 0) cts.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ExecuteAsync(items, ConflictResolution.Ignore, progress, cts.Token));

        Assert.Empty(Directory.EnumerateFiles(_destDir, "*", SearchOption.AllDirectories));
    }

    /// <summary>
    /// Em Substituir, se a cópia da nova versão falhar, o arquivo atual do destino permanece no lugar
    /// (antes, ele já teria sido movido para old/ e o destino ficaria sem o arquivo).
    /// </summary>
    [Fact]
    public async Task ExecuteSync_Replace_ShouldKeepDestination_WhenCopyFails()
    {
        var srcPath = Path.Combine(_sourceDir, "doc.txt");
        var dstPath = Path.Combine(_destDir, "doc.txt");
        await File.WriteAllTextAsync(srcPath, "nova versao maior");
        await File.WriteAllTextAsync(dstPath, "antiga");
        var items = await ScanAsync();

        File.Delete(srcPath);
        var result = await ExecuteAsync(items, ConflictResolution.Replace);

        Assert.Equal(1, result.Failed);
        Assert.Equal("antiga", await File.ReadAllTextAsync(dstPath));
        Assert.False(Directory.Exists(Path.Combine(_destDir, "old")));
    }

    /// <summary>
    /// Órfão que já não existe no momento da execução é contado como pulado, não como "movido com sucesso".
    /// </summary>
    [Fact]
    public async Task ExecuteSync_ShouldReportSkipped_WhenOrphanAlreadyGone()
    {
        var dstPath = Path.Combine(_destDir, "sumiu.txt");
        await File.WriteAllTextAsync(dstPath, "x");
        var items = await ScanAsync();
        File.Delete(dstPath);

        var result = await ExecuteAsync(items, ConflictResolution.Ignore);

        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.Trashed);
        Assert.Contains("já não existia", items[0].StatusMessage);
    }

    /// <summary>
    /// Pastas que ficam vazias no destino após mover órfãos são removidas; o conteúdo vai para lixo/ com a hierarquia.
    /// </summary>
    [Fact]
    public async Task ExecuteSync_ShouldPruneEmptyFolders_AfterMovingOrphans()
    {
        var orphanDir = Path.Combine(_destDir, "antiga", "sub");
        Directory.CreateDirectory(orphanDir);
        await File.WriteAllTextAsync(Path.Combine(orphanDir, "x.txt"), "x");

        var items = await ScanAsync();
        var result = await ExecuteAsync(items, ConflictResolution.Ignore);

        Assert.Equal(1, result.Trashed);
        Assert.False(Directory.Exists(Path.Combine(_destDir, "antiga")));
        Assert.True(File.Exists(Path.Combine(_destDir, "lixo", "antiga", "sub", "x.txt")));
    }

    /// <summary>
    /// Sem canal de atualização, o motor aplica o estado diretamente nos itens (uso headless).
    /// Com canal, nada é aplicado diretamente: as atualizações chegam pelo canal.
    /// </summary>
    [Fact]
    public async Task ExecuteSync_ShouldRouteItemUpdatesThroughChannel_WhenProvided()
    {
        await File.WriteAllTextAsync(Path.Combine(_sourceDir, "a.txt"), "a");
        var items = await ScanAsync();
        var updates = new List<SyncItemUpdate>();

        await _engine.ExecuteSyncAsync(items, _destDir, _ => Task.FromResult(ConflictResolution.Ignore),
            new InlineProgress<SyncItemUpdate>(updates.Add), null, null, CancellationToken.None);

        Assert.False(items[0].IsProcessed);
        var update = Assert.Single(updates);
        update.Apply();
        Assert.True(items[0].IsProcessed);
    }

    /// <summary>
    /// Com a verificação de cópias (padrão), cópias e substituições conferem por SHA-256 com a origem,
    /// inclusive arquivos maiores que o buffer de cópia, e o log registra a conferência.
    /// </summary>
    [Fact]
    public async Task ExecuteSync_ShouldVerifyCopiesBySha256()
    {
        var big = new byte[5 * 1024 * 1024 + 123];
        new Random(42).NextBytes(big);
        File.WriteAllBytes(Path.Combine(_sourceDir, "grande.bin"), big);
        File.WriteAllText(Path.Combine(_sourceDir, "conflito.txt"), "versao da origem");
        File.WriteAllText(Path.Combine(_destDir, "conflito.txt"), "destino");
        File.SetLastWriteTimeUtc(Path.Combine(_destDir, "conflito.txt"), DateTime.UtcNow.AddHours(-2));

        Assert.True(_engine.VerifyCopies);
        var items = await ScanAsync();
        var logs = new List<string>();
        var result = await _engine.ExecuteSyncAsync(items, _destDir, _ => Task.FromResult(ConflictResolution.Replace), null, null,
            new InlineLogger(logs), CancellationToken.None);

        Assert.False(result.HasFailures);
        Assert.Equal(big, File.ReadAllBytes(Path.Combine(_destDir, "grande.bin")));
        Assert.Equal("versao da origem", File.ReadAllText(Path.Combine(_destDir, "conflito.txt")));
        Assert.Contains(logs, l => l.StartsWith("Verificação: 2 cópia(s) conferida(s)"));
        Assert.Empty(Directory.GetFiles(_destDir, "*" + PathHelper.TempSuffix, SearchOption.AllDirectories));
    }

    /// <summary>
    /// Com a verificação desligada, a cópia funciona igual e o log não menciona conferência.
    /// </summary>
    [Fact]
    public async Task ExecuteSync_WithoutVerification_ShouldNotLogVerification()
    {
        File.WriteAllText(Path.Combine(_sourceDir, "a.txt"), "a");
        _engine.VerifyCopies = false;
        var items = await ScanAsync();
        var logs = new List<string>();
        var result = await _engine.ExecuteSyncAsync(items, _destDir, _ => Task.FromResult(ConflictResolution.Replace), null, null,
            new InlineLogger(logs), CancellationToken.None);

        Assert.Equal(1, result.Copied);
        Assert.DoesNotContain(logs, l => l.StartsWith("Verificação"));
    }

    /// <summary>Logger síncrono para os testes (Progress&lt;T&gt; entregaria as mensagens de forma assíncrona).</summary>
    private sealed class InlineLogger(List<string> sink) : IProgress<string>
    {
        public void Report(string value) => sink.Add(value);
    }
}
