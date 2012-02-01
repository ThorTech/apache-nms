/*
 * Licensed to the Apache Software Foundation (ASF) under one or more
 * contributor license agreements.  See the NOTICE file distributed with
 * this work for additional information regarding copyright ownership.
 * The ASF licenses this file to You under the Apache License, Version 2.0
 * (the "License"); you may not use this file except in compliance with
 * the License.  You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections;
using System.Collections.Specialized;
using System.Threading;
using Apache.NMS.Util;
using Apache.NMS.ActiveMQ.Commands;
using Apache.NMS.ActiveMQ.Util;

namespace Apache.NMS.ActiveMQ
{
    /// <summary>
    /// Default provider of ISession
    /// </summary>
    public class Session : ISession, IDispatcher
    {
        /// <summary>
        /// Private object used for synchronization, instead of public "this"
        /// </summary>
        private readonly object myLock = new object();

        private readonly IDictionary consumers = Hashtable.Synchronized(new Hashtable());
        private readonly IDictionary producers = Hashtable.Synchronized(new Hashtable());

        private readonly SessionExecutor executor;
        private readonly TransactionContext transactionContext;

        private Connection connection;

        private bool dispatchAsync;
        private bool exclusive;
        private bool retroactive;
        private byte priority = 0;

        private readonly SessionInfo info;
        private int consumerCounter;
        private int producerCounter;
        private long nextDeliveryId;
        private long lastDeliveredSequenceId;
        private bool disposed = false;
        private bool closed = false;
        private bool closing = false;
        private TimeSpan disposeStopTimeout = TimeSpan.FromMilliseconds(30000);
        private TimeSpan closeStopTimeout = TimeSpan.FromMilliseconds(Timeout.Infinite);
        private TimeSpan requestTimeout;
        private readonly AcknowledgementMode acknowledgementMode;

        public Session(Connection connection, SessionId sessionId, AcknowledgementMode acknowledgementMode)
        {
            this.info = new SessionInfo();
            this.info.SessionId = sessionId;
            this.connection = connection;
            this.connection.Oneway(this.info);

            this.acknowledgementMode = acknowledgementMode;
            this.requestTimeout = connection.RequestTimeout;
            this.dispatchAsync = connection.DispatchAsync;
            this.transactionContext = new TransactionContext(this);

            Uri brokerUri = connection.BrokerUri;

            // Set propertieDs on session using parameters prefixed with "session."
            if(!String.IsNullOrEmpty(brokerUri.Query) && !brokerUri.OriginalString.EndsWith(")"))
            {
                string query = brokerUri.Query.Substring(brokerUri.Query.LastIndexOf(")") + 1);
                StringDictionary options = URISupport.ParseQuery(query);
                options = URISupport.GetProperties(options, "session.");
                URISupport.SetProperties(this, options);
            }

            this.ConsumerTransformer = connection.ConsumerTransformer;
            this.ProducerTransformer = connection.ProducerTransformer;

            this.executor = new SessionExecutor(this, this.consumers);

            if(connection.IsStarted)
            {
                this.Start();
            }

            connection.AddSession(this);
        }

        ~Session()
        {
            Dispose(false);
        }

        #region Property Accessors

        /// <summary>
        /// Sets the prefetch size, the maximum number of messages a broker will dispatch to consumers
        /// until acknowledgements are received.
        /// </summary>
        public int PrefetchSize
        {
            set{ this.connection.PrefetchPolicy.SetAll(value); }
        }

        /// <summary>
        /// Sets the maximum number of messages to keep around per consumer
        /// in addition to the prefetch window for non-durable topics until messages
        /// will start to be evicted for slow consumers.
        /// Must be > 0 to enable this feature
        /// </summary>
        public int MaximumPendingMessageLimit
        {
            set{ this.connection.PrefetchPolicy.MaximumPendingMessageLimit = value; }
        }

        /// <summary>
        /// Enables or disables whether asynchronous dispatch should be used by the broker
        /// </summary>
        public bool DispatchAsync
        {
            get{ return this.dispatchAsync; }
            set{ this.dispatchAsync = value; }
        }

        /// <summary>
        /// Enables or disables exclusive consumers when using queues. An exclusive consumer means
        /// only one instance of a consumer is allowed to process messages on a queue to preserve order
        /// </summary>
        public bool Exclusive
        {
            get{ return this.exclusive; }
            set{ this.exclusive = value; }
        }

        /// <summary>
        /// Enables or disables retroactive mode for consumers; i.e. do they go back in time or not?
        /// </summary>
        public bool Retroactive
        {
            get{ return this.retroactive; }
            set{ this.retroactive = value; }
        }

        /// <summary>
        /// Sets the default consumer priority for consumers
        /// </summary>
        public byte Priority
        {
            get{ return this.priority; }
            set{ this.priority = value; }
        }

        public Connection Connection
        {
            get { return this.connection; }
        }

        public SessionId SessionId
        {
            get { return info.SessionId; }
        }

        public TransactionContext TransactionContext
        {
            get { return this.transactionContext; }
        }

        public TimeSpan RequestTimeout
        {
            get { return this.requestTimeout; }
            set { this.requestTimeout = value; }
        }

        public bool Transacted
        {
            get { return this.IsTransacted; }
        }

        public virtual AcknowledgementMode AcknowledgementMode
        {
            get { return this.acknowledgementMode; }
        }

        public virtual bool IsClientAcknowledge
        {
            get { return this.acknowledgementMode == AcknowledgementMode.ClientAcknowledge; }
        }

        public virtual bool IsAutoAcknowledge
        {
            get { return this.acknowledgementMode == AcknowledgementMode.AutoAcknowledge; }
        }

        public virtual bool IsDupsOkAcknowledge
        {
            get { return this.acknowledgementMode == AcknowledgementMode.DupsOkAcknowledge; }
        }

        public virtual bool IsIndividualAcknowledge
        {
            get { return this.acknowledgementMode == AcknowledgementMode.IndividualAcknowledge; }
        }

        public virtual bool IsTransacted
        {
            get{ return this.acknowledgementMode == AcknowledgementMode.Transactional; }
        }

        public SessionExecutor Executor
        {
            get { return this.executor; }
        }

        public long NextDeliveryId
        {
            get { return Interlocked.Increment(ref this.nextDeliveryId); }
        }

        public long DisposeStopTimeout
        {
            get { return (long) this.disposeStopTimeout.TotalMilliseconds; }
            set { this.disposeStopTimeout = TimeSpan.FromMilliseconds(value); }
        }

        public long CloseStopTimeout
        {
            get { return (long) this.closeStopTimeout.TotalMilliseconds; }
            set { this.closeStopTimeout = TimeSpan.FromMilliseconds(value); }
        }

        private ConsumerTransformerDelegate consumerTransformer;
        /// <summary>
        /// A Delegate that is called each time a Message is dispatched to allow the client to do
        /// any necessary transformations on the received message before it is delivered.
        /// The Session instance sets the delegate on each Consumer it creates.
        /// </summary>
        public ConsumerTransformerDelegate ConsumerTransformer
        {
            get { return this.consumerTransformer; }
            set { this.consumerTransformer = value; }
        }

        private ProducerTransformerDelegate producerTransformer;
        /// <summary>
        /// A delegate that is called each time a Message is sent from this Producer which allows
        /// the application to perform any needed transformations on the Message before it is sent.
        /// The Session instance sets the delegate on each Producer it creates.
        /// </summary>
        public ProducerTransformerDelegate ProducerTransformer
        {
            get { return this.producerTransformer; }
            set { this.producerTransformer = value; }
        }

        #endregion

        #region ISession Members

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected void Dispose(bool disposing)
        {
            if(this.disposed)
            {
                return;
            }

            if(disposing)
            {
                // Dispose managed code here.
            }

            try
            {
                // Force a Stop when we are Disposing vs a Normal Close.
                this.executor.Stop(this.disposeStopTimeout);

                Close();
            }
            catch
            {
                // Ignore network errors.
            }

            this.disposed = true;
        }

        public void Close()
        {
            lock(myLock)
            {
                if(this.closed)
                {
                    return;
                }

                try
                {
                    if(transactionContext.InNetTransaction)
                    {
                        this.transactionContext.AddSynchronization(new SessionCloseSynchronization(this));
                    }
                    else
                    {
                        Tracer.InfoFormat("Closing The Session with Id {0}", this.info.SessionId.ToString());
                        DoClose();
                        Tracer.InfoFormat("Closed The Session with Id {0}", this.info.SessionId.ToString());
                    }
                }
                catch(Exception ex)
                {
                    Tracer.ErrorFormat("Error during session close: {0}", ex);
                }
            }
        }

        internal void DoClose()
        {
			Shutdown();
            RemoveInfo info = new RemoveInfo();
            info.ObjectId = this.info.SessionId;
            info.LastDeliveredSequenceId = this.lastDeliveredSequenceId;
            this.connection.Oneway(info);
		}
		
        internal void Shutdown()
        {
            lock(myLock)
            {
                if(this.closed)
                {
                    return;
                }

                try
                {
                    this.closing = true;

                    // Stop all message deliveries from this Session
                    this.executor.Stop(this.closeStopTimeout);

                    lock(consumers.SyncRoot)
                    {
                        foreach(MessageConsumer consumer in consumers.Values)
                        {
                            consumer.FailureError = this.connection.FirstFailureError;
                            consumer.Shutdown();
                            this.lastDeliveredSequenceId =
                                Math.Min(this.lastDeliveredSequenceId, consumer.LastDeliveredSequenceId);
                        }
                    }
                    consumers.Clear();

                    lock(producers.SyncRoot)
                    {
                        foreach(MessageProducer producer in producers.Values)
                        {
                            producer.Shutdown();
                        }
                    }
                    producers.Clear();

                    // If in a transaction roll it back
                    if(this.IsTransacted && this.transactionContext.InLocalTransaction)
                    {
                        try
                        {
                            this.transactionContext.Rollback();
                        }
                        catch
                        {
                        }
                    }

                    Connection.RemoveSession(this);
                }
                catch(Exception ex)
                {
                    Tracer.ErrorFormat("Error during session close: {0}", ex);
                }
                finally
                {
                    this.closed = true;
                    this.closing = false;
                }
            }
        }

        public IMessageProducer CreateProducer()
        {
            return CreateProducer(null);
        }

        public IMessageProducer CreateProducer(IDestination destination)
        {
            MessageProducer producer = null;

            try
            {
                ActiveMQDestination dest = null;
                if(destination != null)
                {
                    dest = ActiveMQDestination.Transform(destination);
                }

                producer = new MessageProducer(this, GetNextProducerId(), dest, this.RequestTimeout);

                producer.ProducerTransformer = this.ProducerTransformer;

                this.AddProducer(producer);
                this.Connection.Oneway(producer.ProducerInfo);
            }
            catch(Exception)
            {
                if(producer != null)
                {
                    this.RemoveProducer(producer.ProducerId);
                    producer.Close();
                }

                throw;
            }

            return producer;
        }

        public IMessageConsumer CreateConsumer(IDestination destination)
        {
            return CreateConsumer(destination, null, false);
        }

        public IMessageConsumer CreateConsumer(IDestination destination, string selector)
        {
            return CreateConsumer(destination, selector, false);
        }

        public IMessageConsumer CreateConsumer(IDestination destination, string selector, bool noLocal)
        {
            if(destination == null)
            {
                throw new InvalidDestinationException("Cannot create a Consumer with a Null destination");
            }

            ActiveMQDestination dest = ActiveMQDestination.Transform(destination);
            int prefetchSize = this.Connection.PrefetchPolicy.DurableTopicPrefetch;

            if(dest is ITopic || dest is ITemporaryTopic)
            {
                prefetchSize = this.connection.PrefetchPolicy.TopicPrefetch;
            }
            else if(dest is IQueue || dest is ITemporaryQueue)
            {
                prefetchSize = this.connection.PrefetchPolicy.QueuePrefetch;
            }

            MessageConsumer consumer = null;

            try
            {
                consumer = new MessageConsumer(this, GetNextConsumerId(), dest, null, selector, prefetchSize,
                                               this.connection.PrefetchPolicy.MaximumPendingMessageLimit,
                                               noLocal, false, this.connection.DispatchAsync);

                consumer.ConsumerTransformer = this.ConsumerTransformer;

                this.AddConsumer(consumer);
                this.Connection.SyncRequest(consumer.ConsumerInfo);

                if(this.Connection.IsStarted)
                {
                    consumer.Start();
                }
            }
            catch(Exception)
            {
                if(consumer != null)
                {
                    this.RemoveConsumer(consumer.ConsumerId);
                    consumer.Close();
                }

                throw;
            }

            return consumer;
        }

        public IMessageConsumer CreateDurableConsumer(ITopic destination, string name, string selector, bool noLocal)
        {
            if(destination == null)
            {
                throw new InvalidDestinationException("Cannot create a Consumer with a Null destination");
            }

            ActiveMQDestination dest = ActiveMQDestination.Transform(destination);
            MessageConsumer consumer = null;

            try
            {
                consumer = new MessageConsumer(this, GetNextConsumerId(), dest, name, selector,
                                               this.connection.PrefetchPolicy.DurableTopicPrefetch,
                                               this.connection.PrefetchPolicy.MaximumPendingMessageLimit,
                                               noLocal, false, this.connection.DispatchAsync);

                consumer.ConsumerTransformer = this.ConsumerTransformer;

                this.AddConsumer(consumer);
                this.Connection.SyncRequest(consumer.ConsumerInfo);

                if(this.Connection.IsStarted)
                {
                    consumer.Start();
                }
            }
            catch(Exception)
            {
                if(consumer != null)
                {
                    this.RemoveConsumer(consumer.ConsumerId);
                    consumer.Close();
                }

                throw;
            }

            return consumer;
        }

        public void DeleteDurableConsumer(string name)
        {
            RemoveSubscriptionInfo command = new RemoveSubscriptionInfo();
            command.ConnectionId = Connection.ConnectionId;
            command.ClientId = Connection.ClientId;
            command.SubcriptionName = name;
            this.connection.SyncRequest(command);
        }

        public IQueueBrowser CreateBrowser(IQueue queue)
        {
            return this.CreateBrowser(queue, null);
        }

        public IQueueBrowser CreateBrowser(IQueue queue, string selector)
        {
            if(queue == null)
            {
                throw new InvalidDestinationException("Cannot create a Consumer with a Null destination");
            }

            ActiveMQDestination dest = ActiveMQDestination.Transform(queue);
            QueueBrowser browser = null;

            try
            {
                browser = new QueueBrowser(this, GetNextConsumerId(), dest, selector, this.DispatchAsync);
            }
            catch(Exception)
            {
                if(browser != null)
                {
                    browser.Close();
                }

                throw;
            }

            return browser;
        }

        public IQueue GetQueue(string name)
        {
            return new ActiveMQQueue(name);
        }

        public ITopic GetTopic(string name)
        {
            return new ActiveMQTopic(name);
        }

        public ITemporaryQueue CreateTemporaryQueue()
        {
            return (ITemporaryQueue)this.connection.CreateTemporaryDestination(false);
        }

        public ITemporaryTopic CreateTemporaryTopic()
        {
            return (ITemporaryTopic)this.connection.CreateTemporaryDestination(true);
        }

        /// <summary>
        /// Delete a destination (Queue, Topic, Temp Queue, Temp Topic).
        /// </summary>
        public void DeleteDestination(IDestination destination)
        {
            this.connection.DeleteDestination(destination);
        }

        public IMessage CreateMessage()
        {
            ActiveMQMessage answer = new ActiveMQMessage();
            return ConfigureMessage(answer) as IMessage;
        }

        public ITextMessage CreateTextMessage()
        {
            ActiveMQTextMessage answer = new ActiveMQTextMessage();
            return ConfigureMessage(answer) as ITextMessage;
        }

        public ITextMessage CreateTextMessage(string text)
        {
            ActiveMQTextMessage answer = new ActiveMQTextMessage(text);
            return ConfigureMessage(answer) as ITextMessage;
        }

        public IMapMessage CreateMapMessage()
        {
            return ConfigureMessage(new ActiveMQMapMessage()) as IMapMessage;
        }

        public IBytesMessage CreateBytesMessage()
        {
            return ConfigureMessage(new ActiveMQBytesMessage()) as IBytesMessage;
        }

        public IBytesMessage CreateBytesMessage(byte[] body)
        {
            ActiveMQBytesMessage answer = new ActiveMQBytesMessage();
            answer.Content = body;
            return ConfigureMessage(answer) as IBytesMessage;
        }

        public IStreamMessage CreateStreamMessage()
        {
            return ConfigureMessage(new ActiveMQStreamMessage()) as IStreamMessage;
        }

        public IObjectMessage CreateObjectMessage(object body)
        {
            ActiveMQObjectMessage answer = new ActiveMQObjectMessage();
            answer.Body = body;
            return ConfigureMessage(answer) as IObjectMessage;
        }

        public void Commit()
        {
            this.DoCommit();
        }

        public void Rollback()
        {
            this.DoRollback();
        }

        #endregion

        internal void DoSend( ActiveMQMessage message, MessageProducer producer, MemoryUsage producerWindow, TimeSpan sendTimeout )
        {
            ActiveMQMessage msg = message;

            if(IsTransacted)
            {
                DoStartTransaction();
                msg.TransactionId = TransactionContext.TransactionId;
            }

            msg.RedeliveryCounter = 0;
            msg.BrokerPath = null;

            if(this.connection.CopyMessageOnSend)
            {
                msg = (ActiveMQMessage)msg.Clone();
            }

            msg.OnSend();
            msg.ProducerId = msg.MessageId.ProducerId;

            if(sendTimeout.TotalMilliseconds <= 0 && !msg.ResponseRequired && !connection.AlwaysSyncSend &&
               (!msg.Persistent || connection.AsyncSend || msg.TransactionId != null))
            {
                this.connection.Oneway(msg);

                if(producerWindow != null)
                {
                    // Since we defer lots of the marshaling till we hit the wire, this
                    // might not provide and accurate size. We may change over to doing
                    // more aggressive marshaling, to get more accurate sizes.. this is more
                    // important once users start using producer window flow control.
                    producerWindow.IncreaseUsage(msg.Size());
                }
            }
            else
            {
                if(sendTimeout.TotalMilliseconds > 0)
                {
                    this.connection.SyncRequest(msg, sendTimeout);
                }
                else
                {
                    this.connection.SyncRequest(msg);
                }
            }
        }

        internal virtual void DoCommit()
        {
            if(!IsTransacted)
            {
                throw new InvalidOperationException(
                        "You cannot perform a Commit() on a non-transacted session. Acknowlegement mode is: "
                        + this.AcknowledgementMode);
            }

            this.TransactionContext.Commit();
        }

        internal virtual void DoRollback()
        {
            if(!IsTransacted)
            {
                throw new InvalidOperationException(
                        "You cannot perform a Commit() on a non-transacted session. Acknowlegement mode is: "
                        + this.AcknowledgementMode);
            }

            this.TransactionContext.Rollback();
        }

        /// <summary>
        /// Ensures that a transaction is started
        /// </summary>
        internal virtual void DoStartTransaction()
        {
            if(IsTransacted)
            {
                this.TransactionContext.Begin();
            }
        }

        public void AddConsumer(MessageConsumer consumer)
        {
            if(!this.closing)
            {
                ConsumerId id = consumer.ConsumerId;

                // Registered with Connection before we register at the broker.
                consumers[id] = consumer;
                connection.addDispatcher(id, this);
            }
        }

        public void RemoveConsumer(ConsumerId objectId)
        {
            connection.removeDispatcher(objectId);
            if(!this.closing)
            {
                consumers.Remove(objectId);
            }
        }

        public void AddProducer(MessageProducer producer)
        {
            if(!this.closing)
            {
                ProducerId id = producer.ProducerId;

                this.producers[id] = producer;
                this.connection.addProducer(id, producer);
            }
        }

        public void RemoveProducer(ProducerId objectId)
        {
            connection.removeProducer(objectId);
            if(!this.closing)
            {
                producers.Remove(objectId);
            }
        }

        public ConsumerId GetNextConsumerId()
        {
            ConsumerId id = new ConsumerId();
            id.ConnectionId = info.SessionId.ConnectionId;
            id.SessionId = info.SessionId.Value;
            id.Value = Interlocked.Increment(ref consumerCounter);

            return id;
        }

        public ProducerId GetNextProducerId()
        {
            ProducerId id = new ProducerId();
            id.ConnectionId = info.SessionId.ConnectionId;
            id.SessionId = info.SessionId.Value;
            id.Value = Interlocked.Increment(ref producerCounter);

            return id;
        }

        public void Stop()
        {
            if(this.executor != null)
            {
                this.executor.Stop();
            }
        }

        public void Start()
        {
            foreach(MessageConsumer consumer in this.consumers.Values)
            {
                consumer.Start();
            }

            if(this.executor != null)
            {
                this.executor.Start();
            }
        }

        public bool Started
        {
            get
            {
                return this.executor != null ? this.executor.Running : false;
            }
        }

        internal void Redispatch(MessageDispatchChannel channel)
        {
            MessageDispatch[] messages = channel.RemoveAll();
            System.Array.Reverse(messages);

            foreach(MessageDispatch message in messages)
            {
                this.executor.ExecuteFirst(message);
            }
        }

        public void Dispatch(MessageDispatch dispatch)
        {
            if(this.executor != null)
            {
                this.executor.Execute(dispatch);
            }
        }

        internal void ClearMessagesInProgress()
        {
            if(this.executor != null)
            {
                this.executor.ClearMessagesInProgress();
            }

            // Because we are called from inside the Transport Reconnection logic
            // we spawn the Consumer clear to another Thread so that we can avoid
            // any lock contention that might exist between the consumer and the
            // connection that is reconnecting.
            lock(this.consumers.SyncRoot)
            {
                foreach(MessageConsumer consumer in this.consumers.Values)
                {
                    consumer.InProgressClearRequired();
                    ThreadPool.QueueUserWorkItem(ClearMessages, consumer);
                }
            }
        }

        private static void ClearMessages(object value)
        {
            MessageConsumer consumer = value as MessageConsumer;

            if(Tracer.IsDebugEnabled)
            {
                Tracer.Debug("Performing Async Clear of In Progress Messages on Consumer: " + consumer.ConsumerId);
            }

            consumer.ClearMessagesInProgress();
        }

        internal void Acknowledge()
        {
            lock(this.consumers.SyncRoot)
            {
                foreach(MessageConsumer consumer in this.consumers.Values)
                {
                    consumer.Acknowledge();
                }
            }
        }

        private ActiveMQMessage ConfigureMessage(ActiveMQMessage message)
        {
            message.Connection = this.connection;

            if(this.IsTransacted)
            {
                // Allows Acknowledge to be called in a transaction with no effect per JMS Spec.
                message.Acknowledger += new AcknowledgeHandler(DoNothingAcknowledge);
            }

            return message;
        }

        internal void SendAck(MessageAck ack)
        {
            this.SendAck(ack, false);
        }

        internal void SendAck(MessageAck ack, bool lazy)
        {
            if(lazy || connection.SendAcksAsync || this.IsTransacted )
            {
                this.connection.Oneway(ack);
            }
            else
            {
                this.connection.SyncRequest(ack);
            }
        }

        /// <summary>
        /// Prevents message from throwing an exception if a client calls Acknoweldge on
        /// a message that is part of a transaction either being produced or consumed.  The
        /// JMS Spec indicates that users should be able to call Acknowledge with no effect
        /// if the message is in a transaction.
        /// </summary>
        /// <param name="message">
        /// A <see cref="ActiveMQMessage"/>
        /// </param>
        private static void DoNothingAcknowledge(ActiveMQMessage message)
        {
        }

        class SessionCloseSynchronization : ISynchronization
        {
            private readonly Session session;

            public SessionCloseSynchronization(Session session)
            {
                this.session = session;
            }

            public void BeforeEnd()
            {
            }

            public void AfterCommit()
            {
                this.session.DoClose();
            }

            public void AfterRollback()
            {
                this.session.DoClose();
            }
        }

    }
}
