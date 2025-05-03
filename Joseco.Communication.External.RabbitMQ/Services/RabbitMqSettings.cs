using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Joseco.Communication.External.RabbitMQ.Services;

public class RabbitMqSettings
{
    public string? Host { get; set; }
    public int Port { get; set; }
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string? VirtualHost { get; set; }
    public bool UseSsl { get; set; }
}
