using System;
using System.Text.Json;
using System.Threading.Tasks;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RideSharing.Domain.Events;

namespace RideSharing.Infrastructure.Messaging;

public class RideEventPublisher : IDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly ILogger<RideEventPublisher> _logger;
    private const string Topic = "rides.events";

    public RideEventPublisher(IConfiguration configuration, ILogger<RideEventPublisher> logger)
    {
        _logger = logger;
        _producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers  = configuration.GetConnectionString("Kafka") ?? "localhost:9092",
            Acks              = Acks.Leader,
            EnableIdempotence = false
        }).Build();
    }

    public async Task PublishAsync(RideStateChangedEvent evt)
    {
        try
        {
            await _producer.ProduceAsync(Topic, new Message<string, string>
            {
                Key   = evt.RideId,
                Value = JsonSerializer.Serialize(evt)
            });
        }
        catch (ProduceException<string, string> ex)
        {
            _logger.LogError(ex, "Failed to publish ride event {EventType} for ride {RideId}",
                evt.EventType, evt.RideId);
        }
    }

    public void Dispose() => _producer?.Dispose();
}
