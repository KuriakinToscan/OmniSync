using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace OmniSync.Services;

/// <summary>
/// Situação do executável em relação à pasta fixa de instalação.
/// </summary>
public enum InstallState
{
    /// <summary>Em execução a partir da pasta fixa.</summary>
    RunningInstalled,
    /// <summary>Não há cópia na pasta fixa.</summary>
    NotInstalled,
    /// <summary>Há cópia na pasta fixa, igual a este executável.</summary>
    InstalledSame,
    /// <summary>Há cópia na pasta fixa, diferente deste executável (outra versão).</summary>
    InstalledDifferent,
    /// <summary>Não se aplica: execução pelo <c>dotnet</c> (desenvolvimento) ou executável não identificado.</summary>
    NotApplicable
}

/// <summary>
/// Instala o executável numa pasta fixa por usuário (<c>%LOCALAPPDATA%\Programs\OmniSync</c> no Windows,
/// <c>~/.local/share/Programs/OmniSync</c> no Linux) e cria os atalhos. O agendador chama o executável por esse
/// caminho, que não muda quando uma nova versão é baixada em outra pasta.
/// </summary>
public static class AppInstaller
{
    /// <summary>Nome do executável publicado.</summary>
    public static string ExecutableName => OperatingSystem.IsWindows() ? "OmniSync.exe" : "OmniSync";

