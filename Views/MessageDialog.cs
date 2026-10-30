using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace OmniSync.Views;

/// <summary>
/// Diálogo modal simples: confirmação (título, mensagem, botão de ação e "Cancelar") ou aviso com "OK".
/// </summary>
public sealed class MessageDialog : Window
{
    private bool _result;

    private MessageDialog(string title, string message, string confirmText, bool showCancel = true)
    {
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this[!BackgroundProperty] = this.GetResourceObservable("OsCardBrush").ToBinding();

        var confirm = new Button { Content = confirmText, IsDefault = true };
        confirm.Classes.Add("accent");
        confirm.Classes.Add("primary");
        confirm.Click += (_, _) => { _result = true; Close(); };

        var cancel = new Button { Content = "Cancelar", IsCancel = true, MinHeight = 40, IsVisible = showCancel };
        cancel.Classes.Add("secondary");
        cancel.Click += (_, _) => Close();
        if (!showCancel)
        {
            confirm.IsCancel = true;
        }

        Content = new StackPanel
        {
            Margin = new Thickness(24, 22, 24, 20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.Bold },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, confirm }
                }
            }
        };
    }

    /// <summary>
    /// Exibe o diálogo sobre <paramref name="owner"/> e retorna <c>true</c> se o usuário confirmou.
    /// </summary>
    /// <param name="owner">Janela proprietária.</param>
    /// <param name="title">Título.</param>
    /// <param name="message">Mensagem.</param>
    /// <param name="confirmText">Rótulo do botão de confirmação.</param>
    public static async Task<bool> ShowAsync(Window owner, string title, string message, string confirmText)
    {
        var dialog = new MessageDialog(title, message, confirmText);
        await dialog.ShowDialog(owner);
        return dialog._result;
    }

    /// <summary>
    /// Exibe um aviso com um único botão "OK" sobre <paramref name="owner"/>.
    /// </summary>
    /// <param name="owner">Janela proprietária.</param>
    /// <param name="title">Título.</param>
    /// <param name="message">Mensagem.</param>
    public static Task InformAsync(Window owner, string title, string message)
    {
        return new MessageDialog(title, message, "OK", showCancel: false).ShowDialog(owner);
    }
}
