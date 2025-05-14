using Joseco.Communication.External.Contracts.Message;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Joseco.Communication.External.Contracts.Services;

public interface IIntegrationMessageConsumer<T> where T : IntegrationMessage
{
    Task HandleAsync(T message, CancellationToken cancellationToken);

}
