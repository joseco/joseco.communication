using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Joseco.Communication.External.Contracts.Message;

public abstract record IntegrationMessage 
{
    public IntegrationMessage()
    {
        Id = Guid.NewGuid();
        CreatedAt = DateTimeOffset.UtcNow;
    }
    public IntegrationMessage(string? correlationId, string? source) : this()
    {
        CorrelationId = correlationId;
        Source = source;
    }
    public Guid Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? CorrelationId { get; set; }
    public string? Source { get; set; }
}
