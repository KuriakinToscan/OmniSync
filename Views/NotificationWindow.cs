using System;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OmniSync.Cli;
using OmniSync.Services;
using OmniSync.Services.Scheduling;

namespace OmniSync.Views;

/// <summary>
/// Janela pequena de aviso ao fim de uma execução agendada, no canto inferior direito, sempre à frente.
/// Avisos informativos sem ação pendente (ex.: "sincronizada") fecham sozinhos após <see cref="AutoCloseSeconds"/>
/// segundos, salvo com o mouse sobre a janela. Avisos de problema e avisos com "Adiar" ficam até o usuário escolher.
/// "Adiar" (15 min, 1 h ou 4 h) repete a execução no horário, que só avisa de novo se ainda houver o que avisar.
/// O log registra quando o aviso aparece e como terminou.
/// </summary>
public sealed class NotificationWindow : Window
{
    /// <summary>Tempo até fechar sozinha (avisos que não são problema).</summary>
    public const int AutoCloseSeconds = 30;

    private const double ScreenMargin = 16;

    private readonly CliNotification _notification;
    private readonly TextBlock _message;
    private readonly TextBlock _error;
    private readonly StackPanel _buttons;
    private IDisposable? _autoClose;
    private string _closeReason = "fechado pela janela";

    /// <summary>
    /// Cria a janela de aviso.
    /// </summary>
    /// <param name="notification">Conteúdo do aviso.</param>
    public NotificationWindow(CliNotification notification)
    {
        _notification = notification;
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
        open.Click += (_, _) => { OpenApp(); CloseWith("aberto o OmniSync"); };

        var close = new Button { Content = "Fechar", IsCancel = true };
        close.Classes.Add("secondary");
        close.Click += (_, _) => CloseWith("fechado pelo usuário");

        _message = new TextBlock { Text = notification.Message, TextWrapping = TextWrapping.Wrap };
        _error = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false };
        _error.Classes.Add("conflict");
        var text = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = notification.Title, FontSize = 15, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap },
                _message,
                _error
            }
        };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        header.Children.Add(icon);
        Grid.SetColumn(text, 1);
        header.Children.Add(text);

        _buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        if (notification.Snooze != null)
        {
            _buttons.Children.Add(BuildSnoozeButton());
        }
        _buttons.Children.Add(close);
        _buttons.Children.Add(open);

        Content = new StackPanel
        {
            Margin = new Thickness(20, 18, 20, 16),
            Spacing = 14,
            Children = { header, _buttons }
        };

        Opened += (_, _) =>
        {
            FileLogService.Write($"[AVISO AO USUÁRIO] Janela exibida: {notification.Title}.");
            Dispatcher.UIThread.Post(PlaceBottomRight, DispatcherPriority.Loaded);
        };
        Closed += (_, _) => FileLogService.Write($"[AVISO AO USUÁRIO] Janela encerrada: {_closeReason}.");
        if (AutoCloses(notification))
        {
            _autoClose = DispatcherTimer.RunOnce(() => CloseWith($"fechado por tempo ({AutoCloseSeconds} s)"),
                TimeSpan.FromSeconds(AutoCloseSeconds));
            // Com o mouse sobre o aviso, o usuário está lendo ou escolhendo: não fecha sozinho
            PointerEntered += (_, _) => StopAutoClose();
        }
    }

    /// <summary>
    /// Botão "Adiar" com os intervalos de <see cref="SnoozeService.Options"/>.
    /// </summary>
    private Button BuildSnoozeButton()
    {
        var flyout = new MenuFlyout { Placement = PlacementMode.TopEdgeAlignedRight };
        foreach (var delay in SnoozeService.Options)
        {
            var item = new MenuItem { Header = SnoozeService.Describe(delay) };
            item.Click += (_, _) => SnoozeFor(delay);
            flyout.Items.Add(item);
        }
        flyout.Opened += (_, _) => StopAutoClose();

        var snooze = new Button { Content = "Adiar ▾", Flyout = flyout };
        snooze.Classes.Add("secondary");
        ToolTip.SetTip(snooze, "Repetir mais tarde e avisar de novo só se ainda houver o que fazer");
        return snooze;
    }

    /// <summary>
    /// Registra o adiamento; em caso de sucesso, mostra o novo horário e fecha em seguida.
    /// </summary>
    private void SnoozeFor(TimeSpan delay)
    {
        var request = _notification.Snooze;
        var exe = Environment.ProcessPath;
        if (request == null || string.IsNullOrEmpty(exe)) return;

        _buttons.IsEnabled = false;
        string? error;
        try
        {
            error = new SnoozeService(SystemScheduler.Current, request.StateDirectory).Snooze(request, exe, DateTime.Now, delay);
        }
        catch (Exception ex)
        {
            error = ErrorMessages.Translate(ex);
            FileLogService.Write($"[ERRO] Adiamento não registrado: {ErrorMessages.ForLog(ex)}");
        }

        if (error != null)
        {
            _error.Text = $"Não foi possível adiar: {error}";
            _error.IsVisible = true;
            _buttons.IsEnabled = true;
            Dispatcher.UIThread.Post(PlaceBottomRight, DispatcherPriority.Loaded);
            return;
        }

        var due = DateTime.Now + delay;
        _message.Text = $"Adiado para as {due:HH:mm}{(due.Date != DateTime.Today ? $" de {due:dd/MM}" : "")}. " +
                        "O OmniSync verifica de novo nesse horário e só avisa se ainda houver o que fazer.";
        _error.IsVisible = false;
        Dispatcher.UIThread.Post(PlaceBottomRight, DispatcherPriority.Loaded);
        _closeReason = $"adiado por {SnoozeService.Describe(delay)}";
        DispatcherTimer.RunOnce(() => CloseWith(_closeReason), TimeSpan.FromSeconds(4));
    }

    /// <summary>
    /// Indica se o aviso fecha sozinho: só os informativos sem ação pendente. Problemas e avisos com "Adiar"
    /// (o usuário pode ter pedido esse lembrete) ficam na tela até uma escolha.
    /// </summary>
    /// <param name="notification">Aviso.</param>
    public static bool AutoCloses(CliNotification notification) => !notification.IsProblem && notification.Snooze == null;

    private void CloseWith(string reason)
    {
        _closeReason = reason;
        Close();
    }

    private void StopAutoClose()
    {
        _autoClose?.Dispose();
        _autoClose = null;
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
