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
