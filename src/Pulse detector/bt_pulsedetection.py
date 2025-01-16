import asyncio
import json
from bleak import BleakClient, BleakScanner
import paho.mqtt.client as mqtt

# Load configuration from config.json
def load_config(file="config.json"):
    try:
        with open(file, "r") as f:
            return json.load(f)
    except FileNotFoundError:
        print(f"Configuration file '{file}' not found.")
        exit(1)
    except json.JSONDecodeError:
        print(f"Error decoding JSON in '{file}'.")
        exit(1)

# Main function
async def main():
    # Load configurations
    config = load_config()
    mqtt_broker = config["mqtt_broker"]
    mqtt_port = config["mqtt_port"]
    mqtt_topic = config["mqtt_topic"]
    service_uuid = config["service_uuid"]
    characteristic_uuid = config["characteristic_uuid"]

    # Step 1: Scan and select device
    print("Scanning for BLE devices...")
    devices = await BleakScanner.discover()

    if not devices:
        print("No devices found. Please ensure your BLE device is advertising.")
        return

    print("\nAvailable devices:")
    for i, device in enumerate(devices):
        print(f"{i}: {device.name} ({device.address})")
    
    while True:
        try:
            selection = int(input("\nEnter the number of the device you want to connect to: "))
            if 0 <= selection < len(devices):
                target_device = devices[selection]
                break
        except ValueError:
            print("Invalid input. Please enter a valid number.")
        print("Invalid selection. Try again.")

    print(f"Connecting to {target_device.name} ({target_device.address})...")
    async with BleakClient(target_device.address) as client:
        print("Connected!")

        # MQTT Client Setup
        mqtt_client = mqtt.Client()
        mqtt_client.connect(mqtt_broker, mqtt_port, 60)

        # List GATT services and characteristics
        print("Listing GATT services and characteristics...")
        for service in client.services:
            print(f"Service UUID: {service.uuid}")
            if service.uuid == service_uuid:
                print(f"Found target service: {service.uuid}")
                for char in service.characteristics:
                    print(f"  Characteristic UUID: {char.uuid}")

        # Callback for heart rate notifications
        def notification_handler(sender, data):
            flags = data[0]
            heart_rate = data[1] if (flags & 0x01) == 0 else int.from_bytes(data[1:3], byteorder="little")
            print(f"Heart Rate: {heart_rate} BPM")
            
            # Publish to MQTT if valid heart rate
            if 60 <= heart_rate <= 170:
                mqtt_client.publish(mqtt_topic, heart_rate)
                print(f"Published to MQTT: {heart_rate} BPM")

        # Subscribe to notifications for the specific characteristic
        print("Subscribing to Heart Rate Measurement notifications...")
        if service_uuid and characteristic_uuid:
            await client.start_notify(characteristic_uuid, notification_handler)
        else:
            print("Service UUID or Characteristic UUID not specified in config.json.")
            return

        print("Monitoring heart rate... Press Ctrl+C to exit.")
        try:
            await asyncio.sleep(float("inf"))  # Run indefinitely
        except KeyboardInterrupt:
            print("\nExiting program...")

        await client.stop_notify(characteristic_uuid)

# Run the script
asyncio.run(main())
