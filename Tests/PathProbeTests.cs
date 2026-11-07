using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OmniSync.Services;
using Xunit;

namespace OmniSync.Tests;

/// <summary>
/// Verificação de pasta fora da interface com limite de tempo (unidade de rede caída não pode congelar a janela).
/// </summary>
public class PathProbeTests
{
    [Fact]
    public async Task ExistingAndMissingFolders_AreReported()
    {
        var dir = Directory.CreateTempSubdirectory("omnisync-probe-");
        try
        {
            Assert.Equal(ProbeResult.Exists, await PathProbe.DirectoryExistsAsync(dir.FullName));
            Assert.Equal(ProbeResult.Missing, await PathProbe.DirectoryExistsAsync(Path.Combine(dir.FullName, "nao-existe")));
            Assert.Equal(ProbeResult.Missing, await PathProbe.DirectoryExistsAsync("  "));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public async Task HangingCheck_ReturnsTimedOutWithinLimit()
    {
        using var release = new ManualResetEventSlim(false);
        var watch = Stopwatch.StartNew();
        // Simula o Directory.Exists preso numa unidade de rede caída
        var result = await PathProbe.DirectoryExistsAsync(@"Z:\nas\pasta", _ => { release.Wait(TimeSpan.FromSeconds(30)); return true; },
            TimeSpan.FromMilliseconds(300));
        watch.Stop();
        release.Set();

        Assert.Equal(ProbeResult.TimedOut, result);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Demorou {watch.Elapsed.TotalSeconds:F1} s");
    }

    [Fact]
    public async Task FailingCheck_ReturnsFailed()
    {
        var result = await PathProbe.DirectoryExistsAsync("/x", _ => throw new IOException("conexão caiu"));
        Assert.Equal(ProbeResult.Failed, result);
    }

    [Fact]
    public void TimeoutMessage_NamesPathAndSeconds()
    {
        var message = PathProbe.TimeoutMessage(@"Z:\ominysinc\Campo", TimeSpan.FromSeconds(5));
        Assert.Contains(@"Z:\ominysinc\Campo", message);
        Assert.Contains("5 s", message);
    }

    [Theory]
    [InlineData("Nenhuma conexão pôde ser feita", "nenhuma conexão pôde ser feita")]
    [InlineData("SSH recusou", "SSH recusou")]
    [InlineData("", "")]
    [InlineData("x", "x")]
    public void AfterColon_LowersOnlyOrdinaryWords(string input, string expected)
    {
        Assert.Equal(expected, ErrorMessages.AfterColon(input));
    }
}
