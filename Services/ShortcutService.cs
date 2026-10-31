using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OmniSync.Services;

/// <summary>
/// Atalhos do OmniSync na área de trabalho e no menu Iniciar (Windows, arquivos <c>.lnk</c> pelo
/// <c>WScript.Shell</c>) ou no menu de aplicativos (Linux, arquivos <c>.desktop</c>).
/// </summary>
public static class ShortcutService
{
    private const string Description = "Sincronizador de arquivos com lixo/ e old/";

    /// <summary>
    /// Cria os atalhos pedidos para <paramref name="executable"/>, substituindo atalhos anteriores do OmniSync.
    /// </summary>
    /// <param name="executable">Executável instalado.</param>
    /// <param name="desktop">Criar atalho na área de trabalho.</param>
    /// <param name="startMenu">Criar atalho no menu Iniciar / menu de aplicativos.</param>
    /// <returns>Mensagem de erro (minúscula, para compor frase), ou <c>null</c> em caso de sucesso.</returns>
    public static string? CreateShortcuts(string executable, bool desktop, bool startMenu)
    {
        var failed = new List<string>();
        if (desktop && !TryCreate(executable, DesktopShortcutPath())) failed.Add("na área de trabalho");
        if (startMenu && !TryCreate(executable, StartMenuShortcutPath())) failed.Add(OperatingSystem.IsWindows() ? "no menu Iniciar" : "no menu de aplicativos");
        return failed.Count == 0 ? null : $"não foi possível criar o atalho {string.Join(" nem ", failed)} (detalhes no log).";
    }

    /// <summary>Caminho do atalho na área de trabalho.</summary>
    public static string DesktopShortcutPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolderOption.Create),
        OperatingSystem.IsWindows() ? "OmniSync.lnk" : "omnisync.desktop");

    /// <summary>Caminho do atalho no menu Iniciar (Windows) ou no menu de aplicativos (Linux).</summary>
    public static string StartMenuShortcutPath() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs, Environment.SpecialFolderOption.Create), "OmniSync.lnk")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
            "applications", "omnisync.desktop");

    /// <summary>
    /// Conteúdo do arquivo <c>.desktop</c> do Linux (testável).
    /// </summary>
    /// <param name="executable">Executável instalado.</param>
    /// <param name="icon">Ícone PNG, ou <c>null</c>.</param>
    public static string BuildDesktopEntry(string executable, string? icon) =>
        "[Desktop Entry]\n" +
        "Type=Application\n" +
        "Name=OmniSync\n" +
        $"Comment={Description}\n" +
        $"Exec=\"{executable.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"\n" +
        (icon != null ? $"Icon={icon}\n" : string.Empty) +
        "Terminal=false\n" +
        "Categories=Utility;FileTools;\n";

    private static bool TryCreate(string executable, string shortcutPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
            if (OperatingSystem.IsWindows())
            {
                CreateWindowsShortcut(shortcutPath, executable);
            }
            else
            {
                var icon = Path.Combine(Path.GetDirectoryName(executable)!, AppInstaller.IconFileName);
                File.WriteAllText(shortcutPath, BuildDesktopEntry(executable, File.Exists(icon) ? icon : null));
                File.SetUnixFileMode(shortcutPath, File.GetUnixFileMode(shortcutPath) | UnixFileMode.UserExecute);
            }
            FileLogService.Write($"[INSTALAÇÃO] Atalho criado: {shortcutPath}");
            return true;
        }
        catch (Exception ex)
        {
            // COM indisponível, pasta bloqueada por política etc.: o atalho é conveniência, a instalação continua
            FileLogService.Write($"[AVISO] Não foi possível criar o atalho {shortcutPath}: {ex.Message}");
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void CreateWindowsShortcut(string shortcutPath, string executable)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
                        ?? throw new InvalidOperationException("WScript.Shell indisponível.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            try
            {
                shortcut.TargetPath = executable;
                shortcut.WorkingDirectory = Path.GetDirectoryName(executable);
                shortcut.Description = Description;
                shortcut.IconLocation = executable + ",0";
                shortcut.Save();
            }
            finally
            {
                Marshal.FinalReleaseComObject(shortcut);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }
}
