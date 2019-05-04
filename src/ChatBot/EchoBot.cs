// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChatBot.Models;
using ChatBot.Services;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Bot.Builder.AI.Luis;
using Microsoft.Bot.Builder.Dialogs;

namespace ChatBot
{
    /// <summary>
    /// Represents a bot that processes incoming activities.
    /// For each user interaction, an instance of this class is created and the OnTurnAsync method is called.
    /// This is a Transient lifetime service.  Transient lifetime services are created
    /// each time they're requested. For each Activity received, a new instance of this
    /// class is created. Objects that are expensive to construct, or have a lifetime
    /// beyond the single turn, should be carefully managed.
    /// For example, the <see cref="MemoryStorage"/> object and associated
    /// <see cref="IStatePropertyAccessor{T}"/> object are created with a singleton lifetime.
    /// </summary>
    /// <seealso cref="https://docs.microsoft.com/en-us/aspnet/core/fundamentals/dependency-injection?view=aspnetcore-2.1"/>
    public class EchoBot : IBot
    {

        private readonly TextToSpeechService _ttsService;
        // Conversation steps
        public const string GatherInfo = "gatherInfo";
        public const string TimePrompt = "timePrompt";
        public const string AmountPeoplePrompt = "amountPeoplePrompt";
        public const string NamePrompt = "namePrompt";
        public const string ConfirmationPrompt = "confirmationPrompt";

        private readonly DialogSet _dialogs;
        private readonly EchoBotAccessors _accessors;
        private readonly ILogger _logger;
        
        /// <summary>
        /// Services configured from the ".bot" file.
        /// </summary>
        private readonly BotServices _services;

        /// <summary>
        /// Initializes a new instance of the <see cref="EchoBot"/> class.
        /// </summary>
        /// <param name="accessors">A class containing <see cref="IStatePropertyAccessor{T}"/> used to manage state.</param>
        /// <param name="loggerFactory">A <see cref="ILoggerFactory"/> that is hooked to the Azure App Service provider.</param>
        /// <seealso cref="https://docs.microsoft.com/en-us/aspnet/core/fundamentals/logging/?view=aspnetcore-2.1#windows-eventlog-provider"/>
        public EchoBot(BotServices services, EchoBotAccessors accessors, ILoggerFactory loggerFactory, IOptions<MySettings> config)
        {
            if (loggerFactory == null)
            {
                throw new System.ArgumentNullException(nameof(loggerFactory));
            }

            _logger = loggerFactory.CreateLogger<EchoBot>();
            _logger.LogTrace("EchoBot turn start.");
            _accessors = accessors ?? throw new System.ArgumentNullException(nameof(accessors));
            _services = services ?? throw new System.ArgumentNullException(nameof(services));
            if (!_services.LuisServices.ContainsKey(BotConstants.LuisKey))
            {
                throw new System.ArgumentException($"Invalid configuration. Please check your '.bot' file for a LUIS service named '{BotConstants.LuisKey}'.");
            }
            _dialogs = new DialogSet(accessors.ConversationDialogState);
            // This array defines how the Waterfall will execute.
            var waterfallSteps = new WaterfallStep[]
            {
    TimeStepAsync,
    AmountPeopleStepAsync,
    NameStepAsync,
    ConfirmationStepAsync,
    FinalStepAsync,
            };

            // Add named dialogs to the DialogSet. These names are saved in the dialog state.
            _dialogs.Add(new WaterfallDialog(GatherInfo, waterfallSteps));
            _dialogs.Add(new TextPrompt("name"));
            _dialogs.Add(new NumberPrompt<int>("age"));
            _dialogs.Add(new ConfirmPrompt("confirm"));

            _dialogs.Add(new TextPrompt(TimePrompt));
            _dialogs.Add(new TextPrompt(AmountPeoplePrompt, AmountPeopleValidatorAsync));
            _dialogs.Add(new TextPrompt(NamePrompt));
            _dialogs.Add(new ConfirmPrompt(ConfirmationPrompt));
            _ttsService = new TextToSpeechService(config.Value.VoiceFontName, config.Value.VoiceFontLanguage);
        }

