using System;
using System.Collections.Generic;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using OmniSync.Models;
using OmniSync.Services;

namespace OmniSync.ViewModels;

/// <summary>
/// Nó da árvore "Pastas com alterações": uma pasta relativa e a contagem de alterações abaixo dela.
/// </summary>
public partial class FolderNode : ObservableObject
{
    /// <summary>Nome exibido.</summary>
    public required string Name { get; init; }

    /// <summary>Caminho relativo da pasta (vazio para a raiz).</summary>
    public required string RelativePath { get; init; }

    /// <summary>Alterações (novos, conflitos, órfãos e erros) nesta pasta e nas subpastas.</summary>
    public int ChangeCount { get; set; }

    /// <summary>Conflitos nesta pasta e nas subpastas.</summary>
    public int ConflictCount { get; set; }

    /// <summary>Subpastas com alterações.</summary>
    public List<FolderNode> Children { get; } = new();

    /// <summary>Se o nó está expandido na árvore.</summary>
    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>Indica se há conflitos (para o selo laranja).</summary>
    public bool HasConflicts => ConflictCount > 0;

    /// <summary>
    /// Indica se o item pertence a esta pasta ou a uma subpasta dela.
    /// </summary>
    /// <param name="item">Item da análise.</param>
    public bool Contains(SyncItem item)
    {
        if (RelativePath.Length == 0) return true;
        var folder = item.FolderPath;
        return string.Equals(folder, RelativePath, PathHelper.PathComparison) ||
               (folder.Length > RelativePath.Length &&
                folder.StartsWith(RelativePath, PathHelper.PathComparison) &&
                IsSeparator(folder[RelativePath.Length]));
    }

    private static bool IsSeparator(char c) => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;
}

/// <summary>
/// Monta a árvore de pastas com alterações a partir dos itens da análise.
/// </summary>
public static class FolderTreeBuilder
{
    /// <summary>
    /// Constrói a árvore. Itens idênticos não contam; a raiz sempre existe e agrega tudo.
    /// </summary>
    /// <param name="items">Itens da análise.</param>
    /// <param name="rootName">Nome exibido para a raiz.</param>
    /// <returns>Nó raiz, expandido, com os filhos ordenados por nome.</returns>
    public static FolderNode Build(IEnumerable<SyncItem> items, string rootName)
    {
        var root = new FolderNode { Name = rootName, RelativePath = string.Empty, IsExpanded = true };
        var index = new Dictionary<string, FolderNode>(PathHelper.PathComparer);
        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };

        foreach (var item in items)
        {
            if (item.IsIdentical && !item.HasError) continue;

            bool conflict = item.IsConflict;
            root.ChangeCount++;
            if (conflict) root.ConflictCount++;

            var folder = item.FolderPath;
            if (folder.Length == 0) continue;

            var parent = root;
            var segments = folder.Split(separators, StringSplitOptions.RemoveEmptyEntries);
            var path = string.Empty;
            foreach (var segment in segments)
            {
                path = path.Length == 0 ? segment : path + Path.DirectorySeparatorChar + segment;
                if (!index.TryGetValue(path, out var node))
                {
                    node = new FolderNode { Name = segment, RelativePath = path };
                    index[path] = node;
                    parent.Children.Add(node);
                }
                node.ChangeCount++;
                if (conflict) node.ConflictCount++;
                parent = node;
            }
        }

        Sort(root);
        foreach (var child in root.Children)
        {
            // Primeiro nível aberto quando há poucas pastas, para a árvore não começar vazia
            child.IsExpanded = root.Children.Count <= 8;
        }
        return root;
    }

    private static void Sort(FolderNode node)
    {
        if (node.Children.Count == 0) return;
        node.Children.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
        foreach (var child in node.Children)
        {
            Sort(child);
        }
    }
}
