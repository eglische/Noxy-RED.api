using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using Voxta.Model.WebsocketMessages.ClientMessages;
using Voxta.Providers.Host;

namespace Voxta.SampleProviderApp.Providers
{
    // Configuration options specific to AutoReply
    public class AutoReplyProviderOptions
    {
        public int AutoReplyDelay { get; set; } = 10000; // Default to 10 seconds if not configured
        public string AutoReplyTopic { get; set; } = "/noxyred/autoreply"; // New topic for auto-reply control
    }

    // Configuration options for MQTT
    public class MqttOptions
    {
        public string BrokerAddress { get; set; } = "127.0.0.1"; // Default broker address
        public int Port { get; set; } = 1883; // Default port
        public int QoS { get; set; } = 2; // Default QoS level
    }

    // The main provider class for auto-reply functionality with MQTT integration
    public class AutoReplyProvider : ProviderBase
    {
        private readonly ILogger<AutoReplyProvider> _logger;
        private readonly IMqttClient _mqttClient;
        private readonly MqttQualityOfServiceLevel _mqttQoS;
        private int _currentAutoReplyDelay;
        private bool _autoReplyEnabled = true;

        public AutoReplyProvider(
            IRemoteChatSession session,
            ILogger<AutoReplyProvider> logger,
            IConfiguration configuration
        )
            : base(session, logger)
        {
            _logger = logger;

            // Load AutoReplyProviderOptions directly from configuration
            var autoReplyOptions = new AutoReplyProviderOptions();
            configuration.GetSection("SampleProviderApp").Bind(autoReplyOptions);

            // Load MqttOptions directly from configuration
            var mqttOptions = new MqttOptions();
            configuration.GetSection("MQTT").Bind(mqttOptions);

            _currentAutoReplyDelay = autoReplyOptions.AutoReplyDelay;
            _autoReplyEnabled = _currentAutoReplyDelay > 0;

            // Initialize MQTT client
            var mqttFactory = new MqttFactory();
            _mqttClient = mqttFactory.CreateMqttClient();
            _mqttQoS = (MqttQualityOfServiceLevel)Enum.ToObject(typeof(MqttQualityOfServiceLevel), mqttOptions.QoS);

            // Configure MQTT message handling
            _mqttClient.ApplicationMessageReceivedAsync += OnMqttMessageReceivedAsync;

            // Connect and subscribe to MQTT topic
            StartMqttClient(mqttOptions, autoReplyOptions.AutoReplyTopic);
        }

        private async void StartMqttClient(MqttOptions mqttOptions, string autoReplyTopic)
        {
            var mqttClientOptions = new MqttClientOptionsBuilder()
                .WithTcpServer(mqttOptions.BrokerAddress, mqttOptions.Port)
                .WithCleanSession()
                .Build();

            try
            {
                _logger.LogInformation("Connecting to MQTT broker at {BrokerAddress}:{Port}", mqttOptions.BrokerAddress, mqttOptions.Port);
                await _mqttClient.ConnectAsync(mqttClientOptions, CancellationToken.None);

                _logger.LogInformation("Connected to MQTT broker. Subscribing to auto-reply topic: {Topic}", autoReplyTopic);
                await _mqttClient.SubscribeAsync(autoReplyTopic, _mqttQoS);
                _logger.LogInformation("Successfully subscribed to MQTT topic: {Topic}", autoReplyTopic);

                // Configure auto-reply if enabled
                if (_autoReplyEnabled)
                {
                    ConfigureAutoReply(TimeSpan.FromMilliseconds(_currentAutoReplyDelay), OnAutoReply);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect or subscribe to MQTT topic: {Topic}", autoReplyTopic);
            }
        }

        private async Task OnMqttMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
        {
            var topic = e.ApplicationMessage.Topic;
            var payload = System.Text.Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment.Array, e.ApplicationMessage.PayloadSegment.Offset, e.ApplicationMessage.PayloadSegment.Count);

            _logger.LogInformation("MQTT message received on topic {Topic} with payload: {Payload}", topic, payload);
            HandleAutoReplyTopic(payload);
            await Task.CompletedTask;
        }

        private void HandleAutoReplyTopic(string payload)
        {
            _logger.LogInformation("Processing auto-reply topic payload: {Payload}", payload);

            if (int.TryParse(payload, out int delay))
            {
                if (delay > 0)
                {
                    _currentAutoReplyDelay = delay;
                    _autoReplyEnabled = true;
                    ConfigureAutoReply(TimeSpan.FromMilliseconds(_currentAutoReplyDelay), OnAutoReply);
                    _logger.LogInformation("Auto-reply delay updated to {Delay}ms", _currentAutoReplyDelay);
                }
                else
                {
                    _autoReplyEnabled = false;
                    _logger.LogInformation("Auto-reply has been disabled via MQTT.");
                }
            }
            else if (payload.ToLower() == "off" || payload == "0")
            {
                _autoReplyEnabled = false;
                _logger.LogInformation("Auto-reply has been disabled via MQTT.");
            }
            else
            {
                _logger.LogWarning("Invalid payload received for auto-reply topic: {Payload}", payload);
            }
        }

        private void OnAutoReply()
        {
            if (!_autoReplyEnabled)
            {
                _logger.LogInformation("Auto-reply is disabled, skipping reply.");
                return;
            }

            _logger.LogInformation("Auto-replying after delay of {Delay}ms of inactivity", _currentAutoReplyDelay);
            Send(new ClientSendMessage
            {
                SessionId = SessionId,
                Text = "[{{ char }} continues talking to {{ user }}]"
            });
        }
    }
}
