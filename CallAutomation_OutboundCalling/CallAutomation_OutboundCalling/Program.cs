using Azure.Communication;
using Azure.Communication.CallAutomation;
using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Azure.Messaging.EventGrid.SystemEvents;
using Azure.Identity;
using System;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();


// Your ACS resource connection string
//var acsConnectionString = "endpoint=https://voicecallsresource.unitedstates.communication.azure.com/;accesskey=7ti8DW9LzHA0rm+mgcyt1cHR2LjYyq1yVI+Hmx3o3A4c5WBib5ZQ1fZL/nRp6FccIx5emB/maWhuQmFKXgOVwg==";

// Your ACS resource phone number will act as source number to start outbound call
var acsPhonenumber = "+18337406500"; // <<< purchased phone number that can be within Azure under VoiceCallResource under the sub menu called Phone Numbers.  You bought this one for 2 dollars.

//"+18332522649" <<< expired free phone number.., its no longer needed we should delete this comment;

// Target phone number you want to receive the call.
var targetPhonenumber = "+15038398829"; //"+15034099763";

// Base url of the app
var callbackUriHost = "https://40zsv7kt.usw2.devtunnels.ms:8080"; //Environment.GetEnvironmentVariable("DEV_TUNNEL_URL"); 

//Console.WriteLine("Dev Tunnel URL is: " + callbackUriHost);

// Your cognitive service endpoint
var cognitiveServiceEndpoint = "https://mobullzai.cognitiveservices.azure.com/";

// text to play
const string SpeechToTextVoice = "en-US-NancyNeural";
const string MainMenu =
    """ 
    Hello this is Jeff's assistant Friday.  Please know that I do not have the technology to be interrupted.
    As Jeff is your son he would like to know what your thoughts are on his first version of artificial intelligence?
    If you think that it is fun, then please say the phrase Fun.
    The second choice is to choose that it isn't very valuable, and in that case please say Whatever. 
    """;
const string ConfirmedText = "Jeff thought you might think calling you with an artficial intelligence that could understand a single word would be fun! Jeff will have me, Friday, call you twice and on the second time you should choose the other options you didn't prior";
const string CancelText = """
You must understand the following was accomplished. 
First he got automated phone calls to work.
Second he accomplished converting text to a emotionally intelligent voice.
Jeff also wants you to see that I can understand varying single phrases and change my responses.
If this was your first phone call then I will call you again.
""";
const string CustomerQueryTimeout = "I�m sorry I didn�t receive a response, please try again.";
const string NoResponse = "I didn't receive an input, we will go ahead and confirm your bathing suit cover up choice again later. Goodbye";
const string InvalidAudio = "I�m sorry, I didn�t understand your response, please try again.";
const string ConfirmChoiceLabel = "Confirm";
const string RetryContext = "retry";


var options = new DefaultAzureCredentialOptions
{
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
    //var jeff = Environment.GetEnvironmentVariable("").To
    Console.WriteLine("The environment variable for DEV_TUNNEL_URL was: " + Environment.GetEnvironmentVariable("DEV_TUNNEL_URL"));
    var callbackUri = new Uri(new Uri(callbackUriHost), "/api/callbacks");
    CallInvite callInvite = new CallInvite(target, caller);
    Console.WriteLine("The variable must be set with this url to work.., here is the callbackUri variable value: " + callbackUri);
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

            logger.LogInformation("The current length of the MainMenu text is: " + MainMenu.Length.ToString());

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
                "Fun",
                "First",
                "One"
            }) {
                Tone = DtmfTone.One
            },
            new RecognitionChoice("Cancel", new List<string> {
                "Whatever",
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
