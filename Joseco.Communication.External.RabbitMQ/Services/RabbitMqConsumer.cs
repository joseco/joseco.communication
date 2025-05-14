using Joseco.Communication.External.Contracts.Message;
using Joseco.Communication.External.Contracts.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
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

    public RabbitMqConsumer(IServiceProvider provider, ILogger<RabbitMqConsumer<T>> logger, RabbitMqSettings settings, 
        string queueName, bool declareQueue = false, string? exchangeName = null)
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
            HostName = _settings.Host,
            Password = _settings.Password,
            UserName = _settings.UserName
        }; ;
        _connection = await factory.CreateConnectionAsync();
        _channel = await _connection.CreateChannelAsync();

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if(_exchangeName != null)
        {
            _logger.LogInformation("Declaring exchange {ExchangeName}", _exchangeName);
            await _channel.ExchangeDeclareAsync(exchange: _exchangeName, type: ExchangeType.Fanout);
        }

        if (_declareQueue)
        {
            _logger.LogInformation("Declaring queue {QueueName}", _queueName);

            await _channel.QueueDeclareAsync(queue: _queueName, durable: true, exclusive: false, autoDelete: false, arguments: null);
            QueueDeclareOk queueDeclareResult = await _channel.QueueDeclareAsync();
            string queueName = queueDeclareResult.QueueName;
            await _channel.QueueBindAsync(queue: queueName, exchange: _exchangeName, routingKey: string.Empty);
        }           

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += async (model, ea) =>
        {
            using var scope = _provider.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<IIntegrationMessageConsumer<T>>();

            var body = ea.Body.ToArray();
            var json = DeserializeMessage(body);

            if (json != null)
            {
                try
                {
                    await handler.HandleAsync(json, stoppingToken);
                    await _channel.BasicAckAsync(ea.DeliveryTag, false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing message for Consumer {consumerName}", GetType().Name);
                    // Puedes hacer retry, dead-letter, etc.


                }
            }
        };

        await _channel.BasicConsumeAsync(queue: _queueName, autoAck: false, consumer: consumer);

    }

    public async override Task StopAsync(CancellationToken cancellationToken)
    {
        await _channel?.CloseAsync();
        await _connection?.CloseAsync();
        base.StopAsync(cancellationToken);
    }

    private T DeserializeMessage(byte[] body)
    {
        var json = Encoding.UTF8.GetString(body);
        return JsonConvert.DeserializeObject<T>(json);
    }
}