using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;

namespace OmniSync.Views;

/// <summary>
/// O que o mostrador está escolhendo: a hora ou os minutos.
/// </summary>
public enum ClockFaceMode
{
    /// <summary>Hora: anel externo 00–11, anel interno 12–23.</summary>
    Hours,

    /// <summary>Minutos: um clique arredonda para 5 em 5; arrastando, de 1 em 1.</summary>
    Minutes
}

/// <summary>
/// Mostrador de relógio de 24 h, no estilo do seletor de horário do Google Agenda no celular. No modo
/// <see cref="ClockFaceMode.Hours"/>, o anel externo tem 00–11 e o interno 12–23; ao soltar o botão, o mostrador
/// passa para os minutos (<see cref="HourPicked"/>). No modo <see cref="ClockFaceMode.Minutes"/>, um clique escolhe
/// o múltiplo de 5 mais próximo e o arraste escolhe minuto a minuto. A geometria fica nos métodos estáticos
/// (<see cref="AngleOf"/>, <see cref="HourAt"/>, <see cref="MinuteAt"/>) para ser testada sem desenhar.
/// </summary>
public sealed class ClockFace : Control
{
    /// <summary>Raio do anel externo, em fração do raio do mostrador.</summary>
    public const double OuterRingRatio = 0.80;

    /// <summary>Raio do anel interno (12–23), em fração do raio do mostrador.</summary>
    public const double InnerRingRatio = 0.54;

    /// <summary>Hora escolhida (0–23).</summary>
    public static readonly StyledProperty<int> HourProperty =
        AvaloniaProperty.Register<ClockFace, int>(nameof(Hour), coerce: (_, v) => Math.Clamp(v, 0, 23));

    /// <summary>Minuto escolhido (0–59).</summary>
    public static readonly StyledProperty<int> MinuteProperty =
        AvaloniaProperty.Register<ClockFace, int>(nameof(Minute), coerce: (_, v) => Math.Clamp(v, 0, 59));

    /// <summary>Modo do mostrador.</summary>
    public static readonly StyledProperty<ClockFaceMode> ModeProperty =
        AvaloniaProperty.Register<ClockFace, ClockFaceMode>(nameof(Mode));

    /// <summary>Fundo do mostrador.</summary>
    public static readonly StyledProperty<IBrush?> FaceBrushProperty =
        AvaloniaProperty.Register<ClockFace, IBrush?>(nameof(FaceBrush));

    /// <summary>Cor do ponteiro e da marca do valor escolhido.</summary>
    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<ClockFace, IBrush?>(nameof(AccentBrush));

    /// <summary>Cor dos números do anel interno.</summary>
    public static readonly StyledProperty<IBrush?> SubtleBrushProperty =
        AvaloniaProperty.Register<ClockFace, IBrush?>(nameof(SubtleBrush));

    private bool _pressed;
    private bool _dragged;
    private Point _pressPoint;

    /// <inheritdoc cref="HourProperty"/>
    public int Hour { get => GetValue(HourProperty); set => SetValue(HourProperty, value); }

    /// <inheritdoc cref="MinuteProperty"/>
    public int Minute { get => GetValue(MinuteProperty); set => SetValue(MinuteProperty, value); }

    /// <inheritdoc cref="ModeProperty"/>
    public ClockFaceMode Mode { get => GetValue(ModeProperty); set => SetValue(ModeProperty, value); }

    /// <inheritdoc cref="FaceBrushProperty"/>
    public IBrush? FaceBrush { get => GetValue(FaceBrushProperty); set => SetValue(FaceBrushProperty, value); }

