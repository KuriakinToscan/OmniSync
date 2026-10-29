using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OmniSync.Services;
using Xunit;

namespace OmniSync.Tests;

/// <summary>
/// Testes do <see cref="RestoreService"/>: a restauração nunca exclui, nunca sobrescreve
/// e nunca atua fora de <c>&lt;destino&gt;/lixo/</c>.
/// </summary>
public class RestoreServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _destDir;
    private readonly string _trashDir;
    private readonly RestoreService _service = new();

    /// <summary>
    /// Cria a estrutura temporária <c>raiz/Destino/lixo</c>.
    /// </summary>
    public RestoreServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "OmniSyncRestoreTests_" + Guid.NewGuid().ToString("N"));
        _destDir = Path.Combine(_root, "Destino");
        _trashDir = Path.Combine(_destDir, "lixo");
        Directory.CreateDirectory(_trashDir);
    }

    /// <summary>
    /// Remove a estrutura temporária.
    /// </summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task WriteAsync(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    private async Task<Models.RestoreResult> RestoreAllAsync()
    {
        var entries = await _service.ListTrashAsync(_destDir, CancellationToken.None);
        return await _service.RestoreAsync(_destDir, entries, null, CancellationToken.None);
    }

    /// <summary>
    /// Arquivo cujo local original está livre volta para lá, e a pasta lixo/ vazia é removida.
    /// </summary>
    [Fact]
    public async Task Restore_ShouldMoveFileBack_AndRemoveEmptyTrash()
    {
        await WriteAsync(Path.Combine(_trashDir, "sub", "a.txt"), "a");

        var result = await RestoreAllAsync();

        Assert.Equal(1, result.Restored);
        Assert.Equal("a", await File.ReadAllTextAsync(Path.Combine(_destDir, "sub", "a.txt")));
        Assert.False(Directory.Exists(_trashDir));
    }

    /// <summary>
    /// Se o local original já está ocupado, a cópia em lixo/ é mantida intacta (antes era apagada).
    /// </summary>
    [Fact]
    public async Task Restore_ShouldKeepInTrash_WhenTargetExists()
    {
        await WriteAsync(Path.Combine(_trashDir, "doc.txt"), "versao antiga");
        await WriteAsync(Path.Combine(_destDir, "doc.txt"), "versao atual");

        var result = await RestoreAllAsync();

        Assert.Equal(0, result.Restored);
        Assert.Equal(1, result.KeptInTrash);
        Assert.Equal("versao atual", await File.ReadAllTextAsync(Path.Combine(_destDir, "doc.txt")));
        Assert.Equal("versao antiga", await File.ReadAllTextAsync(Path.Combine(_trashDir, "doc.txt")));
    }

    /// <summary>
    /// A pasta lixo/ do diretório pai do destino não é tocada (antes era despejada no pai).
    /// </summary>
    [Fact]
    public async Task Restore_ShouldIgnoreTrashOfParentFolder()
    {
        var parentTrashFile = Path.Combine(_root, "lixo", "alheio.txt");
        await WriteAsync(parentTrashFile, "de outro uso");

        var result = await RestoreAllAsync();

        Assert.Equal(0, result.Restored);
        Assert.True(File.Exists(parentTrashFile));
        Assert.False(File.Exists(Path.Combine(_root, "alheio.txt")));
    }

    /// <summary>
    /// Nomes com sufixo de desambiguação voltam com o nome original quando ele está livre.
    /// </summary>
    [Fact]
    public async Task Restore_ShouldStripCollisionSuffix_WhenOriginalNameIsFree()
    {
        await WriteAsync(Path.Combine(_trashDir, "foto_20261028_114500.jpg"), "v1");

        var result = await RestoreAllAsync();

        Assert.Equal(1, result.Restored);
        Assert.Equal("v1", await File.ReadAllTextAsync(Path.Combine(_destDir, "foto.jpg")));
    }

    /// <summary>
    /// Falhas não apagam nada: a pasta lixo/ só é removida se ficar vazia.
    /// </summary>
    [Fact]
    public async Task Restore_ShouldKeepTrashFolder_WhenSomethingRemains()
    {
        await WriteAsync(Path.Combine(_trashDir, "livre.txt"), "1");
        await WriteAsync(Path.Combine(_trashDir, "ocupado.txt"), "2");
        await WriteAsync(Path.Combine(_destDir, "ocupado.txt"), "atual");

        await RestoreAllAsync();

        Assert.True(File.Exists(Path.Combine(_trashDir, "ocupado.txt")));
        Assert.True(File.Exists(Path.Combine(_destDir, "livre.txt")));
    }

    /// <summary>
    /// Entradas que apontam para fora de lixo/ são recusadas.
    /// </summary>
    [Fact]
    public async Task Restore_ShouldRejectEntriesOutsideTrash()
    {
        var outside = Path.Combine(_root, "fora.txt");
        await WriteAsync(outside, "x");

        var entry = new Models.TrashEntry { RelativePath = "fora.txt", FullPath = outside, Size = 1, ModifiedUtc = DateTime.UtcNow };
        var result = await _service.RestoreAsync(_destDir, new[] { entry }, null, CancellationToken.None);

        Assert.Equal(1, result.Failed);
        Assert.True(File.Exists(outside));
        Assert.False(File.Exists(Path.Combine(_destDir, "fora.txt")));
    }

    /// <summary>
    /// Apenas as entradas selecionadas são restauradas.
    /// </summary>
    [Fact]
    public async Task Restore_ShouldOnlyMoveSelectedEntries()
    {
        await WriteAsync(Path.Combine(_trashDir, "a.txt"), "a");
        await WriteAsync(Path.Combine(_trashDir, "b.txt"), "b");
        var entries = await _service.ListTrashAsync(_destDir, CancellationToken.None);

        var result = await _service.RestoreAsync(_destDir, entries.Where(e => e.RelativePath == "a.txt"), null, CancellationToken.None);

        Assert.Equal(1, result.Restored);
        Assert.True(File.Exists(Path.Combine(_destDir, "a.txt")));
        Assert.True(File.Exists(Path.Combine(_trashDir, "b.txt")));
    }
}
