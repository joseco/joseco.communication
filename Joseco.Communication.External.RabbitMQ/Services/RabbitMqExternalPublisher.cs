using Joseco.Communication.External.Contracts.Message;
using Joseco.Communication.External.Contracts.Services;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using RabbitMQ.Client;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.RegularExpressions;

namespace Joseco.Communication.External.RabbitMQ.Services;

internal class RabbitMqExternalPublisher : IExternalPublisher
{
    private readonly ILogger<RabbitMqExternalPublisher> _logger;
    private readonly ConnectionFactory _connectionFactory;
    private readonly JsonSerializer _jsonSerializer;

    private const int DefaultBufferSize = 1024;
    private static readonly Encoding Encoding = new UTF8Encoding(false);

    // Instrumentación OpenTelemetry
    private static readonly ActivitySource ActivitySource = new("Joseco.Communication.RabbitMQ");
    private static readonly TextMapPropagator Propagator = Propagators.DefaultTextMapPropagator;

    private static readonly Meter Meter = new("Joseco.Communication.RabbitMQ");

    private static readonly Counter<long> MessagesPublishedCounter = Meter.CreateCounter<long>("rabbitmq.messages.published");
    private static readonly Counter<long> MessagesFailedCounter = Meter.CreateCounter<long>("rabbitmq.messages.failed");
    private static readonly Counter<long> MessageSizeCounter = Meter.CreateCounter<long>("rabbitmq.messages.bytes");


    public RabbitMqExternalPublisher(ILogger<RabbitMqExternalPublisher> logger, RabbitMqSettings settings)
    {
        _logger = logger;
        _connectionFactory = new ConnectionFactory()
        {
            HostName = settings.Host,
            Password = settings.Password,
            UserName = settings.UserName
        };

        var defaultSerializerSettings = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.Auto
        };
        _jsonSerializer = JsonSerializer.Create(defaultSerializerSettings);
    }

    public async Task PublishAsync<T>(T message, string? destination = null, bool declareDestination = false) where T : IntegrationMessage
    {

        using var connection = await _connectionFactory.CreateConnectionAsync();
        using var channel = await connection.CreateChannelAsync();

        string typeName = typeof(T).Name;
        string exchangeName = destination ?? PascalToKebabCase(typeName);

        if (declareDestination)
        {
            _logger.LogInformation("Declaring destination {Destination}",  exchangeName);

            // Declare the exchange if it doesn't exist
            await channel.ExchangeDeclareAsync(exchange: exchangeName, type: ExchangeType.Fanout);
        }

        var properties = new BasicProperties();
        properties.Headers ??= new Dictionary<string, object>();
        properties.ContentType = "application/json";

        // Start a new Activity, fallback to root if none exists
        using var activity = ActivitySource.HasListeners()
            ? ActivitySource.StartActivity("RabbitMQ Publish", ActivityKind.Producer, Activity.Current?.Context ?? default)
            : null;

        if (activity != null)
        {
            activity.SetTag("messaging.system", "rabbitmq");
            activity.SetTag("messaging.destination", exchangeName);
            activity.SetTag("messaging.destination_kind", "exchange");
            activity.SetTag("messaging.message_payload_type", typeof(T).FullName);
        }

        // Inject tracing context into RabbitMQ message headers
        Propagator.Inject(
            new PropagationContext(activity?.Context ?? default, Baggage.Current),
            properties,
            static (props, key, value) => props.Headers[key] = Encoding.UTF8.GetBytes(value));

        _logger.LogInformation("Publishing message of type {TypeName} to destination {Destination}", typeName, exchangeName);

        try
        {
            var body = SerializeObjectToByteArray(message);

            await channel.BasicPublishAsync(exchange: exchangeName,
                routingKey: string.Empty,
                mandatory: true,
                basicProperties: properties,
                body: body);

            MessagesPublishedCounter.Add(1, new KeyValuePair<string, object?>("exchange", exchangeName));
            MessageSizeCounter.Add(body.Length, new KeyValuePair<string, object?>("exchange", exchangeName));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error publishing message to exchange {ExchangeName}", exchangeName);

            MessagesFailedCounter.Add(1, new KeyValuePair<string, object?>("exchange", exchangeName));
            throw;
        }
        

        
    }

    private static string PascalToKebabCase(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        return Regex.Replace(
            value,
            "(?<!^)([A-Z][a-z]|(?<=[a-z])[A-Z0-9])",
            "-$1",
            RegexOptions.Compiled)
            .Trim()
            .ToLower();
    }

    private byte[] SerializeObjectToByteArray<T>(T obj)
    {
        using var memoryStream = new MemoryStream(DefaultBufferSize);
        using (var streamWriter = new StreamWriter(memoryStream, Encoding, DefaultBufferSize, true))
        using (var jsonWriter = new JsonTextWriter(streamWriter))
        {
            jsonWriter.Formatting = _jsonSerializer.Formatting;
            _jsonSerializer.Serialize(jsonWriter, obj, obj!.GetType());
        }

        return memoryStream.ToArray();
    }
}
