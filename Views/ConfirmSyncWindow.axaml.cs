using System;
using Avalonia.Controls;
using OmniSync.ViewModels;

namespace OmniSync.Views;

/// <summary>
/// Janela modal associada ao <see cref="ConfirmSyncViewModel"/>.
/// </summary>
public partial class ConfirmSyncWindow : Window
{
    /// <summary>
    /// Inicializa os componentes visuais.
    /// </summary>
    public ConfirmSyncWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Fecha a janela quando o ViewModel solicitar.
    /// </summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is ConfirmSyncViewModel vm)
        {
            vm.RequestClose += Close;
        }
    }
}
