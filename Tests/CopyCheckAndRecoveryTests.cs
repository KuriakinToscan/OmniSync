using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OmniSync.Models;
using OmniSync.Services;
using OmniSync.Services.FileSystems;
using OmniSync.Services.Remote;
using Xunit;

namespace OmniSync.Tests;

/// <summary>
/// Testes do acabamento da v2.0 (PR C): limpeza de temporários deixados por execução interrompida, conferência das
/// cópias pelo servidor ou pelo tamanho, parada quando o servidor fica inacessível e montagem segura do comando de
/// SHA-256 no servidor.
/// </summary>
public class CopyCheckAndRecoveryTests
{
    private static readonly DateTime FileTime = new(2026, 3, 5, 9, 30, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "OmniSyncPrC_" + Guid.NewGuid().ToString("N"));
    private string Src => Path.Combine(_root, "Origem");
    private string Dst => Path.Combine(_root, "Destino");

    /// <summary>
    /// Sistema de arquivos em memória com a conferência e as falhas controladas pelo teste.
    /// </summary>
    private sealed class ScriptedFileSystem : IFileSystem
    {
        private readonly MemoryFileSystem _inner;

        public ScriptedFileSystem(MemoryFileSystem inner) => _inner = inner;

        /// <summary>Conferência anunciada ao motor.</summary>
        public CopyCheck Check { get; set; } = CopyCheck.ReadBack;

        /// <summary>Resposta do "servidor" ao pedido de SHA-256 (recebe o conteúdo gravado).</summary>
        public Func<byte[], byte[]?> ServerHash { get; set; } = bytes => SHA256.HashData(bytes);

        /// <summary>Quantas gravações funcionam antes de o "servidor" ficar inacessível; <c>null</c> = nunca cai.</summary>
        public int? CreatesBeforeOutage { get; set; }

        /// <summary>Pedidos de SHA-256 ao servidor.</summary>
        public int ServerHashCalls { get; private set; }

        /// <summary>Gravações abertas com releitura.</summary>
        public int ReadBackCreates { get; private set; }

        public string Kind => "teste";
        public CopyCheck CopyCheck => Check;

        public byte[]? ComputeSha256OnServer(string path)
        {
            ServerHashCalls++;
            using var stream = _inner.OpenRead(path);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return ServerHash(buffer.ToArray());
        }

        public Stream CreateNew(string path, long expectedLength, bool readBack)
        {
            if (CreatesBeforeOutage is { } left)
            {
                if (left <= 0)
                {
                    throw new RemoteAccessException(RemoteAccessProblem.Unreachable, RemoteAddress.Parse("sftp://teste@servidor/"),
                        "O servidor servidor:22 ficou inacessível: tempo esgotado.");
                }
                CreatesBeforeOutage = left - 1;
            }
            if (readBack) ReadBackCreates++;
            return _inner.CreateNew(path, expectedLength, readBack);
        }

        public string Combine(string directory, string relativePath) => _inner.Combine(directory, relativePath);
        public string? GetDirectoryName(string path) => _inner.GetDirectoryName(path);
        public string GetRelativePath(string root, string path) => _inner.GetRelativePath(root, path);
        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
        public bool FileExists(string path) => _inner.FileExists(path);
        public void CreateDirectory(string path) => _inner.CreateDirectory(path);
        public IReadOnlyList<FsEntry> ListDirectory(string path) => _inner.ListDirectory(path);
        public IEnumerable<FsEntry> EnumerateFilesRecursive(string path) => _inner.EnumerateFilesRecursive(path);
        public FsEntry? GetFile(string path) => _inner.GetFile(path);
        public Stream OpenRead(string path) => _inner.OpenRead(path);
        public void SetLastWriteTimeUtc(string path, DateTime lastWriteUtc) => _inner.SetLastWriteTimeUtc(path, lastWriteUtc);
        public void MoveFile(string sourcePath, string targetPath) => _inner.MoveFile(sourcePath, targetPath);
        public void DeleteFile(string path) => _inner.DeleteFile(path);
        public bool IsDirectoryEmpty(string path) => _inner.IsDirectoryEmpty(path);
        public void DeleteEmptyDirectory(string path) => _inner.DeleteEmptyDirectory(path);
    }

    private (MemoryFileSystem Source, MemoryFileSystem DestStore, ScriptedFileSystem Dest) Build(int files = 3)
    {
        var source = new MemoryFileSystem();
        var destStore = new MemoryFileSystem();
        for (int i = 1; i <= files; i++)
        {
            source.Seed(Path.Combine(Src, $"arquivo{i}.txt"), $"conteudo {i}", FileTime);
        }
        destStore.CreateDirectory(Dst);
        return (source, destStore, new ScriptedFileSystem(destStore));
    }

