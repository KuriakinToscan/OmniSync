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
    /// Cabeçalho do CSV: nomes ASCII em snake_case, que abrem sem problemas de codificação em Excel, LibreOffice,
    /// pandas e R. As colunas <c>*_na_analise</c> trazem o estado do destino no momento da análise
    /// (antes da sincronização); <c>*_codigo</c> trazem valores estáveis para filtros e scripts.
    /// </summary>
    public const string Header =
        "caminho_relativo;acao;acao_codigo;decisao;decisao_codigo;status;erro;" +
        "tamanho_origem_bytes;tamanho_destino_bytes_na_analise;modificacao_origem;modificacao_destino_na_analise";

    /// <summary>
    /// Grava os itens no arquivo CSV indicado.
    /// </summary>
    /// <param name="path">Caminho do arquivo de saída.</param>
    /// <param name="items">Itens a exportar.</param>
    public static async Task WriteCsvAsync(string path, IEnumerable<SyncItem> items)
    {
        await File.WriteAllTextAsync(path, BuildCsv(items), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    /// <summary>
    /// Monta o conteúdo do CSV (sem BOM).
    /// </summary>
    /// <param name="items">Itens a exportar.</param>
    public static string BuildCsv(IEnumerable<SyncItem> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Header);
        foreach (var item in items)
        {
            sb.Append(Escape(item.RelativePath)).Append(';')
              .Append(Escape(item.ActionDisplay)).Append(';')
              .Append(ActionCode(item.ActionType)).Append(';')
              .Append(item.IsConflict ? DecisionLabel(item.Resolution) : string.Empty).Append(';')
              .Append(item.IsConflict ? DecisionCode(item.Resolution) : string.Empty).Append(';')
              .Append(Escape(item.StatusMessage)).Append(';')
              .Append(item.HasError ? "sim" : "não").Append(';')
              .Append(item.SourceFullPath != null ? item.SourceSize.ToString(CultureInfo.InvariantCulture) : string.Empty).Append(';')
              .Append(item.DestinationModifiedUtc.HasValue ? item.DestinationSize.ToString(CultureInfo.InvariantCulture) : string.Empty).Append(';')
              .Append(item.SourceModifiedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty).Append(';')
              .Append(item.DestinationModifiedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty)
              .AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Código estável da ação.</summary>
    public static string ActionCode(SyncActionType action) => action switch
    {
        SyncActionType.New => "new",
        SyncActionType.Conflict => "conflict",
        SyncActionType.OrphanTrash => "orphan",
        SyncActionType.Identical => "identical",
        _ => "unknown"
    };

    /// <summary>Decisão do conflito em português, como aparece na tela.</summary>
    public static string DecisionLabel(ConflictResolution resolution) => resolution switch
    {
        ConflictResolution.Replace => "Substituir",
        ConflictResolution.Ignore => "Manter destino",
        _ => "Perguntar"
    };

    /// <summary>Código estável da decisão do conflito.</summary>
    public static string DecisionCode(ConflictResolution resolution) => resolution switch
    {
        ConflictResolution.Replace => "replace",
        ConflictResolution.Ignore => "keep",
        _ => "ask"
    };

    private static string Escape(string value)
    {
        if (value.IndexOfAny(new[] { ';', '"', '\n', '\r' }) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
