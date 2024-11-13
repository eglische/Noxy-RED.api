using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using NAudio.Wave;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Voxta.Providers.Host;

namespace Voxta.SampleProviderApp.Providers
{
    public class AudioProvider : ProviderBase, IAsyncDisposable
    {
        private readonly IMqttClient _mqttClient;
        private readonly string _soundEffectTopic;
        private readonly MqttQualityOfServiceLevel _mqttQoS;
        private readonly ILogger<AudioProvider> _logger;
        private readonly ConcurrentDictionary<string, WaveOutEvent> _playingAudio = new ConcurrentDictionary<string, WaveOutEvent>();
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private readonly IConfiguration _configuration;
        private bool _disposed = false;

        // The audio directory, now relative to where the executable is run
        private readonly string _audioDirectory;

        public AudioProvider(
            ILogger<AudioProvider> logger,
            IConfiguration configuration
        ) : base(null, logger) // Pass null as no session is needed for this
        {
            _logger = logger;
            _configuration = configuration;

            // Load MQTT settings from configuration
            var mqttOptions = _configuration.GetSection("MQTT").Get<MqttOptions>();

            if (mqttOptions == null || string.IsNullOrEmpty(mqttOptions.BrokerAddress))
            {
                throw new ArgumentNullException(nameof(mqttOptions.BrokerAddress), "BrokerAddress is missing in the configuration.");
            }

            mqttOptions.Port = mqttOptions.Port == 0 ? 1883 : mqttOptions.Port;
            mqttOptions.QoS = mqttOptions.QoS == 0 ? 1 : mqttOptions.QoS;

            _mqttClient = new MqttFactory().CreateMqttClient();
            _soundEffectTopic = mqttOptions.SoundEffectTopic ?? "/sound/effects";
            _mqttQoS = (MqttQualityOfServiceLevel)Enum.ToObject(typeof(MqttQualityOfServiceLevel), mqttOptions.QoS);

            _logger.LogInformation(
                "AudioProvider initialized with BrokerAddress: {BrokerAddress}, Port: {Port}, SoundEffectTopic: {SoundEffectTopic}, and QoS: {QoS}",
                mqttOptions.BrokerAddress, mqttOptions.Port, _soundEffectTopic, _mqttQoS);

            // Use a relative path from the directory where the executable is running
            _audioDirectory = Path.Combine(AppContext.BaseDirectory, "audio", "presets");

            // Ensure the directory exists
            if (!Directory.Exists(_audioDirectory))
            {
                _logger.LogError("Audio directory not found: {AudioDirectory}", _audioDirectory);
                throw new DirectoryNotFoundException($"Audio directory not found: {_audioDirectory}");
            }

            // Index audio files
            IndexAudioFiles();
        }

        private void IndexAudioFiles()
        {
            var audioFiles = Directory.GetFiles(_audioDirectory, "p_*.wav")
                                      .ToDictionary(Path.GetFileNameWithoutExtension);

            foreach (var file in audioFiles)
            {
                _logger.LogInformation("Indexed audio file: {AudioFile}", file.Key);
            }
        }

        protected override async Task OnStartAsync()
        {
            await base.OnStartAsync();
            await RetryWithBackoffAsync(ConnectAndSubscribeAsync, _cancellationTokenSource.Token);
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
                        throw;  // Exit if retries exceed max limit
                    }

