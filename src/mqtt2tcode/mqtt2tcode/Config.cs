using System;
using System.IO;
using Newtonsoft.Json.Linq;

class Config
{
    public string BrokerIp { get; set; }
    public int BrokerPort { get; set; }
    public string Topic { get; set; }
    public int BaudRate { get; set; } = 115200; // Default to 115200
    public int TimeoutSeconds { get; set; } = 3; // Default to 3 seconds
    public string ComPort { get; set; } = null; // Optional COM port, default to null

    public static Config LoadConfig(string filePath)
    {
        var json = File.ReadAllText(filePath);
        JObject parsed = JObject.Parse(json);

        var config = new Config
        {
            BrokerIp = parsed["MqttSettings"]["BrokerIp"].ToString(),
            BrokerPort = int.Parse(parsed["MqttSettings"]["BrokerPort"].ToString()),
            Topic = parsed["MqttSettings"]["Topic"].ToString(),
            BaudRate = parsed["SerialSettings"]["BaudRate"]?.ToObject<int>() ?? 115200,
            TimeoutSeconds = parsed["SerialSettings"]["TimeoutSeconds"]?.ToObject<int>() ?? 3,
            ComPort = parsed["SerialSettings"]["ComPort"]?.ToString() // Handle optional COM port
        };

        return config;
    }
}
