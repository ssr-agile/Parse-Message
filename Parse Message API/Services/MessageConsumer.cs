using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Parse_Message_API.Services
{
    public class MessageConsumer : BackgroundService
    {
        private readonly string _hostname = "localhost";
        private readonly string _exchangeName = "delayed_exchange";
        private readonly string _queueName = "delayed_queue";
        private readonly string _routingKey = "delayed_task";

        private readonly ILogger<MessageConsumer> _logger;
        private IConnection? _connection;
        private IModel? _channel;

        public MessageConsumer(ILogger<MessageConsumer> logger)
        {
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                var factory = new ConnectionFactory { HostName = _hostname };
                _connection = factory.CreateConnection();
                _channel = _connection.CreateModel();

                _channel.ExchangeDeclare(
                    exchange: _exchangeName,
                    type: "x-delayed-message",
                    durable: true,
                    autoDelete: false,
                    arguments: new Dictionary<string, object?>
                    {
                        { "x-delayed-type", "direct" }
                    });

                _channel.QueueDeclare(
                    queue: _queueName,
                    durable: true,
                    exclusive: false,
                    autoDelete: false,
                    arguments: null);

                _channel.QueueBind(_queueName, _exchangeName, _routingKey);

                _logger.LogInformation("RabbitMQ Consumer started. Waiting for messages...");

                var consumer = new EventingBasicConsumer(_channel);
                consumer.Received += (model, eventArgs) =>
                {
                    var body = eventArgs.Body.ToArray();
                    var message = Encoding.UTF8.GetString(body);

                    try
                    {
                        _logger.LogInformation("[Received] Message: {Message}", message);
                        Console.WriteLine($"[Received at {DateTime.Now}] Message: {message}");
                        // Process the message here...

                        _channel.BasicAck(eventArgs.DeliveryTag, multiple: false);
                        _logger.LogInformation("Message processed successfully: {Message}", message);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing message: {Message}", message);
                        _channel.BasicNack(eventArgs.DeliveryTag, multiple: false, requeue: true);
                    }
                };

                _channel.BasicConsume(queue: _queueName, autoAck: false, consumer: consumer);

                while (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(1000, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "RabbitMQ consumer failed to start.");
            }
        }

        public override void Dispose()
        {
            _logger.LogWarning("RabbitMQ Consumer shutting down...");

            _channel?.Close();
            _channel?.Dispose();
            _connection?.Close();
            _connection?.Dispose();
            base.Dispose();
        }
    }
}
