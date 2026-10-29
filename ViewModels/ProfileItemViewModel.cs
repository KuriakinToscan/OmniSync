using System;
using CommunityToolkit.Mvvm.ComponentModel;
using OmniSync.Models;

namespace OmniSync.ViewModels;

/// <summary>
/// Estado exibido pelo ponto colorido de um par na barra lateral.
/// </summary>
public enum ProfileStatusKind
{
    /// <summary>Sem análise nem execução recente.</summary>
    Idle,
    /// <summary>Plano analisado aguardando sincronização.</summary>
    PlanReady,
    /// <summary>Sincronização em andamento.</summary>
    Running,
    /// <summary>Última sincronização sem falhas.</summary>
    Success,
    /// <summary>Última sincronização com falhas ou interrompida.</summary>
    Problem
}

/// <summary>
/// Item da barra lateral "Pares salvos": envolve um <see cref="SyncProfile"/> e o estado de sessão do par.
/// </summary>
public partial class ProfileItemViewModel : ObservableObject
{
    /// <summary>Par persistido.</summary>
    public SyncProfile Profile { get; }

    /// <summary>Nome exibido.</summary>
    [ObservableProperty]
    private string _name;

    /// <summary>Linha secundária (estado ou última execução).</summary>
    [ObservableProperty]
    private string _subtitle = string.Empty;

    /// <summary>Estado para a cor do ponto.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsPlanReady), nameof(IsRunning), nameof(IsSuccess), nameof(IsProblem))]
    private ProfileStatusKind _status;

    /// <summary>Sem estado relevante.</summary>
    public bool IsIdle => Status == ProfileStatusKind.Idle;

    /// <summary>Plano pronto.</summary>
    public bool IsPlanReady => Status == ProfileStatusKind.PlanReady;

    /// <summary>Em execução.</summary>
    public bool IsRunning => Status == ProfileStatusKind.Running;

    /// <summary>Último resultado sem falhas.</summary>
    public bool IsSuccess => Status == ProfileStatusKind.Success;

    /// <summary>Último resultado com falhas ou cancelado.</summary>
    public bool IsProblem => Status == ProfileStatusKind.Problem;

    /// <summary>
    /// Cria o item a partir do par salvo.
    /// </summary>
    public ProfileItemViewModel(SyncProfile profile)
    {
        Profile = profile;
        _name = profile.Name;
        RefreshFromLastRun();
    }

    partial void OnNameChanged(string value)
    {
        Profile.Name = value;
    }

    /// <summary>
    /// Mostra o estado de um plano analisado ainda não executado.
    /// </summary>
    /// <param name="changes">Alterações planejadas.</param>
    /// <param name="pendingDecisions">Conflitos sem decisão.</param>
    public void ShowPlanReady(int changes, int pendingDecisions)
    {
        Status = ProfileStatusKind.PlanReady;
        Subtitle = changes == 0
            ? "Analisado · nada a fazer"
            : pendingDecisions > 0
                ? $"Plano pronto · {pendingDecisions} sem decisão"
                : $"Plano pronto · {changes} alterações";
    }

    /// <summary>
    /// Mostra o progresso da execução.
    /// </summary>
    /// <param name="percent">Percentual concluído.</param>
    public void ShowRunning(double percent)
    {
        Status = ProfileStatusKind.Running;
        Subtitle = $"Sincronizando · {percent:0}%";
    }

    /// <summary>
    /// Volta a exibir o resultado da última execução persistida.
    /// </summary>
    public void RefreshFromLastRun()
    {
        if (string.IsNullOrWhiteSpace(Profile.SourcePath) || string.IsNullOrWhiteSpace(Profile.DestinationPath))
        {
            Status = ProfileStatusKind.Idle;
            Subtitle = "Escolha origem e destino";
            return;
        }

        if (!Profile.LastRunUtc.HasValue || Profile.LastOutcome == ProfileRunOutcome.None)
        {
            Status = ProfileStatusKind.Idle;
            Subtitle = "Nunca sincronizado";
            return;
        }

        var when = FormatWhen(Profile.LastRunUtc.Value.ToLocalTime(), DateTime.Now);
        switch (Profile.LastOutcome)
        {
            case ProfileRunOutcome.Success:
                Status = ProfileStatusKind.Success;
                Subtitle = $"Sincronizado {when}";
                break;
            case ProfileRunOutcome.Failures:
                Status = ProfileStatusKind.Problem;
                Subtitle = $"{Profile.LastFailedCount} falha(s) · {when}";
                break;
            default:
                Status = ProfileStatusKind.Problem;
                Subtitle = $"Interrompido · {when}";
                break;
        }
    }

    /// <summary>
    /// Formata um momento relativo ao agora: "hoje, 14:05", "ontem, 18:42" ou "12/10/2026".
    /// </summary>
    /// <param name="local">Momento no fuso local.</param>
    /// <param name="now">Agora no fuso local.</param>
    public static string FormatWhen(DateTime local, DateTime now)
    {
        if (local.Date == now.Date) return $"hoje, {local:HH:mm}";
        if (local.Date == now.Date.AddDays(-1)) return $"ontem, {local:HH:mm}";
        return local.ToString("dd/MM/yyyy");
    }
}
