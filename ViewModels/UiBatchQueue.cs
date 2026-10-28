using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Avalonia.Threading;

namespace OmniSync.ViewModels;

/// <summary>
/// Fila thread-safe que acumula itens produzidos em threads de background e os entrega em lote
/// à thread de UI a cada <c>interval</c>. Evita uma chamada ao Dispatcher por arquivo processado,
/// que era o principal gargalo em árvores com dezenas de milhares de arquivos.
/// </summary>
/// <typeparam name="T">Tipo dos itens enfileirados.</typeparam>
public sealed class UiBatchQueue<T> : IProgress<T>
{
    private readonly ConcurrentQueue<T> _queue = new();
    private readonly Action<IReadOnlyList<T>> _apply;
    private readonly DispatcherTimer _timer;

    /// <summary>
    /// Cria a fila. Deve ser instanciada na thread de UI.
    /// </summary>
    /// <param name="apply">Ação executada na thread de UI com cada lote.</param>
    /// <param name="interval">Intervalo entre entregas.</param>
    public UiBatchQueue(Action<IReadOnlyList<T>> apply, TimeSpan interval)
    {
        _apply = apply;
        _timer = new DispatcherTimer(interval, DispatcherPriority.Background, (_, _) => Flush());
        _timer.Start();
    }

    /// <summary>
    /// Enfileira um item (pode ser chamado de qualquer thread).
    /// </summary>
    public void Report(T value) => _queue.Enqueue(value);

    /// <summary>
    /// Entrega imediatamente tudo o que estiver na fila. Deve ser chamado na thread de UI.
    /// </summary>
    public void Flush()
    {
        if (_queue.IsEmpty) return;
        var batch = new List<T>(Math.Min(_queue.Count, 4096));
        while (_queue.TryDequeue(out var item))
        {
            batch.Add(item);
        }
        _apply(batch);
    }
}

/// <summary>
/// Implementação de <see cref="IProgress{T}"/> que invoca a ação de forma síncrona na thread chamadora
/// (sem postar para a UI). Útil quando a ação já é thread-safe.
/// </summary>
/// <typeparam name="T">Tipo do valor reportado.</typeparam>
public sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
{
    /// <inheritdoc />
    public void Report(T value) => action(value);
}