                    int delay = Math.Min(initialDelaySeconds * (int)Math.Pow(2, retryCount), maxDelaySeconds);
                    _logger.LogWarning($"Retrying MQTT connection in {delay} seconds (attempt {retryCount}/{maxRetries})...");
                    await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
                }
            }
        }

        private async Task ConnectAndSubscribeAsync()
        {
            var mqttOptions = _configuration.GetSection("MQTT").Get<MqttOptions>() ?? new MqttOptions
            {
                BrokerAddress = "127.0.0.1",
                Port = 1883,
                SoundEffectTopic = "/sound/effects",
                QoS = 1
            };

            if (string.IsNullOrEmpty(mqttOptions.BrokerAddress))
            {
                throw new ArgumentNullException(nameof(mqttOptions.BrokerAddress), "BrokerAddress cannot be null or empty.");
            }

            var options = new MqttClientOptionsBuilder()
                .WithTcpServer(mqttOptions.BrokerAddress, mqttOptions.Port)
                .WithCleanSession()
                .Build();

            _logger.LogInformation("Connecting to MQTT broker at {BrokerAddress}:{Port}", mqttOptions.BrokerAddress, mqttOptions.Port);

            await _mqttClient.ConnectAsync(options, _cancellationTokenSource.Token);

            _logger.LogInformation("Connected to MQTT broker.");

            await _mqttClient.SubscribeAsync(mqttOptions.SoundEffectTopic, (MqttQualityOfServiceLevel)mqttOptions.QoS);

            _logger.LogInformation("Subscribed to MQTT topic: {SoundEffectTopic}", mqttOptions.SoundEffectTopic);

            _mqttClient.ApplicationMessageReceivedAsync += OnMqttMessageReceivedAsync;
        }
        private async Task OnMqttMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
        {
            var topic = e.ApplicationMessage.Topic;
            var payload = System.Text.Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment.Array);

            _logger.LogInformation("Received MQTT message on topic {Topic} with payload: {Payload}", topic, payload);

            if (topic == _soundEffectTopic)
            {
                HandleSoundEffectTopic(payload);
            }

            await Task.CompletedTask;
        }

        private void HandleSoundEffectTopic(string payload)
        {
            if (payload.StartsWith("p_"))
            {
                PlayAudio(payload);
            }
            else if (payload.StartsWith("stop:"))
            {
                StopAudio(payload.Substring(5));
            }
            else if (payload.StartsWith("fade:"))
            {
                FadeAudio(payload.Substring(5));
            }
        }

        private void PlayAudio(string filePrefix)
        {
            var matchingFiles = Directory.GetFiles(_audioDirectory, $"{filePrefix}*.wav");

            if (matchingFiles.Length > 0)
            {
                var audioFile = matchingFiles.First();
                _logger.LogInformation("Playing audio file: {AudioFile}", audioFile);

                var outputDevice = new WaveOutEvent();
                var audioReader = new AudioFileReader(audioFile);

                outputDevice.Init(audioReader);
                outputDevice.Play();

                _playingAudio.TryAdd(filePrefix, outputDevice);

                outputDevice.PlaybackStopped += (sender, args) =>
                {
                    _playingAudio.TryRemove(filePrefix, out _);
                    outputDevice.Dispose();
                    audioReader.Dispose();
                    _logger.LogInformation("Finished playing audio file: {AudioFile}", audioFile);
                };
            }
            else
            {
                _logger.LogWarning("No audio file found matching prefix: {FilePrefix}", filePrefix);
            }
        }

        private void StopAudio(string filePrefix)
        {
            if (_playingAudio.TryGetValue(filePrefix, out var outputDevice))
            {
                _logger.LogInformation("Stopping audio file: {FilePrefix}", filePrefix);
                outputDevice.Stop();
            }
            else
            {
                _logger.LogWarning("No audio playing for prefix: {FilePrefix}", filePrefix);
            }
        }

        private void FadeAudio(string filePrefix)
        {
            if (_playingAudio.TryGetValue(filePrefix, out var outputDevice))
            {
                _logger.LogInformation("Fading out audio file: {FilePrefix}", filePrefix);

                var fadeDuration = TimeSpan.FromSeconds(2);
                var audioReader = (AudioFileReader)outputDevice.GetType().GetProperty("WaveProvider").GetValue(outputDevice);

                Task.Run(() =>
                {
                    float initialVolume = audioReader.Volume;
                    for (float v = initialVolume; v >= 0; v -= 0.01f)
                    {
                        audioReader.Volume = v;
                        Thread.Sleep((int)(fadeDuration.TotalMilliseconds / 100));
                    }
                    outputDevice.Stop();
                });
            }
            else
            {
                _logger.LogWarning("No audio playing for prefix: {FilePrefix}", filePrefix);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            _logger.LogInformation("Disposing AudioProvider...");
            _cancellationTokenSource.Cancel();

            foreach (var outputDevice in _playingAudio.Values)
            {
                outputDevice.Stop();
                outputDevice.Dispose();
            }

            if (_mqttClient.IsConnected)
            {
                await _mqttClient.DisconnectAsync();
                _logger.LogInformation("Disconnected from MQTT broker.");
            }

            _mqttClient.Dispose();
            _cancellationTokenSource.Dispose();
            _disposed = true;
        }

        public class MqttOptions
        {
            public string BrokerAddress { get; set; }
            public int Port { get; set; }
            public string SoundEffectTopic { get; set; }
            public int QoS { get; set; }
        }
    }
}
