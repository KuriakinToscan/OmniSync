using System;
using Avalonia.Controls;
using OmniSync.ViewModels;

namespace OmniSync.Views;

/// <summary>
/// Janela modal exibida quando um arquivo existe tanto na Origem quanto no Destino com conteúdos divergentes.
/// Apresenta comparação lado a lado de tamanho e data de modificação.
/// </summary>
public partial class ConflictDialogWindow : Window
{
    /// <summary>
    /// Inicializa os componentes visuais da janela de conflito.
    /// </summary>
    public ConflictDialogWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Conecta o evento <see cref="ConflictDialogViewModel.RequestClose"/> ao método <see cref="Window.Close(object?)"/>
    /// para retornar a escolha do usuário à janela principal.
    /// </summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is ConflictDialogViewModel vm)
        {
            vm.RequestClose += () => Close(vm.Result);
        }
    }
}
