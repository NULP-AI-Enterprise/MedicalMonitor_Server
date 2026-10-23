using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using PatientMonitoring.Api.Contracts;

namespace PatientMonitoring.Api.Services.Relay;

public interface ISseStreamService
{
    IAsyncEnumerable<VitalSignsResponse> SubscribeAsync(Guid? patientId = null, CancellationToken cancellationToken = default);
    void Broadcast(VitalSignsResponse vitals);
}

public sealed class SseStreamService : ISseStreamService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ConcurrentDictionary<Guid, (Guid? PatientId, Channel<VitalSignsResponse> Channel)> _subscriptions = new();

    public async IAsyncEnumerable<VitalSignsResponse> SubscribeAsync(
        Guid? patientId = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var subId = Guid.NewGuid();
        var channel = Channel.CreateUnbounded<VitalSignsResponse>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = true
        });

        _subscriptions[subId] = (patientId, channel);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                VitalSignsResponse item;
                try
                {
                    item = await channel.Reader.ReadAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                yield return item;
            }
        }
        finally
        {
            _subscriptions.TryRemove(subId, out _);
        }
    }

    public void Broadcast(VitalSignsResponse vitals)
    {
        foreach (var (_, (patientId, channel)) in _subscriptions)
        {
            if (patientId is null || patientId == vitals.PatientId)
            {
                channel.Writer.TryWrite(vitals);
            }
        }
    }
}
