using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmniSync.Models;

namespace OmniSync.ViewModels;

/// <summary>
/// ViewModel responsável pela lógica da janela modal de resolução de conflitos (<see cref="Views.ConflictDialogWindow"/>).
/// Permite ao usuário comparar os metadados do arquivo na Origem e no Destino e decidir entre Substituir ou Ignorar.
/// </summary>
public partial class ConflictDialogViewModel : ViewModelBase
{
    /// <summary>
    /// Item de sincronização atualmente em conflito sendo exibido no modal.
    /// </summary>
    public SyncItem Item { get; }

    /// <summary>
    /// Quando marcado na interface, aplica a decisão escolhida (Substituir ou Ignorar)
    /// automaticamente para todos os conflitos subsequentes da mesma execução.
    /// </summary>
    [ObservableProperty]
    private bool _applyToAllRemaining = false;

    /// <summary>
    /// Decisão selecionada pelo usuário (padrão seguro: <see cref="ConflictResolution.Ignore"/>).
    /// </summary>
    public ConflictResolution Result { get; private set; } = ConflictResolution.Ignore;

    /// <summary>
    /// Evento disparado para solicitar o fechamento da janela modal após a escolha do usuário.
    /// </summary>
    public event Action? RequestClose;

    /// <summary>
    /// Inicializa uma nova instância de <see cref="ConflictDialogViewModel"/> para o item conflitante informado.
    /// </summary>
    /// <param name="item">Item em conflito a ser inspecionado pelo usuário.</param>
    public ConflictDialogViewModel(SyncItem item)
    {
        Item = item;
    }

    /// <summary>
    /// Indica se a versão da Origem é a mais recente.
    /// </summary>
    public bool IsSourceNewer => Item.ConflictDirection == ConflictDirection.SourceNewer;

    /// <summary>
    /// Indica se a versão do Destino é a mais recente.
    /// </summary>
    public bool IsDestinationNewer => Item.ConflictDirection == ConflictDirection.DestinationNewer;

    /// <summary>
    /// Frase que orienta a decisão conforme o lado mais recente.
    /// </summary>
    public string DirectionHint => Item.ConflictDirection switch
    {
        ConflictDirection.SourceNewer => "A origem foi modificada depois do destino. Substituir é o caminho usual.",
        ConflictDirection.DestinationNewer => "Atenção: o destino é mais recente que a origem. Substituir trocaria uma versão mais nova por uma mais antiga.",
        ConflictDirection.SameTime => "As datas coincidem, mas o conteúdo difere.",
        _ => "O arquivo existe nos dois lados com conteúdo diferente."
    };

    /// <summary>
    /// Comando acionado pelo botão "Substituir (Mover p/ old/)".
    /// Define o resultado como <see cref="ConflictResolution.Replace"/> e fecha o diálogo.
    /// </summary>
    [RelayCommand]
    private void Replace()
    {
        Result = ConflictResolution.Replace;
        RequestClose?.Invoke();
    }

    /// <summary>
    /// Comando acionado pelo botão "Ignorar (Manter Destino)".
    /// Define o resultado como <see cref="ConflictResolution.Ignore"/> e fecha o diálogo.
    /// </summary>
    [RelayCommand]
    private void Ignore()
    {
        Result = ConflictResolution.Ignore;
        RequestClose?.Invoke();
    }
}
