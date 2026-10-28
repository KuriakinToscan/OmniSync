using CommunityToolkit.Mvvm.ComponentModel;

namespace OmniSync.ViewModels;

/// <summary>
/// Classe base para todos os ViewModels da aplicação, fornecendo suporte a notificações
/// de mudança de propriedade (<see cref="ObservableObject"/>) segundo o padrão MVVM.
/// </summary>
public abstract class ViewModelBase : ObservableObject
{
}
