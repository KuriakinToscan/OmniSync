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
    /// Verifica se arquivos presentes exclusivamente na Origem são classificados como <see cref="SyncActionType.New"/>.
    /// </summary>
    [Fact]
    public async Task Scan_ShouldIdentifyNewFiles_WhenOnlyInSource()
    {
        var filePath = Path.Combine(_sourceDir, "novo_arquivo.txt");
        await File.WriteAllTextAsync(filePath, "conteudo novo");

        var items = await _engine.ScanDirectoriesAsync(_sourceDir, _destDir, useDeepHash: false, null, CancellationToken.None);

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

        var items = await _engine.ScanDirectoriesAsync(_sourceDir, _destDir, useDeepHash: false, null, CancellationToken.None);

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

        var items = await _engine.ScanDirectoriesAsync(_sourceDir, _destDir, useDeepHash: false, null, CancellationToken.None);

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

        var items = await _engine.ScanDirectoriesAsync(_sourceDir, _destDir, useDeepHash: false, null, CancellationToken.None);

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

        var items = await _engine.ScanDirectoriesAsync(_sourceDir, _destDir, useDeepHash: false, null, CancellationToken.None);

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

        var items = await _engine.ScanDirectoriesAsync(_sourceDir, _destDir, useDeepHash: true, null, CancellationToken.None);

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

        var items = await _engine.ScanDirectoriesAsync(_sourceDir, _destDir, useDeepHash: false, null, CancellationToken.None);

        await _engine.ExecuteSyncAsync(
            items,
            _sourceDir,
            _destDir,
            resolveConflictAsync: _ => Task.FromResult(ConflictResolution.Ignore),
            progress: null,
            logger: null,
            CancellationToken.None);

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

        var items = await _engine.ScanDirectoriesAsync(_sourceDir, _destDir, useDeepHash: false, null, CancellationToken.None);

        await _engine.ExecuteSyncAsync(
            items,
            _sourceDir,
            _destDir,
            resolveConflictAsync: _ => Task.FromResult(ConflictResolution.Ignore),
            progress: null,
            logger: null,
            CancellationToken.None);

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

        var items = await _engine.ScanDirectoriesAsync(_sourceDir, _destDir, useDeepHash: false, null, CancellationToken.None);

        await _engine.ExecuteSyncAsync(
            items,
            _sourceDir,
            _destDir,
            resolveConflictAsync: _ => Task.FromResult(ConflictResolution.Replace),
            progress: null,
            logger: null,
            CancellationToken.None);

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

        var items = await _engine.ScanDirectoriesAsync(_sourceDir, _destDir, useDeepHash: false, null, CancellationToken.None);

        await _engine.ExecuteSyncAsync(
            items,
            _sourceDir,
            _destDir,
            resolveConflictAsync: _ => Task.FromResult(ConflictResolution.Ignore),
            progress: null,
            logger: null,
            CancellationToken.None);

        Assert.True(File.Exists(dstPath));
        Assert.Equal("antigo conteudo destino", await File.ReadAllTextAsync(dstPath));
        Assert.False(Directory.Exists(Path.Combine(_destDir, "old")) && Directory.EnumerateFiles(Path.Combine(_destDir, "old")).Any());
    }
}
