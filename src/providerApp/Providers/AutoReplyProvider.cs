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
    public class AutoReplyProviderOptions
    {
        public int AutoReplyDelay { get; set; } = 0;
        public string AutoReplyTopic { get; set; } = "/noxyred/autoreply";
    }

    public class MqttOptions
    {
        public string BrokerAddress { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 1883;
        public int QoS { get; set; } = 2;
    }

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

            var autoReplyOptions = new AutoReplyProviderOptions();
            configuration.GetSection("Noxy-RED.api").Bind(autoReplyOptions);

            var mqttOptions = new MqttOptions();
            configuration.GetSection("MQTT").Bind(mqttOptions);

            _currentAutoReplyDelay = autoReplyOptions.AutoReplyDelay;
            _autoReplyEnabled = _currentAutoReplyDelay > 0;

            var mqttFactory = new MqttFactory();
            _mqttClient = mqttFactory.CreateMqttClient();
            _mqttQoS = (MqttQualityOfServiceLevel)Enum.ToObject(typeof(MqttQualityOfServiceLevel), mqttOptions.QoS);

            _mqttClient.ApplicationMessageReceivedAsync += OnMqttMessageReceivedAsync;

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
            else if (payload.Equals("interrupt", StringComparison.OrdinalIgnoreCase))
            {
                Send(new ClientInterruptMessage { SessionId = SessionId });
                _logger.LogInformation("ClientInterruptMessage sent to cancel TTS speech output.");

                Send(new ClientSendMessage
                {
                    SessionId = SessionId,
                    Text = "/secret An automated interrupt has occurred and is part of the scenario. Keep the story going and ignore it happened."
                });
                _logger.LogInformation("Secret message sent to chat after interrupt.");
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
