using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace OmniSync.Views;

/// <summary>
/// Seletor de horário com mostrador de relógio (estilo Google Agenda): no topo, a hora e os minutos escolhidos
/// (clicáveis, alternam o que o mostrador escolhe); no meio, o <see cref="ClockFace"/>; embaixo, Cancelar e OK.
/// Teclado: setas para cima/baixo mudam o valor em edição, esquerda/direita alternam hora/minutos, Enter confirma
/// e Esc cancela. Usado no horário do agendamento e no "Escolher horário…" do Adiar, sempre num flyout
/// (<see cref="ShowFlyout"/>).
/// </summary>
public sealed class ClockTimePicker : UserControl
{
    private readonly ClockFace _face;
    private readonly Button _hourButton;
    private readonly Button _minuteButton;

    /// <summary>OK: o horário escolhido.</summary>
    public event EventHandler<TimeSpan>? Confirmed;

    /// <summary>Cancelar ou Esc.</summary>
    public event EventHandler? Canceled;

    /// <summary>
    /// Cria o seletor.
    /// </summary>
    /// <param name="initial">Horário inicial (horas e minutos; segundos ignorados).</param>
    /// <param name="title">Título acima do horário (ex.: "Horário do agendamento").</param>
    public ClockTimePicker(TimeSpan initial, string title)
    {
        Focusable = true;
        _face = new ClockFace
        {
            Hour = ((initial.Hours % 24) + 24) % 24,
            Minute = Math.Clamp(initial.Minutes, 0, 59),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        AutomationPropertiesHelper(_face, "Mostrador do relógio");

        _hourButton = BuildPartButton("Hora");
        _hourButton.Click += (_, _) => SetMode(ClockFaceMode.Hours);
        _minuteButton = BuildPartButton("Minutos");
        _minuteButton.Click += (_, _) => SetMode(ClockFaceMode.Minutes);

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 2,
            Children =
            {
                _hourButton,
                new TextBlock { Text = ":", FontSize = 34, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -4, 0, 0) },
                _minuteButton
            }
        };

        var cancel = new Button { Content = "Cancelar" };
        cancel.Classes.Add("secondary");
        cancel.Click += (_, _) => Canceled?.Invoke(this, EventArgs.Empty);
        var ok = new Button { Content = "OK", MinWidth = 72, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Classes.Add("accent");
        ok.Classes.Add("primary");
        ok.Click += (_, _) => Confirm();

        var caption = new TextBlock { Text = title, FontWeight = FontWeight.SemiBold };
        var hint = new TextBlock
        {
            Text = "Clique na hora e depois nos minutos. Arraste para minutos fora de 5 em 5.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 250
        };
        hint.Classes.Add("caption");

        Content = new StackPanel
        {
            Spacing = 10,
            Width = 260,
            Children =
            {
                caption,
                header,
                _face,
                hint,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, ok }
                }
            }
        };

        _face.HourPicked += (_, _) => SetMode(ClockFaceMode.Minutes);
        _face.PropertyChanged += (_, e) =>
        {
            if (e.Property == ClockFace.HourProperty || e.Property == ClockFace.MinuteProperty) UpdateHeader();
        };
        SetMode(ClockFaceMode.Hours);
    }

    /// <summary>Horário escolhido no momento.</summary>
    public TimeSpan Value => new(_face.Hour, _face.Minute, 0);

    /// <summary>
    /// Abre o seletor num flyout junto de <paramref name="anchor"/>. OK fecha e chama <paramref name="onPicked"/>;
    /// Cancelar, Esc ou clique fora fecham sem mudar nada.
    /// </summary>
    /// <param name="anchor">Controle de referência (o botão que abriu).</param>
    /// <param name="initial">Horário inicial.</param>
    /// <param name="title">Título do seletor.</param>
    /// <param name="placement">Posição do flyout em relação ao controle.</param>
    /// <param name="onPicked">Ação com o horário confirmado.</param>
    /// <returns>O flyout aberto (para testes ou para fechar de fora).</returns>
    public static Flyout ShowFlyout(Control anchor, TimeSpan initial, string title, PlacementMode placement, Action<TimeSpan> onPicked)
    {
        var picker = new ClockTimePicker(initial, title);
        var flyout = new Flyout { Content = picker, Placement = placement };
        picker.Confirmed += (_, time) =>
        {
            flyout.Hide();
            onPicked(time);
        };
        picker.Canceled += (_, _) => flyout.Hide();
        flyout.Opened += (_, _) => picker.Focus();
        flyout.ShowAt(anchor);
        return flyout;
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        switch (e.Key)
        {
            case Key.Up:
                Step(+1);
                break;
            case Key.Down:
                Step(-1);
                break;
            case Key.Left:
                SetMode(ClockFaceMode.Hours);
                break;
            case Key.Right:
                SetMode(ClockFaceMode.Minutes);
                break;
            case Key.Enter:
                Confirm();
                break;
            case Key.Escape:
                Canceled?.Invoke(this, EventArgs.Empty);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>
    /// Muda o valor em edição em <paramref name="delta"/> (hora ou minuto), dando a volta no mostrador.
    /// </summary>
    /// <param name="delta">+1 ou −1.</param>
    public void Step(int delta)
    {
        if (_face.Mode == ClockFaceMode.Hours)
        {
            _face.Hour = ((_face.Hour + delta) % 24 + 24) % 24;
        }
        else
        {
            _face.Minute = ((_face.Minute + delta) % 60 + 60) % 60;
        }
    }

    /// <summary>
    /// Passa o mostrador para a hora ou para os minutos e destaca a parte correspondente no topo.
    /// </summary>
    /// <param name="mode">Modo.</param>
    public void SetMode(ClockFaceMode mode)
    {
        _face.Mode = mode;
        _hourButton.Classes.Set("active", mode == ClockFaceMode.Hours);
        _minuteButton.Classes.Set("active", mode == ClockFaceMode.Minutes);
        UpdateHeader();
    }

    private void Confirm() => Confirmed?.Invoke(this, Value);

    private void UpdateHeader()
    {
        _hourButton.Content = _face.Hour.ToString("00");
        _minuteButton.Content = _face.Minute.ToString("00");
    }

    private static Button BuildPartButton(string name)
    {
        var button = new Button
        {
            FontSize = 34,
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(10, 2),
            MinWidth = 64,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        button.Classes.Add("clock-part");
        AutomationPropertiesHelper(button, name);
        return button;
    }

    private static void AutomationPropertiesHelper(Control control, string name) =>
        Avalonia.Automation.AutomationProperties.SetName(control, name);
}
