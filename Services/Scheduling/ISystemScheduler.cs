using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using OmniSync.Models;

namespace OmniSync.Services.Scheduling;

/// <summary>
/// Registro dos agendamentos no agendador do sistema operacional.
/// </summary>
public interface ISystemScheduler
{
    /// <summary>
    /// Cria ou substitui o agendamento da tarefa.
    /// </summary>
    /// <param name="profile">Tarefa (usa o id e o nome).</param>
    /// <param name="schedule">Agendamento a registrar.</param>
    /// <param name="executable">Executável que o agendador vai chamar.</param>
    /// <returns>Mensagem de erro em português, ou <c>null</c> em caso de sucesso.</returns>
    string? Register(SyncProfile profile, SyncSchedule schedule, string executable);

    /// <summary>
    /// Remove o agendamento da tarefa, se existir.
    /// </summary>
    /// <param name="taskId">Identificador da tarefa.</param>
    /// <returns>Mensagem de erro em português, ou <c>null</c> em caso de sucesso (inclusive quando não existia).</returns>
    string? Unregister(Guid taskId);

    /// <summary>
    /// Indica se o agendamento da tarefa existe no agendador do sistema.
    /// </summary>
    /// <param name="taskId">Identificador da tarefa.</param>
    bool Exists(Guid taskId);

    /// <summary>
    /// Registra um disparo único (adiamento), que executa uma vez no horário e depois se desfaz.
    /// </summary>
    /// <param name="name">Nome do disparo (ver <see cref="SnoozeService.BuildName"/>).</param>
    /// <param name="description">Descrição exibida no agendador.</param>
    /// <param name="arguments">Argumentos da execução.</param>
    /// <param name="executable">Executável que o agendador vai chamar.</param>
    /// <param name="when">Horário, em hora local.</param>
    /// <returns>Mensagem de erro em português, ou <c>null</c> em caso de sucesso.</returns>
    string? RegisterOnce(string name, string description, IReadOnlyList<string> arguments, string executable, DateTime when);

    /// <summary>
    /// Remove um disparo único ainda pendente, se existir.
    /// </summary>
    /// <param name="name">Nome do disparo.</param>
    /// <returns>Mensagem de erro em português, ou <c>null</c> em caso de sucesso (inclusive quando não existia).</returns>
    string? UnregisterOnce(string name);
}

/// <summary>
/// Escolhe o agendador do sistema em uso.
/// </summary>
public static class SystemScheduler
{
    /// <summary>Agendador da plataforma atual.</summary>
    public static ISystemScheduler Current { get; } =
        OperatingSystem.IsWindows() ? new WindowsTaskScheduler() : new SystemdTimerScheduler();

    /// <summary>
    /// Executa um utilitário do sistema e devolve código de saída e saídas de texto.
    /// </summary>
    /// <param name="fileName">Programa (ex.: <c>schtasks.exe</c>).</param>
    /// <param name="args">Argumentos, um por item (sem montar aspas à mão).</param>
    /// <param name="encoding">Codificação das saídas, ou <c>null</c> para o padrão.</param>
    internal static (int ExitCode, string Output) Run(string fileName, IEnumerable<string> args, Encoding? encoding = null)
    {
        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (encoding != null)
        {
            info.StandardOutputEncoding = encoding;
            info.StandardErrorEncoding = encoding;
        }
        foreach (var arg in args) info.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60_000))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                return (-1, $"{fileName} não respondeu em 60 s.");
            }
            var output = (stdout.Result + "\n" + stderr.Result).Trim();
            FileLogService.Write($"[AGENDA] {fileName} {string.Join(' ', info.ArgumentList)} → {process.ExitCode}: {output}");
            return (process.ExitCode, output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            FileLogService.Write($"[AGENDA] Falha ao executar {fileName}: {ex.Message}");
            return (-1, $"Não foi possível executar {fileName}: {ex.Message}");
        }
    }

    /// <summary>Primeira linha não vazia de um texto (para mensagens curtas).</summary>
    internal static string FirstLine(string text) =>
        text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
}
