using LanCacheRouter.Health;

namespace LanCacheRouter.Tests.Health;

public class HealthTrackerTests
{
    private static HealthTransition? Feed(HealthTracker tracker, params bool[] results)
    {
        HealthTransition? last = null;
        foreach (bool result in results)
            last = tracker.RecordResult(result) ?? last;

        return last;
    }

    [Fact]
    public void StartsUnknown()
    {
        Assert.Equal(HealthState.Unknown, new HealthTracker(2, 3).State);
    }

    [Fact]
    public void Unknown_BecomesHealthy_AfterSuccessThreshold()
    {
        HealthTracker tracker = new(failThreshold: 2, successThreshold: 3);

        Assert.Null(tracker.RecordResult(true));
        Assert.Null(tracker.RecordResult(true));
        Assert.Equal(HealthState.Unknown, tracker.State);

        HealthTransition? transition = tracker.RecordResult(true);

        Assert.Equal(new HealthTransition(HealthState.Unknown, HealthState.Healthy), transition);
        Assert.Equal(HealthState.Healthy, tracker.State);
    }

    [Fact]
    public void Unknown_BecomesUnhealthy_AfterFailThreshold()
    {
        HealthTracker tracker = new(2, 3);

        Assert.Null(tracker.RecordResult(false));
        Assert.Equal(new HealthTransition(HealthState.Unknown, HealthState.Unhealthy), tracker.RecordResult(false));
    }

    [Fact]
    public void Healthy_SingleFailureDoesNotFlip()
    {
        HealthTracker tracker = new(2, 1);
        Feed(tracker, true);

        Assert.Null(tracker.RecordResult(false));
        Assert.Equal(HealthState.Healthy, tracker.State);
    }

    [Fact]
    public void Healthy_BecomesUnhealthy_AfterConsecutiveFailures()
    {
        HealthTracker tracker = new(2, 1);
        Feed(tracker, true);

        tracker.RecordResult(false);

        Assert.Equal(new HealthTransition(HealthState.Healthy, HealthState.Unhealthy), tracker.RecordResult(false));
    }

    [Fact]
    public void Success_BetweenFailures_ResetsFailureCount()
    {
        HealthTracker tracker = new(2, 1);
        Feed(tracker, true);

        Feed(tracker, false, true, false);

        Assert.Equal(HealthState.Healthy, tracker.State);
    }

    [Fact]
    public void Unhealthy_RecoversOnlyAfterConsecutiveSuccesses()
    {
        HealthTracker tracker = new(1, 3);
        Feed(tracker, false);
        Assert.Equal(HealthState.Unhealthy, tracker.State);

        Feed(tracker, true, true, false, true, true);
        Assert.Equal(HealthState.Unhealthy, tracker.State);

        Assert.Equal(new HealthTransition(HealthState.Unhealthy, HealthState.Healthy), tracker.RecordResult(true));
    }

    [Fact]
    public void Thresholds_AreSymmetricForThresholdOne()
    {
        HealthTracker tracker = new(1, 1);

        Assert.Equal(HealthState.Healthy, tracker.RecordResult(true)!.Value.To);
        Assert.Equal(HealthState.Unhealthy, tracker.RecordResult(false)!.Value.To);
        Assert.Equal(HealthState.Healthy, tracker.RecordResult(true)!.Value.To);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, -1)]
    public void Constructor_RejectsThresholdsBelowOne(int fail, int success)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HealthTracker(fail, success));
    }

    [Fact]
    public void Transition_FormatsForLogs()
    {
        Assert.Equal("Unknown -> Healthy", new HealthTransition(HealthState.Unknown, HealthState.Healthy).ToString());
    }

    [Fact]
    public void AnyRandomSequence_FollowsTheSimpleRuleBookExactly()
    {
        // Reference model: only consecutive results count. Enough successes in a row mean Healthy, enough failures in a row Unhealthy.
        Random random = new(1019);

        for (int round = 0; round < 3000; round++)
        {
            int fail = random.Next(1, 6), success = random.Next(1, 6);
            HealthTracker tracker = new(fail, success);
            HealthState expected = HealthState.Unknown;
            int failures = 0, successes = 0;

            for (int step = 0; step < 40; step++)
            {
                bool ok = random.Next(100) < 60;
                (failures, successes) = ok ? (0, successes + 1) : (failures + 1, 0);
                HealthState before = expected;
                if (successes >= success)
                    expected = HealthState.Healthy;
                else if (failures >= fail)
                    expected = HealthState.Unhealthy;

                HealthTransition? transition = tracker.RecordResult(ok);
                HealthTransition? expectedTransition = before == expected ? null : new HealthTransition(before, expected);

                Assert.Equal(expected, tracker.State);
                Assert.Equal(expectedTransition, transition);
            }
        }
    }

    [Fact]
    public void Success_OnAnUnknownTracker_NeverGoesStraightToUnhealthy_AndViceVersa()
    {
        HealthTracker tracker = new(1, 1);

        Assert.Equal(HealthState.Healthy, tracker.RecordResult(true)!.Value.To);
        Assert.Null(tracker.RecordResult(true)); // staying Healthy is not a transition
        Assert.Equal(HealthState.Unhealthy, tracker.RecordResult(false)!.Value.To);
        Assert.Null(tracker.RecordResult(false));
    }

    [Fact]
    public void ATrackerCanStartHealthy_AndFollowsTheSameRulesFromThere()
    {
        HealthTracker tracker = new(2, 3, HealthState.Healthy);

        Assert.Equal(HealthState.Healthy, tracker.State);
        Assert.Null(tracker.RecordResult(false)); // one failure does not flip it
        Assert.Null(tracker.RecordResult(true)); // and the count starts over
        Assert.Null(tracker.RecordResult(false));
        Assert.Equal(new HealthTransition(HealthState.Healthy, HealthState.Unhealthy), tracker.RecordResult(false));
    }

    [Theory]
    [InlineData(HealthState.Unknown)]
    [InlineData(HealthState.Healthy)]
    [InlineData(HealthState.Unhealthy)]
    public void InitialState_IsReported(HealthState initial)
    {
        Assert.Equal(initial, new HealthTracker(1, 1, initial).State);
    }

    [Fact]
    public void ATrackerIsNeverDisabled()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HealthTracker(1, 1, HealthState.Disabled));
    }

    [Fact]
    public async Task StateCanBeReadFromAnyThreadWhileTheProbeLoopWritesIt()
    {
        HealthTracker tracker = new(1, 1);
        using CancellationTokenSource stop = new();
        Task reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
                Assert.True(Enum.IsDefined(tracker.State));
        });

        for (int i = 0; i < 200000; i++)
            tracker.RecordResult(i % 2 == 0);

        await stop.CancelAsync();
        await reader;
    }
}
