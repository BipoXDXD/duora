using System.Diagnostics.Metrics;
using Duora.Infrastructure.Operations;

namespace Duora.IntegrationTests.Outbox;

public sealed partial class RetentionTests
{
    [Fact]
    public async Task ProlongedWaitIsInventoryAndNeverRetentionOrProcessingLag()
    {
        await using var h = await Harness.CreateAsync(postgres);
        var delivery = await h.Delivery("Pending", null);
        await h.Sql("""
            update ops.outbox_deliveries set consumer='selfie-attempts-suspension',last_error='AwaitingOtherMeasure',
              available_at=$2+interval '1 hour' where id=$1
            """, delivery, h.Time.GetUtcNow());
        using var metrics = new ObservationMetrics();
        var health = await metrics.Observe(h.Service);
        Assert.Equal(new RetentionTableHealth("outbox_events", 1, 0), Table(health, "outbox_events"));
        Assert.Equal(new RetentionTableHealth("outbox_deliveries", 1, 0), Table(health, "outbox_deliveries"));
        metrics.Equal("rows", "selfie-attempts-suspension", "awaiting_other_measure", 1);
        metrics.Equal("oldest_seconds", "selfie-attempts-suspension", "awaiting_other_measure", 40 * 86400);
        metrics.Equal("processing_lag_seconds", "selfie-attempts-suspension", "awaiting_other_measure", 0);
        h.Time.Advance(TimeSpan.FromMinutes(30));
        await metrics.Observe(h.Service);
        metrics.Equal("oldest_seconds", "selfie-attempts-suspension", "awaiting_other_measure", 40 * 86400 + 1800);
        metrics.Equal("processing_lag_seconds", "selfie-attempts-suspension", "awaiting_other_measure", 0);
        await h.Run();
        Assert.Equal(1, await h.Exists("outbox_deliveries", delivery));
    }

    [Theory]
    [InlineData("Pending", null, -120, 0, "ready", 120)]
    [InlineData("Pending", "IOException", -120, 0, "ready", 120)]
    [InlineData("Pending", "IOException", 120, 0, "scheduled", 0)]
    [InlineData("Pending", "AwaitingOtherMeasure", 120, 0, "awaiting_other_measure", 0)]
    [InlineData("Pending", "AwaitingOtherMeasure", 0, 0, "ready", 0)]
    [InlineData("Pending", "AwaitingOtherMeasure", -120, 0, "ready", 120)]
    [InlineData("Pending", "Busy", -120, 0, "ready", 120)]
    [InlineData("InFlight", "AwaitingOtherMeasure", -120, 30, "in_flight", 0)]
    [InlineData("InFlight", "AwaitingOtherMeasure", -120, 0, "lease_expired", 0)]
    [InlineData("InFlight", "AwaitingOtherMeasure", -120, -60, "lease_expired", 60)]
    [InlineData("Failed", "AwaitingOtherMeasure", -120, 0, "failed", 0)]
    public async Task ConsumerLagFollowsClaimEligibilityInsteadOfEventAge(
        string status, string? error, int availableSeconds, int leaseSeconds, string state, double lag)
    {
        await using var h = await Harness.CreateAsync(postgres);
        var delivery = await h.Delivery(status, status == "Failed" ? h.Time.GetUtcNow().AddDays(-31) : null);
        await h.Sql("""
            update ops.outbox_deliveries set consumer='selfie-attempts-suspension',last_error=$2,
              available_at=$3,lease_expires_at=case when status='InFlight' then $4 else null end
            where id=$1
            """, delivery, error, h.Time.GetUtcNow().AddSeconds(availableSeconds), h.Time.GetUtcNow().AddSeconds(leaseSeconds));
        using var metrics = new ObservationMetrics();
        var health = await metrics.Observe(h.Service);
        metrics.Equal("rows", "selfie-attempts-suspension", state, 1);
        metrics.Equal("oldest_seconds", "selfie-attempts-suspension", state, 40 * 86400);
        metrics.Equal("processing_lag_seconds", "selfie-attempts-suspension", state, lag);
        Assert.Equal(0, Table(health, "outbox_events").OldestSeconds);
        Assert.Equal(0, Table(health, "outbox_deliveries").OldestSeconds);
        Assert.Equal(status == "Failed" ? 1 : 0, health.UnreviewedFailures);
    }

