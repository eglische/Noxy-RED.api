using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using Voxta.Model.WebsocketMessages.ServerMessages;
using Voxta.Providers.Host;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Voxta.SampleProviderApp.Providers
{
    public class ActionProvider : ProviderBase, IAsyncDisposable
    {
        private readonly IMqttClient _mqttClient;
        private readonly string _triggerTopic;
        private readonly MqttQualityOfServiceLevel _mqttQoS;
        private readonly ILogger<ActionProvider> _logger;
        private readonly string _mqttInputTopic;
        private readonly string _brokerAddress;
        private readonly int _port;

        // ConcurrentDictionary to track processed messages
        private readonly ConcurrentDictionary<string, bool> _processedMessages = new ConcurrentDictionary<string, bool>();

        // Cancellation token for managing task cancellation
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private bool _disposed = false;

        public ActionProvider(
            IRemoteChatSession session,
            ILogger<ActionProvider> logger,
            IConfiguration configuration
        ) : base(session, logger)
        {
            _logger = logger;
            var mqttOptions = configuration.GetSection("MQTT").Get<ActionMqttOptions>();
            _mqttClient = new MqttFactory().CreateMqttClient();
            _triggerTopic = mqttOptions.TriggerTopic;
            _mqttInputTopic = mqttOptions.TriggerTopic;
            _brokerAddress = mqttOptions.BrokerAddress;
            _port = mqttOptions.Port;
            _mqttQoS = (MqttQualityOfServiceLevel)Enum.ToObject(typeof(MqttQualityOfServiceLevel), mqttOptions.QoS);

            _logger.LogInformation("ActionProvider initialized with BrokerAddress: {BrokerAddress}, Port: {Port}, TriggerTopic: {TriggerTopic} and QoS: {QoS}",
                _brokerAddress, _port, _triggerTopic, _mqttQoS);
        }

        protected override async Task OnStartAsync()
        {
            await base.OnStartAsync();
            await RetryWithBackoffAsync(ConnectAndSubscribeAsync, _cancellationTokenSource.Token);
        }

        // Retry logic with exponential backoff for MQTT connection
        private async Task RetryWithBackoffAsync(Func<Task> action, CancellationToken cancellationToken)
        {
            const int maxRetries = 5;
            int retryCount = 0;
            const int initialDelaySeconds = 2;
            const int maxDelaySeconds = 60;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await action();
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to connect to MQTT broker. Retrying...");

                    if (++retryCount > maxRetries)
                    {
                        _logger.LogCritical("Maximum retry attempts reached. Shutting down connection.");
                        throw;  // Exit if retries exceed max limit
                    }

                    // Exponential backoff logic
                    int delay = Math.Min(initialDelaySeconds * (int)Math.Pow(2, retryCount), maxDelaySeconds);
                    _logger.LogWarning($"Retrying MQTT connection in {delay} seconds (attempt {retryCount}/{maxRetries})...");
                    await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
                }
            }
        }

        // Connecting and subscribing to MQTT topics
        private async Task ConnectAndSubscribeAsync()
        {
            var options = new MqttClientOptionsBuilder()
                .WithTcpServer(_brokerAddress, _port)
                .WithCleanSession()
                .Build();

            _logger.LogInformation("Attempting to connect to MQTT broker at {BrokerAddress}:{Port}", _brokerAddress, _port);
            await _mqttClient.ConnectAsync(options, _cancellationTokenSource.Token);
            _logger.LogInformation("Connected to MQTT broker.");

            // Subscribe to the trigger topic
            await _mqttClient.SubscribeAsync(_triggerTopic, _mqttQoS);
            _logger.LogInformation("Subscribed to MQTT topic: {TriggerTopic}", _triggerTopic);

            // Handle chat triggers and relay them to MQTT
            HandleMessage<ServerActionAppTriggerMessage>(message =>
            {
                _logger.LogInformation("Received trigger {TriggerName} from chat", message.Name);

                // Log and process the trigger
                _logger.LogInformation("Processing trigger {TriggerName} for MQTT sending.", message.Name);

                // Send corresponding MQTT message with the trigger name as the payload
                SendMqttMessage(message.Name).Wait();
            });
        }

        // Method to send MQTT message
        private async Task SendMqttMessage(string action)
        {
            if (_mqttClient == null || !_mqttClient.IsConnected)
            {
                _logger.LogWarning("MQTT client is not connected. Skipping message send.");
                return;
            }

            // Convert the action name to MQTT payload
            var payload = System.Text.Encoding.UTF8.GetBytes(action);
            var mqttMessage = new MqttApplicationMessageBuilder()
                .WithTopic(_mqttInputTopic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(_mqttQoS)
                .Build();

            // Log before sending the message
            _logger.LogInformation("Sending MQTT message with action {Action} to topic {Topic}", action, _mqttInputTopic);

            // Publish the MQTT message
            await _mqttClient.PublishAsync(mqttMessage, _cancellationTokenSource.Token);

            // Log after successfully sending the message
            _logger.LogInformation("Successfully sent MQTT message with action {Action} to topic {Topic}", action, _mqttInputTopic);
        }

        // Graceful shutdown of MQTT and resource disposal
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            _logger.LogInformation("Disposing ActionProvider...");

            _cancellationTokenSource.Cancel();  // Trigger cancellation of tasks

            if (_mqttClient.IsConnected)
            {
                await _mqttClient.DisconnectAsync();
                _logger.LogInformation("Disconnected from MQTT broker.");
            }

            _mqttClient.Dispose();
            _cancellationTokenSource.Dispose();
            _disposed = true;

            _logger.LogInformation("Resources disposed successfully.");
        }

        public class ActionMqttOptions
        {
            public string BrokerAddress { get; set; }
            public int Port { get; set; }
            public string TriggerTopic { get; set; }
            public int QoS { get; set; }
        }
    }
}
