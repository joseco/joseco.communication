using Joseco.Communication.External.Contracts.Message;
using Joseco.Communication.External.Contracts.Services;
using Joseco.Communication.External.RabbitMQ;
using Joseco.Communication.External.RabbitMQ.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Joseco.Communication.External.RabbitMQ;

public static class DependencyInjection
{
    public static IServiceCollection AddRabbitMQ(this IServiceCollection services, RabbitMqSettings rabbitMQSettings)
    {
        services.TryAddSingleton(rabbitMQSettings);
        services.AddSingleton(sp =>
        {
            var factory = new ConnectionFactory
            {
                HostName = rabbitMQSettings?.Host!,
                UserName = rabbitMQSettings?.UserName!,
                Password = rabbitMQSettings?.Password!,
                VirtualHost = rabbitMQSettings?.VirtualHost!
            };
            return factory.CreateConnectionAsync().GetAwaiter().GetResult();
        });

        services.AddScoped<IExternalPublisher, RabbitMqExternalPublisher>();

        return services;
    }

    public static IServiceCollection AddRabbitMqConsumer<TMessage, THandler>(
        this IServiceCollection services, string queueName,
        bool declareQueue = false, string? exchangeName = null)
        where TMessage : IntegrationMessage
        where THandler : class, IIntegrationMessageConsumer<TMessage>
    {
        services.AddScoped<IIntegrationMessageConsumer<TMessage>, THandler>();
        services.AddHostedService(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<RabbitMqConsumer<TMessage>>>();
            var settings = provider.GetRequiredService<RabbitMqSettings>();
            return new RabbitMqConsumer<TMessage>(provider, logger, settings, queueName, declareQueue, exchangeName);
        });

        return services;
    }

    public static IHealthChecksBuilder AddRabbitMqHealthCheck(
        this IHealthChecksBuilder builder)
    {
        builder.AddRabbitMQ();
        return builder;
    }
}
