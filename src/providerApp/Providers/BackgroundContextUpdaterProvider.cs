using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using Voxta.Model.Shared;
using Voxta.Model.WebsocketMessages.ClientMessages;
using Voxta.Providers.Host;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

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
    private static bool _eventHandlerAttached = false;

    private readonly ConcurrentDictionary<string, DateTime> _recentMessages = new();
    private readonly TimeSpan _duplicateWindow = TimeSpan.FromSeconds(5); // Reduced window for more precise duplicate detection
    private readonly object _messageLock = new(); // Lock object for synchronizing access to _recentMessages

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
        if (!_eventHandlerAttached)
        {
            _mqttClient.ApplicationMessageReceivedAsync += OnMqttMessageReceivedAsync;
            _eventHandlerAttached = true;
            _logger.LogInformation("ApplicationMessageReceivedAsync handler attached.");
        }
        else
        {
            _logger.LogWarning("Attempted to attach ApplicationMessageReceivedAsync handler, but it was already attached!");
        }
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
        var payloadHash = ComputeHash(payload);

        _logger.LogDebug("Received MQTT message on topic: {Topic} with payload: {Payload}", topic, payload);

        lock (_messageLock)
        {
            _logger.LogDebug("Checking for duplicate message with hash: {PayloadHash}", payloadHash);

            // Check for duplicates within the specified window using hash of the payload
            if (_recentMessages.TryGetValue(payloadHash, out var lastReceived))
            {
                if (DateTime.UtcNow - lastReceived < _duplicateWindow)
                {
                    _logger.LogWarning("Duplicate message detected within the duplicate window, skipping payload: {Payload}", payload);
                    return;
                }
            }

            // Update the recent messages with the new timestamp
            _recentMessages[payloadHash] = DateTime.UtcNow;
            _logger.LogDebug("Message added to recent messages with hash: {PayloadHash}", payloadHash);

            // Clean up old messages outside the duplicate window
            var expiredKeys = _recentMessages.Where(kvp => DateTime.UtcNow - kvp.Value > _duplicateWindow).Select(kvp => kvp.Key).ToList();
            foreach (var key in expiredKeys)
            {
                _recentMessages.TryRemove(key, out _);
                _logger.LogDebug("Removed expired message with hash: {ExpiredKey}", key);
            }
        }

        _logger.LogDebug("Processing MQTT message with topic: {Topic} and payload: {Payload}", topic, payload);
        ProcessMqttMessage(topic, payload);
    }

    private void ProcessMqttMessage(string topic, string payload)
    {
        if (topic == _chatTopic)
        {
            _logger.LogInformation("Handling chat topic message(chat): {Payload}", payload);
            HandleChatTopic(payload);
        }
        else if (topic == _messageTopic)
        {
            _logger.LogInformation("Handling message topic message(message): {Payload}", payload);
            HandleMessageTopic(payload);
        }
        else
        {
            _logger.LogWarning("Unexpected topic received: {Topic}", topic);
        }
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

        // Always send the message to Voxta Chat
        SendWhenFree(new ClientSendMessage
        {
            SessionId = SessionId,
            Text = payload // Send the original payload
        });

        _logger.LogInformation("Sent chat message (debug): {Message}", payload);
    }

    private async Task SendMessageToMqtt(string chatMessage)
    {
        var mqttMessage = new MqttApplicationMessageBuilder()
            .WithTopic(_chatTopic)
            .WithPayload(chatMessage)
            .WithQualityOfServiceLevel(_mqttQoS)
            .Build();

        _logger.LogDebug("Sending MQTT message with topic: {ChatTopic} and message: {Message}", _chatTopic, chatMessage);
        await _mqttClient.PublishAsync(mqttMessage);
        _logger.LogInformation("Forwarded chat message to MQTT: {Message}", chatMessage);
    }

    private string ComputeHash(string input)
    {
        using (var sha256 = SHA256.Create())
        {
            var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
            return BitConverter.ToString(bytes).Replace("-", "").ToLower();
        }
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