        /// <summary>
        /// Every conversation turn for our Echo Bot will call this method.
        /// There are no dialogs used, since it's "single turn" processing, meaning a single
        /// request and response.
        /// </summary>
        /// <param name="turnContext">A <see cref="ITurnContext"/> containing all the data needed
        /// for processing this conversation turn. </param>
        /// <param name="cancellationToken">(Optional) A <see cref="CancellationToken"/> that can be used by other objects
        /// or threads to receive notice of cancellation.</param>
        /// <returns>A <see cref="Task"/> that represents the work queued to execute.</returns>
        /// <seealso cref="BotStateSet"/>
        /// <seealso cref="ConversationState"/>
        /// <seealso cref="IMiddleware"/>
        public async Task OnTurnAsync(ITurnContext turnContext, CancellationToken cancellationToken = default(CancellationToken))
        {
            // Handle Message activity type, which is the main activity type for shown within a conversational interface
            // Message activities may contain text, speech, interactive cards, and binary or unknown attachments.
            // see https://aka.ms/about-bot-activity-message to learn more about the message and other activity types
            if (turnContext.Activity.Type == ActivityTypes.ConversationUpdate && turnContext.Activity.MembersAdded.FirstOrDefault()?.Id == turnContext.Activity.Recipient.Id)
            {
                var msg = "Hi! I'm a restaurant assistant bot. I can add help you with your reservation.";
                await turnContext.SendActivityAsync(msg, _ttsService.GenerateSsml(msg, BotConstants.EnglishLanguage));
            }
            else
            {
                var dialogContext = await _dialogs.CreateContextAsync(turnContext, cancellationToken);
                var dialogResult = await dialogContext.ContinueDialogAsync(cancellationToken);

                if (!turnContext.Responded)
                {
                    // Check LUIS model
                    switch (dialogResult.Status)
                    {
                        case DialogTurnStatus.Empty:
                            // Your code goes here
                            // Check LUIS model
                            var result = await _services.LuisServices[BotConstants.LuisKey].RecognizeAsync(turnContext, cancellationToken);
                            var topIntent = result?.GetTopScoringIntent();
                            switch (topIntent?.intent)
                            {
                                case "TodaysSpecialty":
                                    await TodaysSpecialtiesHandlerAsync(turnContext);
                                    break;
                                case "ReserveTable":
                                    var amountPeople = result.Entities["AmountPeople"] != null ? (string)result.Entities["AmountPeople"]?.First : null;
                                    var time = GetTimeValueFromResult(result);
                                    await ReservationHandlerAsync(dialogContext, amountPeople, time, cancellationToken);
                                    break;

                                case "GetDiscounts":
                                    await GetDiscountsHandlerAsync(turnContext);
                                    break;

                                default:
                                    // Check QnA Maker model
                                    var response = await _services.QnAServices[BotConstants.QnAMakerKey].GetAnswersAsync(turnContext);
                                    if (response != null && response.Length > 0)
                                    {
                                        await turnContext.SendActivityAsync(response[0].Answer, cancellationToken: cancellationToken);
                                    }
                                    else
                                    {
                                        await turnContext.SendActivityAsync("Sorry, I didn't understand that.");
                                    }

                                    break;
                            }

                            break;
                        case DialogTurnStatus.Waiting:
                            // The active dialog is waiting for a response from the user, so do nothing.
                            break;
                        case DialogTurnStatus.Complete:
                            await dialogContext.EndDialogAsync();
                            break;
                        default:
                            await dialogContext.CancelAllDialogsAsync();
                            break;
                    }

                }

                // Save the conversation state.
                await _accessors.ConversationState.SaveChangesAsync(turnContext);
            
            }
        }

        private async Task TodaysSpecialtiesHandlerAsync(ITurnContext context)
        {
            var actions = new[]
            {
        new CardAction(type: ActionTypes.ShowImage, title: "Carbonara", value: "Carbonara", image: $"{BotConstants.Site}/carbonara.jpg"),
        new CardAction(type: ActionTypes.ShowImage, title: "Pizza", value: "Pizza", image: $"{BotConstants.Site}/pizza.jpg"),
        new CardAction(type: ActionTypes.ShowImage, title: "Lasagna", value: "Lasagna", image: $"{BotConstants.Site}/lasagna.jpg"),
    };

            var cards = actions
                .Select(x => new HeroCard
                {
                    Images = new List<CardImage> { new CardImage(x.Image) },
                    Buttons = new List<CardAction> { x },
                }.ToAttachment())
                .ToList();
            var activity = (Activity)MessageFactory.Carousel(cards, "For today we have:");

            await context.SendActivityAsync(activity);
        }
        private string GetTimeValueFromResult(RecognizerResult result)
        {
            var timex = (string)result.Entities["datetime"]?.First["timex"].First;
            if (timex != null)
            {
                timex = timex.Contains(":") ? timex : $"{timex}:00";
                return DateTime.Parse(timex).ToString("MMMM dd \\a\\t HH:mm tt");
            }

            return null;
        }
        private async Task ReservationHandlerAsync(DialogContext dialogContext, string amountPeople, string time, CancellationToken cancellationToken)
        {
            var state = await _accessors.ReservationState.GetAsync(dialogContext.Context, () => new ReservationData(), cancellationToken);
            state.AmountPeople = amountPeople;
            state.Time = time;
            await dialogContext.BeginDialogAsync(GatherInfo);
        }

