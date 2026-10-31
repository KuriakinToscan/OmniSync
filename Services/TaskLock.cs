using System;
using System.IO;

namespace OmniSync.Services;

/// <summary>
/// Trava por tarefa (<c>locks/&lt;id&gt;.lock</c>, aberto com exclusividade) que impede duas sincronizações da
/// mesma tarefa ao mesmo tempo: a interface e o agendador, ou duas execuções agendadas. O sistema libera o arquivo
/// mesmo se o processo terminar de forma abrupta.
/// </summary>
public sealed class TaskLock : IDisposable
{
    private readonly FileStream _stream;

    private TaskLock(FileStream stream) => _stream = stream;

    /// <summary>Pasta padrão das travas.</summary>
    public static string DefaultDirectory => Path.Combine(AppPaths.DataDirectory, "locks");

    /// <summary>
    /// Tenta obter a trava da tarefa.
    /// </summary>
    /// <param name="taskId">Identificador da tarefa.</param>
    /// <param name="directory">Pasta das travas (padrão: <see cref="DefaultDirectory"/>).</param>
    /// <returns>A trava, ou <c>null</c> se outra execução da mesma tarefa já a tem.</returns>
    public static TaskLock? TryAcquire(Guid taskId, string? directory = null)
    {
        var dir = directory ?? DefaultDirectory;
        try
        {
            Directory.CreateDirectory(dir);
            var stream = new FileStream(Path.Combine(dir, $"{taskId:D}.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            return new TaskLock(stream);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Libera a trava.</summary>
    public void Dispose() => _stream.Dispose();
}
