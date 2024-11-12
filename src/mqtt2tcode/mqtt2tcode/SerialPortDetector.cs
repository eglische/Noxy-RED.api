using System;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Client;

class SerialPortDetector
{
    private static IMqttClient mqttClient;

    public static void Start()
    {
        string selectedPort = null;
        int baudRate = 115200;
        int timeoutSeconds = 5;

        // Loop until a valid connection is established
        while (true)
        {
            selectedPort = PromptForComPort();

            // Try to connect to the serial port
            if (ConnectToPort(selectedPort, baudRate, timeoutSeconds))
            {
                Console.WriteLine($"Successfully connected to {selectedPort}.");
                StartMqttClient(); // Start MQTT after initial connection
                SendInitialCommands(); // Send initial serial commands after connection
                break; // Break the loop when a valid connection is made
            }
        }

        // Start the main loop to handle potential disconnections and automatic reconnections
        MainLoop(selectedPort, baudRate, timeoutSeconds);
    }

    public static string PromptForComPort()
    {
        var comPorts = SerialPort.GetPortNames();

        if (comPorts.Length == 0)
        {
            Console.WriteLine("No COM ports found!");
            return null;  // No COM ports found
        }

        Console.WriteLine("Available COM Ports: " + string.Join(", ", comPorts));

        while (true)
        {
            // Prompt the user to select a COM port
            Console.Write("Enter the COM port you want to connect to (e.g., COM1, COM20): ");
            string selectedPort = Console.ReadLine();

            // Validate the selection
            if (Array.Exists(comPorts, port => port.Equals(selectedPort, StringComparison.OrdinalIgnoreCase)))
            {
                return selectedPort; // Valid COM port
            }
            else
            {
                Console.WriteLine("Invalid selection! Please enter a valid COM port.");
            }
        }
    }

    public static bool ConnectToPort(string port, int baudRate, int timeoutSeconds)
    {
        if (string.IsNullOrEmpty(port))
        {
            Console.WriteLine("No valid port was selected.");
            return false; // No valid port selected
        }

        try
        {
            using (var serialPort = new SerialPort(port, baudRate))
            {
                serialPort.Open();
                Console.WriteLine($"Connected to {port} at baud rate {baudRate}");

                // Wait for 5 seconds (simulate waiting for the device to be ready)
                Thread.Sleep(5000);
                Console.WriteLine($"Device assumed ready on {port} with baud rate {baudRate}");

                SendInitialCommands(); // Send commands after connection
                return true; // Successfully connected
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine($"Unauthorized access to {port}: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error connecting to {port}: {ex.Message}");
        }

        return false; // Connection failed
    }

    public static void MainLoop(string comPort, int baudRate, int timeoutSeconds)
    {
        while (true)
        {
            try
            {
                // Open the serial port and keep it open until a disconnection or error occurs
                using (var serialPort = new SerialPort(comPort, baudRate))
                {
                    serialPort.Open();
                    Console.WriteLine($"Connected to {comPort}. Monitoring...");

                    // Send initial commands on reconnect
                    SendInitialCommands();

                    // Reinitialize MQTT after reconnect
                    StartMqttClient();

                    // Keep the serial port open indefinitely
                    while (serialPort.IsOpen)
                    {
                        // Simulate monitoring the device
                        Thread.Sleep(1000);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Lost connection to {comPort}. Error: {ex.Message}");
            }

            // If we reach here, the connection was lost, so try to reconnect
            Console.WriteLine("Attempting to reconnect...");
            Thread.Sleep(1000); // 1-second cooldown before retrying
        }
    }

    // Method to send initial commands to the serial port
    public static void SendInitialCommands()
    {
        string[] commands = { "L050", "R050", "L150", "R150", "L250", "R250" };

        foreach (string command in commands)
        {
            // Write the command to the serial port
            Console.WriteLine($"Sending command: {command}");
            // Replace this with actual serial port write
            // Example: serialPort.WriteLine(command);
            Thread.Sleep(100); // 100 ms delay between commands
        }
    }

    public static void StartMqttClient()
    {
        if (mqttClient != null && mqttClient.IsConnected)
        {
            return; // MQTT client is already running
        }

        var factory = new MqttFactory();
        mqttClient = factory.CreateMqttClient();

        var options = new MqttClientOptionsBuilder()
            .WithClientId("mqtt2serial_bridge")
            .WithTcpServer("127.0.0.1", 1883)  // Adjust the MQTT server and port accordingly
            .WithCleanSession()
            .Build();

        mqttClient.ConnectedAsync += async e =>
        {
            Console.WriteLine("Connected to MQTT broker.");
            // Subscribe to the topic
            await mqttClient.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic("your/topic").Build());
            Console.WriteLine($"Subscribed to topic: your/topic");
        };

        mqttClient.DisconnectedAsync += e =>
        {
            Console.WriteLine("Disconnected from MQTT broker.");
            return Task.CompletedTask;
        };

        // Handle incoming MQTT messages and forward them to the serial port
        mqttClient.ApplicationMessageReceivedAsync += e =>
        {
            var payload = System.Text.Encoding.UTF8.GetString(e.ApplicationMessage.Payload);
            Console.WriteLine($"Received MQTT message: {payload}");

            // Send the MQTT payload to the serial port (placeholder)
            Console.WriteLine($"Forwarding to serial: {payload}");
            // Replace this with actual serial port write
            // Example: serialPort.WriteLine(payload);

            return Task.CompletedTask;
        };

        try
        {
            mqttClient.ConnectAsync(options).Wait();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error connecting to MQTT broker: {ex.Message}");
        }
    }
}
