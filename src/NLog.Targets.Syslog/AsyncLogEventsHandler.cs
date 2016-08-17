using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NLog.Common;
using NLog.Layouts;
using NLog.Targets.Syslog.MessageCreation;
using NLog.Targets.Syslog.MessageSend;

namespace NLog.Targets.Syslog
{
    internal class AsyncLogEventsHandler
    {
        private readonly MessageBuildersFacade messageBuilder;
        private Layout layout;
        private readonly MessageTransmittersFacade messageTransmitter;
        private readonly Action<LogEventInfo> mergeEventProperties;
        private readonly ConcurrentQueue<LogEventAndMessages> queue;
        private readonly CancellationTokenSource cts;
        private volatile bool disposed;

        public AsyncLogEventsHandler(SyslogTarget target, Action<LogEventInfo> mergeEventPropertiesAction)
        {
            messageBuilder = target.MessageBuilder;
            messageTransmitter = target.MessageTransmitter;
            mergeEventProperties = mergeEventPropertiesAction;
            queue = new ConcurrentQueue<LogEventAndMessages>();
            cts = new CancellationTokenSource();
        }

        public void Initialize(Layout targetLayout)
        {
            layout = targetLayout;
            ProcessQueueAsync(cts.Token);
        }

        public void Handle(IEnumerable<AsyncLogEventInfo> asyncLogEvents)
        {
            asyncLogEvents.ForEach(asyncLogEvent =>
            {
                mergeEventProperties(asyncLogEvent.LogEvent);
                var logEventAndMessages = new LogEventAndMessages(messageBuilder, asyncLogEvent, layout);
                queue.Enqueue(logEventAndMessages);
                InternalLogger.Debug($"Enqueued {logEventAndMessages}");
            });
        }

        private void ProcessQueueAsync(CancellationToken token)
        {
            if (disposed || token.IsCancellationRequested)
                return;

            LogEventAndMessages logEventAndMessages;
            var sendOrDelayTask = queue.TryDequeue(out logEventAndMessages) ?
                SendMsgSetAsync(logEventAndMessages, token, new TaskCompletionSource<object>()) :
                Task.Delay(messageTransmitter.RetryTime, token);

            sendOrDelayTask
                .ContinueWith(t =>
                {
                    if (t.IsCanceled)
                        InternalLogger.Debug("Task canceled");
                    else if (t.Exception != null) // t.IsFaulted is true
                        InternalLogger.Debug(t.Exception.GetBaseException(), "Task faulted with exception");
                    else
                        ProcessQueueAsync(token);
                }, token);
        }

        private Task SendMsgSetAsync(LogEventAndMessages logEventAndMessages, CancellationToken token, TaskCompletionSource<object> tcs, Exception exception = null)
        {
            if (disposed || token.IsCancellationRequested)
                return CancelledTcsTask(tcs);

            var messages = logEventAndMessages.Messages;

            // All messages have been dequeued and either all messages have been sent or
            // an exception has occurred and has been propagated till here
            // (no message was sent after it)
            if (messages.Count == 0)
                return MessagesDequeuedTcsTask(logEventAndMessages, tcs, exception);

            messageTransmitter
                .SendMessageAsync(messages.Dequeue(), token)
                .Then(t => SendMsgSetAsync(logEventAndMessages, token, tcs, t.Exception), token);

            return tcs.Task;
        }

        private static Task CancelledTcsTask(TaskCompletionSource<object> tcs)
        {
            tcs.SetCanceled();
            return tcs.Task;
        }

        private static Task MessagesDequeuedTcsTask(LogEventAndMessages logEventAndMessages, TaskCompletionSource<object> tcs, Exception exception)
        {
            InternalLogger.Debug($"Dequeued {logEventAndMessages}");

            var asyncContinuation = logEventAndMessages.AsyncLogEvent.Continuation;

            if (exception != null)
            {
                asyncContinuation(exception.GetBaseException());
                tcs.SetException(exception);
            }
            else
            {
                asyncContinuation(null);
                tcs.SetResult(null);
            }

            return tcs.Task;
        }

        public void Dispose()
        {
            try
            {
                if (disposed)
                    return;
                disposed = true;
                messageTransmitter.Dispose();
                cts.Cancel();
            }
            catch (Exception ex)
            {
                InternalLogger.Debug(ex, $"{GetType().Name} dispose error");
            }
        }
    }
}