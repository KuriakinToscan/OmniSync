using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using OmniSync.ViewModels;

namespace OmniSync.Views;

/// <summary>
/// Janela redimensionável do registro da sessão, com filtro de avisos e erros e cópia para a área de transferência.
/// Substitui o painel suspenso de tamanho fixo.
/// </summary>
public partial class LogWindow : Window
{
    /// <summary>
    /// Inicializa os componentes visuais.
    /// </summary>
    public LogWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Ao abrir, mostra as entradas mais recentes (topo da lista).
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (LogList.ItemCount > 0)
        {
            LogList.ScrollIntoView(0);
        }
    }

    /// <summary>
    /// Copia as linhas exibidas (respeitando o filtro) em ordem cronológica.
    /// </summary>
    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || Clipboard == null) return;
        var lines = vm.ActivityLogs.Reverse().Select(entry => entry.ToString());
        try
        {
            await Clipboard.SetTextAsync(string.Join(Environment.NewLine, lines));
            CopyFeedback.Text = $"{vm.ActivityLogs.Count} linhas copiadas.";
        }
        catch (Exception ex)
        {
            CopyFeedback.Text = $"Não foi possível copiar: {ex.Message}";
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
