using System;
using System.IO;
using OmniSync.Models;
using OmniSync.Services;
using Xunit;

namespace OmniSync.Tests;

/// <summary>
/// Testes da fase C: mensagens de erro em português e formato do CSV.
/// </summary>
public class ReportAndErrorTests
{
    /// <summary>
    /// Códigos Win32 comuns viram mensagens em português com orientação.
    /// </summary>
    [Theory]
    [InlineData(unchecked((int)0x80070020), "em uso")]
    [InlineData(unchecked((int)0x80070021), "em uso")]
    [InlineData(unchecked((int)0x80070070), "Espaço insuficiente")]
    [InlineData(unchecked((int)0x80070027), "Espaço insuficiente")]
    [InlineData(unchecked((int)0x800700CE), "Caminho longo")]
    [InlineData(unchecked((int)0x80070035), "rede")]
    public void Translate_ShouldMapWindowsCodes(int hresult, string expected)
    {
        var ex = new IOException("The process cannot access the file", hresult);
        Assert.Contains(expected, ErrorMessages.Translate(ex, windows: true));
    }

    /// <summary>
    /// errno do Linux também é traduzido.
    /// </summary>
    [Theory]
    [InlineData(28, "Espaço insuficiente")]
    [InlineData(30, "protegido contra gravação")]
    [InlineData(16, "em uso")]
    public void Translate_ShouldMapUnixErrno(int errno, string expected)
    {
        Assert.Contains(expected, ErrorMessages.Translate(new IOException("No space left on device", errno), windows: false));
    }

    /// <summary>
    /// Tipos de exceção sem código conhecido usam a tradução pelo tipo; o resto mantém a mensagem original.
    /// </summary>
    [Fact]
    public void Translate_ShouldFallBackToTypeThenOriginal()
    {
        Assert.Contains("Acesso negado", ErrorMessages.Translate(new UnauthorizedAccessException("Access to the path is denied."), windows: true));
        Assert.Contains("Pasta não encontrada", ErrorMessages.Translate(new DirectoryNotFoundException("x"), windows: false));
        Assert.Equal("erro desconhecido", ErrorMessages.Translate(new IOException("erro desconhecido"), windows: true));
    }

    /// <summary>
    /// O log guarda a tradução e a mensagem original.
    /// </summary>
    [Fact]
    public void ForLog_ShouldKeepOriginalMessage()
    {
        var text = ErrorMessages.ForLog(new UnauthorizedAccessException("Access to the path is denied."));
        Assert.Contains("Acesso negado", text);
        Assert.Contains("Access to the path is denied.", text);
    }

    /// <summary>
    /// Cabeçalho ASCII, decisão em português e códigos estáveis.
    /// </summary>
    [Fact]
    public void BuildCsv_ShouldUseAsciiHeaderAndCodes()
    {
        var item = new SyncItem
        {
            RelativePath = "pasta;com/ponto.txt",
            SourceFullPath = "/s/a",
            ActionType = SyncActionType.Conflict,
            Resolution = ConflictResolution.Ignore,
            SourceSize = 10,
            DestinationSize = 20,
            SourceModifiedUtc = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc),
            DestinationModifiedUtc = new DateTime(2026, 10, 30, 11, 0, 0, DateTimeKind.Utc)
        };
        var lines = ReportExporter.BuildCsv(new[] { item }).Split(Environment.NewLine);

        Assert.Equal(ReportExporter.Header, lines[0]);
        Assert.All(lines[0], c => Assert.True(c < 128));
        Assert.StartsWith("\"pasta;com/ponto.txt\";", lines[1]);
        Assert.Contains(";conflito;Manter destino;manter;", lines[1]);
        Assert.Contains(";10;20;", lines[1]);
        Assert.Equal(11, lines[0].Split(';').Length);
    }
}
