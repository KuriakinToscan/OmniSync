using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OmniSync.ViewModels;

/// <summary>
/// ViewModel do diálogo de confirmação exibido antes de executar a sincronização.
/// Em remoções em massa, exige confirmação explícita do usuário.
/// </summary>
public partial class ConfirmSyncViewModel : ViewModelBase
{
    /// <summary>
    /// Resumo do plano a confirmar.
    /// </summary>
    public SyncPlanSummary Summary { get; }

    /// <summary>
    /// Marcação "entendo que N arquivos irão para lixo/", exigida quando <see cref="SyncPlanSummary.IsMassOrphan"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private bool _massOrphanAcknowledged;

    /// <summary>
    /// Marcação "confirmo substituir versões mais novas do destino", exigida quando
    /// <see cref="SyncPlanSummary.HasDestinationNewerReplacements"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private bool _destinationNewerAcknowledged;

    /// <summary>
    /// Resultado: <c>true</c> se o usuário confirmou.
    /// </summary>
    public bool Result { get; private set; }

    /// <summary>
    /// Evento disparado para fechar a janela.
    /// </summary>
    public event Action? RequestClose;

    /// <summary>
    /// Indica se o botão Sincronizar está habilitado.
    /// </summary>
    public bool CanConfirm =>
        (!Summary.IsMassOrphan || MassOrphanAcknowledged) &&
        (!Summary.HasDestinationNewerReplacements || DestinationNewerAcknowledged);

    /// <summary>
    /// Lista dos arquivos em que uma versão mais nova do destino será substituída (até 8, mais o restante em número).
    /// </summary>
    public string DestinationNewerListText
    {
        get
        {
            const int max = 8;
            var list = Summary.DestinationNewerReplacements;
            var lines = list.Take(max).Select(p => "• " + p).ToList();
            if (list.Count > max)
            {
                lines.Add($"… e mais {list.Count - max}");
            }
            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>
    /// Texto da caixa de confirmação das substituições de versões mais novas.
    /// </summary>
    public string DestinationNewerText => Summary.DestinationNewerReplacements.Count == 1
        ? "Confirmo substituir este arquivo, embora a versão do destino seja mais nova. A versão atual vai para old/."
        : $"Confirmo substituir estes {Summary.DestinationNewerReplacements.Count} arquivos, embora as versões do destino sejam mais novas. As versões atuais vão para old/.";

    /// <summary>
    /// Texto da caixa de confirmação de remoção em massa.
    /// </summary>
    public string MassOrphanText =>
        $"Entendo que {Summary.OrphanCount:N0} {(Summary.OrphanCount == 1 ? "arquivo" : "arquivos")} ({Summary.OrphanPercent:n0}% do destino) {(Summary.OrphanCount == 1 ? "será movido" : "serão movidos")} para lixo/.";

    /// <summary>
    /// Parcela do destino que irá para <c>lixo/</c>.
    /// </summary>
    public string OrphanShareText => Summary.DestinationFileCount > 0
        ? $"{Summary.OrphanPercent:0}% do destino"
        : "—";

    /// <summary>
    /// Indica se há conflitos sem decisão.
    /// </summary>
    public bool HasAsk => Summary.AskCount > 0;

    /// <summary>
    /// Inicializa o diálogo com o resumo do plano.
    /// </summary>
    public ConfirmSyncViewModel(SyncPlanSummary summary)
    {
        Summary = summary;
    }

    /// <summary>
    /// Confirma a execução.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        Result = true;
        RequestClose?.Invoke();
    }

    /// <summary>
    /// Desiste da execução.
    /// </summary>
    [RelayCommand]
    private void Cancel()
    {
        Result = false;
        RequestClose?.Invoke();
    }
}
