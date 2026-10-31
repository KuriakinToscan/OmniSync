using System;
using System.IO;
using OmniSync.Models;

namespace OmniSync.Services.Scheduling;

/// <summary>
/// Verifica se o agendamento salvo numa tarefa ainda funciona: o executável registrado existe e o agendador do
/// sistema ainda tem a entrada. Detecta o caso comum de o usuário apagar o executável baixado ou de a entrada ser
/// removida à mão no Agendador de Tarefas.
/// </summary>
public static class ScheduleHealth
{
    /// <summary>
    /// Descreve o problema do agendamento da tarefa, ou <c>null</c> se está em ordem (ou desligado).
    /// </summary>
    /// <param name="profile">Tarefa.</param>
    /// <param name="scheduler">Agendador do sistema.</param>
    /// <param name="fileExists">Teste de existência de arquivo (padrão: <see cref="File.Exists(string)"/>).</param>
    /// <returns>Motivo em português, começando em minúscula, para compor frase.</returns>
    public static string? Check(SyncProfile profile, ISystemScheduler scheduler, Func<string, bool>? fileExists = null)
    {
        var schedule = profile.Schedule;
        if (schedule is not { Enabled: true }) return null;
        fileExists ??= File.Exists;

        if (string.IsNullOrWhiteSpace(schedule.RegisteredExecutable) || !fileExists(schedule.RegisteredExecutable))
        {
            return $"o executável do agendamento não existe mais ({schedule.RegisteredExecutable ?? "não registrado"})";
        }
        if (!scheduler.Exists(profile.Id))
        {
            return OperatingSystem.IsWindows()
                ? "a entrada não está mais no Agendador de Tarefas do Windows"
                : "o timer não está mais no systemd do usuário";
        }
        return null;
    }
}
