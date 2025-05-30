using Joseco.Communication.External.Contracts.Message;
using Joseco.Communication.External.Contracts.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;

namespace Joseco.Communication.External.RabbitMQ.Services;

internal class RabbitMqConsumer<T> : BackgroundService
    where T : IntegrationMessage
{
    private readonly IServiceProvider _provider;
    private readonly string _queueName;
    private readonly ILogger<RabbitMqConsumer<T>> _logger;
    private readonly RabbitMqSettings _settings;

    private readonly string? _exchangeName;
    private readonly bool _declareQueue;

    private IConnection _connection;
    private IChannel _channel;

    private static readonly ActivitySource ActivitySource = new("Joseco.Communication.RabbitMQ");
    private static readonly TextMapPropagator Propagator = Propagators.DefaultTextMapPropagator;

    private static readonly Meter Meter = new("Joseco.Communication.RabbitMQ");
    private static readonly Counter<long> MessagesReceivedCounter = Meter.CreateCounter<long>("rabbitmq.messages.received");
    private static readonly Counter<long> MessagesFailedCounter = Meter.CreateCounter<long>("rabbitmq.messages.failed");

    public RabbitMqConsumer(IServiceProvider provider, 
        ILogger<RabbitMqConsumer<T>> logger, 
        RabbitMqSettings settings, 
        string queueName, 
        bool declareQueue = false, 
        string? exchangeName = null)
    {
        _provider = provider;
        _queueName = queueName;
        _logger = logger;
        _settings = settings;
        _declareQueue = declareQueue;
        _exchangeName = exchangeName;
    }

    public async override Task StartAsync(CancellationToken cancellationToken)
    {
        var factory = new ConnectionFactory
        {
            HostName = _settings.Host!,
            Password = _settings.Password!,
            UserName = _settings.UserName!
        }; ;
        _connection = await factory.CreateConnectionAsync();
        _channel = await _connection.CreateChannelAsync();

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if(_channel == null)
        {
            _logger.LogError("Channel is null.");
            return;
        }

        if(_exchangeName != null)
        {
            _logger.LogInformation("Declaring exchange {ExchangeName}", _exchangeName);
            await _channel.ExchangeDeclareAsync(exchange: _exchangeName, type: ExchangeType.Fanout);
        }

        if (_declareQueue && _exchangeName != null)
        {
            _logger.LogInformation("Declaring queue {QueueName}", _queueName);

            QueueDeclareOk queueDeclareResult = await _channel.QueueDeclareAsync(queue: _queueName, durable: true, exclusive: false, autoDelete: false, arguments: null);
            
            string queueName = queueDeclareResult.QueueName;
            await _channel.QueueBindAsync(queue: queueName, exchange: _exchangeName, routingKey: string.Empty);
        }           

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += async (model, ea) =>
        {
            //Adding instrumentation
            var parentContext = Propagator.Extract(
                default,
                ea.BasicProperties,
                static (props, key) =>
                {
                    if (props.Headers != null && props.Headers.TryGetValue(key, out var value) && value is byte[] bytes)
                    {
                        return new[] { Encoding.UTF8.GetString(bytes) }; 
                    }
                    return Enumerable.Empty<string>(); ;
                });

            Baggage.Current = parentContext.Baggage;

            using var activity = ActivitySource.StartActivity("RabbitMQ Consume", ActivityKind.Consumer, parentContext.ActivityContext);

            if (activity != null)
            {
                activity.SetTag("messaging.system", "rabbitmq");
                activity.SetTag("messaging.destination", _queueName);
                activity.SetTag("messaging.destination_kind", "queue");
                activity.SetTag("messaging.rabbitmq.exchange", ea.Exchange);
                activity.SetTag("messaging.rabbitmq.routing_key", ea.RoutingKey);
                activity.SetTag("messaging.operation", "process");
                activity.SetTag("message.type", typeof(T).Name);
            }

            using var scope = _provider.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<IIntegrationMessageConsumer<T>>();

            var body = ea.Body.ToArray();
            var json = DeserializeMessage(body);

            if (json != null)
            {
                try
                {
                    await handler.HandleAsync(json, stoppingToken);

                    MessagesReceivedCounter.Add(1, new KeyValuePair<string, object?>("queue", _queueName));

                    await _channel.BasicAckAsync(ea.DeliveryTag, false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing message for Consumer {consumerName}", GetType().Name);
                    // Puedes hacer retry, dead-letter, etc.

                    MessagesFailedCounter.Add(1, new KeyValuePair<string, object?>("queue", _queueName));

                    // Enviar el mensaje a una cola de error o hacer un retry
                    //await _channel.BasicNackAsync(ea.DeliveryTag, false, true);

                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                }
            }
        };

        await _channel.BasicConsumeAsync(queue: _queueName, autoAck: false, consumer: consumer, cancellationToken: stoppingToken);

    }

    public async override Task StopAsync(CancellationToken cancellationToken)
    {
        await _channel?.CloseAsync();
        await _connection?.CloseAsync();
        base.StopAsync(cancellationToken);
    }

    private static T? DeserializeMessage(byte[] body)
    {
        var json = Encoding.UTF8.GetString(body);
        return JsonConvert.DeserializeObject<T>(json!);
    }
}