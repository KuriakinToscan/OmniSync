using System;
using System.IO;
using System.Threading.Tasks;

namespace OmniSync.Services;

/// <summary>Resultado de <see cref="PathProbe.DirectoryExistsAsync"/>.</summary>
public enum ProbeResult
{
    /// <summary>A pasta existe.</summary>
    Exists,

    /// <summary>A pasta não existe (ou não é uma pasta).</summary>
    Missing,

    /// <summary>A verificação não terminou dentro do limite (rede ou servidor sem resposta).</summary>
    TimedOut,

    /// <summary>A verificação falhou com erro (sem permissão, conexão caída etc.).</summary>
    Failed
}

/// <summary>
/// Verifica se uma pasta existe fora da thread da interface, com limite de tempo. Numa unidade de rede caída
/// (ex.: <c>Z:</c> mapeada para um NAS desligado), <see cref="Directory.Exists(string)"/> fica preso até o timeout do
/// Windows (cerca de 60 s); chamado na thread da interface, congela a janela ("Não está respondendo"). Aqui a
/// chamada roda em segundo plano e, se passar do limite, a interface segue e avisa; a chamada presa termina sozinha.
/// </summary>
public static class PathProbe
{
    /// <summary>Limite padrão de espera.</summary>
    public static TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Verifica, em segundo plano e com limite de tempo, se <paramref name="path"/> é uma pasta existente.
    /// </summary>
    /// <param name="path">Caminho a verificar.</param>
    /// <param name="exists">Verificação a usar (padrão: <see cref="Directory.Exists(string)"/>); permite usar a de um
    /// sistema de arquivos remoto ou simular lentidão em testes.</param>
    /// <param name="timeout">Limite; <c>null</c> usa <see cref="DefaultTimeout"/>.</param>
    /// <returns>O resultado; nunca lança exceção.</returns>
    public static async Task<ProbeResult> DirectoryExistsAsync(string path, Func<string, bool>? exists = null, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return ProbeResult.Missing;
        var check = exists ?? Directory.Exists;
        var work = Task.Run(() => check(path));
        var finished = await Task.WhenAny(work, Task.Delay(timeout ?? DefaultTimeout)).ConfigureAwait(true);
        if (finished != work)
        {
            // A chamada presa continua em segundo plano; observa a exceção dela para não virar exceção não tratada
            _ = work.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return ProbeResult.TimedOut;
        }
        if (work.IsFaulted) return ProbeResult.Failed;
        return work.Result ? ProbeResult.Exists : ProbeResult.Missing;
    }

    /// <summary>Mensagem para o usuário quando a verificação passou do limite.</summary>
    /// <param name="path">Caminho verificado.</param>
    /// <param name="timeout">Limite usado; <c>null</c> usa <see cref="DefaultTimeout"/>.</param>
    public static string TimeoutMessage(string path, TimeSpan? timeout = null)
    {
        var seconds = Math.Max(1, (int)Math.Round((timeout ?? DefaultTimeout).TotalSeconds));
        return $"{path} não respondeu em {seconds} s. A rede ou o servidor pode estar desconectado; confira e tente de novo.";
    }
}
