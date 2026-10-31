using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using OmniSync.Services;

namespace OmniSync.Views;

/// <summary>
/// Resposta à oferta de instalação.
/// </summary>
public enum InstallChoice
{
    /// <summary>Instalado (ou atualizado) com sucesso.</summary>
    Installed,
    /// <summary>"Agora não": perguntar de novo na próxima abertura.</summary>
    Later,
    /// <summary>"Não perguntar de novo" (só na oferta ao abrir, quando não há versão instalada).</summary>
    Never
}

/// <summary>
/// Oferta de instalação na pasta fixa (<see cref="AppInstaller.InstallDirectory"/>), com os atalhos na área de
/// trabalho e no menu Iniciar. Aparece ao abrir o OmniSync fora dessa pasta e ao ligar um agendamento.
/// </summary>
public sealed class InstallWindow : Window
{
    private InstallChoice _choice = InstallChoice.Later;
    private readonly CheckBox _desktop;
    private readonly CheckBox _startMenu;
    private readonly CheckBox? _never;
    private readonly TextBlock _error;
    private readonly Button _install;

    private InstallWindow(InstallState state, string? reason, bool allowDismiss)
    {
        bool update = state == InstallState.InstalledDifferent;
        Title = update ? "Atualizar o OmniSync" : "Instalar o OmniSync";
        Width = 500;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this[!BackgroundProperty] = this.GetResourceObservable("OsCardBrush").ToBinding();

        string message;
        if (update)
        {
            var installed = AppInstaller.InstalledVersion();
            message = $"Já existe outra versão do OmniSync{(installed != null ? $" ({installed})" : string.Empty)} em " +
                      $"{AppInstaller.InstallDirectory}. Substituir pela versão aberta agora ({AppInstaller.CurrentVersion})? " +
                      "Os atalhos e os agendamentos passam a usar esta versão.";
        }
        else
        {
            message = $"Copia o OmniSync para {AppInstaller.InstallDirectory}, uma pasta fixa do seu usuário (não precisa " +
                      "de administrador). Os atalhos e os agendamentos continuam funcionando mesmo que você apague o arquivo baixado.";
        }

        _desktop = new CheckBox { Content = "Criar atalho na área de trabalho", IsChecked = true };
        _startMenu = new CheckBox
        {
            Content = OperatingSystem.IsWindows() ? "Criar atalho no menu Iniciar" : "Criar atalho no menu de aplicativos",
            IsChecked = true
        };
        _error = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false };
        _error.Classes.Add("danger-text");

        _install = new Button { Content = update ? "Atualizar" : "Instalar", IsDefault = true };
        _install.Classes.Add("accent");
        _install.Classes.Add("primary");
        _install.Click += (_, _) => DoInstall();

        var later = new Button { Content = "Agora não", IsCancel = true, MinHeight = 40 };
        later.Classes.Add("secondary");
        later.Click += (_, _) =>
        {
            _choice = _never?.IsChecked == true ? InstallChoice.Never : InstallChoice.Later;
            Close();
        };

        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        if (allowDismiss && !update)
        {
            _never = new CheckBox { Content = "Não perguntar de novo", VerticalAlignment = VerticalAlignment.Center };
            buttons.Children.Add(_never);
        }
        Grid.SetColumn(later, 1);
        Grid.SetColumn(_install, 2);
        buttons.Children.Add(later);
        buttons.Children.Add(_install);

        var body = new StackPanel { Margin = new Thickness(24, 22, 24, 20), Spacing = 14 };
        body.Children.Add(new TextBlock { Text = Title, FontSize = 18, FontWeight = FontWeight.Bold });
        if (!string.IsNullOrEmpty(reason))
        {
            body.Children.Add(new TextBlock { Text = reason, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold });
        }
        body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new StackPanel { Spacing = 6, Children = { _desktop, _startMenu } });
        body.Children.Add(_error);
        body.Children.Add(buttons);
        Content = body;
    }

    private void DoInstall()
    {
        _install.IsEnabled = false;
        var error = AppInstaller.Install(_desktop.IsChecked == true, _startMenu.IsChecked == true, OpenIcon);
        // Atalho que falhou não impede o uso: o executável já está na pasta fixa
        if (error == null || File.Exists(AppInstaller.InstalledExecutable) && error.StartsWith("O OmniSync foi instalado", StringComparison.Ordinal))
        {
            if (error != null) FileLogService.Write($"[AVISO] {error}");
            _choice = InstallChoice.Installed;
            Close();
            return;
        }
        _error.Text = error;
        _error.IsVisible = true;
        _install.IsEnabled = true;
    }

    private static Stream OpenIcon() => AssetLoader.Open(new Uri("avares://OmniSync/Assets/omnisync.png"));

    /// <summary>
    /// Exibe a oferta de instalação sobre <paramref name="owner"/>.
    /// </summary>
    /// <param name="owner">Janela proprietária.</param>
    /// <param name="state">Situação atual da instalação (<see cref="InstallState.NotInstalled"/> ou <see cref="InstallState.InstalledDifferent"/>).</param>
    /// <param name="reason">Frase de contexto exibida acima da explicação (ex.: por que o agendamento precisa), ou <c>null</c>.</param>
    /// <param name="allowDismiss">Mostra "Não perguntar de novo" (só na oferta ao abrir).</param>
    public static async Task<InstallChoice> ShowAsync(Window owner, InstallState state, string? reason, bool allowDismiss)
    {
        var window = new InstallWindow(state, reason, allowDismiss);
        await window.ShowDialog(owner);
        return window._choice;
    }
}
