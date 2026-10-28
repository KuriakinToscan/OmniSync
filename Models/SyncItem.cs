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
    /// Notifica a interface gráfica para atualizar as propriedades derivadas sempre que <see cref="Resolution"/> mudar.
    /// </summary>
    partial void OnResolutionChanged(ConflictResolution value)
    {
        OnPropertyChanged(nameof(ResolutionIndex));
    }
}
