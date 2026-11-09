using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using OmniSync.Services;

namespace OmniSync.Views;

/// <summary>
/// DIAGNÓSTICO TEMPORÁRIO (build de teste do clique duplo, fora da main): grava no log do dia, com o prefixo
/// <c>[DIAG]</c>, a ativação da janela, as mensagens de mouse e ativação do Windows, cada aperto e soltura do
/// mouse (onde caiu e se algum controle o consumiu), a captura do ponteiro, a troca de foco e os cliques em botões.
/// Serve para descobrir onde se perde o clique que traz a janela de volta à frente.
/// </summary>
public static class ClickDiagnostics
{
    private const uint WmActivate = 0x0006;
    private const uint WmSetFocus = 0x0007;
    private const uint WmKillFocus = 0x0008;
    private const uint WmMouseActivate = 0x0021;
    private const uint WmNcActivate = 0x0086;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmPointerDown = 0x0246;
    private const uint WmPointerUp = 0x0247;
    private const uint WmCaptureChanged = 0x0215;

    /// <summary>
    /// Liga o registro à janela.
    /// </summary>
    /// <param name="window">Janela principal.</param>
    public static void Attach(Window window)
    {
        Log("Diagnóstico de clique ligado.");
        window.Activated += (_, _) => Log($"Janela ATIVADA. Foco atual: {Describe(window.FocusManager?.GetFocusedElement() as Visual)}");
        window.Deactivated += (_, _) => Log("Janela DESATIVADA.");

        window.AddHandler(InputElement.PointerPressedEvent, (_, e) => LogPointer("PRESS túnel", window, e),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.PointerPressedEvent, (_, e) => LogPointer("PRESS bolha", window, e),
            RoutingStrategies.Bubble, handledEventsToo: true);
        window.AddHandler(InputElement.PointerReleasedEvent, (_, e) => LogPointer("SOLTA túnel", window, e),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.PointerReleasedEvent, (_, e) => LogPointer("SOLTA bolha", window, e),
            RoutingStrategies.Bubble, handledEventsToo: true);
        window.AddHandler(InputElement.PointerCaptureLostEvent, (_, e) =>
            Log($"Captura do ponteiro perdida por {Describe(e.Source as Visual)}"),
            RoutingStrategies.Bubble | RoutingStrategies.Direct, handledEventsToo: true);
        window.AddHandler(InputElement.GotFocusEvent, (_, e) =>
            Log($"Foco -> {Describe(e.Source as Visual)}"),
            RoutingStrategies.Bubble, handledEventsToo: true);
        window.AddHandler(Button.ClickEvent, (_, e) =>
            Log($"CLIQUE no botão {Describe(e.Source as Visual)}"),
            RoutingStrategies.Bubble, handledEventsToo: true);

        if (OperatingSystem.IsWindows())
        {
            try
            {
                Win32Properties.AddWndProcHookCallback(window, WndProcHook);
            }
            catch (Exception ex)
            {
                Log($"Gancho do WndProc indisponível: {ex.Message}");
            }
        }
    }

    private static IntPtr WndProcHook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        string? name = msg switch
        {
            WmActivate => $"WM_ACTIVATE wParam={wParam.ToInt64() & 0xFFFF}",
            WmSetFocus => "WM_SETFOCUS",
            WmKillFocus => "WM_KILLFOCUS",
            WmMouseActivate => $"WM_MOUSEACTIVATE hit={lParam.ToInt64() & 0xFFFF} msg=0x{(lParam.ToInt64() >> 16) & 0xFFFF:X}",
            WmNcActivate => $"WM_NCACTIVATE {wParam.ToInt64()}",
            WmLButtonDown => "WM_LBUTTONDOWN",
            WmLButtonUp => "WM_LBUTTONUP",
            WmPointerDown => "WM_POINTERDOWN",
            WmPointerUp => "WM_POINTERUP",
            WmCaptureChanged => "WM_CAPTURECHANGED",
            _ => null
        };
        if (name != null) Log("Win32 " + name);
        return IntPtr.Zero;
    }

    private static void LogPointer(string kind, Window window, PointerEventArgs e)
    {
        var point = e.GetPosition(window);
        var hit = window.InputHitTest(point) as Visual;
        Log($"{kind} em ({point.X:0},{point.Y:0}) origem={Describe(e.Source as Visual)} " +
            $"sob o mouse={Describe(hit)} consumido={e.Handled} captura={Describe(e.Pointer.Captured as Visual)} " +
            $"ativa={window.IsActive}");
    }

    private static string Describe(Visual? visual)
    {
        if (visual == null) return "(nenhum)";
        var button = visual as Button ?? visual.FindAncestorOfType<Button>();
        var text = visual.GetType().Name + (string.IsNullOrEmpty((visual as Control)?.Name) ? string.Empty : "#" + ((Control)visual).Name);
        if (button != null && !ReferenceEquals(button, visual))
        {
            text += $" dentro de Button[{ButtonLabel(button)}]";
        }
        else if (button != null)
        {
            text = $"Button[{ButtonLabel(button)}]";
        }
        return text;
    }

    private static string ButtonLabel(Button button)
    {
        var tip = ToolTip.GetTip(button) as string;
        if (button.Content is string s) return s;
        return tip ?? button.Name ?? "?";
    }

    private static void Log(string message) => FileLogService.Write("[DIAG] " + message);
}
