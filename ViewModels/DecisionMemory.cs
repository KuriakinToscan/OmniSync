using System;
using System.Collections.Generic;
using System.Linq;
using OmniSync.Models;
using OmniSync.Services;

namespace OmniSync.ViewModels;

/// <summary>
/// Guarda as decisões de conflito tomadas pelo usuário quando o plano é descartado (nova análise, "Alterar",
/// mudança de opção) e as reaplica na análise seguinte do mesmo par Origem/Destino.
/// Uma decisão só volta se as duas versões do arquivo forem as mesmas da análise em que ela foi tomada
/// (tamanho e data de modificação nos dois lados); se qualquer lado mudou, o conflito volta para "Perguntar".
/// </summary>
public sealed class DecisionMemory
{
    private sealed record Snapshot(
        ConflictResolution Resolution,
        long SourceSize,
        DateTime? SourceModifiedUtc,
        long DestinationSize,
        DateTime? DestinationModifiedUtc);

    private readonly Dictionary<string, Snapshot> _decisions = new(PathHelper.PathComparer);
    private string? _pairKey;

    /// <summary>Quantidade de decisões guardadas.</summary>
    public int Count => _decisions.Count;

    /// <summary>
    /// Monta a chave que identifica o par analisado (origem e destino efetivo).
    /// </summary>
    /// <param name="sourcePath">Pasta de Origem.</param>
    /// <param name="effectiveDestinationPath">Destino efetivo.</param>
    public static string PairKey(string sourcePath, string effectiveDestinationPath)
    {
        static string Normalize(string path)
        {
            try
            {
                return PathHelper.NormalizeDirectory(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
            {
                return path.Trim();
            }
        }

        return Normalize(sourcePath) + "\n" + Normalize(effectiveDestinationPath);
    }

    /// <summary>
    /// Substitui as decisões guardadas pelas dos conflitos pendentes de <paramref name="items"/>.
    /// Conflitos ainda em "Perguntar" e itens já processados não são guardados.
    /// </summary>
    /// <param name="pairKey">Chave do par em que os itens foram analisados (<see cref="PairKey"/>).</param>
    /// <param name="items">Itens do plano que será descartado.</param>
    public void Capture(string pairKey, IEnumerable<SyncItem> items)
    {
        _decisions.Clear();
        _pairKey = pairKey;
        foreach (var item in items.Where(i => i.IsConflict && !i.IsProcessed && i.Resolution != ConflictResolution.AskUser))
        {
            _decisions[item.RelativePath] = new Snapshot(
                item.Resolution, item.SourceSize, item.SourceModifiedUtc, item.DestinationSize, item.DestinationModifiedUtc);
        }
    }

    /// <summary>
    /// Reaplica as decisões guardadas aos conflitos de uma nova análise do mesmo par.
    /// </summary>
    /// <param name="pairKey">Chave do par da nova análise.</param>
    /// <param name="items">Itens da nova análise.</param>
    /// <returns>Quantidade de decisões reaplicadas.</returns>
    public int Apply(string pairKey, IEnumerable<SyncItem> items)
    {
        if (_decisions.Count == 0 || !string.Equals(_pairKey, pairKey, StringComparison.Ordinal)) return 0;

        int applied = 0;
        foreach (var item in items.Where(i => i.IsConflict && i.Resolution == ConflictResolution.AskUser))
        {
            if (_decisions.TryGetValue(item.RelativePath, out var saved) &&
                saved.SourceSize == item.SourceSize &&
                saved.SourceModifiedUtc == item.SourceModifiedUtc &&
                saved.DestinationSize == item.DestinationSize &&
                saved.DestinationModifiedUtc == item.DestinationModifiedUtc)
            {
                item.Resolution = saved.Resolution;
                applied++;
            }
        }
        return applied;
    }

    /// <summary>
    /// Esquece todas as decisões guardadas.
    /// </summary>
    public void Clear()
    {
        _decisions.Clear();
        _pairKey = null;
    }
}
