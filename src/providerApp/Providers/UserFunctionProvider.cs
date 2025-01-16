using Microsoft.Extensions.Logging;
using Voxta.Model.Shared;
using Voxta.Model.WebsocketMessages.ClientMessages;
using Voxta.Model.WebsocketMessages.ServerMessages;
using Voxta.Providers.Host;

namespace Voxta.SampleProviderApp.Providers;

// This example shows how to create commands that the AI can call.
// This will slow down the AI since there will be an LLM run before generating text.
public class UserFunctionProvider(
    IRemoteChatSession session,
    ILogger<UserFunctionProvider> logger
) : ProviderBase(session, logger)
{
    protected override async Task OnStartAsync()
    {
        await base.OnStartAsync();
        
        // Register our action
        Send(new ClientUpdateContextMessage
        {
            SessionId = SessionId,
            ContextKey = "SampleUserFunctions",
            Actions = 
            [
                             
            ]
        });
        
        /* Act when an action is called
        HandleMessage<ServerActionMessage>(message =>
        {
            // We only want to handle user actions
            if (message.Role != ChatMessageRole.User) return;
            
            switch (message.Value)
            {
                case "run_diagnostic":
                    //TODO: Run the diagnostic
                    Logger.LogInformation("Running the self-diagnostic");
                    
                    Send(new ClientSendMessage
                    {
                        SessionId = SessionId,
                        // We want to avoid a loop!
                        DoUserActionInference = false,
                        CharacterResponsePrefix = "[The requested self-diagnostic has completed successfully, everything is in order]"
                    });
                    break;
                case "play_music":
                    if(!message.TryGetArgument("music_search_query", out var query))
                        query = "anything";
                    //TODO: Play the song
                    Logger.LogInformation("Playing music. Search query: '{Query}", query);
                    
                    Send(new ClientSendMessage
                    {
                        SessionId = SessionId,
                        // We want to avoid a loop!
                        DoUserActionInference = false,
                        Text = $"/note As requested, the song \"{query}\" starts playing."
                    });
                    break;
            }
        });*/
    }
}