    /// <inheritdoc cref="AccentBrushProperty"/>
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }

    /// <inheritdoc cref="SubtleBrushProperty"/>
    public IBrush? SubtleBrush { get => GetValue(SubtleBrushProperty); set => SetValue(SubtleBrushProperty, value); }

    /// <summary>A hora foi escolhida com o mouse (botão solto); quem usa o mostrador passa para os minutos.</summary>
    public event EventHandler? HourPicked;

    static ClockFace()
    {
        AffectsRender<ClockFace>(HourProperty, MinuteProperty, ModeProperty, FaceBrushProperty, AccentBrushProperty,
            SubtleBrushProperty, TextElement.ForegroundProperty);
        FocusableProperty.OverrideDefaultValue<ClockFace>(false);
    }

    /// <summary>
    /// Cria o mostrador com as cores do tema (claro ou escuro).
    /// </summary>
    public ClockFace()
    {
        Width = 240;
        Height = 240;
        Cursor = new Cursor(StandardCursorType.Hand);
        this[!FaceBrushProperty] = this.GetResourceObservable("OsInsetBrush").ToBinding();
        this[!AccentBrushProperty] = this.GetResourceObservable("OsAccentBrush").ToBinding();
        this[!SubtleBrushProperty] = this.GetResourceObservable("OsSubtleTextBrush").ToBinding();
    }

    /// <summary>
    /// Ângulo de um ponto em relação ao centro, em graus no sentido horário a partir das 12 h (0 ≤ ângulo &lt; 360).
    /// </summary>
    /// <param name="dx">Deslocamento horizontal (positivo para a direita).</param>
    /// <param name="dy">Deslocamento vertical (positivo para baixo, como na tela).</param>
    public static double AngleOf(double dx, double dy)
    {
        double degrees = Math.Atan2(dx, -dy) * 180.0 / Math.PI;
        return degrees < 0 ? degrees + 360.0 : degrees;
    }

    /// <summary>
    /// Hora (0–23) sob o ponto: posição do relógio pelo ângulo; anel interno (12–23) quando o ponto está mais
    /// perto do centro que a meia distância entre os anéis.
    /// </summary>
    /// <param name="dx">Deslocamento horizontal em relação ao centro.</param>
    /// <param name="dy">Deslocamento vertical em relação ao centro (positivo para baixo).</param>
    /// <param name="radius">Raio do mostrador.</param>
    public static int HourAt(double dx, double dy, double radius)
    {
        int position = (int)Math.Round(AngleOf(dx, dy) / 30.0) % 12;
        double distance = Math.Sqrt(dx * dx + dy * dy);
        bool inner = distance < radius * (InnerRingRatio + OuterRingRatio) / 2.0;
        return inner ? position + 12 : position;
    }

    /// <summary>
    /// Minuto (0–59) sob o ponto.
    /// </summary>
    /// <param name="dx">Deslocamento horizontal em relação ao centro.</param>
    /// <param name="dy">Deslocamento vertical em relação ao centro (positivo para baixo).</param>
    /// <param name="snapToFive">Arredonda para o múltiplo de 5 mais próximo (clique sem arraste).</param>
    public static int MinuteAt(double dx, double dy, bool snapToFive)
    {
        double angle = AngleOf(dx, dy);
        return snapToFive
            ? (int)Math.Round(angle / 30.0) % 12 * 5
            : (int)Math.Round(angle / 6.0) % 60;
    }

    /// <summary>
    /// Posição do número no mostrador, em relação ao centro.
    /// </summary>
    /// <param name="position">Posição no relógio (0 = topo, 1 = 1 h ou 5 min, …, 11).</param>
    /// <param name="ringRadius">Raio do anel.</param>
    public static Point PositionOf(double position, double ringRadius)
    {
        double radians = position * 30.0 * Math.PI / 180.0;
        return new Point(Math.Sin(radians) * ringRadius, -Math.Cos(radians) * ringRadius);
    }

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _pressed = true;
        _dragged = false;
        _pressPoint = e.GetPosition(this);
        e.Pointer.Capture(this);
        PickAt(_pressPoint, snapToFive: true);
        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_pressed) return;
        var point = e.GetPosition(this);
        var moved = point - _pressPoint;
        if (!_dragged && Math.Sqrt(moved.X * moved.X + moved.Y * moved.Y) < 4) return;
        _dragged = true;
        PickAt(point, snapToFive: false);
        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_pressed) return;
        _pressed = false;
        e.Pointer.Capture(null);
        e.Handled = true;
        if (Mode == ClockFaceMode.Hours)
        {
            HourPicked?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc/>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _pressed = false;
    }

    private void PickAt(Point point, bool snapToFive)
    {
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        double dx = point.X - center.X, dy = point.Y - center.Y;
        double radius = Math.Min(Bounds.Width, Bounds.Height) / 2;
        if (Mode == ClockFaceMode.Hours)
        {
            Hour = HourAt(dx, dy, radius);
        }
        else
        {
            Minute = MinuteAt(dx, dy, snapToFive);
        }
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        double radius = Math.Min(Bounds.Width, Bounds.Height) / 2;
        if (radius <= 0) return;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var foreground = TextElement.GetForeground(this) ?? Brushes.Black;
        var accent = AccentBrush ?? Brushes.SteelBlue;
        var typeface = new Typeface(TextElement.GetFontFamily(this));

        context.DrawEllipse(FaceBrush ?? Brushes.Transparent, null, center, radius, radius);

        // Valor escolhido: posição (0–11, fracionária nos minutos fora do múltiplo de 5) e anel
        double selectedPosition;
        double selectedRing;
        if (Mode == ClockFaceMode.Hours)
        {
            selectedPosition = Hour % 12;
            selectedRing = radius * (Hour >= 12 ? InnerRingRatio : OuterRingRatio);
        }
        else
        {
            selectedPosition = Minute / 5.0;
            selectedRing = radius * OuterRingRatio;
        }
        const double bubble = 17;
        var tip = center + PositionOf(selectedPosition, selectedRing);
        var hand = new Pen(accent, 2);
        context.DrawEllipse(accent, null, center, 3.5, 3.5);
        context.DrawLine(hand, center, tip);
        context.DrawEllipse(accent, null, tip, bubble, bubble);

        // Números
        for (int i = 0; i < 12; i++)
        {
            if (Mode == ClockFaceMode.Hours)
            {
                DrawLabel(context, typeface, center, radius * OuterRingRatio, i, i.ToString("00", CultureInfo.InvariantCulture),
                    14, Hour == i ? Brushes.White : foreground);
                DrawLabel(context, typeface, center, radius * InnerRingRatio, i, (i + 12).ToString("00", CultureInfo.InvariantCulture),
                    12.5, Hour == i + 12 ? Brushes.White : SubtleBrush ?? foreground);
            }
            else
            {
                DrawLabel(context, typeface, center, radius * OuterRingRatio, i, (i * 5).ToString("00", CultureInfo.InvariantCulture),
                    14, Minute == i * 5 ? Brushes.White : foreground);
            }
        }

        // Minuto fora do múltiplo de 5: ponto no centro da marca, como no Google Agenda
        if (Mode == ClockFaceMode.Minutes && Minute % 5 != 0)
        {
            context.DrawEllipse(Brushes.White, null, tip, 3, 3);
        }
    }

    private static void DrawLabel(DrawingContext context, Typeface typeface, Point center, double ring, int position,
        string text, double size, IBrush brush)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, brush);
        var at = center + PositionOf(position, ring);
        context.DrawText(formatted, new Point(at.X - formatted.Width / 2, at.Y - formatted.Height / 2));
    }
}