    private static Task<SyncResult> RunAsync(SyncEngine engine, ScanResult scan, string dest) =>
        engine.ExecuteSyncAsync(scan.Items, dest, _ => Task.FromResult(ConflictResolution.Replace), null, null, null, CancellationToken.None);

    // ---------- temporários deixados por execução interrompida ----------

    /// <summary>
    /// A análise anota só os temporários do destino com mais de 1 hora; a limpeza apaga esses e deixa o recente
    /// (pode ser a cópia em andamento de outra tarefa), e nenhum temporário entra no plano.
    /// </summary>
    [Fact]
    public async Task StaleTemps_OnlyOldOnesAreListedAndRemoved()
    {
        var (source, destStore, dest) = Build(files: 1);
        var oldTemp = Path.Combine(Dst, "sub", "grande.bin" + PathHelper.TempSuffix);
        var recentTemp = Path.Combine(Dst, "andamento.bin" + PathHelper.TempSuffix);
        var sourceTemp = Path.Combine(Src, "resto.bin" + PathHelper.TempSuffix);
        destStore.Seed(oldTemp, "metade", DateTime.UtcNow.AddHours(-3));
        destStore.Seed(recentTemp, "em andamento", DateTime.UtcNow.AddMinutes(-5));
        source.Seed(sourceTemp, "origem", DateTime.UtcNow.AddHours(-3));
        var engine = new SyncEngine(source, dest);

        var scan = await engine.ScanDirectoriesAsync(Src, Dst, new ScanOptions(), null, CancellationToken.None);

        Assert.Equal(new[] { oldTemp }, scan.StaleTempFiles);
        Assert.Contains(scan.Warnings, w => w.Contains("1 arquivo temporário", StringComparison.Ordinal));
        Assert.DoesNotContain(scan.Items, i => i.RelativePath.EndsWith(PathHelper.TempSuffix, StringComparison.Ordinal));

        var logs = new List<string>();
        int removed = engine.RemoveStaleTempFiles(Dst, scan.StaleTempFiles, new InlineLog(logs));

        Assert.Equal(1, removed);
        Assert.False(destStore.FileExists(oldTemp));
        Assert.True(destStore.FileExists(recentTemp));
        Assert.True(source.FileExists(sourceTemp));
        Assert.Contains(logs, l => l.Contains("1 temporário de execução anterior removido", StringComparison.Ordinal));
    }

    /// <summary>
    /// A limpeza confere de novo cada arquivo: fora do destino, sem o sufixo ou que ficou recente não é apagado.
    /// </summary>
    [Fact]
    public void StaleTemps_RemovalRechecksEachFile()
    {
        var (_, destStore, dest) = Build(files: 0);
        var outside = Path.Combine(_root, "Outra", "x.bin" + PathHelper.TempSuffix);
        var notTemp = Path.Combine(Dst, "dados.bin");
        var touched = Path.Combine(Dst, "voltou.bin" + PathHelper.TempSuffix);
        destStore.Seed(outside, "a", DateTime.UtcNow.AddDays(-1));
        destStore.Seed(notTemp, "b", DateTime.UtcNow.AddDays(-1));
        destStore.Seed(touched, "c", DateTime.UtcNow);
        var engine = new SyncEngine(new MemoryFileSystem(), dest);

        int removed = engine.RemoveStaleTempFiles(Dst, new[] { outside, notTemp, touched, Path.Combine(Dst, "sumiu" + PathHelper.TempSuffix) }, null);

        Assert.Equal(0, removed);
        Assert.True(destStore.FileExists(outside));
        Assert.True(destStore.FileExists(notTemp));
        Assert.True(destStore.FileExists(touched));
    }

    // ---------- conferência das cópias ----------

    /// <summary>
    /// Com SHA no servidor, o temporário não é relido pela rede: o servidor calcula o hash de cada cópia, e o
    /// resultado conta as cópias conferidas por SHA-256.
    /// </summary>
    [Fact]
    public async Task ServerHash_ChecksEachCopyWithoutReadingBack()
    {
        var (source, destStore, dest) = Build();
        dest.Check = CopyCheck.ServerHash;
        var engine = new SyncEngine(source, dest);
        var scan = await engine.ScanDirectoriesAsync(Src, Dst, new ScanOptions(), null, CancellationToken.None);

        var result = await RunAsync(engine, scan, Dst);

        Assert.Equal(0, result.Failed);
        Assert.Equal(3, result.Copied);
        Assert.Equal(3, result.VerifiedByHash);
        Assert.Equal(0, result.VerifiedBySize);
        Assert.Equal(3, dest.ServerHashCalls);
        Assert.Equal(0, dest.ReadBackCreates);
        Assert.Equal("conteudo 2", destStore.Read(Path.Combine(Dst, "arquivo2.txt")));
    }

