using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace OmniSync.Models;

/// <summary>
/// Representa um arquivo individual mapeado durante a análise diferencial entre Origem e Destino.
/// Implementa <see cref="ObservableObject"/> para atualização reativa na grade da interface gráfica.
/// </summary>
public partial class SyncItem : ObservableObject
{
    /// <summary>
    /// Caminho relativo do arquivo em relação à raiz da pasta sincronizada (ex.: <c>Subpasta\arquivo.jpg</c>).
    /// </summary>
    public required string RelativePath { get; set; }

    /// <summary>
    /// Caminho absoluto completo do arquivo no diretório de Origem, ou <c>null</c> quando o arquivo é órfão (existe só no Destino).
    /// </summary>
    public string? SourceFullPath { get; set; }

    /// <summary>
    /// Caminho absoluto completo onde o arquivo existe (ou será gravado) no diretório de Destino.
    /// </summary>
    public string? DestinationFullPath { get; set; }

    /// <summary>
    /// Tamanho do arquivo na Origem em bytes (0 caso não exista na Origem).
    /// </summary>
    public long SourceSize { get; set; }

    /// <summary>
    /// Tamanho do arquivo no Destino em bytes (0 caso ainda não exista no Destino).
    /// </summary>
    public long DestinationSize { get; set; }

    /// <summary>
    /// Data/hora da última modificação (UTC) do arquivo na Origem.
    /// </summary>
    public DateTime? SourceModifiedUtc { get; set; }

    /// <summary>
    /// Data/hora da última modificação (UTC) do arquivo no Destino.
    /// </summary>
    public DateTime? DestinationModifiedUtc { get; set; }

    /// <summary>
    /// Tipo de ação de sincronização atribuída ao arquivo após a análise diferencial.
    /// </summary>
    public SyncActionType ActionType { get; set; }

    /// <summary>
    /// Resolução de conflito escolhida para este item (aplicável quando <see cref="ActionType"/> é <see cref="SyncActionType.Conflict"/>).
    /// </summary>
    [ObservableProperty]
    private ConflictResolution _resolution = ConflictResolution.AskUser;

    /// <summary>
    /// Mensagem descritiva do estado atual do item (ex.: "Novo (Pendente cópia)", "Copiado com sucesso").
    /// </summary>
    [ObservableProperty]
    private string _statusMessage = "Pendente";

    /// <summary>
    /// Indica se este item já foi processado com sucesso na sessão atual de sincronização.
    /// </summary>
    [ObservableProperty]
    private bool _isProcessed = false;

    /// <summary>
    /// Caminho exibido na lista: relativo à pasta selecionada na árvore (ou o caminho relativo completo).
    /// </summary>
    [ObservableProperty]
    private string? _displayPath;

    /// <summary>
    /// Indica se a última tentativa de processar este item terminou em erro.
    /// </summary>
    [ObservableProperty]
    private bool _hasError = false;

    /// <summary>Indica se o item é um arquivo novo (somente na Origem).</summary>
    public bool IsNew => ActionType == SyncActionType.New;

    /// <summary>Indica se o item é idêntico nos dois lados.</summary>
    public bool IsIdentical => ActionType == SyncActionType.Identical;

    /// <summary>Indica se o item está em conflito (mesmo caminho, conteúdo diferente).</summary>
    public bool IsConflict => ActionType == SyncActionType.Conflict;

    /// <summary>Indica se o item é órfão (somente no Destino).</summary>
    public bool IsOrphan => ActionType == SyncActionType.OrphanTrash;

    /// <summary>
    /// Indica qual lado possui a versão modificada mais recentemente em um conflito.
    /// </summary>
    public ConflictDirection ConflictDirection
    {
        get
        {
            if (ActionType != SyncActionType.Conflict || !SourceModifiedUtc.HasValue || !DestinationModifiedUtc.HasValue)
            {
                return ConflictDirection.None;
            }

            var delta = (SourceModifiedUtc.Value - DestinationModifiedUtc.Value).TotalSeconds;
            if (Math.Abs(delta) <= 2.0) return ConflictDirection.SameTime;
            return delta > 0 ? ConflictDirection.SourceNewer : ConflictDirection.DestinationNewer;
        }
    }

