using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Voxta.Model.Shared;
using Voxta.Model.WebsocketMessages.ClientMessages;
using Voxta.Providers.Host;

namespace Voxta.SampleProviderApp.Providers
{
    public class ContextProvider : ProviderBase, IAsyncDisposable
    {
        private readonly IMqttClient _mqttClient;
        private readonly string _contextTopic;
        private readonly MqttQualityOfServiceLevel _mqttQoS;
        private readonly ILogger<ContextProvider> _logger;
        private readonly string _brokerAddress;
        private readonly int _port;

        private readonly ConcurrentDictionary<string, ContextDefinition> _registeredContexts = new();
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private bool _disposed = false;

        public ContextProvider(
            IRemoteChatSession session,
            ILogger<ContextProvider> logger,
            IConfiguration configuration
        ) : base(session, logger)
        {
            _logger = logger;
            var mqttOptions = configuration.GetSection("MQTT").Get<ContextMqttOptions>();
            _mqttClient = new MqttFactory().CreateMqttClient();
            _contextTopic = mqttOptions.ContextTopic; // Topic for incoming context updates
            _brokerAddress = mqttOptions.BrokerAddress;
            _port = mqttOptions.Port;
            _mqttQoS = (MqttQualityOfServiceLevel)Enum.ToObject(typeof(MqttQualityOfServiceLevel), mqttOptions.QoS);

            _logger.LogInformation("ContextProvider initialized with BrokerAddress: {BrokerAddress}, Port: {Port}, ContextTopic: {ContextTopic}, and QoS: {QoS}",
                _brokerAddress, _port, _contextTopic, _mqttQoS);
        }

        protected override async Task OnStartAsync()
        {
            await base.OnStartAsync();
            await RetryWithBackoffAsync(ConnectAndSubscribeAsync, _cancellationTokenSource.Token);
        }

        private async Task ConnectAndSubscribeAsync()
        {
            var options = new MqttClientOptionsBuilder()
                .WithTcpServer(_brokerAddress, _port)
                .WithCleanSession()
                .Build();

            _logger.LogInformation("Connecting to MQTT broker at {BrokerAddress}:{Port}", _brokerAddress, _port);
            await _mqttClient.ConnectAsync(options, _cancellationTokenSource.Token);
            _logger.LogInformation("Connected to MQTT broker.");

            await _mqttClient.SubscribeAsync(_contextTopic, _mqttQoS);
            _logger.LogInformation("Subscribed to MQTT topic: {ContextTopic}", _contextTopic);

            _mqttClient.ApplicationMessageReceivedAsync += OnMqttMessageReceivedAsync;
        }

        private async Task OnMqttMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
        {
            var topic = e.ApplicationMessage.Topic;
            var payload = System.Text.Encoding.UTF8.GetString(e.ApplicationMessage.Payload);

            _logger.LogInformation("Received MQTT message on topic {Topic} with payload: {Payload}", topic, payload);

            try
            {
                // Log and inject the session ID
                _logger.LogDebug("SessionId retrieved from ProviderBase: {SessionId}", SessionId);
                var modifiedPayload = InjectSessionId(payload, SessionId);
                _logger.LogInformation("Modified payload with SessionId: {Payload}", modifiedPayload);

                // Deserialize the modified payload
                var contextMessage = JsonSerializer.Deserialize<ClientUpdateContextMessage>(
                    modifiedPayload,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (contextMessage == null || string.IsNullOrEmpty(contextMessage.ContextKey))
                {
                    _logger.LogWarning("Invalid context message received: {Payload}", payload);
                    return;
                }

                ProcessContextMessage(contextMessage);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process MQTT message payload: {Payload}", payload);
            }
        }

        private string InjectSessionId(string payload, Guid sessionId)
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();

                // Copy all existing properties
                foreach (var property in root.EnumerateObject())
                {
                    property.WriteTo(writer);
                }

                // Add the SessionId property
                writer.WritePropertyName("SessionId");
                writer.WriteStringValue(sessionId.ToString());

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private void ProcessContextMessage(ClientUpdateContextMessage contextMessage)
        {
            foreach (var context in contextMessage.Contexts ?? Array.Empty<ContextDefinition>())
            {
                if (context.Disabled)
                {
                    RemoveContext(context.Name);
                }
                else
                {
                    AddContext(context);
                }
            }
        }

        private void AddContext(ContextDefinition context)
        {
            if (string.IsNullOrEmpty(context.Name))
            {
                _logger.LogWarning("Context name is required for adding a context.");
                return;
            }

            if (_registeredContexts.TryAdd(context.Name, context))
            {
                _logger.LogInformation("Added context: {Name}", context.Name);

                // Update registered contexts in Voxta
                UpdateChatContext();
            }
            else
            {
                _logger.LogWarning("Context already exists: {Name}", context.Name);
            }
        }

        private void RemoveContext(string contextName)
        {
            if (string.IsNullOrEmpty(contextName))
            {
                _logger.LogWarning("Context name is required for removing a context.");
                return;
            }

            if (_registeredContexts.TryRemove(contextName, out _))
            {
                _logger.LogInformation("Removed context: {Name}", contextName);

                // Update registered contexts in Voxta
                UpdateChatContext();
            }
            else
            {
                _logger.LogWarning("Context does not exist: {Name}", contextName);
            }
        }

        private void UpdateChatContext()
        {
            var contextMessage = new ClientUpdateContextMessage
            {
                SessionId = SessionId, // Use the session ID managed by ProviderBase
                ContextKey = "Contexts",
                Contexts = _registeredContexts.Values.ToArray()
            };

            Send(contextMessage);
            _logger.LogInformation("Updated Voxta chat context. Total contexts: {ContextCount}", _registeredContexts.Count);
        }

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
                        throw;
                    }

                    int delay = Math.Min(initialDelaySeconds * (int)Math.Pow(2, retryCount), maxDelaySeconds);
                    _logger.LogWarning($"Retrying MQTT connection in {delay} seconds (attempt {retryCount}/{maxRetries})...");
                    await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            _logger.LogInformation("Disposing ContextProvider...");
            _cancellationTokenSource.Cancel();

            if (_mqttClient.IsConnected)
            {
                await _mqttClient.DisconnectAsync();
                _logger.LogInformation("Disconnected from MQTT broker.");
            }

            _mqttClient.Dispose();
            _cancellationTokenSource.Dispose();
            _disposed = true;
        }

        public class ContextMqttOptions
        {
            public string BrokerAddress { get; set; }
            public int Port { get; set; }
            public string ContextTopic { get; set; }
            public int QoS { get; set; }
        }
    }
}
