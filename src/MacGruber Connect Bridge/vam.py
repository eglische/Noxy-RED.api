import socket
import paho.mqtt.client as mqtt
import re
import threading

# TCP Server settings
TCP_PORT = 39340
TCP_HOST = "0.0.0.0"  # Bind to all interfaces
BUFFER_SIZE = 256

# MQTT settings
MQTT_BROKER = "localhost"
MQTT_PORT = 1883
MQTT_TOPIC = "/noxyred/vam"

# Regular expression for matching "generic$$$"
GENERIC_PATTERN = re.compile(r"^generic\d{3}")

# MQTT Client setup
mqtt_client = mqtt.Client()

def on_mqtt_connect(client, userdata, flags, rc):
    print(f"Connected to MQTT broker with result code {rc}")
    # Subscribe to the topic
    client.subscribe(MQTT_TOPIC)

def on_mqtt_message(client, userdata, msg):
    print(f"Received MQTT message from topic {msg.topic}: {msg.payload.decode()}")

# Initialize MQTT connection
mqtt_client.on_connect = on_mqtt_connect
mqtt_client.on_message = on_mqtt_message
mqtt_client.connect(MQTT_BROKER, MQTT_PORT, 60)

# Start MQTT client loop in a separate thread
mqtt_client.loop_start()

def handle_client_connection(client_socket, client_address):
    print(f"New connection from {client_address}")
    
    try:
        while True:
            # Receive data from client
            data = client_socket.recv(BUFFER_SIZE)
            if not data:
                break

            message = data.decode("utf-8").strip()
            print(f"Received TCP message: {message}")

            # Forward to MQTT if it matches "generic$$$"
            if GENERIC_PATTERN.match(message):
                mqtt_client.publish(MQTT_TOPIC, message)
                print(f"Forwarded to MQTT: {message}")
            else:
                print(f"Ignored message: {message}")

    except ConnectionResetError:
        print(f"Connection reset by {client_address}")

    finally:
        # Close client connection
        client_socket.close()
        print(f"Connection closed: {client_address}")

def start_tcp_server():
    server_socket = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server_socket.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server_socket.bind((TCP_HOST, TCP_PORT))
    server_socket.listen(5)
    print(f"TCP server started on {TCP_HOST}:{TCP_PORT}")

    while True:
        # Accept new client connection
        client_socket, client_address = server_socket.accept()
        # Handle client connection in a new thread
        client_handler = threading.Thread(target=handle_client_connection, args=(client_socket, client_address))
        client_handler.start()

if __name__ == "__main__":
    try:
        # Run the TCP server
        start_tcp_server()
    except KeyboardInterrupt:
        print("Server shutting down...")
    finally:
        mqtt_client.loop_stop()
        mqtt_client.disconnect()
