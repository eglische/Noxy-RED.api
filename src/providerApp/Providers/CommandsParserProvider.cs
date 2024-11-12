using Microsoft.Extensions.Logging;
using Voxta.Providers.Host;
using Voxta.Model.WebsocketMessages.ClientMessages;

namespace Voxta.SampleProviderApp.Providers
{
    public class CommandsParserProvider : ProviderBase
    {
        private readonly ILogger<CommandsParserProvider> _logger;

        public CommandsParserProvider(
            IRemoteChatSession session,
            ILogger<CommandsParserProvider> logger
        ) : base(session, logger)
        {
            _logger = logger;
        }

        protected override async Task OnStartAsync()
        {
            await base.OnStartAsync();

            // Log the initialization of CommandsParserProvider
            _logger.LogInformation("CommandsParserProvider started successfully.");

            // Add any custom initialization logic here, such as registering commands or events
        }

        public async Task ParseCommandAsync(string command)
        {
            // Log the command received
            _logger.LogInformation("Received command: {Command}", command);

            // Parse the command and send an appropriate response to the chat session
            switch (command.ToLower())
            {
                case "start":
                    // Send a message to start the process
                    SendMessageToChat(new ClientSendMessage
                    {
                        SessionId = SessionId,
                        Text = "Starting the process."
                    });
                    break;

                case "stop":
                    // Send a message to stop the process
                    SendMessageToChat(new ClientSendMessage
                    {
                        SessionId = SessionId,
                        Text = "Stopping the process."
                    });
                    break;

                // Add more commands and their corresponding logic here
                default:
                    _logger.LogWarning("Unknown command: {Command}", command);
                    break;
            }
            await Task.CompletedTask; // Add this to fulfill the async requirement
        }

        private void SendMessageToChat(ClientSendMessage message)
        {
            // Example logic to send a message to the chat system or session
            base.SendWhenFree(message); // Assuming base class has a SendWhenFree method
            _logger.LogInformation("Sent message to chat: {Message}", message.Text);
        }
    }
}
