# Joseco.Communication

This is a C# library for communication between systems providing a simple and efficient way to send and receive messages between systems using asynchronous messages using a message broker like RabbitMQ. 

Initially, the only broker supported is RabbitMQ, but the library is designed to be extensible, allowing for the addition of other brokers in the future.

## Installation

You can install the library via NuGet Package Manager Console:
```bash
Install-Package Joseco.Communication.External.RabbitMQ
```
or via .NET CLI:
```bash
dotnet add package Joseco.Communication.External.RabbitMQ
```

### Using Contract-Only Package

If you only need the contract definitions and not the borker implementation, you can install the `Joseco.Communication.External.Contracts` package:
```bash
Install-Package Joseco.Communication.External.Contracts
```
or via .NET CLI:
```bash
dotnet add package Joseco.Communication.External.Contracts
```

This package contains the abstractions implemented by the `Joseco.Communication.External.RabbitMQ` package wich includes:

- `IntegrationMessage` Represents a message that can be sent o received from the message broker.
- `IExternalPublisher` Represents the publisher interface for sending messages to the message broker.
- `IIntegrationMessageConsumer` Represents the consumer interface for receiving messages from the message broker.

This useful in scenarios when you need to separate assembly/project from the message broker implementation. This allows you to share the contracts across multiple projects without needing to reference the entire Joseco.Communication.External.RabbitMQ package.

## Usage

`Joseco.Communication.External.RabbitMQ` allows you to send and receive messages using RabbitMQ as the message broker. The library provides a simple and efficient way to send and receive messages between systems using asynchronous messages.

To configure the RabbitMQ implementation, you need to register the services in your dependency injection container. 

```csharp

RabbitMqSettings settings = new RabbitMqSettings()
{
	Host = "host-address",
    UserName = "rabbitMqUser",
    Password = "rabbitMqPassword"
    VirtualHost = "/"
};

services.AddJosecoRabbitMq(settings);
```

Also, you can register the HealthCheck service to monitor the health of the RabbitMQ connection. This is useful for ensuring that your application can communicate with the message broker and handle any issues that may arise.
```csharp
services.AddHealthChecks()
	.AddRabbitMqHealthCheck();
```

It's important to register the RabbitMQ services before registering the HealthCheck.

### Sending Messages

To send a message, you need to create your own message class that inherits from the `IntegrationMessage` abstract record. This message class should contain the properties that you want to send in the message.

```csharp

public class MyMessage : IntegrationMessage
{
	public string Property1 { get; set; }
	public int Property2 { get; set; }
}
```

`IExternalPublisher` is the interface used to send messages to the message broker. It provides a method `PublishAsync` that takes an `IntegrationMessage` object as a parameter. 
To use it, you can injected the `IExternalPublisher` interface into your class and call the `PublishAsync` method to send a message.
```csharp

public class MyService
{
	private readonly IExternalPublisher _publisher;

	public MyService(IExternalPublisher publisher)
	{
		_publisher = publisher;
	}
	public async Task SendMessageAsync()
	{
		var message = new MyMessage
		{
			Property1 = "Hello",
			Property2 = 123
		};

		// By default, the destination always is an Exchange.
		// The destinationName parameter is optinal. If you don't pass any value or pass a null value, the destination name will be the same as the Message type name using the kebab case format.
		var destinationName = "my-exchange-name"; // This parameter is 
		
		var declareDestinationFirst = true; // Set to true if you want to declare the exchange before sending the message. This parameter is optional. The default value is false.
		
		await _publisher.PublishAsync(message, destinationName, declareDestinationFirst);


	}
}
```

### Receiving Messages

To receive messages, you need to create a consumer class that implements the `IIntegrationMessageConsumer<T>` interface. This interface provides a method `HandleAsync` that takes an `IntegrationMessage` object as a parameter that containts the message recieved from the Broker.

```csharp
public class MyMessageConsumer : IIntegrationMessageConsumer<MyMessage>
{
	public Task HandleAsync(MyMessage message)
	{
		// Handle the message here
		Console.WriteLine($"Received message: {message.Property1}, {message.Property2}");
		return Task.CompletedTask;
	}
}
```

To register the consumer, you need to use the `AddIntegrationMessageConsumer` method in your dependency injection container. This method takes the consumer type and the queue name as parameters.
```csharp
services.AddRabbitMqConsumer<MyMessage, MyMessageConsumer>("my-queue-name");
```

