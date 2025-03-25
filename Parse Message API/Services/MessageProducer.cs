using System;
using System.Text;
using RabbitMQ.Client;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Parse_Message_API.Model;

namespace Parse_Message_API.Services
{
    public class MessageProducer
    {
        private readonly string _hostname = "localhost";
        private readonly string _exchangeName = "delayed_exchange";
        private readonly string _routingKey = "delayed_task";
        private readonly string _queueName = "delayed_queue";
        private readonly ILogger<MessageProducer> _logger;

        public MessageProducer(ILogger<MessageProducer> logger)
        {
            _logger = logger;
        }

        public void SendMessage(Guid Id, string Title, string Date, string Time, string App, bool Repeat, DateTime Created_At, DateTime Trigger_At, List<AxUsers> Send_to)
        {
            try
            {
                var factory = new ConnectionFactory { HostName = _hostname };
                using var connection = factory.CreateConnection();
                using var channel = connection.CreateModel();

                // Declare delayed exchange
                channel.ExchangeDeclare(
                    exchange: _exchangeName,
                    type: "x-delayed-message",
                    durable: true,
                    autoDelete: false,
                    arguments: new Dictionary<string, object?>
                    {
                        { "x-delayed-type", "direct" }
                    });

                // Declare & Bind Queue
                channel.QueueDeclare(queue: _queueName, durable: true, exclusive: false, autoDelete: false);
                channel.QueueBind(queue: _queueName, exchange: _exchangeName, routingKey: _routingKey);

                // Convert Trigger_At to UTC
                DateTime triggerTimeUtc = Trigger_At.Kind == DateTimeKind.Unspecified
                    ? Trigger_At.ToUniversalTime()
                    : Trigger_At;

                // Calculate delay
                long delayMilliseconds = (long)(triggerTimeUtc - DateTime.UtcNow).TotalMilliseconds;
                if (delayMilliseconds < 0) delayMilliseconds = 0;

                var message = new
                {
                    Id,
                    Title,
                    Date,
                    Time,
                    App,
                    Repeat,
                    Trigger_At = triggerTimeUtc, // Ensure UTC format
                    Created_At,
                    Send_to
                };

                string msg = JsonSerializer.Serialize(message);
                var body = Encoding.UTF8.GetBytes(msg);

                var properties = channel.CreateBasicProperties();
                properties.Headers = new Dictionary<string, object?>
                {
                    { "x-delay", (int)delayMilliseconds } // Cast to int
                };

                // Publish message
                channel.BasicPublish(
                    exchange: _exchangeName,
                    routingKey: _routingKey,
                    mandatory: false,
                    basicProperties: properties,
                    body: body);

                _logger.LogInformation($"Message scheduled: {msg} (Trigger_At: {triggerTimeUtc}, Delay: {delayMilliseconds} ms)");
                Console.WriteLine($"Message scheduled: {msg} (Trigger_At: {triggerTimeUtc}, Delay: {delayMilliseconds} ms)");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to schedule message: {Title} for {Trigger_At}", Title, Trigger_At);
            }
        }
    }
}