    /// <summary>Pasta fixa de instalação.</summary>
    public static string InstallDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "Programs", "OmniSync");

    /// <summary>Nome do ícone PNG gravado ao lado do executável no Linux (lido pelo atalho <c>.desktop</c>).</summary>
    public const string IconFileName = "omnisync.png";

    /// <summary>Caminho do executável instalado.</summary>
    public static string InstalledExecutable => Path.Combine(InstallDirectory, ExecutableName);

    /// <summary>Executável deste processo, ou <c>null</c> quando não é o OmniSync publicado (ex.: <c>dotnet run</c>).</summary>
    public static string? CurrentExecutable
    {
        get
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path)) return null;
            return string.Equals(Path.GetFileName(path), ExecutableName, PathHelper.PathComparison) ? path : null;
        }
    }

    /// <summary>
    /// Situação deste executável em relação à pasta fixa.
    /// </summary>
    public static InstallState GetState() => GetState(CurrentExecutable, InstalledExecutable);

    /// <summary>
    /// Situação de <paramref name="current"/> em relação a <paramref name="installed"/> (testável).
    /// </summary>
    /// <param name="current">Executável em execução, ou <c>null</c> se não identificado.</param>
    /// <param name="installed">Executável na pasta fixa.</param>
    public static InstallState GetState(string? current, string installed)
    {
        if (current == null) return InstallState.NotApplicable;
        if (PathHelper.AreSamePath(Path.GetFullPath(current), Path.GetFullPath(installed))) return InstallState.RunningInstalled;
        if (!File.Exists(installed)) return InstallState.NotInstalled;
        return SameContent(current, installed) ? InstallState.InstalledSame : InstallState.InstalledDifferent;
    }

    /// <summary>
    /// Copia este executável para a pasta fixa (substituindo a versão anterior) e cria os atalhos pedidos.
    /// </summary>
    /// <param name="desktopShortcut">Criar atalho na área de trabalho.</param>
    /// <param name="startMenuShortcut">Criar atalho no menu Iniciar (no Linux, no menu de aplicativos).</param>
    /// <param name="iconSource">Abre o ícone PNG gravado ao lado do executável no Linux (usado pelo atalho), ou <c>null</c>.</param>
    /// <returns>Mensagem de erro em português, ou <c>null</c> em caso de sucesso.</returns>
    public static string? Install(bool desktopShortcut, bool startMenuShortcut, Func<Stream>? iconSource = null)
    {
        var current = CurrentExecutable;
        if (current == null) return "Este executável não pode ser instalado (execução de desenvolvimento).";
        return Install(current, InstallDirectory, desktopShortcut, startMenuShortcut, iconSource);
    }

    /// <summary>
    /// Copia <paramref name="sourceExecutable"/> para <paramref name="installDirectory"/> e cria os atalhos (testável).
    /// </summary>
    /// <param name="sourceExecutable">Executável a copiar.</param>
    /// <param name="installDirectory">Pasta de destino.</param>
    /// <param name="desktopShortcut">Criar atalho na área de trabalho.</param>
    /// <param name="startMenuShortcut">Criar atalho no menu Iniciar.</param>
    /// <param name="iconSource">Abre o ícone PNG gravado ao lado do executável no Linux, ou <c>null</c>.</param>
    /// <returns>Mensagem de erro em português, ou <c>null</c> em caso de sucesso.</returns>
    public static string? Install(string sourceExecutable, string installDirectory, bool desktopShortcut, bool startMenuShortcut,
        Func<Stream>? iconSource = null)
    {
        var target = Path.Combine(installDirectory, Path.GetFileName(sourceExecutable));
        var temp = target + ".omnisync-tmp";
        try
        {
            Directory.CreateDirectory(installDirectory);
            File.Copy(sourceExecutable, temp, overwrite: true);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temp, File.GetUnixFileMode(sourceExecutable) | UnixFileMode.UserExecute);
            }
            File.Move(temp, target, overwrite: true);
            FileLogService.Write($"[INSTALAÇÃO] OmniSync copiado para {target}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PathHelper.TryDeleteFile(temp);
            return $"Não foi possível copiar o OmniSync para {installDirectory}. Se a versão instalada estiver aberta, " +
                   $"feche-a e tente de novo. Detalhe: {ErrorMessages.Translate(ex)}";
        }

        if (!OperatingSystem.IsWindows() && iconSource != null)
        {
            // O .desktop do Linux aponta para um PNG; no Windows o atalho usa o ícone embutido no .exe
            try
            {
                using var icon = iconSource();
                using var file = File.Create(Path.Combine(installDirectory, IconFileName));
                icon.CopyTo(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                FileLogService.Write($"[AVISO] Ícone do atalho não gravado: {ex.Message}");
            }
        }

        var shortcutError = ShortcutService.CreateShortcuts(target, desktopShortcut, startMenuShortcut);
        return shortcutError == null ? null : $"O OmniSync foi instalado, mas {shortcutError}";
    }

    /// <summary>
    /// Versão do executável instalado (ex.: "1.5.0"), ou <c>null</c> se ausente ou sem informação de versão.
    /// </summary>
    public static string? InstalledVersion()
    {
        try
        {
            if (!File.Exists(InstalledExecutable)) return null;
            var version = FileVersionInfo.GetVersionInfo(InstalledExecutable).ProductVersion;
            if (string.IsNullOrWhiteSpace(version)) return null;
            int plus = version.IndexOf('+');
            return plus > 0 ? version[..plus] : version;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Versão deste executável (ex.: "1.5.0").
    /// </summary>
    public static string CurrentVersion
    {
        get
        {
            var version = typeof(AppInstaller).Assembly.GetName().Version;
            return version == null ? "?" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    /// <summary>
    /// Abre o executável instalado em um novo processo.
    /// </summary>
    /// <returns><c>true</c> se o processo foi iniciado.</returns>
    public static bool LaunchInstalled()
    {
        try
        {
            Process.Start(new ProcessStartInfo(InstalledExecutable) { UseShellExecute = false, WorkingDirectory = InstallDirectory });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            FileLogService.Write($"[AVISO] Não foi possível abrir {InstalledExecutable}: {ex.Message}");
            return false;
        }
    }

    private static bool SameContent(string a, string b)
    {
        try
        {
            var infoA = new FileInfo(a);
            var infoB = new FileInfo(b);
            if (infoA.Length != infoB.Length) return false;
            using var streamA = File.OpenRead(a);
            using var streamB = File.OpenRead(b);
            return CryptographicOperations.FixedTimeEquals(SHA256.HashData(streamA), SHA256.HashData(streamB));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
