using Joseco.Communication.External.Contracts.Message;

namespace Joseco.Communication.External.Contracts.Services;

public interface IExternalPublisher
{
    Task PublishAsync<T>(T message, string? destination = null, bool declareDestination = false) where T : IntegrationMessage;
}