    /// <summary>
    /// Índice da decisão de conflito para o seletor da grade: 0 = Perguntar, 1 = Substituir, 2 = Ignorar.
    /// </summary>
    public int ResolutionIndex
    {
        get => Resolution switch
        {
            ConflictResolution.Replace => 1,
            ConflictResolution.Ignore => 2,
            _ => 0
        };
        set => Resolution = value switch
        {
            1 => ConflictResolution.Replace,
            2 => ConflictResolution.Ignore,
            _ => ConflictResolution.AskUser
        };
    }

    /// <summary>Decisão "Substituir" (para os botões de opção do painel de detalhes).</summary>
    public bool IsResolutionReplace
    {
        get => Resolution == ConflictResolution.Replace;
        set { if (value) Resolution = ConflictResolution.Replace; }
    }

    /// <summary>Decisão "Manter destino" (para os botões de opção do painel de detalhes).</summary>
    public bool IsResolutionIgnore
    {
        get => Resolution == ConflictResolution.Ignore;
        set { if (value) Resolution = ConflictResolution.Ignore; }
    }

    /// <summary>Decisão "Perguntar na hora" (para os botões de opção do painel de detalhes).</summary>
    public bool IsResolutionAsk
    {
        get => Resolution == ConflictResolution.AskUser;
        set { if (value) Resolution = ConflictResolution.AskUser; }
    }

    /// <summary>
    /// Nome do arquivo sem a pasta.
    /// </summary>
    public string FileName => System.IO.Path.GetFileName(RelativePath);

    /// <summary>
    /// Pasta relativa que contém o arquivo (vazia na raiz).
    /// </summary>
    public string FolderPath => System.IO.Path.GetDirectoryName(RelativePath) ?? string.Empty;

    /// <summary>
    /// Pasta relativa para exibição ("raiz" quando vazia).
    /// </summary>
    public string FolderDisplay => string.IsNullOrEmpty(FolderPath) ? "(raiz)" : FolderPath;

    /// <summary>
    /// Lado com a versão mais recente, para a coluna "Mais recente" da lista.
    /// </summary>
    public string NewerSideText => ConflictDirection switch
    {
        ConflictDirection.SourceNewer => "Origem",
        ConflictDirection.DestinationNewer => "Destino",
        ConflictDirection.SameTime => "Mesma hora",
        _ => "—"
    };

    /// <summary>Indica conflito em que a origem é mais recente.</summary>
    public bool IsSourceNewer => ConflictDirection == ConflictDirection.SourceNewer;

    /// <summary>Indica conflito em que o destino é mais recente.</summary>
    public bool IsDestinationNewer => ConflictDirection == ConflictDirection.DestinationNewer;

    /// <summary>Indica se o item é um conflito ainda decidível (não processado).</summary>
    public bool CanDecide => IsConflict && !IsProcessed;

    /// <summary>Indica conflito pendente ainda sem decisão (será perguntado na hora).</summary>
    public bool NeedsDecision => IsConflict && !IsProcessed && Resolution == ConflictResolution.AskUser;

    /// <summary>Indica se existe versão na origem.</summary>
    public bool HasSource => SourceFullPath != null;

    /// <summary>Indica se existe versão no destino.</summary>
    public bool HasDestination => DestinationModifiedUtc.HasValue;

    /// <summary>
    /// Tamanho exibido na lista: o da origem, ou o do destino para órfãos.
    /// </summary>
    public string DisplaySize => SourceFullPath != null ? FormatBytes(SourceSize) : FormatBytes(DestinationSize);

    /// <summary>
    /// Explicação do que acontecerá com o item, para o painel de detalhes.
    /// </summary>
    public string PlanDescription => ActionType switch
    {
        SyncActionType.New => "Existe só na origem. Será copiado para o destino com a mesma data de modificação.",
        SyncActionType.OrphanTrash => "Existe só no destino. Será movido para lixo/, sem ser apagado.",
        SyncActionType.Identical => "Igual nos dois lados. Nada será feito.",
        SyncActionType.Conflict => "Existe nos dois lados com conteúdo diferente. Escolha o que fazer:",
        _ => string.Empty
    };

    /// <summary>
    /// Verbo curto da ação planejada, exibido na lista.
    /// </summary>
    public string ActionVerb => ActionType switch
    {
        SyncActionType.New => "Copiar",
        SyncActionType.Conflict => "Conflito",
        SyncActionType.OrphanTrash => "Para lixo/",
        SyncActionType.Identical => "Idêntico",
        _ => "—"
    };

