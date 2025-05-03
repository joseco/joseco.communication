using Joseco.Communication.External.Contracts.Message;
using Joseco.Communication.External.Contracts.Services;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using RabbitMQ.Client;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Joseco.Communication.External.RabbitMQ.Services;

internal class RabbitMqExternalPublisher : IExternalPublisher
{
    private readonly ILogger<RabbitMqExternalPublisher> _logger;
    private readonly ConnectionFactory _connectionFactory;
    private readonly JsonSerializer _jsonSerializer;

    private const int DefaultBufferSize = 1024;
    private static readonly Encoding Encoding = new UTF8Encoding(false);

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

        _logger.LogInformation("Publishing message of type {TypeName} to destination {Destination}", typeName, exchangeName);


        var body = SerializeObjectToByteArray(message);

        await channel.BasicPublishAsync(exchange: exchangeName, routingKey: string.Empty, body: body);
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
