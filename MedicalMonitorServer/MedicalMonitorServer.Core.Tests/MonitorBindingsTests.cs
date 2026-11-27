using MedicalMonitorServer.Core.Monitoring;

namespace MedicalMonitorServer.Core.Tests;

public class MonitorBindingsTests
{
    private const string M1 = "192.168.2.101";
    private const string M2 = "192.168.2.102";

    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly MonitorBindings _bindings = new();

    /// <summary>The monitor's patient-info message: carries the id, no values.</summary>
    private RouteResult Info(string monitor, string patientId, int bed = 0) =>
        _bindings.Report(monitor, patientId, bed, hasReadings: false, Now);

    /// <summary>An ordinary vitals message: values, no patient identification.</summary>
    private RouteResult Vitals(string monitor) =>
        _bindings.Report(monitor, null, 0, hasReadings: true, Now);

    [Fact]
    public void ReportedId_BindsPatient_AndLaterVitalsWithoutPidGoToThem()
    {
        var first = Info(M1, "P1", bed: 5);

        Assert.Equal("P1", first.PatientId);
        Assert.Equal(new BindingChange(M1, "P1", false, null, null, null, 5), first.Change);

        var vitals = Vitals(M1);
        Assert.Equal("P1", vitals.PatientId);
        Assert.Null(vitals.Change);
    }

    [Fact]
    public void TwoMonitors_EachRoutesToItsOwnPatient()
    {
        Info(M1, "P1");
        Info(M2, "P2");

        Assert.Equal("P1", Vitals(M1).PatientId);
        Assert.Equal("P2", Vitals(M2).PatientId);
    }

    [Fact]
    public void ValuesWithoutPatientId_CreateAnonymousPatient_Once()
    {
        Info(M1, "");

        var first = Vitals(M1);
        Assert.True(AnonymousPatient.IsAnonymous(first.PatientId));
        Assert.True(first.Change!.CreatedAnonymous);

        var second = Vitals(M1);
        Assert.Equal(first.PatientId, second.PatientId);
        Assert.Null(second.Change);
    }

    [Fact]
    public void ValuesBeforeAnyPatientInfo_CreateAnonymousPatient()
    {
        Assert.True(AnonymousPatient.IsAnonymous(Vitals(M1).PatientId));
    }

    [Fact]
    public void NoValuesAndNoId_BindsNobody()
    {
        var result = Info(M1, "");

        Assert.Null(result.PatientId);
        Assert.Null(result.Change);
        Assert.NotNull(result.DropReason);
    }

    [Fact]
    public void IdEnteredOnMonitor_SupersedesAnonymousPatient()
    {
        var anonymous = Vitals(M1).PatientId;

        var result = Info(M1, "P1");

        Assert.Equal("P1", result.PatientId);
        Assert.Equal(anonymous, result.Change!.SupersededPatientId);
        Assert.Null(result.Change.UnboundPatientId);
    }

    [Fact]
    public void SameIdOnAnotherMonitor_MovesPatient_AndOldMonitorIsDropped()
    {
        Info(M1, "P1");

        var moved = Info(M2, "P1", bed: 7);
        Assert.Equal("P1", moved.PatientId);
        Assert.Equal(M1, moved.Change!.PreviousMonitorIp);
        Assert.Equal(7, moved.Change.BedNumber);

        // M1 keeps repeating the old id; without a change it must not take the patient back.
        var stale = Info(M1, "P1");
        Assert.Null(stale.PatientId);
        Assert.Contains(M2, stale.DropReason);
        Assert.Null(Vitals(M1).PatientId);
        Assert.Equal("P1", Vitals(M2).PatientId);
    }

    [Fact]
    public void NewIdOnMonitor_UnbindsPreviousPatient()
    {
        Info(M1, "P1");

        var result = Info(M1, "P2");

        Assert.Equal("P2", result.PatientId);
        Assert.Equal("P1", result.Change!.UnboundPatientId);
        Assert.Null(_bindings.FindMonitorOf("P1"));
    }

