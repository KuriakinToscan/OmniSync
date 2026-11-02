using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OmniSync.Services.FileSystems;
using OmniSync.Services.Remote;

namespace OmniSync.Views;

/// <summary>
/// Perguntas da conexão remota com janelas: confirmação da chave de um servidor novo e pedido de senha.
/// Pode ser chamada de qualquer thread; as janelas abrem na thread da interface.
/// </summary>
public sealed class WindowRemotePrompt : IRemotePrompt
{
    private readonly Window _owner;

    /// <summary>Cria as perguntas sobre a janela indicada.</summary>
    public WindowRemotePrompt(Window owner) => _owner = owner;

    /// <inheritdoc />
    public Task<bool> ConfirmHostKeyAsync(RemoteAddress address, string fingerprint) =>
        Dispatcher.UIThread.InvokeAsync(() => HostKeyDialog.ShowAsync(_owner, address, fingerprint));

    /// <inheritdoc />
    public Task<SecretAnswer?> AskSecretAsync(RemoteAddress address, bool isPassphrase, bool previousFailed, bool canStore) =>
        Dispatcher.UIThread.InvokeAsync(() => PasswordDialog.ShowAsync(_owner, address, isPassphrase, previousFailed, canStore));
}

/// <summary>Primeira conexão com um servidor: mostra a impressão digital da chave e pergunta se é confiável.</summary>
public sealed class HostKeyDialog : Window
{
    private bool _trusted;

    private HostKeyDialog(RemoteAddress address, string fingerprint)
    {
        Title = "Confirmar o servidor";
        Width = 500;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this[!BackgroundProperty] = this.GetResourceObservable("OsCardBrush").ToBinding();

        var trust = new Button { Content = "Confiar e conectar", IsDefault = true };
        trust.Classes.Add("accent");
        trust.Classes.Add("primary");
        trust.Click += (_, _) => { _trusted = true; Close(); };
        var cancel = new Button { Content = "Cancelar", IsCancel = true, MinHeight = 40 };
        cancel.Classes.Add("secondary");
        cancel.Click += (_, _) => Close();

        var print = new SelectableTextBlock { Text = "SHA256:" + fingerprint, TextWrapping = TextWrapping.Wrap };
        print.Classes.Add("mono");

        Content = new StackPanel
        {
            Margin = new Thickness(24, 22, 24, 20),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = "Primeira conexão com este servidor", FontSize = 18, FontWeight = FontWeight.Bold },
                new TextBlock
                {
                    Text = $"O servidor {address.HostPortKey} apresentou a chave abaixo. Se puder, confira com quem administra o " +
                           "servidor (no próprio servidor: ssh-keygen -lf no arquivo da chave). Depois de confirmada, o OmniSync " +
                           "recusa a conexão se a chave mudar, o que protege contra um servidor falso no lugar do verdadeiro.",
                    TextWrapping = TextWrapping.Wrap
                },
                new Border { Padding = new Thickness(12, 10), CornerRadius = new CornerRadius(6), Child = print, Classes = { "card" } },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, trust }
                }
            }
        };
    }

    /// <summary>Mostra a pergunta; <c>true</c> se o usuário confiou no servidor.</summary>
    public static async Task<bool> ShowAsync(Window owner, RemoteAddress address, string fingerprint)
    {
        var dialog = new HostKeyDialog(address, fingerprint);
        await dialog.ShowDialog(owner);
        return dialog._trusted;
    }
}

/// <summary>Pedido de senha (ou da frase-senha do arquivo de chave), com a opção de guardar no cofre.</summary>
public sealed class PasswordDialog : Window
{
    private SecretAnswer? _answer;

    private PasswordDialog(RemoteAddress address, bool isPassphrase, bool previousFailed, bool canStore)
    {
        Title = isPassphrase ? "Frase-senha da chave" : "Senha do servidor";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this[!BackgroundProperty] = this.GetResourceObservable("OsCardBrush").ToBinding();

        var box = new TextBox { PasswordChar = '•', RevealPassword = false, Watermark = isPassphrase ? "Frase-senha" : "Senha" };
        var store = new CheckBox
        {
            Content = $"Guardar no {CredentialStore.Current.DisplayName}",
            IsChecked = canStore,
            IsEnabled = canStore
        };
        var storeNote = new TextBlock
        {
            Classes = { "caption" },
            TextWrapping = TextWrapping.Wrap,
            Text = canStore
                ? "Guardada, a senha também serve para os agendamentos. Ela nunca vai para o settings.json."
                : "Não há cofre de senhas disponível: a senha vale só enquanto o OmniSync estiver aberto, e os agendamentos desta tarefa vão parar pedindo que você a informe."
        };

        var ok = new Button { Content = "Conectar", IsDefault = true };
        ok.Classes.Add("accent");
        ok.Classes.Add("primary");
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrEmpty(box.Text)) return;
            _answer = new SecretAnswer(box.Text, store.IsChecked == true);
            Close();
        };
        var cancel = new Button { Content = "Cancelar", IsCancel = true, MinHeight = 40 };
        cancel.Classes.Add("secondary");
        cancel.Click += (_, _) => Close();

        var body = new StackPanel { Margin = new Thickness(24, 22, 24, 20), Spacing = 12 };
        body.Children.Add(new TextBlock { Text = Title, FontSize = 18, FontWeight = FontWeight.Bold });
        if (previousFailed)
        {
            var failed = new TextBlock { Text = "O servidor recusou a senha anterior. Tente de novo.", TextWrapping = TextWrapping.Wrap };
            failed.Classes.Add("danger-text");
            body.Children.Add(failed);
        }
        body.Children.Add(new TextBlock
        {
            Text = isPassphrase
                ? $"O arquivo de chave de {address.EndpointKey} é protegido por frase-senha."
                : $"Senha de {address.User} em {address.HostPortKey}.",
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(box);
        body.Children.Add(store);
        body.Children.Add(storeNote);
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, ok }
        });
        Content = body;
        Opened += (_, _) => box.Focus();
    }

    /// <summary>Pede a senha; <c>null</c> se o usuário cancelar.</summary>
    public static async Task<SecretAnswer?> ShowAsync(Window owner, RemoteAddress address, bool isPassphrase, bool previousFailed, bool canStore)
    {
        var dialog = new PasswordDialog(address, isPassphrase, previousFailed, canStore);
        await dialog.ShowDialog(owner);
        return dialog._answer;
    }
}
