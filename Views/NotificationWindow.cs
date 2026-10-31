using System;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OmniSync.Cli;
using OmniSync.Services;

namespace OmniSync.Views;

/// <summary>
/// Janela pequena de aviso ao fim de uma execução agendada, no canto inferior direito, sempre à frente.
/// Fecha sozinha após <see cref="AutoCloseSeconds"/> segundos, salvo quando o aviso é um problema.
/// </summary>
public sealed class NotificationWindow : Window
{
    /// <summary>Tempo até fechar sozinha (avisos que não são problema).</summary>
    public const int AutoCloseSeconds = 30;

    private const double ScreenMargin = 16;

    /// <summary>
    /// Cria a janela de aviso.
    /// </summary>
    /// <param name="notification">Conteúdo do aviso.</param>
    public NotificationWindow(CliNotification notification)
    {
        Title = "OmniSync";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        Topmost = true;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        this[!BackgroundProperty] = this.GetResourceObservable("OsCardBrush").ToBinding();

        var icon = new PathIcon { Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        icon[!PathIcon.DataProperty] = this.GetResourceObservable(notification.IsProblem ? "IconWarning" : "IconCheck").ToBinding();
        icon.Classes.Add(notification.IsProblem ? "conflict" : "accent-text");

        var open = new Button { Content = "Abrir OmniSync", IsDefault = true };
        open.Classes.Add("accent");
        open.Classes.Add("primary");
        open.Click += (_, _) => { OpenApp(); Close(); };

        var close = new Button { Content = "Fechar", IsCancel = true };
        close.Classes.Add("secondary");
        close.Click += (_, _) => Close();

        var text = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = notification.Title, FontSize = 15, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = notification.Message, TextWrapping = TextWrapping.Wrap }
            }
        };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        header.Children.Add(icon);
        Grid.SetColumn(text, 1);
        header.Children.Add(text);

        Content = new StackPanel
        {
            Margin = new Thickness(20, 18, 20, 16),
            Spacing = 14,
            Children =
            {
                header,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { close, open }
                }
            }
        };

        Opened += (_, _) => Dispatcher.UIThread.Post(PlaceBottomRight, DispatcherPriority.Loaded);
        if (!notification.IsProblem)
        {
            DispatcherTimer.RunOnce(Close, TimeSpan.FromSeconds(AutoCloseSeconds));
        }
    }

    private void PlaceBottomRight()
    {
        var screen = Screens.Primary ?? Screens.ScreenFromWindow(this);
        if (screen == null) return;
        var area = screen.WorkingArea;
        double scaling = screen.Scaling;
        int width = (int)Math.Ceiling(Bounds.Width * scaling);
        int height = (int)Math.Ceiling((Bounds.Height + 32) * scaling);
        int margin = (int)(ScreenMargin * scaling);
        Position = new PixelPoint(area.Right - width - margin, area.Bottom - height - margin);
    }

    /// <summary>
    /// Abre a interface: a versão instalada, quando existe; senão, este mesmo executável.
    /// </summary>
    private static void OpenApp()
    {
        var exe = File.Exists(AppInstaller.InstalledExecutable) ? AppInstaller.InstalledExecutable : Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            FileLogService.Write($"[AVISO] Não foi possível abrir o OmniSync pelo aviso: {ex.Message}");
        }
    }
}