    [Fact]
    public async Task WaitingConsumerCannotHideOtherConsumersAndUnknownLabelsAreBounded()
    {
        await using var h = await Harness.CreateAsync(postgres);
        var waiting = await h.Delivery("Pending", null);
        await h.Sql("""
            update ops.outbox_deliveries set consumer='selfie-attempts-suspension',last_error='AwaitingOtherMeasure',
              available_at=$2+interval '1 hour' where id=$1
            """, waiting, h.Time.GetUtcNow());
        await h.Sql("""
            insert into ops.outbox_deliveries(id,outbox_event_id,consumer,recipient_key,status,available_at,deadline_at,created_at,last_error,completed_at,attempts,fencing_token)
            select gen_random_uuid(),outbox_event_id,'transactional-email','test','Pending',$2-interval '2 minutes',$2,$2-interval '40 days',null,null,0,0
              from ops.outbox_deliveries where id=$1
            """, waiting, h.Time.GetUtcNow());
        await h.Sql("""
            insert into ops.outbox_deliveries(id,outbox_event_id,consumer,recipient_key,status,available_at,deadline_at,created_at,last_error,completed_at,attempts,fencing_token)
            select gen_random_uuid(),outbox_event_id,'unexpected-private-label','test','Failed',$2,$2,$2-interval '40 days','AwaitingOtherMeasure',$2-interval '31 days',0,0
              from ops.outbox_deliveries where id=$1
            """, waiting, h.Time.GetUtcNow());
        using var metrics = new ObservationMetrics();
        var health = await metrics.Observe(h.Service);
        metrics.Equal("rows", "selfie-attempts-suspension", "awaiting_other_measure", 1);
        metrics.Equal("processing_lag_seconds", "transactional-email", "ready", 120);
        metrics.Equal("rows", "other", "failed", 1);
        Assert.DoesNotContain(metrics.Values.Keys, key => key.Consumer == "unexpected-private-label");
        Assert.Equal(1, health.UnreviewedFailures);
        Assert.Equal(1, Table(health, "outbox_events").Rows);
        Assert.Equal(3, Table(health, "outbox_deliveries").Rows);
    }

    [Fact]
    public async Task WaitMarkerOnAnotherConsumerDoesNotSuppressItsQueue()
    {
        await using var h = await Harness.CreateAsync(postgres);
        var id = await h.Delivery("Pending", null);
        await h.Sql("""
            update ops.outbox_deliveries set consumer='transactional-email',last_error='AwaitingOtherMeasure',
              available_at=$2+interval '1 minute' where id=$1
            """, id, h.Time.GetUtcNow());
        using var metrics = new ObservationMetrics();
        await metrics.Observe(h.Service);
        metrics.Equal("rows", "transactional-email", "scheduled", 1);
        metrics.Equal("rows", "transactional-email", "awaiting_other_measure", 0);
    }

    [Theory]
    [InlineData("Delivered", -8, false, 86400)]
    [InlineData("Expired", -8, false, 86400)]
    [InlineData("Delivered", -7, false, 0)]
    [InlineData("Delivered", -6, false, 0)]
    [InlineData("Delivered", null, false, 0)]
    [InlineData("Failed", -31, false, 0)]
    [InlineData("Failed", -31, true, 86400)]
    [InlineData("Failed", -30, true, 0)]
    [InlineData("Failed", null, false, 0)]
    [InlineData("Pending", -80, false, 0)]
    [InlineData("InFlight", -80, false, 0)]
    public async Task RetentionLagUsesTerminalDeadlineAndExplicitFailureReview(
        string status, int? completedDays, bool reviewed, double lag)
    {
        await using var h = await Harness.CreateAsync(postgres);
        var id = await h.Delivery(status, completedDays is { } days ? h.Time.GetUtcNow().AddDays(days) : null);
        if (reviewed)
        {
            await h.Sql("update ops.outbox_deliveries set reviewed_at=$2 where id=$1", id, h.Time.GetUtcNow());
        }
        var health = await h.Service.ObserveAsync(Ct);
        Assert.Equal(new RetentionTableHealth("outbox_deliveries", 1, lag), Table(health, "outbox_deliveries"));
        // Parent is not eligible until the actual delivery has been removed, even if terminal/reviewed.
        Assert.Equal(new RetentionTableHealth("outbox_events", 1, 0), Table(health, "outbox_events"));
        await h.Run();
        Assert.Equal(lag > 0 ? 0 : 1, await h.Exists("outbox_deliveries", id));
    }

