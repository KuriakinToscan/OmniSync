using System;
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
    public bool CanConfirm => !Summary.IsMassOrphan || MassOrphanAcknowledged;

    /// <summary>
    /// Texto da caixa de confirmação de remoção em massa.
    /// </summary>
    public string MassOrphanText =>
        $"Entendo que {Summary.OrphanCount} arquivo(s) ({Summary.OrphanPercent:n0}% do destino) serão movidos para lixo/.";

    /// <summary>
    /// Origem e destino em uma linha.
    /// </summary>
    public string RouteText => string.IsNullOrWhiteSpace(Summary.SourcePath)
        ? Summary.DestinationPath
        : $"{Summary.SourcePath}  →  {Summary.DestinationPath}";

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
