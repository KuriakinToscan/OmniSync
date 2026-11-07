using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using OmniSync.Services;

namespace OmniSync.Models;

/// <summary>
/// Parâmetros que controlam a varredura diferencial entre Origem e Destino.
/// </summary>
public sealed class ScanOptions
{
    /// <summary>
    /// Quando <c>true</c>, arquivos de mesmo tamanho são comparados por SHA-256 em vez de data de modificação.
    /// </summary>
    public bool UseDeepHash { get; init; }

    /// <summary>
    /// Quando <c>true</c>, diferenças de exatamente 1 hora (± 2 s) entre as datas de modificação são tratadas como
    /// idênticas. Compensa a mudança de horário de verão em mídias FAT/exFAT, que gravam hora local.
    /// </summary>
    public bool TolerateDstShift { get; init; }

    /// <summary>
    /// Padrões curinga (<c>*</c> e <c>?</c>) aplicados ao nome de arquivos e pastas que devem ser ignorados
    /// nos dois lados (ex.: <c>Thumbs.db</c>, <c>~$*</c>).
    /// </summary>
    public IReadOnlyList<string> ExcludePatterns { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Converte o texto de exclusões de uma tarefa (separado por ponto e vírgula, vírgula ou quebra de linha)
    /// em lista de padrões.
    /// </summary>
    /// <param name="text">Texto digitado pelo usuário.</param>
    public static IReadOnlyList<string> ParsePatterns(string? text) =>
        (text ?? string.Empty)
            .Split(new[] { ';', ',', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// Resultado de uma varredura diferencial: itens classificados e avisos relevantes para o usuário.
/// </summary>
public sealed class ScanResult
{
    /// <summary>
    /// Itens classificados (Novo, Idêntico, Conflito, Órfão).
    /// </summary>
    public List<SyncItem> Items { get; } = new();

    /// <summary>
    /// Avisos que o usuário deve ver antes de executar (pastas ilegíveis, nomes reservados, colisões de maiúsculas).
    /// </summary>
    public List<string> Warnings { get; } = new();

    /// <summary>
    /// Quantidade de arquivos indexados na Origem.
    /// </summary>
    public int SourceFileCount { get; set; }

    /// <summary>
    /// Quantidade de arquivos indexados no Destino (sem contar <c>lixo/</c> e <c>old/</c>).
    /// </summary>
    public int DestinationFileCount { get; set; }

    /// <summary>
    /// Temporários (<c>*.omnisync-tmp</c>) deixados no destino por uma execução anterior interrompida, modificados há
    /// mais de 1 hora. A sincronização os remove antes de copiar (<see cref="OmniSync.Services.ISyncEngine.RemoveStaleTempFiles"/>).
    /// </summary>
    public List<string> StaleTempFiles { get; } = new();
}

/// <summary>
/// Atualização de estado de um <see cref="SyncItem"/> produzida pelo motor em thread de background.
/// A interface aplica as atualizações em lote na thread de UI.
/// </summary>
/// <param name="Item">Item afetado.</param>
/// <param name="StatusMessage">Nova mensagem de status.</param>
/// <param name="IsProcessed">Se o item foi concluído com sucesso.</param>
/// <param name="HasError">Se a operação falhou.</param>
/// <param name="Resolution">Nova decisão de conflito, quando houver.</param>
public sealed record SyncItemUpdate(
    SyncItem Item,
    string StatusMessage,
    bool IsProcessed,
    bool HasError,
    ConflictResolution? Resolution = null)
{
    /// <summary>
    /// Aplica a atualização ao item. Deve ser chamado na thread que possui os bindings do item.
    /// </summary>
    public void Apply()
    {
        if (Resolution.HasValue)
        {
            Item.Resolution = Resolution.Value;
        }
        Item.StatusMessage = StatusMessage;
        Item.IsProcessed = IsProcessed;
        Item.HasError = HasError;
    }
}

/// <summary>
/// Balanço final de uma execução de sincronização.
/// </summary>
public sealed class SyncResult
{
    /// <summary>Arquivos novos copiados.</summary>
    public int Copied { get; set; }

    /// <summary>Conflitos substituídos (versão anterior preservada em <c>old/</c>).</summary>
    public int Replaced { get; set; }

    /// <summary>Órfãos movidos para <c>lixo/</c>.</summary>
    public int Trashed { get; set; }

    /// <summary>Conflitos ignorados (destino mantido).</summary>
    public int Ignored { get; set; }

    /// <summary>Itens pulados porque o arquivo já não existia.</summary>
    public int Skipped { get; set; }

    /// <summary>Total de bytes efetivamente gravados no destino.</summary>
    public long BytesCopied { get; set; }

    /// <summary>Itens que falharam.</summary>
    public List<SyncItem> FailedItems { get; } = new();

    /// <summary>Quantidade de falhas.</summary>
    public int Failed => FailedItems.Count;

    /// <summary>Indica se houve ao menos uma falha.</summary>
    public bool HasFailures => FailedItems.Count > 0;

    /// <summary>Duração total da execução.</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// Motivo da parada antes do fim (servidor remoto inacessível ou que passou a recusar o acesso); <c>null</c>
    /// quando a execução foi até o último item.
    /// </summary>
    public string? AbortReason { get; set; }

    /// <summary>Indica se a parada exige ação do usuário (senha recusada ao reconectar), e não só o servidor voltar.</summary>
    public bool AbortRequiresUser { get; set; }

    /// <summary>Itens que ficaram sem executar por causa da parada (<see cref="AbortReason"/>).</summary>
    public int NotRun { get; set; }

    /// <summary>Indica se a execução parou antes do fim.</summary>
    public bool WasAborted => AbortReason != null;

    /// <summary>Cópias conferidas por SHA-256 (relendo o arquivo ou calculado pelo servidor).</summary>
    public int VerifiedByHash { get; set; }

    /// <summary>Cópias conferidas só pelo tamanho (servidor remoto sem comando de SHA-256).</summary>
    public int VerifiedBySize { get; set; }

    /// <summary>
    /// Resumo textual em uma linha.
    /// </summary>
    public string Summary =>
        string.Join(", ",
            PtText.Count(Copied, "copiado", "copiados"),
            PtText.Count(Replaced, "substituído", "substituídos"),
            PtText.Count(Trashed, "movido", "movidos") + " p/ lixo/",
            PtText.Count(Ignored, "mantido no destino", "mantidos no destino"),
            PtText.Count(Skipped, "pulado", "pulados"),
            PtText.Count(Failed, "falha", "falhas")) +
        (NotRun > 0 ? ", " + PtText.Count(NotRun, "não executado", "não executados") : string.Empty);

    /// <summary>
    /// Resumo para o histórico: o <see cref="Summary"/>, precedido do motivo quando a execução parou e seguido da
    /// conferência só pelo tamanho, quando houve.
    /// </summary>
    public string HistorySummary =>
        (WasAborted ? (AbortRequiresUser ? "Interrompida (o servidor recusou o acesso): " : "Interrompida (servidor inacessível): ") : string.Empty) +
        Summary +
        (VerifiedBySize > 0 ? "; " + PtText.Count(VerifiedBySize, "cópia conferida", "cópias conferidas") + " só pelo tamanho" : string.Empty);
}

/// <summary>
/// Arquivo encontrado na pasta <c>lixo/</c> do destino, candidato à restauração.
/// </summary>
public partial class TrashEntry : ObservableObject
{
    /// <summary>Caminho relativo dentro de <c>lixo/</c>.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Caminho absoluto do arquivo dentro de <c>lixo/</c>.</summary>
    public required string FullPath { get; init; }

    /// <summary>Tamanho em bytes.</summary>
    public long Size { get; init; }

    /// <summary>Data de modificação (UTC).</summary>
    public DateTime ModifiedUtc { get; init; }

    /// <summary>Se o arquivo está marcado para restauração.</summary>
    [ObservableProperty]
    private bool _isSelected = true;

    /// <summary>Tamanho formatado.</summary>
    public string FormattedSize => SyncItem.FormatBytes(Size);

    /// <summary>Data formatada no fuso local.</summary>
    public string FormattedDate => ModifiedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
}

/// <summary>
/// Balanço de uma restauração a partir de <c>lixo/</c>.
/// </summary>
public sealed class RestoreResult
{
    /// <summary>Arquivos devolvidos ao local original.</summary>
    public int Restored { get; set; }

    /// <summary>Arquivos mantidos em <c>lixo/</c> porque já existe arquivo no local original.</summary>
    public int KeptInTrash { get; set; }

    /// <summary>Arquivos que falharam ao restaurar (permanecem em <c>lixo/</c>).</summary>
    public int Failed { get; set; }
}
