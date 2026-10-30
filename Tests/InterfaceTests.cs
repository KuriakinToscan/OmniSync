using System;
using System.Linq;
using OmniSync.Services;
using OmniSync.ViewModels;
using Xunit;

namespace OmniSync.Tests;

/// <summary>
/// Testes da fase "interface": busca sem acentos, ordenação natural, tempos do registro,
/// percentual durante decisões pendentes e textos de resultado.
/// </summary>
public class InterfaceTests
{
    /// <summary>
    /// A busca ignora maiúsculas e acentos nos dois sentidos.
    /// </summary>
    [Theory]
    [InlineData("Relatório_Final.pdf", "relatorio", true)]
    [InlineData("relatorio_final.pdf", "RELATÓRIO", true)]
    [InlineData("Ação/Imagens", "acao/ima", true)]
    [InlineData("Ação/Imagens", "acão/imagens", true)]
    [InlineData("fotos/IMG_10.jpg", "img_2", false)]
    [InlineData("qualquer", "", true)]
    public void TextSearch_ShouldIgnoreCaseAndAccents(string text, string query, bool expected)
    {
        Assert.Equal(expected, TextSearch.ContainsFolded(text, TextSearch.Fold(query)));
    }

    /// <summary>
    /// Números dentro dos nomes são ordenados pelo valor; o restante, sem diferenciar maiúsculas.
    /// </summary>
    [Fact]
    public void NaturalStringComparer_ShouldOrderNumbersByValue()
    {
        var input = new[] { "IMG_10.jpg", "img_2.jpg", "IMG_1.jpg", "b/a.txt", "a/z.txt", "IMG_02.jpg" };
        var sorted = input.OrderBy(x => x, NaturalStringComparer.Instance).ToArray();
        Assert.Equal(new[] { "a/z.txt", "b/a.txt", "IMG_1.jpg", "img_2.jpg", "IMG_02.jpg", "IMG_10.jpg" }, sorted);
    }

    /// <summary>
    /// Tempos abaixo de 1 s aparecem em milissegundos, em vez de "0,0 s".
    /// </summary>
    [Fact]
    public void FormatElapsed_ShouldUseMillisecondsBelowOneSecond()
    {
        Assert.EndsWith(" ms", SyncEngine.FormatElapsed(TimeSpan.FromMilliseconds(42)));
        Assert.StartsWith("42", SyncEngine.FormatElapsed(TimeSpan.FromMilliseconds(42)));
        Assert.EndsWith(" s", SyncEngine.FormatElapsed(TimeSpan.FromSeconds(3.5)));
        Assert.DoesNotContain("ms", SyncEngine.FormatElapsed(TimeSpan.FromSeconds(12)));
    }

    /// <summary>
    /// Enquanto houver arquivo por terminar (por exemplo, um conflito aguardando decisão), o percentual não chega a 100%.
    /// </summary>
    [Fact]
    public void CapPercent_ShouldStayBelow100WhileFilesRemain()
    {
        Assert.Equal(99, MainViewModel.CapPercent(100, 9, 10));
        Assert.Equal(40, MainViewModel.CapPercent(40, 4, 10));
        Assert.Equal(100, MainViewModel.CapPercent(100, 10, 10));
    }

    /// <summary>
    /// O resultado da restauração concorda em número ("1 mantido", "2 mantidos") e omite partes zeradas.
    /// </summary>
    [Fact]
    public void DescribeRestoreResult_ShouldUseCorrectAgreement()
    {
        var one = MainViewModel.DescribeRestoreResult(1, 1, 0);
        Assert.Contains("1 arquivo restaurado", one);
        Assert.Contains("1 mantido em lixo/ (já existia no destino)", one);
        Assert.DoesNotContain("falha", one);

        var many = MainViewModel.DescribeRestoreResult(3, 0, 2);
        Assert.Contains("3 arquivos restaurados", many);
        Assert.Contains("2 falhas", many);
        Assert.DoesNotContain("mantido", many);
    }
}
