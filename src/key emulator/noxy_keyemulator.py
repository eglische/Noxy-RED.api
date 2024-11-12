import paho.mqtt.client as mqtt
from pynput.keyboard import Controller, KeyCode
import time
import json

# Initialize the keyboard controller
keyboard = Controller()

# Load configuration from a JSON file
def load_config():
    with open('config.json', 'r') as config_file:
        return json.load(config_file)

# Load the configuration
config = load_config()

# MQTT settings from config.json
MQTT_BROKER = config["mqtt"]["broker"]
MQTT_PORT = config["mqtt"]["port"]
MQTT_INPUT_TOPIC = config["mqtt"]["input_topic"]
MQTT_QOS = config["mqtt"]["qos"]

# Function to handle keypress combinations using VK codes
def simulate_keystroke(vk_sequence):
    try:
        keys_to_press = []
        # Convert the VK sequence to actual Key objects
        for vk_code in vk_sequence:
            vk_code = int(vk_code)  # Ensure it's an integer
            key = KeyCode.from_vk(vk_code)
            keys_to_press.append(key)
        
        # Press all keys together (supports combinations up to 4 keys)
        for key in keys_to_press:
            keyboard.press(key)
        
        # Release the keys in reverse order
        for key in reversed(keys_to_press):
            keyboard.release(key)

        print(f"Simulated keystroke: {vk_sequence}")
    
    except Exception as e:
        print(f"Error simulating keystroke: {e}")

# Callback function for when the client receives a message from the broker
def on_message(client, userdata, msg):
    message = msg.payload.decode('utf-8')
    print(f"Received message on topic {msg.topic}: {message}")
    
    try:
        # Parse the message (expecting a list of VK codes, e.g., [0x11, 0x12, 0x48])
        vk_sequence = eval(message)  # Expecting a message like [0x11, 0x12, 0x48]
        if isinstance(vk_sequence, list) and len(vk_sequence) <= 4:
            simulate_keystroke(vk_sequence)
        else:
            print("Invalid key sequence format or too many keys")
    
    except Exception as e:
        print(f"Error processing message: {e}")

# Callback function for when the client connects to the broker
def on_connect(client, userdata, flags, rc):
    if rc == 0:
        print("Connected to MQTT broker")
        # Subscribe to the input topic
        client.subscribe(MQTT_INPUT_TOPIC, MQTT_QOS)
        print(f"Subscribed to topic: {MQTT_INPUT_TOPIC}")
    else:
        print(f"Failed to connect, return code {rc}")

# Callback function for when the client subscribes to the topic
def on_subscribe(client, userdata, mid, granted_qos):
    print(f"Subscribed with QoS {granted_qos[0]}")

# Set up the MQTT client
client = mqtt.Client()

# Assign the callback functions
client.on_connect = on_connect
client.on_message = on_message
client.on_subscribe = on_subscribe

# Connect to the broker
client.connect(MQTT_BROKER, MQTT_PORT, 60)

# Start the loop to process MQTT messages
client.loop_start()

print("Listening for MQTT messages...")

# Keep the script running
try:
    while True:
        time.sleep(1)  # Keep the script alive
except KeyboardInterrupt:
    print("Disconnecting from MQTT broker...")
    client.loop_stop()
    client.disconnect()
    print("Disconnected.")
