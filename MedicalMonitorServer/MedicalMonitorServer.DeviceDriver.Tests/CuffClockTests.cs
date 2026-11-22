using MedicalMonitorServer.DeviceDriver.NihonKohden.LsNet;

namespace MedicalMonitorServer.DeviceDriver.Tests;

/// <summary>
/// A Nihon Kohden over LS-NET repeats its last cuff reading every second and never says when
/// it was taken, so the reading is timed by when it was seen to arrive - and only then.
/// </summary>
public class CuffClockTests
{
    private static readonly DateTime T0 = new(2026, 11, 22, 16, 27, 0);

    [Fact]
    public void A_reading_that_was_already_there_has_no_time()
    {
        var clock = new CuffClock();

        Assert.Null(clock.Observe(122, 76, 91, measuring: false, T0));
        Assert.Null(clock.Observe(122, 76, 91, measuring: false, T0.AddSeconds(1)));
    }

    [Fact]
    public void A_reading_seen_to_arrive_is_timed_then_and_keeps_that_time()
    {
        var clock = new CuffClock();
        clock.Observe(null, null, null, measuring: false, T0);

        var arrived = T0.AddSeconds(78);
        Assert.Equal(arrived, clock.Observe(122, 76, 91, measuring: false, arrived));
        Assert.Equal(arrived, clock.Observe(122, 76, 91, measuring: false, arrived.AddSeconds(30)));
    }

    [Fact]
    public void A_new_reading_gets_a_new_time()
    {
        var clock = new CuffClock();
        clock.Observe(122, 76, 91, measuring: false, T0);

        var second = T0.AddMinutes(5);
        Assert.Equal(second, clock.Observe(118, 74, 89, measuring: false, second));
    }

    [Fact]
    public void A_measurement_that_ends_on_the_same_numbers_is_still_a_new_reading()
    {
        var clock = new CuffClock();
        clock.Observe(122, 76, 91, measuring: false, T0);

        // The cuff inflates; the monitor keeps showing the old reading meanwhile.
        Assert.Null(clock.Observe(122, 76, 91, measuring: true, T0.AddSeconds(1)));

        var done = T0.AddSeconds(40);
        Assert.Equal(done, clock.Observe(122, 76, 91, measuring: false, done));
    }

    [Fact]
    public void A_reading_the_monitor_drops_takes_its_time_with_it()
    {
        var clock = new CuffClock();
        clock.Observe(null, null, null, measuring: false, T0);
        clock.Observe(122, 76, 91, measuring: false, T0.AddSeconds(1));

        Assert.Null(clock.Observe(null, null, null, measuring: false, T0.AddSeconds(2)));
    }

    [Fact]
    public void After_the_stream_stopped_what_is_there_was_not_seen_arriving()
    {
        var clock = new CuffClock();
        clock.Observe(null, null, null, measuring: false, T0);
        clock.Observe(122, 76, 91, measuring: false, T0.AddSeconds(1));

        clock.Reset();

        Assert.Null(clock.Observe(122, 76, 91, measuring: false, T0.AddMinutes(10)));
    }
}
