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

            T? message;
            try
            {
                message = DeserializeMessage(ea.Body.ToArray());
            }
            catch (Exception ex)
            {
                message = null;
                _logger.LogError(ex, "Message from queue {QueueName} could not be deserialized", _queueName);
            }

            if (message == null)
            {
                // Retrying can't fix a malformed body.
                MessagesFailedCounter.Add(1, new KeyValuePair<string, object?>("queue", _queueName));
                await DeadLetterAsync(ea, "Message could not be deserialized", 1, stoppingToken);
                await _channel.BasicAckAsync(ea.DeliveryTag, false);
                return;
            }

            int maxAttempts = Math.Max(1, _settings.MaxRetryAttempts);
            for (int attempt = 1; ; attempt++)
            {
                // A new scope per attempt: a failed attempt may leave the DbContext in a dirty state.
                using var scope = _provider.CreateScope();
                var handler = scope.ServiceProvider.GetRequiredService<IIntegrationMessageConsumer<T>>();

                try
                {
                    await handler.HandleAsync(message, stoppingToken);

                    MessagesReceivedCounter.Add(1, new KeyValuePair<string, object?>("queue", _queueName));

                    await _channel.BasicAckAsync(ea.DeliveryTag, false);
                    return;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Shutting down: leave the message unacknowledged so the broker redelivers it.
                    return;
                }
                catch (Exception ex)
                {
                    MessagesFailedCounter.Add(1, new KeyValuePair<string, object?>("queue", _queueName));
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

                    if (attempt >= maxAttempts)
                    {
                        _logger.LogError(ex, "Message {MessageId} from queue {QueueName} failed {Attempts} times; moving it to {ErrorQueue}",
                            message.Id, _queueName, attempt, ErrorQueueName);

                        await DeadLetterAsync(ea, ex.ToString(), attempt, stoppingToken);
                        await _channel.BasicAckAsync(ea.DeliveryTag, false);
                        return;
                    }

                    TimeSpan delay = TimeSpan.FromMilliseconds(_settings.RetryBaseDelayMilliseconds * Math.Pow(2, attempt - 1));
                    _logger.LogWarning(ex, "Message {MessageId} from queue {QueueName} failed (attempt {Attempt}/{MaxAttempts}); retrying in {Delay}",
                        message.Id, _queueName, attempt, maxAttempts, delay);

                    try
                    {
                        await Task.Delay(delay, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        };

        try
        {
            await _channel.BasicConsumeAsync(queue: _queueName, autoAck: false, consumer: consumer, cancellationToken: stoppingToken);
        }
        catch (global::RabbitMQ.Client.Exceptions.OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode == 404)
        {
            _logger.LogCritical(ex,
                "Queue {QueueName} does not exist in the broker. Consumers do not declare queues: add its exchange, queue and binding " +
                "to the RabbitMQ definitions (Store/docker-infrastructure/rabbitmq/definitions.json) or declare it with declareQueue.",
                _queueName);
            throw;
        }

    }

    public async override Task StopAsync(CancellationToken cancellationToken)
    {
        await _channel?.CloseAsync();
        await _connection?.CloseAsync();
        base.StopAsync(cancellationToken);
    }

    private string ErrorQueueName => _queueName + ".error";

    /// <summary>Copies a message that could not be processed to <c>{queue}.error</c>, keeping the failure reason in the headers.</summary>
    private async Task DeadLetterAsync(BasicDeliverEventArgs ea, string reason, int attempts, CancellationToken cancellationToken)
    {
        await _channel.QueueDeclareAsync(queue: ErrorQueueName, durable: true, exclusive: false, autoDelete: false,
            arguments: null, cancellationToken: cancellationToken);

        var properties = new BasicProperties
        {
            Persistent = true,
            ContentType = ea.BasicProperties.ContentType,
            Headers = new Dictionary<string, object?>
            {
                ["x-original-queue"] = _queueName,
                ["x-original-exchange"] = ea.Exchange,
                ["x-failure-reason"] = reason.Length > 4000 ? reason[..4000] : reason,
                ["x-attempts"] = attempts,
                ["x-failed-at"] = DateTimeOffset.UtcNow.ToString("O")
            }
        };

        await _channel.BasicPublishAsync(exchange: string.Empty, routingKey: ErrorQueueName, mandatory: true,
            basicProperties: properties, body: ea.Body, cancellationToken: cancellationToken);
    }

    private static T? DeserializeMessage(byte[] body)
    {
        var json = Encoding.UTF8.GetString(body);
        return JsonConvert.DeserializeObject<T>(json!);
    }
}