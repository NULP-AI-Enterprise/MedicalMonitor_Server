using MedicalMonitorServer.Contracts.Api;
using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.Core.Monitoring;

namespace MedicalMonitorServer.Core.Tests;

/// <summary>
/// What a monitor measures beyond the six vitals - an arterial line, a second temperature, a
/// perfusion index - reaches the stations as a list of parameters, and leaves it when the
/// monitor stops measuring it.
/// </summary>
public class MonitorParametersTests
{
    private static ParameterDto Art(double value) => new("art.sys", "ART Sys", value, "mmHg", "art");

    private static IReadOnlyDictionary<string, ParameterDto> Parameters(params ParameterDto[] parameters) =>
        parameters.ToDictionary(p => p.Code);

    [Fact]
    public void A_known_id_gets_its_key_name_unit_and_group()
    {
        var parameter = MonitorParameters.Describe(new VitalSign { ParameterId = ServerParameterIds.ArtSystolic, Value = 118 });

        Assert.Equal(new ParameterDto("art.sys", "ART Sys", 118, "mmHg", "art"), parameter);
        Assert.Equal("temp.2", MonitorParameters.Describe(new VitalSign { ParameterId = 201, Value = 37.0 })!.Code);
        Assert.Equal("spo2.pi", MonitorParameters.Describe(new VitalSign { ParameterId = 162, Value = 12.0 })!.Code);
    }

    [Fact]
    public void One_of_the_six_vitals_is_not_a_parameter()
    {
        Assert.All(new[] { 101, 151, 160, 161, 600, 170, 171, 172, 200 }, id =>
        {
            Assert.Null(MonitorParameters.CodeOf(id));
            Assert.Null(MonitorParameters.Describe(new VitalSign { ParameterId = id, Name = "x", Value = 1 }));
        });
    }

    [Fact]
    public void An_unknown_id_goes_under_the_monitors_own_name_or_not_at_all()
    {
        var named = MonitorParameters.Describe(new VitalSign { ParameterId = 121, Name = "QT", Unit = "ms", Value = 400 });
        Assert.Equal(new ParameterDto("p.121", "QT", 400, "ms", "other"), named);

        // A number nobody can name is not shown: it could be taken for another value.
        Assert.Null(MonitorParameters.Describe(new VitalSign { ParameterId = 121, Value = 400 }));
        Assert.Null(MonitorParameters.Describe(new VitalSign { ParameterId = 0, Name = "RR", Value = 20 }));
    }

    [Fact]
    public void A_value_without_a_number_is_no_parameter()
    {
        Assert.Null(MonitorParameters.Describe(new VitalSign { ParameterId = 162, Value = null }));
    }

    [Fact]
    public void A_piecemeal_update_adds_parameters_and_keeps_the_others()
    {
        var snapshot = new VitalReadings(HeartRate: 60, Parameters: Parameters(Art(118)));

        var merged = snapshot.Merge(new VitalReadings(Parameters: Parameters(new ParameterDto("spo2.pi", "PI", 12, "%", "spo2"))));

        Assert.Equal(60, merged.HeartRate);
        Assert.Equal(new[] { "art.sys", "spo2.pi" }, merged.Parameters!.Keys.OrderBy(k => k));
    }

    [Fact]
    public void An_update_takes_off_the_parameters_it_withdraws()
    {
        var snapshot = new VitalReadings(Parameters: Parameters(Art(118), new ParameterDto("spo2.pi", "PI", 12, "%", "spo2")));

        var merged = snapshot.Merge(new VitalReadings(WithdrawnParameters: new[] { "spo2.pi" }));

        Assert.Equal(new[] { "art.sys" }, merged.Parameters!.Keys);
        Assert.Null(merged.WithdrawnParameters);        // a snapshot never carries the list on
    }

    [Fact]
    public void Withdrawing_the_last_parameter_leaves_none_rather_than_an_empty_list()
    {
        var merged = new VitalReadings(Parameters: Parameters(Art(118)))
            .Merge(new VitalReadings(WithdrawnParameters: new[] { "art.sys" }));

        Assert.Null(merged.Parameters);
        Assert.Equal(new VitalReadings(), merged);
        Assert.False(merged.HasValues);
    }

