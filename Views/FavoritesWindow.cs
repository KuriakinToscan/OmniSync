using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using OmniSync.Models;
using OmniSync.Services.FileSystems;
using OmniSync.Services.Remote;
using OmniSync.ViewModels;

namespace OmniSync.Views;

/// <summary>
/// Cadastro de favoritos remotos (SFTP): nome, servidor, porta, usuário, pasta e forma de entrar (senha no cofre
/// ou arquivo de chave), com Testar conexão e a chave do servidor confirmada.
/// </summary>
public sealed class FavoritesWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly bool? _forSource;
    private readonly ObservableCollection<RemoteFavorite> _favorites;
    private readonly ListBox _list;
    private readonly TextBox _name = new() { PlaceholderText = "Ex.: NAS do setor" };
    private readonly TextBox _host = new() { PlaceholderText = "Ex.: nas.local ou 10.0.0.5" };
    private readonly TextBox _port = new() { Text = "22", Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _user = new() { PlaceholderText = "Usuário no servidor" };
    private readonly TextBox _folder = new() { PlaceholderText = "Ex.: /volume1/backup ou /home/usuario/dados" };
    private readonly RadioButton _usePassword = new() { Content = "Senha", GroupName = "auth", IsChecked = true };
    private readonly RadioButton _useKey = new() { Content = "Arquivo de chave", GroupName = "auth" };
    private readonly TextBox _password = new() { PasswordChar = '•' };
    private readonly TextBox _keyFile = new() { PlaceholderText = "Ex.: C:\\Users\\voce\\.ssh\\id_ed25519" };
    private readonly TextBox _passphrase = new() { PasswordChar = '•', PlaceholderText = "Frase-senha da chave (se houver)" };
    private readonly TextBlock _secretNote = new() { TextWrapping = TextWrapping.Wrap, Classes = { "caption" } };
    private readonly TextBlock _hostKeyText = new() { TextWrapping = TextWrapping.Wrap, Classes = { "caption" } };
    private readonly CheckBox _verifyByReading = new() { Content = "Conferir cada cópia relendo o arquivo pela rede (dobra o tráfego)" };
    private readonly Button _forgetHostKey = new() { Content = "Esquecer chave do servidor", Classes = { "ghost" } };
    private readonly TextBlock _result = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _passwordPanel;
    private readonly StackPanel _keyPanel;
    private readonly Button _test = new() { Content = "Testar conexão", Classes = { "secondary" } };
    private readonly Button _use;
    private RemoteFavorite? _editing;

    private FavoritesWindow(MainViewModel vm, bool? forSource)
    {
        _vm = vm;
        _forSource = forSource;
        Title = "Favoritos remotos (SFTP)";
        Width = 860;
        Height = 640;
        MinWidth = 700;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this[!BackgroundProperty] = this.GetResourceObservable("OsCardBrush").ToBinding();

        _favorites = new ObservableCollection<RemoteFavorite>(vm.Favorites.Select(Clone));
        _list = new ListBox
        {
            ItemsSource = _favorites,
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<RemoteFavorite>((f, _) => new StackPanel
            {
                Spacing = 2,
                Margin = new Thickness(2, 4),
                Children =
                {
                    new TextBlock { Text = string.IsNullOrWhiteSpace(f?.Name) ? "(sem nome)" : f!.Name, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = f?.Address ?? string.Empty, Classes = { "caption", "mono" }, TextTrimming = TextTrimming.CharacterEllipsis }
                }
            })
        };
        _list.SelectionChanged += (_, _) => Load(_list.SelectedItem as RemoteFavorite);

        var add = new Button { Content = "Novo", Classes = { "secondary" } };
        add.Click += (_, _) => NewFavorite();
        var remove = new Button { Content = "Excluir", Classes = { "danger-text" } };
        remove.Click += async (_, _) => await RemoveAsync();

        var left = new DockPanel { Width = 260 };
        var leftButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 10, 0, 0), Children = { add, remove } };
        DockPanel.SetDock(leftButtons, Dock.Bottom);
        left.Children.Add(leftButtons);
        left.Children.Add(new Border { Classes = { "card" }, Padding = new Thickness(4), Child = _list });

        _usePassword.IsCheckedChanged += (_, _) => UpdateAuthPanels();
        _useKey.IsCheckedChanged += (_, _) => UpdateAuthPanels();

        var browseKey = new Button { Content = "Escolher…", Classes = { "secondary" } };
        browseKey.Click += async (_, _) => await BrowseKeyAsync();

        _passwordPanel = new StackPanel { Spacing = 6, Children = { _password } };
        var keyRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        keyRow.Children.Add(_keyFile);
        Grid.SetColumn(browseKey, 1);
        keyRow.Children.Add(browseKey);
        _keyPanel = new StackPanel { Spacing = 6, Children = { keyRow, _passphrase } };

        _forgetHostKey.Click += (_, _) =>
        {
            if (TryBuildAddress(out var address, out _))
            {
                _vm.ForgetHostKey(address.HostPortKey);
                RemoteConnections.Disconnect(address);
                UpdateHostKeyText();
            }
        };

        _test.Click += async (_, _) => await TestAsync();
        var save = new Button { Content = "Salvar", Classes = { "accent", "primary" } };
        save.Click += (_, _) => Save();
        _use = new Button
        {
            Content = forSource == true ? "Salvar e usar na origem" : "Salvar e usar no destino",
            Classes = { "secondary" },
            IsVisible = forSource != null
        };
        _use.Click += (_, _) =>
        {
            if (!Save()) return;
            _vm.UseFavorite(_editing!, _forSource == true);
            Close();
        };

        var form = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 18, 0) };
        form.Children.Add(Field("Nome", _name));
        var serverRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        serverRow.Children.Add(Field("Servidor", _host));
        var portField = Field("Porta", _port);
        Grid.SetColumn(portField, 1);
        serverRow.Children.Add(portField);
        form.Children.Add(serverRow);
        form.Children.Add(Field("Usuário", _user));
        form.Children.Add(Field("Pasta no servidor", _folder));
        form.Children.Add(new TextBlock { Text = "Entrar com", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, 0) });
        form.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, Children = { _usePassword, _useKey } });
        form.Children.Add(_passwordPanel);
        form.Children.Add(_keyPanel);
        form.Children.Add(_secretNote);
        form.Children.Add(new TextBlock { Text = "Conferência das cópias", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, 0) });
        form.Children.Add(new TextBlock
        {
            Classes = { "caption" },
            TextWrapping = TextWrapping.Wrap,
            Text = "Normalmente o próprio servidor calcula o SHA-256 de cada cópia (sha256sum ou, no Windows, Get-FileHash). " +
                   "Servidor que só aceita SFTP fica com a conferência pelo tamanho, a não ser que você marque a opção abaixo."
        });
        form.Children.Add(_verifyByReading);
        form.Children.Add(new TextBlock { Text = "Chave do servidor", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, 0) });
        form.Children.Add(_hostKeyText);
        form.Children.Add(_forgetHostKey);

        var close = new Button { Content = "Fechar", IsCancel = true, Classes = { "secondary" } };
        close.Click += (_, _) => Close();
        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"), ColumnSpacing = 8, Margin = new Thickness(0, 14, 0, 0) };
        actions.Children.Add(_test);
        Grid.SetColumn(close, 2);
        Grid.SetColumn(_use, 3);
        Grid.SetColumn(save, 4);
        actions.Children.Add(close);
        actions.Children.Add(_use);
        actions.Children.Add(save);

        var right = new DockPanel();
        DockPanel.SetDock(actions, Dock.Bottom);
        right.Children.Add(actions);
        // Resultado fora da rolagem, logo acima dos botões, para ser visto sem rolar
        _result.Margin = new Thickness(0, 10, 18, 0);
        DockPanel.SetDock(_result, Dock.Bottom);
        right.Children.Add(_result);
        right.Children.Add(new ScrollViewer { Content = form, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });

        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 20, Margin = new Thickness(22, 20) };
        var header = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(22, 18, 22, 0),
            Children =
            {
                new TextBlock { Text = "Favoritos remotos", FontSize = 18, FontWeight = FontWeight.Bold },
                new TextBlock
                {
                    Classes = { "caption" },
                    TextWrapping = TextWrapping.Wrap,
                    Text = "Servidores SFTP (NAS, servidor Linux, OpenSSH do Windows). O settings.json guarda só o endereço; a senha fica no " +
                           CredentialStore.Current.DisplayName + "."
                }
            }
        };
        root.Children.Add(left);
        Grid.SetColumn(right, 1);
        root.Children.Add(right);
        var outer = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        outer.Children.Add(header);
        outer.Children.Add(root);
        Content = outer;

        if (_favorites.Count > 0) _list.SelectedIndex = 0;
        else NewFavorite();
    }

    private static StackPanel Field(string label, Control input) => new()
    {
        Spacing = 4,
        Children = { new TextBlock { Text = label, FontWeight = FontWeight.SemiBold }, input }
    };

    private static RemoteFavorite Clone(RemoteFavorite f) => new() { Id = f.Id, Name = f.Name, Address = f.Address, KeyFile = f.KeyFile, VerifyByReading = f.VerifyByReading };

    private void UpdateAuthPanels()
    {
        _passwordPanel.IsVisible = _usePassword.IsChecked == true;
        _keyPanel.IsVisible = _useKey.IsChecked == true;
        UpdateSecretNote();
    }

    private void UpdateSecretNote()
    {
        bool hasStored = TryBuildAddress(out var address, out _) && RemoteConnections.HasStoredSecret(address);
        var store = CredentialStore.Current;
        _password.PlaceholderText = hasStored ? "Senha guardada (deixe em branco para manter)" : "Senha";
        _secretNote.Text = !store.IsAvailable
            ? "Não há cofre de senhas disponível: a senha é pedida ao conectar e vale só enquanto o OmniSync estiver aberto."
            : hasStored
                ? $"Há uma senha guardada no {store.DisplayName}. Digite outra só para trocar."
                : $"Ao salvar, a senha vai para o {store.DisplayName} e serve também para os agendamentos.";
    }

    private void UpdateHostKeyText()
    {
        if (TryBuildAddress(out var address, out _) && _vm.KnownHostKeys.TryGetValue(address.HostPortKey, out var fingerprint))
        {
            _hostKeyText.Text = $"Confirmada para {address.HostPortKey}: SHA256:{fingerprint}";
            _forgetHostKey.IsVisible = true;
        }
        else
        {
            _hostKeyText.Text = "Ainda não confirmada. Na primeira conexão, o OmniSync mostra a chave e pede a sua confirmação.";
            _forgetHostKey.IsVisible = false;
        }
    }

    private void Load(RemoteFavorite? favorite)
    {
        if (favorite == null) return;
        _editing = favorite;
        _name.Text = favorite.Name;
        if (RemoteAddress.TryParse(favorite.Address, out var address, out _))
        {
            _host.Text = address.Host;
            _port.Text = address.Port.ToString(CultureInfo.InvariantCulture);
            _user.Text = address.User;
            _folder.Text = address.Path;
        }
        _useKey.IsChecked = !string.IsNullOrWhiteSpace(favorite.KeyFile);
        _usePassword.IsChecked = string.IsNullOrWhiteSpace(favorite.KeyFile);
        _keyFile.Text = favorite.KeyFile ?? string.Empty;
        _verifyByReading.IsChecked = favorite.VerifyByReading;
        _password.Text = string.Empty;
        _passphrase.Text = string.Empty;
        _result.Text = string.Empty;
        UpdateAuthPanels();
        UpdateHostKeyText();
    }

    private void NewFavorite()
    {
        var favorite = new RemoteFavorite();
        _favorites.Add(favorite);
        _list.SelectedItem = favorite;
        _name.Text = _host.Text = _user.Text = _keyFile.Text = _password.Text = _passphrase.Text = string.Empty;
        _port.Text = "22";
        _folder.Text = "/";
        _usePassword.IsChecked = true;
        _verifyByReading.IsChecked = false;
        _result.Text = string.Empty;
        UpdateAuthPanels();
        UpdateHostKeyText();
        _name.Focus();
    }

    private bool TryBuildAddress(out RemoteAddress address, out string error)
    {
        address = null!;
        if (!int.TryParse(_port.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int port))
        {
            error = "A porta deve ser um número (normalmente 22).";
            return false;
        }
        try
        {
            address = RemoteAddress.Create(_user.Text ?? string.Empty, _host.Text ?? string.Empty, port, _folder.Text ?? "/");
            error = string.Empty;
            return true;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private void ShowResult(bool ok, string message)
    {
        _result.Classes.Set("danger-text", !ok);
        _result.Classes.Set("ok-text", ok);
        _result.Text = message;
    }

    /// <summary>Valida e grava o favorito em edição (e a senha no cofre, se digitada).</summary>
    private bool Save()
    {
        if (_editing == null) return false;
        if (!TryBuildAddress(out var address, out var error))
        {
            ShowResult(false, error);
            return false;
        }
        bool useKey = _useKey.IsChecked == true;
        if (useKey && string.IsNullOrWhiteSpace(_keyFile.Text))
        {
            ShowResult(false, "Escolha o arquivo de chave ou marque Senha.");
            return false;
        }

        _editing.Name = string.IsNullOrWhiteSpace(_name.Text) ? $"{address.User}@{address.Host}" : _name.Text.Trim();
        _editing.Address = address.ToString();
        _editing.KeyFile = useKey ? _keyFile.Text!.Trim() : null;
        _editing.VerifyByReading = _verifyByReading.IsChecked == true;

        var secret = useKey ? _passphrase.Text : _password.Text;
        string stored = string.Empty;
        if (!string.IsNullOrEmpty(secret))
        {
            stored = RemoteConnections.StoreSecret(address, secret)
                ? $" Senha guardada no {CredentialStore.Current.DisplayName}."
                : " A senha não pôde ir para o cofre; ela vale só enquanto o OmniSync estiver aberto.";
            _password.Text = _passphrase.Text = string.Empty;
        }
        RemoteConnections.Disconnect(address);

        _vm.SaveFavorites(_favorites.Where(f => !string.IsNullOrWhiteSpace(f.Address)).Select(Clone));
        RefreshList();
        UpdateSecretNote();
        UpdateHostKeyText();
        ShowResult(true, $"Favorito salvo: {address}.{stored}");
        return true;
    }

    private void RefreshList()
    {
        var selected = _editing;
        _list.ItemsSource = null;
        _list.ItemsSource = _favorites;
        _list.SelectedItem = selected;
    }

    private async Task RemoveAsync()
    {
        if (_list.SelectedItem is not RemoteFavorite favorite) return;
        if (!string.IsNullOrWhiteSpace(favorite.Address) &&
            !await MessageDialog.ShowAsync(this, "Excluir favorito", $"Excluir \"{favorite.Name}\"? A senha guardada no cofre também é apagada. As tarefas que usam o endereço continuam com ele.", "Excluir"))
        {
            return;
        }
        if (RemoteAddress.TryParse(favorite.Address, out var address, out _))
        {
            RemoteConnections.ForgetSecret(address, fromStore: true);
            RemoteConnections.Disconnect(address);
        }
        _favorites.Remove(favorite);
        _vm.SaveFavorites(_favorites.Where(f => !string.IsNullOrWhiteSpace(f.Address)).Select(Clone));
        if (_favorites.Count > 0) _list.SelectedIndex = 0;
        else NewFavorite();
    }

    private async Task BrowseKeyAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Arquivo de chave privada",
            AllowMultiple = false
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path != null) _keyFile.Text = path;
    }

    private async Task TestAsync()
    {
        if (!Save()) return;
        if (!TryBuildAddress(out var address, out _)) return;
        _test.IsEnabled = false;
        ShowResult(true, $"Conectando a {address.HostPortKey}...");
        try
        {
            var (ok, message) = await _vm.TestRemoteAsync(address, new WindowRemotePrompt(this));
            ShowResult(ok, message);
        }
        finally
        {
            _test.IsEnabled = true;
            UpdateSecretNote();
            UpdateHostKeyText();
        }
    }

    /// <summary>
    /// Abre o cadastro de favoritos.
    /// </summary>
    /// <param name="owner">Janela proprietária.</param>
    /// <param name="vm">ViewModel principal (favoritos e conexão).</param>
    /// <param name="forSource">Campo de onde veio (mostra "Salvar e usar…"); <c>null</c> sem campo.</param>
    public static Task ShowAsync(Window owner, MainViewModel vm, bool? forSource)
    {
        return new FavoritesWindow(vm, forSource).ShowDialog(owner);
    }
}
