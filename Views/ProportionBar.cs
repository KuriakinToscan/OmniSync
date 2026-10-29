using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace OmniSync.Views;

/// <summary>
/// Barra horizontal empilhada que mostra a proporção entre novos, substituições e órfãos do plano.
/// Segmentos não vazios têm largura mínima para continuarem visíveis mesmo quando a contagem é pequena.
/// </summary>
public sealed class ProportionBar : Control
{
    /// <summary>Quantidade de arquivos novos.</summary>
    public static readonly StyledProperty<int> NewCountProperty =
        AvaloniaProperty.Register<ProportionBar, int>(nameof(NewCount));

    /// <summary>Quantidade de conflitos.</summary>
    public static readonly StyledProperty<int> ConflictCountProperty =
        AvaloniaProperty.Register<ProportionBar, int>(nameof(ConflictCount));

    /// <summary>Quantidade de órfãos.</summary>
    public static readonly StyledProperty<int> OrphanCountProperty =
        AvaloniaProperty.Register<ProportionBar, int>(nameof(OrphanCount));

    /// <summary>Cor dos novos.</summary>
    public static readonly StyledProperty<IBrush?> NewBrushProperty =
        AvaloniaProperty.Register<ProportionBar, IBrush?>(nameof(NewBrush));

    /// <summary>Cor dos conflitos.</summary>
    public static readonly StyledProperty<IBrush?> ConflictBrushProperty =
        AvaloniaProperty.Register<ProportionBar, IBrush?>(nameof(ConflictBrush));

    /// <summary>Cor dos órfãos.</summary>
    public static readonly StyledProperty<IBrush?> OrphanBrushProperty =
        AvaloniaProperty.Register<ProportionBar, IBrush?>(nameof(OrphanBrush));

    /// <summary>Cor do trilho quando não há alterações.</summary>
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<ProportionBar, IBrush?>(nameof(TrackBrush));

    /// <inheritdoc cref="NewCountProperty"/>
    public int NewCount { get => GetValue(NewCountProperty); set => SetValue(NewCountProperty, value); }

    /// <inheritdoc cref="ConflictCountProperty"/>
    public int ConflictCount { get => GetValue(ConflictCountProperty); set => SetValue(ConflictCountProperty, value); }

    /// <inheritdoc cref="OrphanCountProperty"/>
    public int OrphanCount { get => GetValue(OrphanCountProperty); set => SetValue(OrphanCountProperty, value); }

    /// <inheritdoc cref="NewBrushProperty"/>
    public IBrush? NewBrush { get => GetValue(NewBrushProperty); set => SetValue(NewBrushProperty, value); }

    /// <inheritdoc cref="ConflictBrushProperty"/>
    public IBrush? ConflictBrush { get => GetValue(ConflictBrushProperty); set => SetValue(ConflictBrushProperty, value); }

    /// <inheritdoc cref="OrphanBrushProperty"/>
    public IBrush? OrphanBrush { get => GetValue(OrphanBrushProperty); set => SetValue(OrphanBrushProperty, value); }

    /// <inheritdoc cref="TrackBrushProperty"/>
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }

    static ProportionBar()
    {
        AffectsRender<ProportionBar>(
            NewCountProperty, ConflictCountProperty, OrphanCountProperty,
            NewBrushProperty, ConflictBrushProperty, OrphanBrushProperty, TrackBrushProperty);
    }

    /// <summary>
    /// Calcula as larguras dos segmentos (novos, conflitos, órfãos) para a largura disponível,
    /// garantindo <paramref name="minSegment"/> px a cada segmento não vazio e descontando os espaços entre eles.
    /// </summary>
    /// <param name="counts">Contagens por segmento.</param>
    /// <param name="width">Largura total.</param>
    /// <param name="gap">Espaço entre segmentos.</param>
    /// <param name="minSegment">Largura mínima de um segmento não vazio.</param>
    /// <returns>Largura de cada segmento (0 para os vazios).</returns>
    public static double[] ComputeWidths(int[] counts, double width, double gap, double minSegment)
    {
        var widths = new double[counts.Length];
        int nonEmpty = 0;
        long total = 0;
        foreach (var c in counts)
        {
            if (c > 0) { nonEmpty++; total += c; }
        }
        if (nonEmpty == 0 || width <= 0) return widths;

        double available = Math.Max(0, width - gap * (nonEmpty - 1));
        double minTotal = Math.Min(available, minSegment * nonEmpty);
        double flexible = available - minTotal;
        double minEach = minTotal / nonEmpty;
        for (int i = 0; i < counts.Length; i++)
        {
            if (counts[i] > 0)
            {
                widths[i] = minEach + flexible * counts[i] / total;
            }
        }
        return widths;
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        double radius = bounds.Height / 2;
        int[] counts = { NewCount, ConflictCount, OrphanCount };
        IBrush?[] brushes = { NewBrush, ConflictBrush, OrphanBrush };
        const double gap = 2;

        if (counts[0] + counts[1] + counts[2] == 0)
        {
            if (TrackBrush != null)
            {
                context.DrawRectangle(TrackBrush, null, bounds, radius, radius);
            }
            return;
        }

        var widths = ComputeWidths(counts, bounds.Width, gap, 6);
        using (context.PushClip(new RoundedRect(bounds, radius)))
        {
            double x = 0;
            for (int i = 0; i < counts.Length; i++)
            {
                if (widths[i] <= 0) continue;
                if (brushes[i] != null)
                {
                    context.DrawRectangle(brushes[i], null, new Rect(x, 0, widths[i], bounds.Height));
                }
                x += widths[i] + gap;
            }
        }
    }
}
