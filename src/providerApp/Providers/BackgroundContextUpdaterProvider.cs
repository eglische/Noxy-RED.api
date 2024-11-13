using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using Voxta.Model.Shared;
using Voxta.Model.WebsocketMessages.ClientMessages;
using Voxta.Providers.Host;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Voxta.SampleProviderApp.Providers;

public class BackgroundContextUpdaterProvider : ProviderBase
{
    private readonly IMqttClient _mqttClient;
    private readonly string _chatTopic;
    private readonly string _messageTopic;
    private readonly MqttQualityOfServiceLevel _mqttQoS;
    private readonly ILogger<BackgroundContextUpdaterProvider> _logger;
    private readonly string _brokerAddress;
    private readonly int _port;
    private bool _chatEnabled = false;
    private bool _subscribed = false;

    private List<(string payload, DateTime timestamp)> _recentMessages = new List<(string, DateTime)>();
    private readonly TimeSpan _duplicateWindow = TimeSpan.FromSeconds(2);

    public BackgroundContextUpdaterProvider(
        IRemoteChatSession session,
        ILogger<BackgroundContextUpdaterProvider> logger,
        IConfiguration configuration
    ) : base(session, logger)
    {
        _logger = logger;
        var mqttOptions = configuration.GetSection("MQTT").Get<ContextUpdaterMqttOptions>();
        _mqttClient = new MqttFactory().CreateMqttClient();
        _chatTopic = mqttOptions.ChatTopic;
        _messageTopic = mqttOptions.MessageTopic;
        _brokerAddress = mqttOptions.BrokerAddress;
        _port = mqttOptions.Port;
        _mqttQoS = (MqttQualityOfServiceLevel)Enum.ToObject(typeof(MqttQualityOfServiceLevel), mqttOptions.QoS);

        _logger.LogInformation("BackgroundContextUpdaterProvider initialized with BrokerAddress: {BrokerAddress}, Port: {Port}, ChatTopic: {ChatTopic}, MessageTopic: {MessageTopic}, and QoS: {QoS}",
            _brokerAddress, _port, _chatTopic, _messageTopic, _mqttQoS);

        // Ensure ApplicationMessageReceivedAsync is attached only once
        _mqttClient.ApplicationMessageReceivedAsync -= OnMqttMessageReceivedAsync;
        _mqttClient.ApplicationMessageReceivedAsync += OnMqttMessageReceivedAsync;
    }

    protected override async Task OnStartAsync()
    {
        await base.OnStartAsync();

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(_brokerAddress, _port)
            .WithCleanSession(false) // Preserve session state to prevent redelivery
            .Build();

        try
        {
            _logger.LogInformation("Connecting to MQTT broker at {BrokerAddress}:{Port}", _brokerAddress, _port);
            await _mqttClient.ConnectAsync(options, CancellationToken.None);

            if (!_subscribed)
            {
                _logger.LogInformation("Connected to MQTT broker. Subscribing to MQTT topics: {ChatTopic}, {MessageTopic}", _chatTopic, _messageTopic);

                var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
                    .WithTopicFilter(f => f.WithTopic(_chatTopic).WithQualityOfServiceLevel(_mqttQoS))
                    .WithTopicFilter(f => f.WithTopic(_messageTopic).WithQualityOfServiceLevel(_mqttQoS))
                    .Build();

                await _mqttClient.SubscribeAsync(subscribeOptions);
                _subscribed = true; // Only subscribe once
            }

            _logger.LogInformation("Successfully subscribed to MQTT topics: {ChatTopic}, {MessageTopic}");
            _logger.LogInformation("Setting up chat message monitoring...");
            HandleMessage(ChatMessageRole.Assistant, RemoteChatMessageTiming.Generated, OnChatMessageReceived);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect or subscribe to MQTT topics: {ChatTopic}, {MessageTopic}", _chatTopic, _messageTopic);
        }
    }

    // This method is triggered when chat messages are received
    private void OnChatMessageReceived(RemoteChatMessage message)
    {
        if (_chatEnabled)
        {
            _logger.LogInformation("Forwarding chat message to MQTT: {Message}", message.Text);
            SendMessageToMqtt(message.Text);
        }
        else
        {
            _logger.LogInformation("Chat monitoring is disabled, ignoring message.");
        }
    }

    private async Task OnMqttMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var payload = System.Text.Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment.ToArray());

        // Clean up old messages
        var now = DateTime.UtcNow;
        _recentMessages = _recentMessages
            .Where(m => now - m.timestamp < _duplicateWindow)
            .ToList();

        // Check if message is a duplicate
        if (_recentMessages.Any(m => m.payload == payload))
        {
            _logger.LogDebug("Duplicate message detected within 2-second window: {Payload}", payload);
            return;
        }

        // Add new message to recent messages list
        _recentMessages.Add((payload, now));

        // Process message based on the topic
        await ProcessMqttMessage(topic, payload);
    }

    private async Task ProcessMqttMessage(string topic, string payload)
    {
        if (topic == _chatTopic)
        {
            _logger.LogInformation("Handling chat topic message(chat): {Payload}", payload);
            await Task.Run(() => HandleChatTopic(payload));
        }
        else if (topic == _messageTopic)
        {
            _logger.LogInformation("Handling message topic message(message): {Payload}", payload);
            await Task.Run(() => HandleMessageTopic(payload));
        }
        else
        {
            _logger.LogWarning("Unexpected topic received: {Topic}", topic);
        }

        await Task.CompletedTask;
    }

    private void HandleChatTopic(string payload)
    {
        _logger.LogInformation("Processing chat topic payload: {Payload}", payload);

        if (payload.StartsWith("SwitchChatTopic="))
        {
            var state = payload.Split('=')[1].Trim().Replace("\"", "").Replace("'", "");
            _logger.LogDebug("Processed state after trimming: {State}", state);

            _chatEnabled = state.ToLower() == "true";
            _logger.LogInformation("Chat enabled set to {ChatEnabled}", _chatEnabled);

            if (_chatEnabled)
            {
                _logger.LogInformation("Started monitoring chat messages for MQTT forwarding.");
            }
            else
            {
                _logger.LogInformation("Stopped monitoring chat messages.");
            }
        }
        else
        {
            _logger.LogWarning("Payload does not start with 'SwitchChatTopic=': {Payload}");
        }
    }

    private void HandleMessageTopic(string payload)
    {
        _logger.LogInformation("Received message (debug): {Message}", payload);
        SendWhenFree(new ClientSendMessage
        {
            SessionId = SessionId,
            Text = payload
        });
        //_logger.LogInformation("Sent chat message (debug): {Message}", payload);
    }

    private async Task SendMessageToMqtt(string chatMessage)
    {
        var mqttMessage = new MqttApplicationMessageBuilder()
            .WithTopic(_chatTopic)
            .WithPayload(chatMessage)
            .WithQualityOfServiceLevel(_mqttQoS)
            .Build();

        await _mqttClient.PublishAsync(mqttMessage);
        _logger.LogInformation("Forwarded chat message to MQTT: {Message}", chatMessage);
    }
}

public class ContextUpdaterMqttOptions
{
    public string BrokerAddress { get; set; }
    public int Port { get; set; }
    public string ChatTopic { get; set; }
    public string MessageTopic { get; set; }
    public int QoS { get; set; }
}
