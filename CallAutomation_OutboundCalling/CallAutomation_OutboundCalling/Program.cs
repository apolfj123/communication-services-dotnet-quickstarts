using Azure.Communication;
using Azure.Communication.CallAutomation;
using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Azure.Messaging.EventGrid.SystemEvents;
using Azure.Identity;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Your ACS resource connection string
var acsConnectionString = "endpoint=https://voicecallsresource.unitedstates.communication.azure.com/;accesskey=7ti8DW9LzHA0rm+mgcyt1cHR2LjYyq1yVI+Hmx3o3A4c5WBib5ZQ1fZL/nRp6FccIx5emB/maWhuQmFKXgOVwg==";

// Your ACS resource phone number will act as source number to start outbound call
var acsPhonenumber = "+18337406500"; // <<< purchased phone number that can be within Azure under VoiceCallResource under the sub menu called Phone Numbers.  You bought this one for 2 dollars.

//"+18332522649" <<< expired free phone number.., its no longer needed we should delete this comment;

// Target phone number you want to receive the call.
var targetPhonenumber = "+15038398829";

// Base url of the app
var callbackUriHost = "https://3c4sw5b5.usw2.devtunnels.ms:8080"; //"https://9l8c8q7t.usw2.devtunnels.ms:8080";

// Your cognitive service endpoint
var cognitiveServiceEndpoint = "https://mobullzai.cognitiveservices.azure.com/";

// text to play
const string SpeechToTextVoice = "en-US-NancyNeural";
const string MainMenu =
    """ 
    Hello this is Contoso Bank, we�re calling in regard to your appointment tomorrow 
    at 9am to open a new account. Please say confirm or press 1 if this time is still suitable for you or say cancel or press 2  
    if you would like to cancel this appointment.
    """;
const string ConfirmedText = "Thank you for confirming your appointment tomorrow at 9am, we look forward to meeting with you.";
const string CancelText = """
Your appointment tomorrow at 9am has been cancelled. Please call the bank directly 
if you would like to rebook for another date and time.
""";
const string CustomerQueryTimeout = "I�m sorry I didn�t receive a response, please try again.";
const string NoResponse = "I didn't receive an input, we will go ahead and confirm your appointment. Goodbye";
const string InvalidAudio = "I�m sorry, I didn�t understand your response, please try again.";
const string ConfirmChoiceLabel = "Confirm";
const string RetryContext = "retry";


var options = new DefaultAzureCredentialOptions {
    ManagedIdentityClientId = "d71fd077-68e8-44bb-b103-f4bc7e29204b"
};

var credential = new DefaultAzureCredential(options);

var uri = new Uri("https://voicecallsresource.unitedstates.communication.azure.com/");

CallAutomationClient callAutomationClient = new CallAutomationClient(uri, credential, null);

var app = builder.Build();

app.MapPost("/outboundCall", async (ILogger<Program> logger) =>
{
    PhoneNumberIdentifier target = new PhoneNumberIdentifier(targetPhonenumber);
    PhoneNumberIdentifier caller = new PhoneNumberIdentifier(acsPhonenumber);
    var callbackUri = new Uri(new Uri(callbackUriHost), "/api/callbacks");
    CallInvite callInvite = new CallInvite(target, caller);
    var createCallOptions = new CreateCallOptions(callInvite, callbackUri)
    {
        CallIntelligenceOptions = new CallIntelligenceOptions() { CognitiveServicesEndpoint = new Uri(cognitiveServiceEndpoint) }
    };

    CreateCallResult createCallResult = await callAutomationClient.CreateCallAsync(createCallOptions);

    logger.LogInformation($"Created call with connection id: {createCallResult.CallConnectionProperties.CallConnectionId}");
});

