using System;
using Avalonia.Controls;
using OmniSync.ViewModels;

namespace OmniSync.Views;

/// <summary>
/// Janela modal associada ao <see cref="RestoreViewModel"/>.
/// </summary>
public partial class RestoreWindow : Window
{
    /// <summary>
    /// Inicializa os componentes visuais.
    /// </summary>
    public RestoreWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Fecha a janela quando o ViewModel solicitar.
    /// </summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is RestoreViewModel vm)
        {
            vm.RequestClose += Close;
        }
    }
}
