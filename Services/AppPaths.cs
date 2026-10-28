using System;
using System.IO;

namespace OmniSync.Services;

/// <summary>
/// Locais graváveis por usuário para configurações, logs e relatórios de falha.
/// Windows: <c>%LOCALAPPDATA%\OmniSync</c>; Linux: <c>~/.local/share/OmniSync</c>.
/// Evita gravar ao lado do executável, que pode estar em pasta somente leitura.
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// Pasta de dados do aplicativo (criada sob demanda).
    /// </summary>
    public static string DataDirectory { get; } = ResolveDataDirectory();

    /// <summary>
    /// Pasta dos logs de operação diários.
    /// </summary>
    public static string LogsDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>
    /// Arquivo de configurações do usuário.
    /// </summary>
    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    /// <summary>
    /// Arquivo de registro de falhas inesperadas.
    /// </summary>
    public static string CrashLogFile => Path.Combine(DataDirectory, "crash.log");

    private static string ResolveDataDirectory()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = Path.GetTempPath();
        }
        return Path.Combine(baseDir, "OmniSync");
    }
}
