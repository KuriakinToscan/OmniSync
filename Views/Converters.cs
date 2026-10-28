using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace OmniSync.Views;

/// <summary>
/// Retorna <c>true</c> quando o valor vinculado é igual ao parâmetro (usado para destacar o filtro ativo).
/// </summary>
public sealed class EqualsConverter : IValueConverter
{
    /// <summary>
    /// Instância compartilhada.
    /// </summary>
    public static readonly EqualsConverter Instance = new();

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Equals(value, parameter);

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