    /// <summary>
    /// SHA do servidor diferente do enviado: a cópia é repetida e, persistindo, falha sem nada no lugar do arquivo.
    /// </summary>
    [Fact]
    public async Task ServerHash_MismatchFailsWithoutTouchingDestination()
    {
        var (source, destStore, dest) = Build(files: 1);
        dest.Check = CopyCheck.ServerHash;
        dest.ServerHash = _ => new byte[32];
        var engine = new SyncEngine(source, dest);
        var scan = await engine.ScanDirectoriesAsync(Src, Dst, new ScanOptions(), null, CancellationToken.None);

        var result = await RunAsync(engine, scan, Dst);

        Assert.Equal(1, result.Failed);
        Assert.Contains("SHA-256", result.FailedItems[0].StatusMessage, StringComparison.Ordinal);
        Assert.Equal(3, dest.ServerHashCalls);
        Assert.Empty(destStore.AllFiles());
    }

    /// <summary>
    /// O servidor anuncia SHA mas não consegue calcular (caminho diferente no shell): a cópia é conferida pelo
    /// tamanho e não falha.
    /// </summary>
    [Fact]
    public async Task ServerHash_UnavailableFallsBackToSize()
    {
        var (source, _, dest) = Build(files: 2);
        dest.Check = CopyCheck.ServerHash;
        dest.ServerHash = _ => null;
        var engine = new SyncEngine(source, dest);
        var scan = await engine.ScanDirectoriesAsync(Src, Dst, new ScanOptions(), null, CancellationToken.None);

        var result = await RunAsync(engine, scan, Dst);

        Assert.Equal(0, result.Failed);
        Assert.Equal(0, result.VerifiedByHash);
        Assert.Equal(2, result.VerifiedBySize);
        Assert.Contains("2 cópias conferidas só pelo tamanho", result.HistorySummary, StringComparison.Ordinal);
    }

    /// <summary>
    /// Servidor sem comando de SHA: só o tamanho, sem pedir hash nem reler pela rede.
    /// </summary>
    [Fact]
    public async Task SizeCheck_NeitherReadsBackNorAsksServer()
    {
        var (source, destStore, dest) = Build(files: 2);
        dest.Check = CopyCheck.Size;
        var engine = new SyncEngine(source, dest);
        var scan = await engine.ScanDirectoriesAsync(Src, Dst, new ScanOptions(), null, CancellationToken.None);

        var result = await RunAsync(engine, scan, Dst);

        Assert.Equal(0, result.Failed);
        Assert.Equal(2, result.VerifiedBySize);
        Assert.Equal(0, dest.ServerHashCalls);
        Assert.Equal(0, dest.ReadBackCreates);
        Assert.Equal(FileTime, destStore.GetFile(Path.Combine(Dst, "arquivo1.txt"))!.Value.LastWriteUtc);
    }

    /// <summary>
    /// Disco local e SMB continuam relendo o arquivo gravado, como antes do PR C.
    /// </summary>
    [Fact]
    public async Task ReadBack_RemainsTheDefault()
    {
        var (source, _, dest) = Build(files: 2);
        var engine = new SyncEngine(source, dest);
        var scan = await engine.ScanDirectoriesAsync(Src, Dst, new ScanOptions(), null, CancellationToken.None);

        var result = await RunAsync(engine, scan, Dst);

        Assert.Equal(2, result.VerifiedByHash);
        Assert.Equal(2, dest.ReadBackCreates);
        Assert.Equal(0, dest.ServerHashCalls);
    }

    // ---------- servidor inacessível no meio ----------