    [Fact]
    public void A_complete_report_replaces_the_parameters_with_its_own()
    {
        var snapshot = new VitalReadings(HeartRate: 80, Parameters: Parameters(Art(118), new ParameterDto("cvp.mean", "CVP Mean", 8, "mmHg", "cvp")));

        // The arterial line was taken out: the next report no longer lists it.
        var replaced = snapshot.ReplaceRunning(new VitalReadings(HeartRate: 81, Parameters: Parameters(new ParameterDto("cvp.mean", "CVP Mean", 9, "mmHg", "cvp"))));

        Assert.Equal(new[] { "cvp.mean" }, replaced.Parameters!.Keys);
        Assert.Equal(9, replaced.Parameters["cvp.mean"].Value);
    }

    [Fact]
    public void Readings_that_only_withdraw_are_not_measured_values()
    {
        Assert.False(new VitalReadings(WithdrawnParameters: new[] { "spo2.pi" }).HasValues);
        Assert.True(new VitalReadings(Parameters: Parameters(Art(118))).HasValues);
        Assert.True(new VitalReadings(HeartRate: 60).HasValues);
    }

    [Fact]
    public void The_wire_form_lists_parameters_in_a_fixed_order_and_omits_an_empty_list()
    {
        var dto = new VitalReadings(Parameters: Parameters(new ParameterDto("temp.2", "T2", 37, "°C", "temp"), Art(118))).ToDto();

        Assert.Equal(new[] { "art.sys", "temp.2" }, dto.Parameters!.Select(p => p.Code));
        Assert.Null(new VitalReadings(HeartRate: 60).ToDto().Parameters);
    }

    [Fact]
    public void Parameters_survive_a_round_trip_through_the_wire_form()
    {
        var readings = VitalReadings.From(new VitalsDto(60, 98, null, null, null, null, new[] { Art(118) }));

        Assert.Equal(118, readings.Parameters!["art.sys"].Value);
    }

    [Fact]
    public void A_cuff_reading_carries_the_time_it_was_taken()
    {
        var taken = new DateTimeOffset(2026, 11, 22, 15, 40, 0, TimeSpan.FromHours(2));
        var snapshot = new VitalReadings().Merge(new VitalReadings(Systolic: 120, Diastolic: 80, NibpMeasuredAt: taken));

        Assert.Equal(taken, snapshot.ToDto().Nibp!.MeasuredAt);

        // Values that are not a cuff reading leave the reading and its time alone...
        var later = snapshot.Merge(new VitalReadings(HeartRate: 61));
        Assert.Equal(taken, later.NibpMeasuredAt);

        // ...and a new reading that does not say when it was taken must not keep the old time.
        var untimed = later.Merge(new VitalReadings(Systolic: 125, Diastolic: 82));
        Assert.Null(untimed.NibpMeasuredAt);
    }

    [Fact]
    public void A_withdrawn_cuff_reading_goes_with_its_time()
    {
        var taken = new DateTimeOffset(2026, 11, 22, 16, 28, 42, TimeSpan.FromHours(2));
        var snapshot = new VitalReadings(HeartRate: 80, Systolic: 122, Diastolic: 76, MeanArterialPressure: 91, NibpMeasuredAt: taken);

        var replaced = snapshot.ReplaceRunning(new VitalReadings(HeartRate: 81, CuffWithdrawn: true));

        Assert.Equal(new VitalReadings(HeartRate: 81), replaced);
        Assert.Null(replaced.ToDto().Nibp!.MeasuredAt);
        Assert.Equal(new VitalReadings(HeartRate: 80), snapshot.Merge(new VitalReadings(CuffWithdrawn: true)));
    }

    [Fact]
    public void A_complete_report_without_a_cuff_reading_keeps_the_last_one_and_its_time()
    {
        var taken = new DateTimeOffset(2026, 11, 22, 15, 40, 0, TimeSpan.FromHours(2));
        var snapshot = new VitalReadings(Systolic: 120, Diastolic: 80, NibpMeasuredAt: taken);

        var replaced = snapshot.ReplaceRunning(new VitalReadings(HeartRate: 80));

        Assert.Equal((120, taken), (replaced.Systolic, replaced.NibpMeasuredAt));
    }
}
