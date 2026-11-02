using System;
using System.IO;
using OmniSync.Services.Scheduling;
using OmniSync.Views;
using Xunit;

namespace OmniSync.Tests;

/// <summary>
/// Testes do seletor de horário com relógio (v1.5.2): geometria do mostrador e adiamento até um horário escolhido.
/// </summary>
public class ClockPickerTests
{
    private const double Radius = 120;

    /// <summary>
    /// O ângulo cresce no sentido horário a partir das 12 h, com y para baixo como na tela.
    /// </summary>
    [Theory]
    [InlineData(0, -10, 0)]
    [InlineData(10, 0, 90)]
    [InlineData(0, 10, 180)]
    [InlineData(-10, 0, 270)]
    public void AngleOf_ShouldBeClockwiseFromTop(double dx, double dy, double expected)
    {
        Assert.Equal(expected, ClockFace.AngleOf(dx, dy), 6);
    }

    /// <summary>
    /// Anel externo 00–11 e interno 12–23; a fronteira entre os anéis fica a meia distância.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    [InlineData(6, 6)]
    [InlineData(9, 9)]
    [InlineData(11, 11)]
    public void HourAt_OuterAndInnerRings(int position, int outerHour)
    {
        var outer = ClockFace.PositionOf(position, Radius * ClockFace.OuterRingRatio);
        var inner = ClockFace.PositionOf(position, Radius * ClockFace.InnerRingRatio);
        Assert.Equal(outerHour, ClockFace.HourAt(outer.X, outer.Y, Radius));
        Assert.Equal(outerHour + 12, ClockFace.HourAt(inner.X, inner.Y, Radius));
    }

    /// <summary>
    /// Clique perto da marca de 18 h (anel interno, posição 6) ainda escolhe 18, mesmo um pouco fora do ângulo.
    /// </summary>
    [Fact]
    public void HourAt_ShouldRoundToNearestPosition()
    {
        var near = ClockFace.PositionOf(6.4, Radius * ClockFace.InnerRingRatio);
        Assert.Equal(18, ClockFace.HourAt(near.X, near.Y, Radius));
        var nearTop = ClockFace.PositionOf(11.6, Radius * ClockFace.OuterRingRatio);
        Assert.Equal(0, ClockFace.HourAt(nearTop.X, nearTop.Y, Radius));
    }

    /// <summary>
    /// Clique arredonda os minutos para 5 em 5; arrastando, vale o minuto exato.
    /// </summary>
    [Fact]
    public void MinuteAt_ShouldSnapOnClickAndBeExactOnDrag()
    {
        // 32 minutos = 192°
        var p = ClockFace.PositionOf(32 / 5.0, 100);
        Assert.Equal(30, ClockFace.MinuteAt(p.X, p.Y, snapToFive: true));
        Assert.Equal(32, ClockFace.MinuteAt(p.X, p.Y, snapToFive: false));
        // Perto do topo, depois de 59 min, volta a 0
        var top = ClockFace.PositionOf(59.8 / 5.0, 100);
        Assert.Equal(0, ClockFace.MinuteAt(top.X, top.Y, snapToFive: false));
        Assert.Equal(0, ClockFace.MinuteAt(top.X, top.Y, snapToFive: true));
    }

    /// <summary>
    /// Próxima ocorrência: hoje se ainda não passou; o mesmo minuto ou antes vira amanhã (inclusive na virada do mês).
    /// </summary>
    [Theory]
    [InlineData("2026-11-02 14:20:30", "18:30", "2026-11-02 18:30:00")]
    [InlineData("2026-11-02 14:20:30", "14:21", "2026-11-02 14:21:00")]
    [InlineData("2026-11-02 14:20:30", "14:20", "2026-11-03 14:20:00")]
    [InlineData("2026-11-02 14:20:30", "07:30", "2026-11-03 07:30:00")]
    [InlineData("2026-11-30 23:50:00", "00:10", "2026-12-01 00:10:00")]
    public void NextOccurrence_ShouldRollToTomorrowWhenPast(string now, string time, string expected)
    {
        var result = SnoozeService.NextOccurrence(DateTime.Parse(now, System.Globalization.CultureInfo.InvariantCulture),
            TimeSpan.Parse(time, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), result);
    }

    /// <summary>
    /// SnoozeUntil registra o disparo único no horário escolhido e grava o estado, como o adiamento por intervalo.
    /// </summary>
    [Fact]
    public void SnoozeUntil_ShouldRegisterAtChosenTime()
    {
        var dir = Path.Combine(Path.GetTempPath(), "omnisync-clock-" + Guid.NewGuid().ToString("N"));
        try
        {
            var scheduler = new RecordingScheduler();
            var service = new SnoozeService(scheduler, dir);
            var id = Guid.NewGuid();
            var request = new SnoozeRequest(id, "Campo", new[] { "--analisar", id.ToString(), "--agendado" }, dir);
            var now = new DateTime(2026, 11, 2, 21, 5, 10);

            Assert.Null(service.SnoozeUntil(request, "/opt/OmniSync", now, new TimeSpan(7, 30, 0)));

            var expected = new DateTime(2026, 11, 3, 7, 30, 0);
            Assert.Equal(expected, scheduler.LastWhen);
            var state = service.Read(id);
            Assert.NotNull(state);
            Assert.Equal(expected, state!.DueLocal);
            Assert.Equal(SnoozeService.BuildName(id, expected), state.Name);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// Sugestão inicial do relógio do Adiar: duas horas cheias à frente, dando a volta à meia-noite.
    /// </summary>
    [Fact]
    public void DefaultPickTime_ShouldSuggestFullHourAhead()
    {
        Assert.Equal(new TimeSpan(16, 0, 0), NotificationWindow.DefaultPickTime(new DateTime(2026, 11, 2, 14, 20, 0)));
        Assert.Equal(new TimeSpan(1, 0, 0), NotificationWindow.DefaultPickTime(new DateTime(2026, 11, 2, 23, 40, 0)));
    }

    private sealed class RecordingScheduler : ISystemScheduler
    {
        public DateTime? LastWhen { get; private set; }
        public string? Register(OmniSync.Models.SyncProfile profile, OmniSync.Models.SyncSchedule schedule, string executable) => null;
        public string? Unregister(Guid taskId) => null;
        public bool Exists(Guid taskId) => false;
        public string? RegisterOnce(string name, string description, System.Collections.Generic.IReadOnlyList<string> arguments,
            string executable, DateTime when)
        {
            LastWhen = when;
            return null;
        }
        public string? UnregisterOnce(string name) => null;
    }
}