    /// <summary>
    /// O servidor cai depois de 2 cópias: a execução para no item seguinte, que falha, e os demais ficam como
    /// não executados, sem novas tentativas nem espera. O que já foi copiado fica, e uma nova execução continua.
    /// </summary>
    [Fact]
    public async Task Outage_StopsRunAndLeavesRestForNextRun()
    {
        var (source, destStore, dest) = Build(files: 5);
        dest.CreatesBeforeOutage = 2;
        var engine = new SyncEngine(source, dest);
        var scan = await engine.ScanDirectoriesAsync(Src, Dst, new ScanOptions(), null, CancellationToken.None);

        var started = DateTime.UtcNow;
        var result = await RunAsync(engine, scan, Dst);

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1), "não deve repetir nem esperar com o servidor fora");
        Assert.True(result.WasAborted);
        Assert.False(result.AbortRequiresUser);
        Assert.Contains("inacessível", result.AbortReason, StringComparison.Ordinal);
        Assert.Equal(2, result.Copied);
        Assert.Equal(1, result.Failed);
        Assert.Equal(2, result.NotRun);
        Assert.Contains("2 não executados", result.Summary, StringComparison.Ordinal);
        Assert.StartsWith("Interrompida (servidor inacessível)", result.HistorySummary, StringComparison.Ordinal);
        var notRun = scan.Items.Where(i => i.StatusMessage == "Não executado: servidor inacessível").ToList();
        Assert.Equal(2, notRun.Count);
        Assert.All(notRun, i => Assert.False(i.IsProcessed || i.HasError));
        Assert.Equal(2, destStore.AllFiles().Count);
        Assert.DoesNotContain(destStore.AllFiles(), f => f.EndsWith(PathHelper.TempSuffix, StringComparison.Ordinal));

        // O servidor voltou: a nova análise mostra só o que faltou
        dest.CreatesBeforeOutage = null;
        var again = await engine.ScanDirectoriesAsync(Src, Dst, new ScanOptions(), null, CancellationToken.None);
        Assert.Equal(3, again.Items.Count(i => i.ActionType == SyncActionType.New));
        var resumed = await RunAsync(engine, again, Dst);
        Assert.False(resumed.WasAborted);
        Assert.Equal(3, resumed.Copied);
        Assert.Equal(5, destStore.AllFiles().Count);
    }

    // ---------- comando de SHA-256 no servidor ----------

    /// <summary>
    /// No shell POSIX o caminho vai entre aspas simples, com as aspas internas escapadas: nome de arquivo nunca vira comando.
    /// </summary>
    [Theory]
    [InlineData("/dados/a.txt", "sha256sum -b -- '/dados/a.txt'")]
    [InlineData("/dados/com espaço é.txt", "sha256sum -b -- '/dados/com espaço é.txt'")]
    [InlineData("/d/it's; rm -rf ~ $(x) `y`.txt", "sha256sum -b -- '/d/it'\\''s; rm -rf ~ $(x) `y`.txt'")]
    public void HashCommand_PosixQuotesPath(string path, string expected)
    {
        Assert.Equal(expected, SftpFileSystem.BuildHashCommand(SftpFileSystem.ServerHashTool.Sha256Sum, path));
    }

    /// <summary>
    /// No OpenSSH do Windows o comando vai codificado (<c>-EncodedCommand</c>), com <c>/C:/</c> convertido em <c>C:/</c>
    /// e as aspas simples dobradas dentro do texto do PowerShell.
    /// </summary>
    [Fact]
    public void HashCommand_PowerShellIsEncoded()
    {
        var command = SftpFileSystem.BuildHashCommand(SftpFileSystem.ServerHashTool.PowerShell, "/C:/Users/ana/O'Brien & cia.txt");

        const string prefix = "powershell -NoProfile -NonInteractive -EncodedCommand ";
        Assert.StartsWith(prefix, command, StringComparison.Ordinal);
        var encoded = command[prefix.Length..];
        Assert.Matches("^[A-Za-z0-9+/=]+$", encoded);
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
        Assert.Contains("-LiteralPath 'C:/Users/ana/O''Brien & cia.txt'", script, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash -Algorithm SHA256", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// A saída do <c>sha256sum</c> (inclusive com <c>\</c> inicial) e a do <c>Get-FileHash</c> (maiúsculas) viram o hash;
    /// saída curta, vazia ou com lixo vira <c>null</c> (conferência pelo tamanho).
    /// </summary>
    [Fact]
    public void ParseHash_ReadsBothToolsAndRejectsGarbage()
    {
        var expected = SHA256.HashData("ok"u8);
        var hex = Convert.ToHexString(expected).ToLowerInvariant();

        Assert.Equal(expected, SftpFileSystem.ParseHash($"{hex} */dados/a.txt\n"));
        Assert.Equal(expected, SftpFileSystem.ParseHash($"\\{hex} */dados/a\\\\b.txt\n"));
        Assert.Equal(expected, SftpFileSystem.ParseHash(hex.ToUpperInvariant() + "\r\n"));
        Assert.Null(SftpFileSystem.ParseHash(null));
        Assert.Null(SftpFileSystem.ParseHash(""));
        Assert.Null(SftpFileSystem.ParseHash("sha256sum: /x: No such file or directory"));
        Assert.Null(SftpFileSystem.ParseHash(hex[..60]));
        Assert.Null(SftpFileSystem.ParseHash(hex + "abc  x"));
    }

    /// <summary>Canal de log que guarda as mensagens na hora (sem contexto de sincronização).</summary>
    private sealed class InlineLog : IProgress<string>
    {
        private readonly List<string> _lines;
        public InlineLog(List<string> lines) => _lines = lines;
        public void Report(string value) => _lines.Add(value);
    }
}
