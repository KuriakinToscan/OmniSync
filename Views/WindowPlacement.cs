using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace OmniSync.Views;

/// <summary>
/// Mantém diálogos inteiros dentro da área útil da tela (sem a barra de tarefas): limita a altura ao espaço
/// disponível, para que o conteúdo role em vez de os botões ficarem fora da tela, e reposiciona a janela
/// quando ela cresce além da borda.
/// </summary>
public static class WindowPlacement
{
    private const double Margin = 16;

    /// <summary>
    /// Liga o ajuste à janela. Chame no construtor, depois de <c>InitializeComponent</c>.
    /// </summary>
    /// <param name="window">Diálogo a manter na tela.</param>
    public static void KeepOnScreen(Window window)
    {
        window.Opened += (_, _) =>
        {
            LimitHeight(window);
            Clamp(window);
        };
        window.SizeChanged += (_, _) => Clamp(window);
    }

    private static Screen? ScreenOf(Window window) =>
        window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;

    private static void LimitHeight(Window window)
    {
        var screen = ScreenOf(window);
        if (screen == null) return;
        double available = screen.WorkingArea.Height / screen.Scaling - 2 * Margin - 40; // 40: barra de título
        if (available > 200 && (double.IsInfinity(window.MaxHeight) || window.MaxHeight > available))
        {
            window.MaxHeight = available;
        }
    }

    private static void Clamp(Window window)
    {
        var screen = ScreenOf(window);
        if (screen == null) return;
        var area = screen.WorkingArea;
        double scaling = screen.Scaling;
        int width = (int)Math.Ceiling(window.Bounds.Width * scaling);
        int height = (int)Math.Ceiling((window.Bounds.Height + 40) * scaling);
        int margin = (int)(Margin * scaling);

        var pos = window.Position;
        int x = Math.Clamp(pos.X, area.X + margin, Math.Max(area.X + margin, area.Right - width - margin));
        int y = Math.Clamp(pos.Y, area.Y + margin, Math.Max(area.Y + margin, area.Bottom - height - margin));
        if (x != pos.X || y != pos.Y)
        {
            window.Position = new PixelPoint(x, y);
        }
    }
}