    /// <summary>
    /// Ação exibida na lista: o verbo da categoria, ou a decisão quando o item é conflito.
    /// </summary>
    public string ActionSummary => ActionType == SyncActionType.Conflict ? ResolutionText : ActionVerb;

    /// <summary>
    /// Texto da decisão para a lista (vazio quando o item não é conflito).
    /// </summary>
    public string ResolutionText => ActionType != SyncActionType.Conflict
        ? string.Empty
        : Resolution switch
        {
            ConflictResolution.Replace => "Substituir",
            ConflictResolution.Ignore => "Manter destino",
            _ => "Perguntar"
        };

    /// <summary>
    /// Texto amigável exibido na coluna "Ação" da tabela de prévia.
    /// </summary>
    public string ActionDisplay => ActionType switch
    {
        SyncActionType.Identical => "Idêntico",
        SyncActionType.New => "Novo (copiar)",
        SyncActionType.OrphanTrash => "Órfão (p/ lixo/)",
        SyncActionType.Conflict => ConflictDirection switch
        {
            ConflictDirection.SourceNewer => "Conflito · origem nova",
            ConflictDirection.DestinationNewer => "Conflito · destino novo",
            _ => "Conflito"
        },
        _ => "Desconhecido"
    };

    /// <summary>
    /// Tamanho do arquivo na Origem formatado em unidades legíveis (B, KB, MB, GB, TB).
    /// </summary>
    public string FormattedSourceSize => SourceFullPath != null ? FormatBytes(SourceSize) : "—";

    /// <summary>
    /// Tamanho do arquivo no Destino formatado em unidades legíveis (B, KB, MB, GB, TB).
    /// </summary>
    public string FormattedDestinationSize => DestinationModifiedUtc.HasValue ? FormatBytes(DestinationSize) : "—";

    /// <summary>
    /// Data de modificação na Origem convertida para o fuso horário local e formatada.
    /// </summary>
    public string FormattedSourceDate => SourceModifiedUtc.HasValue
        ? SourceModifiedUtc.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss")
        : "—";

    /// <summary>
    /// Data de modificação no Destino convertida para o fuso horário local e formatada.
    /// </summary>
    public string FormattedDestinationDate => DestinationModifiedUtc.HasValue
        ? DestinationModifiedUtc.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss")
        : "—";

    /// <summary>
    /// Data de modificação na Origem em formato curto para a grade (sem segundos).
    /// </summary>
    public string ShortSourceDate => SourceModifiedUtc.HasValue
        ? SourceModifiedUtc.Value.ToLocalTime().ToString("dd/MM/yy HH:mm")
        : "—";

    /// <summary>
    /// Data de modificação no Destino em formato curto para a grade (sem segundos).
    /// </summary>
    public string ShortDestinationDate => DestinationModifiedUtc.HasValue
        ? DestinationModifiedUtc.Value.ToLocalTime().ToString("dd/MM/yy HH:mm")
        : "—";

    /// <summary>
    /// Converte uma quantidade de bytes para representação textual legível por humanos (B, KB, MB, GB, TB).
    /// </summary>
    /// <param name="bytes">Quantidade de bytes a formatar.</param>
    /// <returns>String formatada com uma casa decimal e sufixo da unidade.</returns>
    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024) >= 1)
        {
            number /= 1024;
            counter++;
            if (counter >= suffixes.Length - 1) break;
        }
        return $"{number:n1} {suffixes[counter]}";
    }

    /// <summary>
    /// Notifica a interface quando o item deixa de ser decidível.
    /// </summary>
    partial void OnIsProcessedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanDecide));
        OnPropertyChanged(nameof(NeedsDecision));
    }

    /// <summary>
    /// Notifica a interface gráfica para atualizar as propriedades derivadas sempre que <see cref="Resolution"/> mudar.
    /// </summary>
    partial void OnResolutionChanged(ConflictResolution value)
    {
        OnPropertyChanged(nameof(ResolutionIndex));
        OnPropertyChanged(nameof(IsResolutionReplace));
        OnPropertyChanged(nameof(IsResolutionIgnore));
        OnPropertyChanged(nameof(IsResolutionAsk));
        OnPropertyChanged(nameof(ResolutionText));
        OnPropertyChanged(nameof(ActionSummary));
        OnPropertyChanged(nameof(NeedsDecision));
    }
}