    [Fact]
    public void IdClearedOnMonitor_ReleasesPatient_ThenValuesStartAnonymous()
    {
        Info(M1, "P1");

        var cleared = Info(M1, "");
        Assert.Null(cleared.PatientId);
        Assert.Equal("P1", cleared.Change!.UnboundPatientId);

        Assert.True(AnonymousPatient.IsAnonymous(Vitals(M1).PatientId));
    }

    [Fact]
    public void Transfer_MovesAnonymousPatient_AndReplacesTargetPlaceholder()
    {
        var patient = Vitals(M1).PatientId!;
        var placeholder = Vitals(M2).PatientId!;
        Info(M2, "", bed: 9);

        var result = _bindings.Transfer(patient, M2);

        Assert.Equal(TransferStatus.Moved, result.Status);
        Assert.Equal(new BindingChange(M2, patient, false, placeholder, null, M1, 9), result.Change);
        Assert.Equal(patient, Vitals(M2).PatientId);

        // Whoever is still attached to M1 is somebody else.
        var left = Vitals(M1).PatientId;
        Assert.True(AnonymousPatient.IsAnonymous(left));
        Assert.NotEqual(patient, left);
    }

    [Fact]
    public void Transfer_HoldsWhileTargetKeepsReportingItsOwnId()
    {
        Info(M1, "P1");
        Info(M2, "GUID-2");

        var result = _bindings.Transfer("P1", M2);
        Assert.Equal("GUID-2", result.Change!.UnboundPatientId);

        Assert.Equal("P1", Info(M2, "GUID-2").PatientId);
        Assert.Equal("P1", Vitals(M2).PatientId);
        Assert.Null(Vitals(M1).PatientId);
    }

    [Fact]
    public void Transfer_IsOverriddenWhenTargetReportsNewPatient()
    {
        Info(M1, "P1");
        Info(M2, "");
        _bindings.Transfer("P1", M2);

        var result = Info(M2, "P3");

        Assert.Equal("P3", result.PatientId);
        Assert.Equal("P1", result.Change!.UnboundPatientId);
    }

    [Fact]
    public void Transfer_ToUnknownMonitor_IsRejected()
    {
        Info(M1, "P1");

        Assert.Equal(TransferStatus.MonitorUnknown, _bindings.Transfer("P1", M2).Status);
        Assert.Equal(M1, _bindings.FindMonitorOf("P1"));
    }

    [Fact]
    public void Transfer_ToCurrentMonitor_ChangesNothing()
    {
        Info(M1, "P1");

        var result = _bindings.Transfer("P1", M1);

        Assert.Equal(TransferStatus.AlreadyBound, result.Status);
        Assert.Null(result.Change);
    }

    [Fact]
    public void Forget_ReleasesMonitor_AndSameIdBindsAgain()
    {
        Info(M1, "P1");

        _bindings.Forget("P1");
        Assert.Null(_bindings.Get(M1)!.PatientId);

        Assert.Equal("P1", Info(M1, "P1").PatientId);
    }

    [Fact]
    public void GetAll_ListsMonitorsWithBedAndReportedId()
    {
        Info(M2, "P2", bed: 4);
        Info(M1, "");

        var all = _bindings.GetAll();

        Assert.Equal(new[] { M1, M2 }, all.Select(m => m.MonitorIp));
        Assert.Equal(4, all[1].BedNumber);
        Assert.Equal("P2", all[1].PatientId);
        Assert.Equal("", all[0].ReportedPatientId);
        Assert.Null(all[0].PatientId);
    }

    // ------------------------------------------------------------ a patient a station admitted is held

    private const string Admitted = "0b9c5e2a-6d0f-4c56-9f0e-3a2f8f1d7c11";

    [Fact]
    public void Hold_ReplacesWhoeverStoodInOnTheMonitor()
    {
        var anonymous = Vitals(M1).PatientId!;

        var change = _bindings.Hold(Admitted, M1, 0, Now);

        Assert.Equal(new BindingChange(M1, Admitted, false, anonymous, null, null, 0), change);
        Assert.Equal(Admitted, Vitals(M1).PatientId);
    }

