using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using OmniSync.Models;

namespace OmniSync.Services;

/// <summary>
/// Exporta o plano/resultado da sincronização em CSV compatível com o Excel em português
/// (UTF-8 com BOM, separador ponto e vírgula).
/// </summary>
public static class ReportExporter
{
    /// <summary>
    /// Grava os itens no arquivo CSV indicado.
    /// </summary>
    /// <param name="path">Caminho do arquivo de saída.</param>
    /// <param name="items">Itens a exportar.</param>
    public static async Task WriteCsvAsync(string path, IEnumerable<SyncItem> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Caminho relativo;Ação;Decisão;Status;Erro;Tamanho origem (bytes);Tamanho destino (bytes);Modificação origem;Modificação destino");
        foreach (var item in items)
        {
            sb.Append(Escape(item.RelativePath)).Append(';')
              .Append(Escape(item.ActionDisplay)).Append(';')
              .Append(item.IsConflict ? Escape(item.Resolution.ToString()) : string.Empty).Append(';')
              .Append(Escape(item.StatusMessage)).Append(';')
              .Append(item.HasError ? "sim" : "não").Append(';')
              .Append(item.SourceFullPath != null ? item.SourceSize.ToString(CultureInfo.InvariantCulture) : string.Empty).Append(';')
              .Append(item.DestinationModifiedUtc.HasValue ? item.DestinationSize.ToString(CultureInfo.InvariantCulture) : string.Empty).Append(';')
              .Append(item.SourceModifiedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty).Append(';')
              .Append(item.DestinationModifiedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty)
              .AppendLine();
        }
        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string Escape(string value)
    {
        if (value.IndexOfAny(new[] { ';', '"', '\n', '\r' }) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
