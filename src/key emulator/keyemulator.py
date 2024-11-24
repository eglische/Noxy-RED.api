import paho.mqtt.client as mqtt
from pynput.keyboard import Controller, Key, Listener, KeyCode
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
MQTT_OUTPUT_TOPIC = config["mqtt"]["output_topic"]
MQTT_QOS = config["mqtt"]["qos"]

# Set up the MQTT client with version 5 (latest)
client = mqtt.Client(protocol=mqtt.MQTTv5)

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
        message = message.strip()  # Remove leading/trailing whitespace
        if message.startswith('[') and message.endswith(']'):
            message = message[1:-1]  # Remove the square brackets
        vk_sequence = [int(code.strip(), 16) for code in message.split(',')]  # Expecting a message like "0x11,0x12,0x48"
        if isinstance(vk_sequence, list) and len(vk_sequence) <= 4:
            simulate_keystroke(vk_sequence)
        else:
            print("Invalid key sequence format or too many keys")
    
    except Exception as e:
        print(f"Error processing message: {e}")

# Callback function for when the client connects to the broker
def on_connect(client, userdata, flags, rc, properties=None):
    if rc == 0:
        print("Connected to MQTT broker")
        # Subscribe to the input topic
        client.subscribe(MQTT_INPUT_TOPIC, MQTT_QOS)
        print(f"Subscribed to topic: {MQTT_INPUT_TOPIC}")
    else:
        print(f"Failed to connect, return code {rc}")

# Callback function for when the client subscribes to the topic
def on_subscribe(client, userdata, mid, granted_qos, properties=None):
    print(f"Subscribed with QoS {granted_qos[0]}")

# Assign the callback functions
client.on_connect = on_connect
client.on_message = on_message
client.on_subscribe = on_subscribe

# Connect to the broker
try:
    client.connect(MQTT_BROKER, MQTT_PORT, 60)
except ConnectionRefusedError as e:
    print(f"Connection failed: {e}")
    exit(1)

# Start the loop to process MQTT messages
client.loop_start()

print("Listening for MQTT messages...")

# Function to publish detected keystrokes to the output topic
def publish_keystroke(vk_sequence):
    try:
        message = ','.join([f"0x{vk:02X}" for vk in vk_sequence])
        client.publish(MQTT_OUTPUT_TOPIC, message, qos=MQTT_QOS)
        print(f"Published keystroke sequence {message} to topic {MQTT_OUTPUT_TOPIC}")
    except Exception as e:
        print(f"Error publishing keystroke: {e}")

# Function to handle key press events
def on_press(key):
    try:
        vk_sequence = []
        if key in [Key.ctrl, Key.shift, Key.alt]:
            vk_sequence.append(key.value.vk)
        elif hasattr(key, 'vk'):
            vk_sequence.append(key.vk)
        
        # If any of the key combinations match, publish the sequence
        if len(vk_sequence) > 0:
            publish_keystroke(vk_sequence)
    except AttributeError:
        print(f"Special key {key} pressed")
    except Exception as e:
        print(f"Error handling key press: {e}")

# Start listening to keystrokes
print("Starting to listen for keystrokes...")
with Listener(on_press=on_press) as listener:
    try:
        while True:
            time.sleep(1)  # Keep the script alive
    except KeyboardInterrupt:
        print("Disconnecting from MQTT broker...")
        client.loop_stop()
        client.disconnect()
        print("Disconnected.")
        listener.stop()
