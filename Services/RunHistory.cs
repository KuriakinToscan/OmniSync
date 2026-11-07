using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmniSync.Services;

/// <summary>
/// Uma execução de tarefa registrada no histórico.
/// </summary>
public sealed class RunRecord
{
    /// <summary>Início (UTC).</summary>
    public DateTime StartedUtc { get; set; }

    /// <summary>Duração.</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>Se foi disparada pelo agendador.</summary>
    public bool Scheduled { get; set; }

    /// <summary>Se só analisou (sem alterar o destino).</summary>
    public bool AnalyzeOnly { get; set; }

    /// <summary>Resultado: <c>ok</c>, <c>falhas</c>, <c>abortado</c>, <c>interrompido</c>, <c>ocupado</c> ou <c>erro</c>.</summary>
    public string Outcome { get; set; } = "ok";

    /// <summary>Alterações pendentes encontradas pela análise (novos + conflitos + órfãos).</summary>
    public int PendingChanges { get; set; }

    /// <summary>Arquivos copiados.</summary>
    public int Copied { get; set; }

    /// <summary>Conflitos substituídos.</summary>
    public int Replaced { get; set; }

    /// <summary>Órfãos movidos para <c>lixo/</c>.</summary>
    public int Trashed { get; set; }

    /// <summary>Falhas.</summary>
    public int Failed { get; set; }

    /// <summary>Cópias conferidas só pelo tamanho (servidor remoto sem SHA-256); 0 quando todas tiveram SHA-256.</summary>
    public int VerifiedBySize { get; set; }

    /// <summary>Resumo em uma linha, em português.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Descrição curta para a interface (ex.: "31/10 18:00 · agendada · 3 copiados, 0 falhas").</summary>
    [JsonIgnore]
    public string Display =>
        $"{StartedUtc.ToLocalTime():dd/MM HH:mm} · {(Scheduled ? "agendada" : "manual")}{(AnalyzeOnly ? ", só análise" : "")} · {Summary}";
}

/// <summary>
/// Histórico das execuções por tarefa, em arquivo próprio (<c>history/&lt;id&gt;.json</c>), fora do
/// <c>settings.json</c>: a interface aberta grava o <c>settings.json</c> inteiro e apagaria o registro de uma
/// execução agendada. Guarda as <see cref="MaxRecords"/> mais recentes, mais nova primeiro.
/// </summary>
public static class RunHistory
{
    /// <summary>Quantidade máxima de execuções guardadas por tarefa.</summary>
    public const int MaxRecords = 30;

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Pasta padrão do histórico.</summary>
    public static string DefaultDirectory => Path.Combine(AppPaths.DataDirectory, "history");

    /// <summary>
    /// Lê o histórico da tarefa (mais nova primeiro). Arquivo ausente ou inválido resulta em lista vazia.
    /// </summary>
    /// <param name="taskId">Identificador da tarefa.</param>
    /// <param name="directory">Pasta do histórico (padrão: <see cref="DefaultDirectory"/>).</param>
    public static List<RunRecord> Load(Guid taskId, string? directory = null)
    {
        var path = FilePath(taskId, directory);
        try
        {
            if (!File.Exists(path)) return new List<RunRecord>();
            var records = JsonSerializer.Deserialize<List<RunRecord>>(File.ReadAllText(path), JsonOptions);
            return records?.Where(r => r != null).OrderByDescending(r => r.StartedUtc).ToList() ?? new List<RunRecord>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            FileLogService.Write($"[AVISO] Histórico da tarefa {taskId} ignorado (arquivo inválido ou inacessível): {ex.Message}");
            return new List<RunRecord>();
        }
    }

    /// <summary>
    /// Acrescenta uma execução ao histórico da tarefa, mantendo as <see cref="MaxRecords"/> mais recentes.
    /// Gravação atômica; falhas de gravação vão só para o log.
    /// </summary>
    /// <param name="taskId">Identificador da tarefa.</param>
    /// <param name="record">Execução.</param>
    /// <param name="directory">Pasta do histórico (padrão: <see cref="DefaultDirectory"/>).</param>
    public static void Append(Guid taskId, RunRecord record, string? directory = null)
    {
        lock (Gate)
        {
            try
            {
                var records = Load(taskId, directory);
                records.Insert(0, record);
                var trimmed = records.OrderByDescending(r => r.StartedUtc).Take(MaxRecords).ToList();
                var path = FilePath(taskId, directory);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(trimmed, JsonOptions));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                FileLogService.Write($"[AVISO] Não foi possível gravar o histórico da tarefa {taskId}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Apaga o histórico da tarefa (ao remover a tarefa).
    /// </summary>
    /// <param name="taskId">Identificador da tarefa.</param>
    /// <param name="directory">Pasta do histórico (padrão: <see cref="DefaultDirectory"/>).</param>
    public static void Delete(Guid taskId, string? directory = null) => PathHelper.TryDeleteFile(FilePath(taskId, directory));

    private static string FilePath(Guid taskId, string? directory) =>
        Path.Combine(directory ?? DefaultDirectory, $"{taskId:D}.json");
}
