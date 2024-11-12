using System;
using System.IO.Ports;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Client;
using Microsoft.Extensions.Configuration;

class SerialPortInspector
{
    private static SerialPort serialPort;
    private static IMqttClient? mqttClient; // Nullable to initialize later

    private static string mqttBroker;
    private static int mqttPort;
    private static string mqttTopic;

    private static int serialBaudRate;
    private static int serialTimeoutSeconds;
    private static string? fixedComPort; // Nullable, as it's optional
    private static void PrintBanner()
    {
        Console.WriteLine("+---------------------------------------------------------------+");
        Console.WriteLine("|                                                               |");
        Console.WriteLine("|                    mqtt2tcode  Bridge  v1.1                   |");
        Console.WriteLine("|                                                  Noxy-RED     |");
        Console.WriteLine("|                                                  by Yeti      |");
        Console.WriteLine("+---------------------------------------------------------------+");
    }

    public static void ListAndSelectComPort()
    {

        // Print ASCII banner at the start
        PrintBanner();

        // Load configuration from appsettings.json
        LoadConfiguration();

        string selectedPort;

        // Check if a fixed COM port is set in the configuration
        if (!string.IsNullOrEmpty(fixedComPort))
        {
            selectedPort = fixedComPort;
            Console.WriteLine($"Using fixed COM port: {selectedPort}");
        }
        else
        {
            // Get the list of available COM ports using SerialPort.GetPortNames
            var comPorts = SerialPort.GetPortNames();

            // Check if any COM ports are found
            if (comPorts.Length == 0)
            {
                Console.WriteLine("No COM ports found!");
                return;
            }

            // List the available COM ports
            var availablePorts = new List<string>();

            Console.WriteLine("Available COM Ports:");
            foreach (var port in comPorts)
            {
                availablePorts.Add(port);
                Console.WriteLine($"{port}");
            }

            // Prompt the user to select a COM port
            Console.Write("Enter the COM port you want to connect to (e.g., COM1, COM20): ");
            selectedPort = Console.ReadLine();

            // Validate the selection
            if (!availablePorts.Contains(selectedPort))
            {
                Console.WriteLine("Invalid selection! Please enter a valid COM port.");
                return;
            }
        }

        // Try to connect to the selected COM port
        if (ConnectToSerialPort(selectedPort).Result)
        {
            Console.WriteLine("Connected to MQTT broker and serial port. Listening for messages...");
        }

        // Start the main loop to listen for messages and handle disconnections
        MainLoop(selectedPort);
    }

    private static async Task<bool> ConnectToSerialPort(string comPort)
    {
        try
        {
            serialPort = new SerialPort(comPort, serialBaudRate);  // Use baud rate from config
            serialPort.DtrEnable = true;
            serialPort.Open();
            Console.WriteLine($"Connected to {comPort}");

            // Add a delay to let the device initialize
            await Task.Delay(serialTimeoutSeconds * 1000);

            // Start the MQTT connection after the serial port is connected
            await StartMqttClient();

            return true; // Connection successful
        }
        catch (UnauthorizedAccessException)
        {
            Console.WriteLine($"{comPort} is already in use or access is denied.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error while connecting to {comPort}: {ex.Message}");
        }

        return false; // Connection failed
    }

    private static async Task StartMqttClient()
    {
        var factory = new MqttFactory();
        mqttClient = factory.CreateMqttClient();

        var options = new MqttClientOptionsBuilder()
            .WithClientId("mqtt2serial_bridge")
            .WithTcpServer(mqttBroker, mqttPort)  // Load MQTT settings from config
            .WithCleanSession()
            .Build();

        mqttClient.ConnectedAsync += async e =>
        {
            Console.WriteLine("Connected to MQTT broker.");
            // Subscribe to the topic
            await mqttClient.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic(mqttTopic).Build());
            Console.WriteLine($"Subscribed to topic: {mqttTopic}");
        };

        mqttClient.DisconnectedAsync += e =>
        {
            Console.WriteLine("Disconnected from MQTT broker.");
            return Task.CompletedTask;
        };

        // Handle incoming MQTT messages and forward them to the serial port
        mqttClient.ApplicationMessageReceivedAsync += e =>
        {
            var payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);
            Console.WriteLine($"Received MQTT message: {payload}");

            if (serialPort != null && serialPort.IsOpen)
            {
                // Forward the payload to the serial port
                serialPort.WriteLine(payload);
                Console.WriteLine($"Forwarded to {serialPort.PortName}: {payload}");
            }
            return Task.CompletedTask;
        };

        try
        {
            await mqttClient.ConnectAsync(options);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error connecting to MQTT broker: {ex.Message}");
        }
    }

    private static void MainLoop(string comPort)
    {
        while (true)
        {
            // Check if the serial port is still open
            if (serialPort != null && !serialPort.IsOpen)
            {
                Console.WriteLine($"The device on {comPort} was disconnected.");
                // Try to reconnect to the port
                while (!ReconnectToSerialPort(comPort))
                {
                    Console.WriteLine("Attempting to reconnect...");
                    Console.WriteLine("If you continue to experience problems, please restart the software and ensure the device is connected.");
                    Task.Delay(5000).Wait(); // Wait for 5 seconds before retrying
                }
                Console.WriteLine($"Reconnected to {comPort}");
            }

            // Keep the loop running
            Task.Delay(1000).Wait();
        }
    }

    private static bool ReconnectToSerialPort(string comPort)
    {
        try
        {
            serialPort.Close();
            serialPort = new SerialPort(comPort, serialBaudRate);
            serialPort.DtrEnable = true;
            serialPort.Open();
            Console.WriteLine($"Successfully reconnected to {comPort}");

            return true; // Reconnection successful
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error while reconnecting to {comPort}: {ex.Message}");
            return false; // Reconnection failed
        }
    }

    private static void LoadConfiguration()
    {
        // Load configuration from appsettings.json
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory) // Base path is the current directory
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        // Get the MQTT settings from the configuration file
        mqttBroker = configuration["MQTT:BrokerAddress"];
        mqttPort = int.Parse(configuration["MQTT:Port"]);
        mqttTopic = configuration["MQTT:Topic"];

        // Get the Serial settings from the configuration file
        serialBaudRate = int.Parse(configuration["SerialSettings:BaudRate"]);
        serialTimeoutSeconds = int.Parse(configuration["SerialSettings:TimeoutSeconds"]);
        fixedComPort = configuration["SerialSettings:ComPort"]; // Nullable

        Console.WriteLine($"Loaded MQTT settings: Broker={mqttBroker}, Port={mqttPort}, Topic={mqttTopic}");
        Console.WriteLine($"Loaded Serial settings: BaudRate={serialBaudRate}, TimeoutSeconds={serialTimeoutSeconds}");
    }
}