app.MapPost("/api/callbacks", async (CloudEvent[] cloudEvents, ILogger<Program> logger) =>
{
    foreach (var cloudEvent in cloudEvents)
    {
        CallAutomationEventBase parsedEvent = CallAutomationEventParser.Parse(cloudEvent);
        logger.LogInformation(
                    "Received call event: {type}, callConnectionID: {connId}, serverCallId: {serverId}",
                    parsedEvent.GetType(),
                    parsedEvent.CallConnectionId,
                    parsedEvent.ServerCallId);

        var callConnection = callAutomationClient.GetCallConnection(parsedEvent.CallConnectionId);
        var callMedia = callConnection.GetCallMedia();

        if (parsedEvent is CallConnected callConnected)
        {   
            logger.LogInformation("Fetching recognize options...");

            // prepare recognize tones
            var recognizeOptions = GetMediaRecognizeChoiceOptions(MainMenu, targetPhonenumber);

            logger.LogInformation("Recognizing options...");

            // Send request to recognize tones
            await callMedia.StartRecognizingAsync(recognizeOptions);
        }
        else if (parsedEvent is RecognizeCompleted recognizeCompleted)
        {
            var choiceResult = recognizeCompleted.RecognizeResult as ChoiceResult;
            var labelDetected = choiceResult?.Label;
            var phraseDetected = choiceResult?.RecognizedPhrase;
            // If choice is detected by phrase, choiceResult.RecognizedPhrase will have the phrase detected, 
            // If choice is detected using dtmf tone, phrase will be null 
            logger.LogInformation("Recognize completed succesfully, labelDetected={labelDetected}, phraseDetected={phraseDetected}", labelDetected, phraseDetected);
            var textToPlay = labelDetected.Equals(ConfirmChoiceLabel, StringComparison.OrdinalIgnoreCase) ? ConfirmedText : CancelText;

            await HandlePlayAsync(callMedia, textToPlay);
        }
        else if (parsedEvent is RecognizeFailed { OperationContext: RetryContext })
        {
            logger.LogError("Encountered error during recognize, operationContext={context}", RetryContext);
            await HandlePlayAsync(callMedia, NoResponse);
        }
        else if (parsedEvent is RecognizeFailed recognizeFailedEvent)
        {
            var resultInformation = recognizeFailedEvent.ResultInformation;
            logger.LogError("Encountered error during recognize, message={msg}, code={code}, subCode={subCode}",
                resultInformation?.Message,
                resultInformation?.Code,
                resultInformation?.SubCode);

            var reasonCode = recognizeFailedEvent.ReasonCode;
            string replyText = reasonCode switch
            {
                var _ when reasonCode.Equals(MediaEventReasonCode.RecognizePlayPromptFailed) ||
                reasonCode.Equals(MediaEventReasonCode.RecognizeInitialSilenceTimedOut) => CustomerQueryTimeout,
                var _ when reasonCode.Equals(MediaEventReasonCode.RecognizeIncorrectToneDetected) => InvalidAudio,
                _ => CustomerQueryTimeout,
            };

            var recognizeOptions = GetMediaRecognizeChoiceOptions(replyText, targetPhonenumber, RetryContext);
            await callMedia.StartRecognizingAsync(recognizeOptions);
        }
        else if ((parsedEvent is PlayCompleted) || (parsedEvent is PlayFailed))
        {
            logger.LogInformation($"Terminating call.");
            await callConnection.HangUpAsync(true);
        }
    }
    return Results.Ok();
}).Produces(StatusCodes.Status200OK);

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

CallMediaRecognizeChoiceOptions GetMediaRecognizeChoiceOptions(string content, string targetParticipant, string context = "")
{
    var playSource = new TextSource(content) { VoiceName = SpeechToTextVoice };

    var recognizeOptions =
        new CallMediaRecognizeChoiceOptions(targetParticipant: new PhoneNumberIdentifier(targetParticipant), GetChoices())
        {
            InterruptCallMediaOperation = false,
            InterruptPrompt = false,
            InitialSilenceTimeout = TimeSpan.FromSeconds(10),
            Prompt = playSource,
            OperationContext = context
        };

    return recognizeOptions;
}

List<RecognitionChoice> GetChoices()
{
    return new List<RecognitionChoice> {
            new RecognitionChoice("Confirm", new List<string> {
                "Confirm",
                "First",
                "One"
            }) {
                Tone = DtmfTone.One
            },
            new RecognitionChoice("Cancel", new List<string> {
                "Cancel",
                "Second",
                "Two"
            }) {
                Tone = DtmfTone.Two
            }
        };
}

async Task HandlePlayAsync(CallMedia callConnectionMedia, string text)
{
    Console.WriteLine($"Playing text to customer: {text}.");

    // Play goodbye message
    var GoodbyePlaySource = new TextSource(text)
    {
        VoiceName = "en-US-NancyNeural"
    };

    await callConnectionMedia.PlayToAllAsync(GoodbyePlaySource);
}

app.Run();
