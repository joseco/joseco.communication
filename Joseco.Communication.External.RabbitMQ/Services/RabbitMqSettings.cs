namespace Joseco.Communication.External.RabbitMQ.Services;

public class RabbitMqSettings
{
    public string? Host { get; set; }
    public int Port { get; set; }
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string? VirtualHost { get; set; }
    public bool UseSsl { get; set; }

    /// <summary>Times a consumer tries a message before sending it to the <c>{queue}.error</c> queue.</summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>Delay before the 2nd attempt; it doubles on every following attempt.</summary>
    public int RetryBaseDelayMilliseconds { get; set; } = 1000;
}
