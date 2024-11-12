using System;

class Program
{
    static void Main(string[] args)
    {
        // List the COM ports and ask the user to choose one to connect to
        SerialPortInspector.ListAndSelectComPort();

        // Wait for user input before closing the console
        Console.WriteLine("\nPress Enter to exit...");
        Console.ReadLine();
    }
}