    [Theory]
    [InlineData("Processed", -31, 86400)]
    [InlineData("Rejected", -31, 86400)]
    [InlineData("Processed", -30, 0)]
    [InlineData("Rejected", null, 0)]
    [InlineData("Received", -80, 0)]
    public async Task ReceiptLagStartsAfterTheProcessedRetentionWindow(string state, int? processedDays, double lag)
    {
        await using var h = await Harness.CreateAsync(postgres);
        var id = await h.Receipt(state, processedDays is { } days ? h.Time.GetUtcNow().AddDays(days) : null);
        Assert.Equal(new RetentionTableHealth("webhook_receipts", 1, lag), Table(await h.Service.ObserveAsync(Ct), "webhook_receipts"));
        await h.Run();
        Assert.Equal(lag > 0 ? 0 : 1, await h.Exists("webhook_receipts", id));
    }

    [Fact]
    public async Task OrphanAndJobLagFollowCleanupPolicyAndResetToZeroAfterCleanup()
    {
        await using var h = await Harness.CreateAsync(postgres);
        await h.Sql("""
            insert into ops.outbox_events(id,type,schema_version,resource_type,resource_id,payload,occurred_at)
            values(gen_random_uuid(),'test',1,'test',gen_random_uuid(),'{}',$1-interval '8 days'),
              (gen_random_uuid(),'test',1,'test',gen_random_uuid(),'{}',$1-interval '7 days')
            """, h.Time.GetUtcNow());
        await h.Sql("""
            insert into ops.jobs(id,kind,aggregate_id,due_at,available_at,status,completed_at)
            values(gen_random_uuid(),'close-round',gen_random_uuid(),$1,$1,'Done',$1-interval '8 days'),
              (gen_random_uuid(),'close-round',gen_random_uuid(),$1,$1,'Done',$1-interval '7 days'),
              (gen_random_uuid(),'close-round',gen_random_uuid(),$1,$1,'Failed',$1-interval '80 days'),
              (gen_random_uuid(),'close-round',gen_random_uuid(),$1,$1,'Pending',null)
            """, h.Time.GetUtcNow());
        var health = await h.Service.ObserveAsync(Ct);
        Assert.Equal(new RetentionTableHealth("outbox_events", 2, 86400), Table(health, "outbox_events"));
        Assert.Equal(new RetentionTableHealth("jobs", 4, 86400), Table(health, "jobs"));
        await h.Run();
        health = await h.Service.ObserveAsync(Ct);
        Assert.Equal(new RetentionTableHealth("outbox_events", 1, 0), Table(health, "outbox_events"));
        Assert.Equal(new RetentionTableHealth("jobs", 4, 0), Table(health, "jobs")); // includes recurring job
    }

    [Fact]
    public async Task ConsumerSeriesEmitZerosAfterTheLastDeliveryDisappears()
    {
        await using var h = await Harness.CreateAsync(postgres);
        var id = await h.Delivery("Pending", null);
        await h.Sql("update ops.outbox_deliveries set consumer='selfie-attempts-suspension',available_at=$2-interval '2 minutes' where id=$1", id, h.Time.GetUtcNow());
        using var metrics = new ObservationMetrics();
        await metrics.Observe(h.Service);
        metrics.Equal("processing_lag_seconds", "selfie-attempts-suspension", "ready", 120);
        await h.Sql("delete from ops.outbox_deliveries where id=$1", id);
        await metrics.Observe(h.Service);
        Assert.Equal(108, metrics.Values.Count); // 6 bounded consumers x 6 states x 3 instruments
        Assert.All(metrics.Values.Values, value => Assert.Equal(0, value));
    }

    private static RetentionTableHealth Table(RetentionHealth health, string table) => Assert.Single(health.Tables, t => t.Table == table);

    private sealed class ObservationMetrics : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly AsyncLocal<bool> _observing = new();
        public Dictionary<(string Metric, string Consumer, string State), double> Values { get; } = [];

        public ObservationMetrics()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Duora.Retention" && instrument.Name.StartsWith("duora.outbox.", StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
            _listener.Start();
        }

        public async Task<RetentionHealth> Observe(RetentionService service)
        {
            Values.Clear();
            _observing.Value = true;
            try
            {
                return await service.ObserveAsync(Ct);
            }
            finally
            {
                _observing.Value = false;
            }
        }

        private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            if (!_observing.Value)
            {
                return; // Other parallel tests use the same static meter.
            }
            var consumer = "";
            var state = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "consumer")
                {
                    consumer = (string)tag.Value!;
                }
                if (tag.Key == "state")
                {
                    state = (string)tag.Value!;
                }
            }
            Values[(instrument.Name["duora.outbox.".Length..], consumer, state)] = value;
        }

        public void Equal(string metric, string consumer, string state, double expected)
        {
            Assert.True(Values.TryGetValue((metric, consumer, state), out var value), $"Missing {metric}/{consumer}/{state}");
            Assert.Equal(expected, value);
        }

        public void Dispose() => _listener.Dispose();
    }
}