        private async Task<bool> AmountPeopleValidatorAsync(PromptValidatorContext<string> promptContext, CancellationToken cancellationToken)
        {
            var userInput = promptContext.Recognized.Value;
            var isValidNumber = int.TryParse(userInput, out int numberPeople);
            return await Task.FromResult(isValidNumber);
        }
        private async Task<DialogTurnResult> TimeStepAsync(WaterfallStepContext stepContext, CancellationToken cancellationToken)
        {
            var state = await _accessors.ReservationState.GetAsync(stepContext.Context, () => new ReservationData(), cancellationToken);
            if (string.IsNullOrEmpty(state.Time))
            {
                var msg = "When do you need the reservation?";
                var response = new PromptOptions { Prompt = MessageFactory.Text(msg, _ttsService.GenerateSsml(msg, BotConstants.EnglishLanguage)) };
                return await stepContext.PromptAsync(TimePrompt, response, cancellationToken);
            }
            else
            {
                return await stepContext.NextAsync();
            }
        }

        private async Task<DialogTurnResult> AmountPeopleStepAsync(WaterfallStepContext stepContext, CancellationToken cancellationToken)
        {
            var state = await _accessors.ReservationState.GetAsync(stepContext.Context, () => new ReservationData(), cancellationToken);

            if (state.Time == null)
            {
                var time = stepContext.Context.Activity.Text;
                state.Time = time;
            }

            if (state.AmountPeople == null)
            {
                var msg = "How many people will you need the reservation for?";
                var response = new PromptOptions { Prompt = MessageFactory.Text(msg, _ttsService.GenerateSsml(msg, BotConstants.EnglishLanguage)) };
                return await stepContext.PromptAsync(AmountPeoplePrompt, response, cancellationToken);
            }
            else
            {
                return await stepContext.NextAsync();
            }
        }
        private async Task<DialogTurnResult> NameStepAsync(WaterfallStepContext stepContext, CancellationToken cancellationToken)
        {
            var state = await _accessors.ReservationState.GetAsync(stepContext.Context, () => new ReservationData(), cancellationToken);
            state.AmountPeople = stepContext.Context.Activity.Text;

            var msg = "And the name on the reservation?";
            var response = new PromptOptions { Prompt = MessageFactory.Text(msg, _ttsService.GenerateSsml(msg, BotConstants.EnglishLanguage)) };
            return await stepContext.PromptAsync(NamePrompt, response, cancellationToken);
        }
        private async Task<DialogTurnResult> ConfirmationStepAsync(WaterfallStepContext stepContext, CancellationToken cancellationToken)
        {
            var state = await _accessors.ReservationState.GetAsync(stepContext.Context, () => new ReservationData(), cancellationToken);
            state.FullName = stepContext.Context.Activity.Text;

            var msg = $"Ok. Let me confirm the information: This is a reservation for {state.Time} for {state.AmountPeople} people. Is that correct?";
            var retryMsg = "Please say 'yes' or 'no' to confirm.";
            var response = new PromptOptions
            {
                Prompt = MessageFactory.Text(msg, _ttsService.GenerateSsml(msg, BotConstants.EnglishLanguage)),
                RetryPrompt = MessageFactory.Text(retryMsg, _ttsService.GenerateSsml(msg, BotConstants.EnglishLanguage)),
            };

            return await stepContext.PromptAsync(ConfirmationPrompt, response, cancellationToken);
        }

        private async Task<DialogTurnResult> FinalStepAsync(WaterfallStepContext stepContext, CancellationToken cancellationToken)
        {
            var state = await _accessors.ReservationState.GetAsync(stepContext.Context, () => new ReservationData(), cancellationToken);
            var confirmation = (bool)stepContext.Result;
            string msg = null;
            if (confirmation)
            {
                msg = $"Great, we will be expecting you on {state.Time}. Thanks for your reservation {state.FirstName}!";
            }
            else
            {
                msg = "Thanks for using the Contoso Assistance. See you soon!";
            }

            await stepContext.Context.SendActivityAsync(msg, _ttsService.GenerateSsml(msg, BotConstants.EnglishLanguage));

            return await stepContext.EndDialogAsync(cancellationToken: cancellationToken);
        }
        private async Task GetDiscountsHandlerAsync(ITurnContext context)
        {
            var msg = "This week we have a 25% discount in all of our wine selection";
            await context.SendActivityAsync(msg);
        }

    }
}
