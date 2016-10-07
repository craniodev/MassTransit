﻿// Copyright 2007-2016 Chris Patterson, Dru Sellers, Travis Smith, et. al.
//  
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use
// this file except in compliance with the License. You may obtain a copy of the 
// License at 
// 
//     http://www.apache.org/licenses/LICENSE-2.0 
// 
// Unless required by applicable law or agreed to in writing, software distributed
// under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR 
// CONDITIONS OF ANY KIND, either express or implied. See the License for the 
// specific language governing permissions and limitations under the License.
namespace MassTransit.AzureServiceBusTransport
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Contexts;
    using GreenPipes;
    using Logging;
    using MassTransit.Pipeline;
    using Microsoft.ServiceBus.Messaging;
    using Pipeline;
    using Policies;
    using Transports;
    using Util;


    public class ServiceBusSubscriptionReceiveTransport :
        IReceiveTransport
    {
        static readonly ILog _log = Logger.Get<ServiceBusReceiveTransport>();
        readonly IRetryPolicy _connectionRetryPolicy;
        readonly ReceiveEndpointObservable _endpointObservers;
        readonly IServiceBusHost _host;
        readonly ReceiveObservable _receiveObservers;
        readonly SubscriptionSettings _settings;

        public ServiceBusSubscriptionReceiveTransport(IServiceBusHost host, SubscriptionSettings settings)
        {
            _host = host;
            _settings = settings;
            _receiveObservers = new ReceiveObservable();
            _endpointObservers = new ReceiveEndpointObservable();

            _connectionRetryPolicy = Retry.Exponential(1000, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));
        }

        void IProbeSite.Probe(ProbeContext context)
        {
            var scope = context.CreateScope("transport");
            scope.Set(new
            {
                Type = "Azure Service Bus",
                Topic = _settings.TopicDescription.Path,
                _settings.PrefetchCount,
                _settings.MaxConcurrentCalls,
                _settings.SubscriptionDescription
            });
        }

        public ReceiveTransportHandle Start(IPipe<ReceiveContext> receivePipe)
        {
            var transportAddress = new Uri(_host.Settings.ServiceUri, _settings.TopicDescription.Path + "/" + _settings.SubscriptionDescription.Name);

            if (_log.IsDebugEnabled)
            {
                _log.DebugFormat("Starting receive transport: {0}", transportAddress);
            }

            var supervisor =
                new TaskSupervisor($"{TypeMetadataCache<ServiceBusReceiveTransport>.ShortName} - {transportAddress}");

            IPipe<NamespaceContext> pipe = Pipe.New<NamespaceContext>(x =>
            {
                x.UseRetry(r =>
                {
                    r.Handle<ServerBusyException>();
                    r.Handle<TimeoutException>();
                    r.Intervals(50, 100, 500, 1000, 5000, 10000);
                });

                x.UseFilter(new PrepareSubscriptionFilter(_settings));

                if (_settings.SubscriptionDescription.RequiresSession)
                {
                    x.UseFilter(new MessageSessionReceiverFilter(receivePipe, _receiveObservers, _endpointObservers, supervisor));
                }
                else
                {
                    x.UseFilter(new MessageReceiverFilter(receivePipe, _receiveObservers, _endpointObservers, supervisor));
                }
            });

            Receiver(pipe, supervisor);

            return new Handle(supervisor);
        }

        public ConnectHandle ConnectReceiveObserver(IReceiveObserver observer)
        {
            return _receiveObservers.Connect(observer);
        }

        public ConnectHandle ConnectReceiveEndpointObserver(IReceiveEndpointObserver observer)
        {
            return _endpointObservers.Connect(observer);
        }

        async void Receiver(IPipe<NamespaceContext> pipe, TaskSupervisor supervisor)
        {
            var transportAddress = new Uri(_host.Settings.ServiceUri, _settings.TopicDescription.Path + "/" + _settings.SubscriptionDescription.Name);

            try
            {
                await _connectionRetryPolicy.RetryUntilCancelled(async () =>
                {
                    if (_log.IsDebugEnabled)
                        _log.DebugFormat("Connecting receive transport: {0}", transportAddress);

                    var context = new ServiceBusNamespaceContext(_host, supervisor.StoppedToken);

                    try
                    {
                        await pipe.Send(context).ConfigureAwait(false);
                    }
                    catch (TaskCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        if (_log.IsErrorEnabled)
                            _log.Error($"Azure Service Bus receiver faulted: {transportAddress}", ex);

                        await _endpointObservers.Faulted(new Faulted(transportAddress, ex)).ConfigureAwait(false);
                    }
                }, supervisor.StoppingToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
            }
        }


        class Faulted :
            ReceiveEndpointFaulted
        {
            public Faulted(Uri inputAddress, Exception exception)
            {
                InputAddress = inputAddress;
                Exception = exception;
            }

            public Uri InputAddress { get; }
            public Exception Exception { get; }
        }


        class Handle :
            ReceiveTransportHandle
        {
            readonly TaskSupervisor _supervisor;

            public Handle(TaskSupervisor supervisor)
            {
                _supervisor = supervisor;
            }

            Task ReceiveTransportHandle.Stop(CancellationToken cancellationToken)
            {
                return _supervisor.Stop("Stop Receive Transport", cancellationToken);
            }
        }
    }
}