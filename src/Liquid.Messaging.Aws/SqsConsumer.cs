﻿using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using AutoMapper;
using Liquid.Core.Configuration;
using Liquid.Core.Context;
using Liquid.Core.Telemetry;
using Liquid.Core.Utils;
using Liquid.Messaging.Aws.Attributes;
using Liquid.Messaging.Aws.Extensions;
using Liquid.Messaging.Configuration;
using Liquid.Messaging.Exceptions;
using Liquid.Messaging.Extensions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Liquid.Messaging.Aws
{
    /// <summary>
    /// AWS SQS Consumer Class.
    /// </summary>
    /// <typeparam name="TMessage">The type of the message.</typeparam>
    /// <seealso cref="ILightConsumer{TMessage}" />
    public abstract class SqsConsumer<TMessage> : ILightConsumer<TMessage>, IDisposable
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly MessagingSettings _messagingSettings;
        private readonly ILightContextFactory _contextFactory;
        private readonly ILightTelemetryFactory _telemetryFactory;
        private readonly SqsConsumerAttribute _attribute;
        private readonly CancellationTokenSource _cancellationToken;
        private IAmazonSQS _client;
        private string _queueUrl;

        private Func<TMessage, IDictionary<string, object>, CancellationToken, Task<bool>> _messageHandler;

        /// <summary>
        /// Gets the logger.
        /// </summary>
        /// <value>
        /// The logger.
        /// </value>
        [ExcludeFromCodeCoverage]
        public ILogger LogService { get; }

        /// <summary>
        /// Gets the mapper service.
        /// </summary>
        /// <value>
        /// The mapper service.
        /// </value>
        [ExcludeFromCodeCoverage]
        public IMapper MapperService { get; }

        /// <summary>
        /// Gets the consumer mediator.
        /// </summary>
        /// <value>
        /// The mediator.
        /// </value>
        [ExcludeFromCodeCoverage]
        public IMediator MediatorService { get; }

        /// <summary>
        /// Consumes the message from  subscription asynchronous.
        /// </summary>
        /// <param name="message">The message to be consumed.</param>
        /// <param name="headers">The custom headers of message.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns></returns>
        public abstract Task<bool> ConsumeAsync(TMessage message, IDictionary<string, object> headers, CancellationToken cancellationToken);


        /// <summary>
        /// Initializes a new instance of the <see cref="SqsConsumer{TMessage}"/> class.
        /// </summary>
        /// <param name="serviceProvider">The service provider.</param>
        /// <param name="mediator">The mediator.</param>
        /// <param name="mapper">The mapper.</param>
        /// <param name="contextFactory">The context factory.</param>
        /// <param name="telemetryFactory">The telemetry factory.</param>
        /// <param name="loggerFactory">The logger factory.</param>
        /// <param name="messagingSettings">The messaging settings.</param>
        /// <exception cref="System.NotImplementedException">The {nameof(SqsConsumerAttribute)} attribute decorator must be added to configuration class.</exception>
        protected SqsConsumer(IServiceProvider serviceProvider,
                              IMediator mediator,
                              IMapper mapper,
                              ILightContextFactory contextFactory,
                              ILightTelemetryFactory telemetryFactory,
                              ILoggerFactory loggerFactory,
                              ILightConfiguration<List<MessagingSettings>> messagingSettings)
        {
            if (!GetType().GetCustomAttributes(typeof(SqsConsumerAttribute), true).Any())
            {
                throw new NotImplementedException($"The {nameof(SqsConsumerAttribute)} attribute decorator must be added to configuration class.");
            }
            _attribute = GetType().GetCustomAttribute<SqsConsumerAttribute>(true);
            _serviceProvider = serviceProvider;
            _contextFactory = contextFactory;
            _messagingSettings = messagingSettings?.Settings?.GetMessagingSettings(_attribute.ConnectionId) ?? throw new MessagingMissingConfigurationException("messaging");
            _telemetryFactory = telemetryFactory;

            MediatorService = mediator;
            MapperService = mapper;

            LogService = loggerFactory.CreateLogger(typeof(SqsConsumer<TMessage>).FullName);
            _cancellationToken = new CancellationTokenSource();
            InitializeClient();
        }

        private void InitializeClient()
        {
            _messageHandler = ConsumeAsync;
            var awsCredentials = new BasicAWSCredentials(_messagingSettings.GetAwsAccessKey(), _messagingSettings.GetAwsSecretKey());
            var awsSqsConfig = new AmazonSQSConfig
            {
                ServiceURL = _messagingSettings.ConnectionString,
                RegionEndpoint = _messagingSettings.GetAwsRegion()
            };

            _client = new AmazonSQSClient(awsCredentials, awsSqsConfig);

            Task.Run(StartProcessMessages);
        }

        private async Task StartProcessMessages()
        {
            try
            {
                _queueUrl = await _client.GetAwsQueueUrlAsync(_attribute.Queue);
                var receiveMessageRequest = new ReceiveMessageRequest
                {
                    QueueUrl = _queueUrl, 
                    AttributeNames = new List<string> { "All" }, 
                    MessageAttributeNames = new List<string> { "All" }
                };

                //Polling messages from SQS
                while (!_cancellationToken.Token.IsCancellationRequested)
                {
                    var receiveMessageResponse = await _client.ReceiveMessageAsync(receiveMessageRequest);
                    receiveMessageResponse?.Messages?.ForEach(async message => await ProcessMessageAsync(message));
                }
            }
            catch (Exception ex)
            {
                LogService.LogError(ex, ex.Message);
                throw new MessagingConsumerException(ex);
            }
        }

        private async Task ProcessMessageAsync(Message message)
        {
            var telemetry = _telemetryFactory.GetTelemetry();
            try
            {
                var telemetryKey = $"SqsConsumer_{_attribute.Queue}";

                telemetry.AddContext($"ConsumeMessage_{nameof(TMessage)}");
                telemetry.StartTelemetryStopWatchMetric(telemetryKey);

                var eventMessage = message.MessageAttributes.ContainsKey(CommonExtensions.ContentTypeHeader) &&
                                   message.MessageAttributes[CommonExtensions.ContentTypeHeader].StringValue.Equals(CommonExtensions.GZipContentType, StringComparison.InvariantCultureIgnoreCase)
                    ? Convert.FromBase64String(message.Body).GzipDecompress().ParseJson<TMessage>()
                    : message.Body.ParseJson<TMessage>();

                using (_serviceProvider.CreateScope())
                {
                    var context = _contextFactory.GetContext();

                    context.SetMessageId(message.MessageId);

                    var headers = message.MessageAttributes.GetCustomHeaders();

                    if (message.MessageAttributes != null) { AddContextHeaders(message.MessageAttributes, context); }

                    var messageProcessed = await _messageHandler.Invoke(eventMessage, headers, _cancellationToken.Token);
                    if (messageProcessed || _attribute.AutoComplete)
                    {
                        var deleteMessageRequest = new DeleteMessageRequest(_queueUrl, message.ReceiptHandle);
                        await _client.DeleteMessageAsync(deleteMessageRequest);
                    }

                    telemetry.CollectTelemetryStopWatchMetric(telemetryKey, new
                    {
                        consumer = telemetryKey,
                        messageType = typeof(TMessage).FullName,
                        aggregationId = context.GetAggregationId(),
                        messageId = message.MessageId,
                        message = eventMessage,
                        processed = messageProcessed,
                        autoComplete = _attribute.AutoComplete,
                        headers
                    });
                }
            }
            catch (Exception ex)
            {
                LogService.LogError(ex, ex.Message);
            }
            finally
            {
                telemetry.RemoveContext($"ConsumeMessage_{nameof(TMessage)}");
            }
        }

        /// <summary>
        /// Adds the context headers.
        /// </summary>
        /// <param name="headers">The headers.</param>
        /// <param name="context">The context.</param>
        private static void AddContextHeaders(IDictionary<string, MessageAttributeValue> headers, ILightContext context)
        {
            if (headers.TryGetValue("liquidCulture", out var culture) && culture?.StringValue.IsNotNullOrEmpty() == true)
                context.SetCulture(culture.StringValue);
            if (headers.TryGetValue("liquidChannel", out var channel) && channel?.StringValue.IsNotNullOrEmpty() == true)
                context.SetChannel(channel.StringValue);
            if (headers.TryGetValue("liquidCorrelationId", out var contextId) && contextId?.StringValue.IsNotNullOrEmpty() == true)
                context.SetContextId(contextId.StringValue.ToGuid());
            if (headers.TryGetValue("liquidBusinessCorrelationId", out var businessContextId) && businessContextId?.StringValue.IsNotNullOrEmpty() == true)
                context.SetBusinessContextId(businessContextId.StringValue.ToGuid());
            if (headers.TryGetValue("liquidAggregationId", out var aggregationId) && aggregationId?.StringValue.IsNotNullOrEmpty() == true)
                context.SetAggregationId(aggregationId.StringValue);
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="dispose"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool dispose)
        {
            _cancellationToken?.Cancel();
            _cancellationToken?.Dispose();
            _client?.Dispose();
        }
    }
}