using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmniSync.Models;

namespace OmniSync.ViewModels;

/// <summary>
/// ViewModel do diálogo de restauração seletiva da pasta <c>lixo/</c>.
/// </summary>
public partial class RestoreViewModel : ViewModelBase
{
    /// <summary>
    /// Arquivos encontrados em <c>lixo/</c>.
    /// </summary>
    public IReadOnlyList<TrashEntry> Entries { get; }

    /// <summary>
    /// Resultado: arquivos escolhidos, ou <c>null</c> se cancelado.
    /// </summary>
    public IReadOnlyList<TrashEntry>? Result { get; private set; }

    /// <summary>
    /// Evento disparado para fechar a janela.
    /// </summary>
    public event Action? RequestClose;

    /// <summary>
    /// Resumo da seleção atual.
    /// </summary>
    public string SelectionText
    {
        get
        {
            var selected = Entries.Where(e => e.IsSelected).ToList();
            return $"{selected.Count} de {Entries.Count} selecionados · {SyncItem.FormatBytes(selected.Sum(e => e.Size))}";
        }
    }

    /// <summary>
    /// Indica se há ao menos um arquivo selecionado.
    /// </summary>
    public bool HasSelection => Entries.Any(e => e.IsSelected);

    /// <summary>
    /// Inicializa o diálogo com os arquivos de <c>lixo/</c>.
    /// </summary>
    public RestoreViewModel(IReadOnlyList<TrashEntry> entries)
    {
        Entries = entries;
        foreach (var entry in entries)
        {
            entry.PropertyChanged += OnEntryChanged;
        }
    }

    private void OnEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrashEntry.IsSelected))
        {
            OnPropertyChanged(nameof(SelectionText));
            OnPropertyChanged(nameof(HasSelection));
            RestoreCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Marca todos.
    /// </summary>
    [RelayCommand]
    private void SelectAll() => SetAll(true);

    /// <summary>
    /// Desmarca todos.
    /// </summary>
    [RelayCommand]
    private void SelectNone() => SetAll(false);

    private void SetAll(bool value)
    {
        foreach (var entry in Entries)
        {
            entry.IsSelected = value;
        }
    }

    /// <summary>
    /// Confirma a restauração dos selecionados.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Restore()
    {
        Result = Entries.Where(e => e.IsSelected).ToList();
        Detach();
        RequestClose?.Invoke();
    }

    /// <summary>
    /// Cancela.
    /// </summary>
    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        Detach();
        RequestClose?.Invoke();
    }

    private void Detach()
    {
        foreach (var entry in Entries)
        {
            entry.PropertyChanged -= OnEntryChanged;
        }
    }
}
