using Joseco.Communication.External.Contracts.Message;

namespace Joseco.Communication.External.Contracts.Services;

public interface IIntegrationMessageConsumer<T> where T : IntegrationMessage
{
    Task HandleAsync(T message, CancellationToken cancellationToken);

}
