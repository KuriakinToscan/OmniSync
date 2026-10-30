using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OmniSync.Services;

/// <summary>
/// Busca de texto tolerante: ignora maiúsculas/minúsculas e acentos ("relatorio" encontra "Relatório").
/// A remoção de acentos usa a decomposição Unicode (FormD), que funciona igual com ou sem ICU.
/// </summary>
public static class TextSearch
{
    /// <summary>
    /// Converte o texto para a forma de comparação: minúsculas e sem marcas diacríticas.
    /// </summary>
    /// <param name="text">Texto original.</param>
    /// <returns>Texto normalizado.</returns>
    public static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Indica se <paramref name="text"/> contém <paramref name="query"/>, ignorando maiúsculas e acentos.
    /// </summary>
    /// <param name="text">Texto onde procurar.</param>
    /// <param name="foldedQuery">Busca já normalizada por <see cref="Fold"/>.</param>
    public static bool ContainsFolded(string? text, string foldedQuery)
    {
        if (foldedQuery.Length == 0) return true;
        return Fold(text).Contains(foldedQuery, StringComparison.Ordinal);
    }
}

/// <summary>
/// Ordenação "natural" de caminhos: trechos numéricos são comparados pelo valor
/// (<c>IMG_2</c> antes de <c>IMG_10</c>); o restante, sem diferenciar maiúsculas.
/// </summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    /// <summary>Instância compartilhada.</summary>
    public static NaturalStringComparer Instance { get; } = new();

    /// <inheritdoc />
    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x == null) return -1;
        if (y == null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                int si = i, sj = j;
                while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                var a = x.AsSpan(si, i - si).TrimStart('0');
                var b = y.AsSpan(sj, j - sj).TrimStart('0');
                if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                int digits = a.SequenceCompareTo(b);
                if (digits != 0) return digits;
                // Mesmo valor: o que tem menos zeros à esquerda vem antes ("1" antes de "01")
                int zeros = (i - si).CompareTo(j - sj);
                if (zeros != 0) return zeros;
                continue;
            }

            int c = string.Compare(x, i, y, j, 1, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            i++;
            j++;
        }
        int tail = (x.Length - i).CompareTo(y.Length - j);
        return tail != 0 ? tail : string.CompareOrdinal(x, y);
    }
}