    [Fact]
    public void Hold_KeepsPatient_WhenMonitorReportsAnotherId()
    {
        Info(M1, "P1");
        Assert.Equal("P1", _bindings.Hold(Admitted, M1, 0, Now)!.UnboundPatientId);

        var result = Info(M1, "P2");

        Assert.Equal(Admitted, result.PatientId);
        Assert.Null(result.Change);
        Assert.Equal(new ReportedIdChange("P1", "P2"), result.Renamed);
        Assert.Equal(Admitted, Vitals(M1).PatientId);
        Assert.Null(Vitals(M1).Renamed);   // said once, when it changed
    }

    [Fact]
    public void Hold_KeepsPatient_WhenMonitorClearsItsId()
    {
        Info(M1, "P1");
        _bindings.Hold(Admitted, M1, 0, Now);

        var result = Info(M1, "");

        Assert.Equal(Admitted, result.PatientId);
        Assert.Equal(new ReportedIdChange("P1", ""), result.Renamed);
        Assert.Equal(Admitted, Vitals(M1).PatientId);
    }

    [Fact]
    public void Hold_OnMonitorNotYetHeard_BindsIt()
    {
        // After a restart the patients are put back before any monitor has spoken.
        var change = _bindings.Hold(Admitted, M1, 4, Now);

        Assert.Equal(new BindingChange(M1, Admitted, false, null, null, null, 4), change);
        var monitor = _bindings.Get(M1)!;
        Assert.Equal(Admitted, monitor.PatientId);
        Assert.Equal(4, monitor.BedNumber);
        Assert.Equal(Now, monitor.LastSeen);   // not "silent since the year one"

        // What the monitor then says about its own patient moves nobody; what it said
        // before the restart is not known.
        var first = Info(M1, "P1");
        Assert.Equal(Admitted, first.PatientId);
        Assert.Equal(new ReportedIdChange(null, "P1"), first.Renamed);
    }

    [Fact]
    public void Hold_OfPatientAlreadyThere_ChangesNothing()
    {
        _bindings.Hold(Admitted, M1, 0, Now);

        Assert.Null(_bindings.Hold(Admitted, M1, 0, Now));
    }

    [Fact]
    public void Hold_WithoutMonitor_HoldsOnceTheyAreGivenOne()
    {
        Info(M1, "P1");
        Assert.Null(_bindings.Hold(Admitted, null, 0, Now));

        _bindings.Transfer(Admitted, M1);

        Assert.Equal(Admitted, Info(M1, "P2").PatientId);
    }

    [Fact]
    public void Transfer_OfHeldPatient_KeepsHold()
    {
        Info(M1, "P1");
        Info(M2, "P2");
        _bindings.Hold(Admitted, M1, 0, Now);

        _bindings.Transfer(Admitted, M2);

        Assert.Equal(Admitted, Info(M2, "P3").PatientId);
        Assert.Null(_bindings.Get(M1)!.PatientId);
    }

    [Fact]
    public void Forget_EndsHold_AndMonitorsIdBindsAgain()
    {
        Info(M1, "P1");
        _bindings.Hold(Admitted, M1, 0, Now);

        _bindings.Forget(Admitted);

        // The monitor forgot what it reported, so the patient it names is back at once ...
        Assert.Equal("P1", Info(M1, "P1").PatientId);
        // ... and nobody is held on it any more.
        Assert.Equal("P2", Info(M1, "P2").PatientId);
    }

    [Fact]
    public void Forget_EndsHold_OfPatientWithoutMonitor()
    {
        _bindings.Hold(Admitted, null, 0, Now);
        _bindings.Forget(Admitted);

        Info(M1, "P1");
        _bindings.Transfer(Admitted, M1);

        Assert.Equal("P2", Info(M1, "P2").PatientId);
    }
}
