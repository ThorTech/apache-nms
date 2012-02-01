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
using System.Threading;
using System.Collections.Generic;
using System.Collections.Specialized;
using Apache.NMS.ActiveMQ.Commands;
using Apache.NMS.ActiveMQ.Util;
using Apache.NMS.Util;

namespace Apache.NMS.ActiveMQ
{
	public enum AckType
	{
		DeliveredAck = 0, // Message delivered but not consumed
		PoisonAck = 1, // Message could not be processed due to poison pill but discard anyway
		ConsumedAck = 2, // Message consumed, discard
		RedeliveredAck = 3, // Message has been Redelivered and is not yet poisoned.
		IndividualAck = 4 // Only the given message is to be treated as consumed.
	}

	/// <summary>
	/// An object capable of receiving messages from some destination
	/// </summary>
	public class MessageConsumer : IMessageConsumer, IDispatcher
	{
		private readonly MessageTransformation messageTransformation;
		private readonly MessageDispatchChannel unconsumedMessages;
		private readonly LinkedList<MessageDispatch> dispatchedMessages = new LinkedList<MessageDispatch>();
		private readonly ConsumerInfo info;
		private Session session;

		private MessageAck pendingAck = null;

		private readonly Atomic<bool> started = new Atomic<bool>();
		private readonly Atomic<bool> deliveringAcks = new Atomic<bool>();

		private int redeliveryTimeout = 500;
		protected bool disposed = false;
		private long lastDeliveredSequenceId = 0;
		private int deliveredCounter = 0;
		private int additionalWindowSize = 0;
		private long redeliveryDelay = 0;
		private int dispatchedCount = 0;
		private volatile bool synchronizationRegistered = false;
		private bool clearDispatchList = false;
		private bool inProgressClearRequiredFlag;

        private Exception failureError;

		private const int DEFAULT_REDELIVERY_DELAY = 0;
		private const int DEFAULT_MAX_REDELIVERIES = 5;

		private event MessageListener listener;

		private IRedeliveryPolicy redeliveryPolicy;

		// Constructor internal to prevent clients from creating an instance.
		internal MessageConsumer(Session session, ConsumerId id, ActiveMQDestination destination,
								 String name, String selector, int prefetch, int maxPendingMessageCount,
								 bool noLocal, bool browser, bool dispatchAsync )
		{
			if(destination == null)
			{
				throw new InvalidDestinationException("Consumer cannot receive on Null Destinations.");
			}

			this.session = session;
			this.redeliveryPolicy = this.session.Connection.RedeliveryPolicy;
			this.messageTransformation = this.session.Connection.MessageTransformation;

			if(session.Connection.MessagePrioritySupported)
			{
				this.unconsumedMessages = new SimplePriorityMessageDispatchChannel();
			}
			else
			{
				this.unconsumedMessages = new FifoMessageDispatchChannel();
			}

			this.info = new ConsumerInfo();
			this.info.ConsumerId = id;
			this.info.Destination = destination;
			this.info.SubscriptionName = name;
			this.info.Selector = selector;
			this.info.PrefetchSize = prefetch;
			this.info.MaximumPendingMessageLimit = maxPendingMessageCount;
			this.info.NoLocal = noLocal;
			this.info.Browser = browser;
			this.info.DispatchAsync = dispatchAsync;
			this.info.Retroactive = session.Retroactive;
			this.info.Exclusive = session.Exclusive;
			this.info.Priority = session.Priority;

			// If the destination contained a URI query, then use it to set public properties
			// on the ConsumerInfo
			if(destination.Options != null)
			{
				// Get options prefixed with "consumer.*"
				StringDictionary options = URISupport.GetProperties(destination.Options, "consumer.");
				// Extract out custom extension options "consumer.nms.*"
				StringDictionary customConsumerOptions = URISupport.ExtractProperties(options, "nms.");

				URISupport.SetProperties(this.info, options);
				URISupport.SetProperties(this, customConsumerOptions, "nms.");
			}
		}

		~MessageConsumer()
		{
			Dispose(false);
		}

		#region Property Accessors

		public long LastDeliveredSequenceId
		{
			get { return this.lastDeliveredSequenceId; }
		}

		public ConsumerId ConsumerId
		{
			get { return this.info.ConsumerId; }
		}

		public ConsumerInfo ConsumerInfo
		{
			get { return this.info; }
		}

		public int RedeliveryTimeout
		{
			get { return redeliveryTimeout; }
			set { redeliveryTimeout = value; }
		}

		public int PrefetchSize
		{
			get { return this.info.PrefetchSize; }
		}

		public IRedeliveryPolicy RedeliveryPolicy
		{
			get { return this.redeliveryPolicy; }
			set { this.redeliveryPolicy = value; }
		}

		public long UnconsumedMessageCount
		{
			get { return this.unconsumedMessages.Count; }
		}

		// Custom Options
		private bool ignoreExpiration = false;
		public bool IgnoreExpiration
		{
			get { return ignoreExpiration; }
			set { ignoreExpiration = value; }
		}

        public Exception FailureError
        {
            get { return this.failureError; }
            set { this.failureError = value; }
        }

		#endregion

		#region IMessageConsumer Members

		private ConsumerTransformerDelegate consumerTransformer;
		/// <summary>
		/// A Delegate that is called each time a Message is dispatched to allow the client to do
		/// any necessary transformations on the received message before it is delivered.
		/// </summary>
		public ConsumerTransformerDelegate ConsumerTransformer
		{
			get { return this.consumerTransformer; }
			set { this.consumerTransformer = value; }
		}

		public event MessageListener Listener
		{
			add
			{
				CheckClosed();

				if(this.PrefetchSize == 0)
				{
					throw new NMSException("Cannot set Asynchronous Listener on a Consumer with a zero Prefetch size");
				}

				bool wasStarted = this.session.Started;

				if(wasStarted == true)
				{
					this.session.Stop();
				}

				listener += value;
				this.session.Redispatch(this.unconsumedMessages);

				if(wasStarted == true)
				{
					this.session.Start();
				}
			}
			remove { listener -= value; }
		}

		public IMessage Receive()
		{
			CheckClosed();
			CheckMessageListener();

			SendPullRequest(0);
			MessageDispatch dispatch = this.Dequeue(TimeSpan.FromMilliseconds(-1));

			if(dispatch == null)
			{
				return null;
			}

			BeforeMessageIsConsumed(dispatch);
			AfterMessageIsConsumed(dispatch, false);

			return CreateActiveMQMessage(dispatch);
		}

		public IMessage Receive(TimeSpan timeout)
		{
			CheckClosed();
			CheckMessageListener();

			MessageDispatch dispatch = null;
			SendPullRequest((long) timeout.TotalMilliseconds);

			if(this.PrefetchSize == 0)
			{
				dispatch = this.Dequeue(TimeSpan.FromMilliseconds(-1));
			}
			else
			{
				dispatch = this.Dequeue(timeout);
			}

			if(dispatch == null)
			{
				return null;
			}

			BeforeMessageIsConsumed(dispatch);
			AfterMessageIsConsumed(dispatch, false);

			return CreateActiveMQMessage(dispatch);
		}

		public IMessage ReceiveNoWait()
		{
			CheckClosed();
			CheckMessageListener();

			MessageDispatch dispatch = null;
			SendPullRequest(-1);

			if(this.PrefetchSize == 0)
			{
				dispatch = this.Dequeue(TimeSpan.FromMilliseconds(-1));
			}
			else
			{
				dispatch = this.Dequeue(TimeSpan.Zero);
			}

			if(dispatch == null)
			{
				return null;
			}

			BeforeMessageIsConsumed(dispatch);
			AfterMessageIsConsumed(dispatch, false);

			return CreateActiveMQMessage(dispatch);
		}

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		protected void Dispose(bool disposing)
		{
			if(disposed)
			{
				return;
			}

			if(disposing)
			{
				// Dispose managed code here.
			}

			try
			{
				Close();
			}
			catch
			{
				// Ignore network errors.
			}

			disposed = true;
		}

		public void Close()
		{
			if(!this.unconsumedMessages.Closed)
			{
				if(this.session.IsTransacted && this.session.TransactionContext.InTransaction)
				{
					this.session.TransactionContext.AddSynchronization(new ConsumerCloseSynchronization(this));
				}
				else
				{
					this.DoClose();
				}
			}
		}

		internal void DoClose()
		{
	        Shutdown();
			RemoveInfo removeCommand = new RemoveInfo();
			removeCommand.ObjectId = this.ConsumerId;
	        if (Tracer.IsDebugEnabled) 
			{
				Tracer.DebugFormat("Remove of Consumer[{0}] sent last delivered Id[{1}].", 
				                   this.ConsumerId, this.lastDeliveredSequenceId);
	        }
	        removeCommand.LastDeliveredSequenceId = lastDeliveredSequenceId;
	        this.session.Connection.Oneway(removeCommand);
		}
		
		/// <summary>
		/// Called from the parent Session of this Consumer to indicate that its
		/// parent session is closing and this Consumer should close down but not
		/// send any message to the Broker as the parent close will take care of
		/// removing its child resources at the broker.
		/// </summary>
		internal void Shutdown()
		{
			if(!this.unconsumedMessages.Closed)
			{
				if(Tracer.IsDebugEnabled)
				{
					Tracer.DebugFormat("Shutdown of Consumer[{0}] started.", ConsumerId);
				}

				// Do we have any acks we need to send out before closing?
				// Ack any delivered messages now.
				if(!this.session.IsTransacted)
				{
					DeliverAcks();
					if(this.IsAutoAcknowledgeBatch)
					{
						Acknowledge();
					}
				}

				if(!this.session.IsTransacted)
				{
					lock(this.dispatchedMessages)
					{
						dispatchedMessages.Clear();
					}
				}

				this.session.RemoveConsumer(this.ConsumerId);
				this.unconsumedMessages.Close();

				if(Tracer.IsDebugEnabled)
				{
					Tracer.DebugFormat("Shutdown of Consumer[{0}] completed.", ConsumerId);
				}
			}			
		}

		#endregion

		protected void SendPullRequest(long timeout)
		{
			if(this.info.PrefetchSize == 0 && this.unconsumedMessages.Empty)
			{
				MessagePull messagePull = new MessagePull();
				messagePull.ConsumerId = this.info.ConsumerId;
				messagePull.Destination = this.info.Destination;
				messagePull.Timeout = timeout;
				messagePull.ResponseRequired = false;

				if(Tracer.IsDebugEnabled)
				{
					Tracer.Debug("Sending MessagePull: " + messagePull);
				}

				session.Connection.Oneway(messagePull);
			}
		}

		protected void DoIndividualAcknowledge(ActiveMQMessage message)
		{
			MessageDispatch dispatch = null;

			lock(this.dispatchedMessages)
			{
				foreach(MessageDispatch originalDispatch in this.dispatchedMessages)
				{
					if(originalDispatch.Message.MessageId.Equals(message.MessageId))
					{
						dispatch = originalDispatch;
						this.dispatchedMessages.Remove(originalDispatch);
						break;
					}
				}
			}

			if(dispatch == null)
			{
				Tracer.DebugFormat("Attempt to Ack MessageId[{0}] failed because the original dispatch is not in the Dispatch List", message.MessageId);
				return;
			}

			MessageAck ack = new MessageAck();

			ack.AckType = (byte) AckType.IndividualAck;
			ack.ConsumerId = this.info.ConsumerId;
			ack.Destination = dispatch.Destination;
			ack.LastMessageId = dispatch.Message.MessageId;
			ack.MessageCount = 1;

			Tracer.Debug("Sending Individual Ack for MessageId: " + ack.LastMessageId.ToString());
			this.session.SendAck(ack);
		}

		protected void DoNothingAcknowledge(ActiveMQMessage message)
		{
		}

		protected void DoClientAcknowledge(ActiveMQMessage message)
		{
			this.CheckClosed();
			Tracer.Debug("Sending Client Ack:");
			this.session.Acknowledge();
		}

		public void Start()
		{
			if(this.unconsumedMessages.Closed)
			{
				return;
			}

			this.started.Value = true;
			this.unconsumedMessages.Start();
			this.session.Executor.Wakeup();
		}

		public void Stop()
		{
			this.started.Value = false;
			this.unconsumedMessages.Stop();
		}

		internal void InProgressClearRequired()
		{
			inProgressClearRequiredFlag = true;
			// deal with delivered messages async to avoid lock contention with in progress acks
			clearDispatchList = true;
		}

		internal void ClearMessagesInProgress()
		{
			if(inProgressClearRequiredFlag)
			{
				// Called from a thread in the ThreadPool, so we wait until we can
				// get a lock on the unconsumed list then we clear it.
				lock(this.unconsumedMessages)
				{
					if(inProgressClearRequiredFlag)
					{
						if(Tracer.IsDebugEnabled)
						{
							Tracer.Debug(this.ConsumerId + " clearing dispatched list (" +
										 this.unconsumedMessages.Count + ") on transport interrupt");
						}

						this.unconsumedMessages.Clear();

						// allow dispatch on this connection to resume
						this.session.Connection.TransportInterruptionProcessingComplete();
						this.inProgressClearRequiredFlag = false;
					}
				}
			}
		}

		public void DeliverAcks()
		{
			MessageAck ack = null;

			if(this.deliveringAcks.CompareAndSet(false, true))
			{
				if(this.IsAutoAcknowledgeEach)
				{
					lock(this.dispatchedMessages)
					{
						ack = MakeAckForAllDeliveredMessages(AckType.DeliveredAck);
						if(ack != null)
						{
                            Tracer.Debug("Consumer - DeliverAcks clearing the Dispatch list");
							this.dispatchedMessages.Clear();
						}
						else
						{
							ack = this.pendingAck;
							this.pendingAck = null;
						}
					}
				}
				else if(pendingAck != null && pendingAck.AckType == (byte) AckType.ConsumedAck)
				{
					ack = pendingAck;
					pendingAck = null;
				}

				if(ack != null)
				{
					MessageAck ackToSend = ack;

					try
					{
						this.session.SendAck(ackToSend);
					}
					catch(Exception e)
					{
						Tracer.DebugFormat("{0} : Failed to send ack, {1}", this.info.ConsumerId, e);
					}
				}
				else
				{
					this.deliveringAcks.Value = false;
				}
			}
		}

		public virtual void Dispatch(MessageDispatch dispatch)
		{
			MessageListener listener = this.listener;

			try
			{
				lock(this.unconsumedMessages.SyncRoot)
				{
					if(this.clearDispatchList)
					{
						// we are reconnecting so lets flush the in progress messages
						this.clearDispatchList = false;
						this.unconsumedMessages.Clear();

						if(this.pendingAck != null && this.pendingAck.AckType == (byte) AckType.DeliveredAck)
						{
							// on resumption a pending delivered ack will be out of sync with
							// re-deliveries.
							if(Tracer.IsDebugEnabled)
							{
								Tracer.Debug("removing pending delivered ack on transport interupt: " + pendingAck);
							}
							this.pendingAck = null;
						}
					}

					if(!this.unconsumedMessages.Closed)
					{
						if(listener != null && this.unconsumedMessages.Running)
						{
							ActiveMQMessage message = CreateActiveMQMessage(dispatch);

							this.BeforeMessageIsConsumed(dispatch);

							try
							{
								bool expired = (!IgnoreExpiration && message.IsExpired());

								if(!expired)
								{
									listener(message);
								}

								this.AfterMessageIsConsumed(dispatch, expired);
							}
							catch(Exception e)
							{
								if(IsAutoAcknowledgeBatch || IsAutoAcknowledgeEach || IsIndividualAcknowledge)
								{
									// Redeliver the message
								}
								else
								{
									// Transacted or Client ack: Deliver the next message.
									this.AfterMessageIsConsumed(dispatch, false);
								}

								Tracer.Error(this.info.ConsumerId + " Exception while processing message: " + e);

								// If aborted we stop the abort here and let normal processing resume.
								// This allows the session to shutdown normally and ack all messages
								// that have outstanding acks in this consumer.
								if( (Thread.CurrentThread.ThreadState & ThreadState.AbortRequested) == ThreadState.AbortRequested)
								{
									Thread.ResetAbort();
								}
							}
						}
						else
						{
							this.unconsumedMessages.Enqueue(dispatch);
						}
					}
				}

				if(++dispatchedCount % 1000 == 0)
				{
					dispatchedCount = 0;
					Thread.Sleep(1);
				}
			}
			catch(Exception e)
			{
				this.session.Connection.OnSessionException(this.session, e);
			}
		}

		public bool Iterate()
		{
			if(this.listener != null)
			{
				MessageDispatch dispatch = this.unconsumedMessages.DequeueNoWait();
				if(dispatch != null)
				{
					try
					{
						ActiveMQMessage message = CreateActiveMQMessage(dispatch);
						BeforeMessageIsConsumed(dispatch);
						listener(message);
						AfterMessageIsConsumed(dispatch, false);
					}
					catch(NMSException e)
					{
						this.session.Connection.OnSessionException(this.session, e);
					}

					return true;
				}
			}

			return false;
		}

		/// <summary>
		/// Used to get an enqueued message from the unconsumedMessages list. The
		/// amount of time this method blocks is based on the timeout value.  if
		/// timeout == Timeout.Infinite then it blocks until a message is received.
		/// if timeout == 0 then it it tries to not block at all, it returns a
		/// message if it is available if timeout > 0 then it blocks up to timeout
		/// amount of time.  Expired messages will consumed by this method.
		/// </summary>
		/// <param name="timeout">
		/// A <see cref="System.TimeSpan"/>
		/// </param>
		/// <returns>
		/// A <see cref="MessageDispatch"/>
		/// </returns>
		private MessageDispatch Dequeue(TimeSpan timeout)
		{
			DateTime deadline = DateTime.Now;

			if(timeout > TimeSpan.Zero)
			{
				deadline += timeout;
			}

			while(true)
			{
				MessageDispatch dispatch = this.unconsumedMessages.Dequeue(timeout);

				// Grab a single date/time for calculations to avoid timing errors.
				DateTime dispatchTime = DateTime.Now;

				if(dispatch == null)
				{
					if(timeout > TimeSpan.Zero && !this.unconsumedMessages.Closed)
					{
						if(dispatchTime > deadline)
						{
							// Out of time.
							timeout = TimeSpan.Zero;
						}
						else
						{
							// Adjust the timeout to the remaining time.
							timeout = deadline - dispatchTime;
						}
					}
					else
					{
                        // Informs the caller of an error in the event that an async exception
                        // took down the parent connection.
                        if(this.failureError != null)
                        {
                            throw NMSExceptionSupport.Create(this.failureError);
                        }

						return null;
					}
				}
				else if(dispatch.Message == null)
				{
					return null;
				}
				else if(!IgnoreExpiration && dispatch.Message.IsExpired())
				{
					Tracer.DebugFormat("{0} received expired message: {1}", info.ConsumerId, dispatch.Message.MessageId);

					BeforeMessageIsConsumed(dispatch);
					AfterMessageIsConsumed(dispatch, true);
					// Refresh the dispatch time
					dispatchTime = DateTime.Now;

					if(timeout > TimeSpan.Zero && !this.unconsumedMessages.Closed)
					{
						if(dispatchTime > deadline)
						{
							// Out of time.
							timeout = TimeSpan.Zero;
						}
						else
						{
							// Adjust the timeout to the remaining time.
							timeout = deadline - dispatchTime;
						}
					}
				}
				else
				{
					return dispatch;
				}
			}
		}

		public void BeforeMessageIsConsumed(MessageDispatch dispatch)
		{
			this.lastDeliveredSequenceId = dispatch.Message.MessageId.BrokerSequenceId;

			if(!IsAutoAcknowledgeBatch)
			{
				lock(this.dispatchedMessages)
				{
					this.dispatchedMessages.AddFirst(dispatch);
				}

				if(this.session.IsTransacted)
				{
					this.AckLater(dispatch, AckType.DeliveredAck);
				}
			}
		}

		public void AfterMessageIsConsumed(MessageDispatch dispatch, bool expired)
		{
			if(this.unconsumedMessages.Closed)
			{
				return;
			}

			if(expired)
			{
				lock(this.dispatchedMessages)
				{
					this.dispatchedMessages.Remove(dispatch);
				}

				AckLater(dispatch, AckType.DeliveredAck);
			}
			else
			{
				if(this.session.IsTransacted)
				{
					// Do nothing.
				}
				else if(this.IsAutoAcknowledgeEach)
				{
					if(this.deliveringAcks.CompareAndSet(false, true))
					{
						lock(this.dispatchedMessages)
						{
							if(this.dispatchedMessages.Count != 0)
							{
								MessageAck ack = MakeAckForAllDeliveredMessages(AckType.ConsumedAck);
								if(ack != null)
								{
									this.dispatchedMessages.Clear();
									this.session.SendAck(ack);
								}
							}
						}
						this.deliveringAcks.Value = false;
					}
				}
				else if(this.IsAutoAcknowledgeBatch)
				{
					AckLater(dispatch, AckType.ConsumedAck);
				}
				else if(IsClientAcknowledge || IsIndividualAcknowledge)
				{
					bool messageAckedByConsumer = false;

					lock(this.dispatchedMessages)
					{
						messageAckedByConsumer = this.dispatchedMessages.Contains(dispatch);
					}

					if(messageAckedByConsumer)
					{
						AckLater(dispatch, AckType.DeliveredAck);
					}
				}
				else
				{
					throw new NMSException("Invalid session state.");
				}
			}
		}

		private MessageAck MakeAckForAllDeliveredMessages(AckType type)
		{
			lock(this.dispatchedMessages)
			{
				if(this.dispatchedMessages.Count == 0)
				{
					return null;
				}

				MessageDispatch dispatch = this.dispatchedMessages.First.Value;
				MessageAck ack = new MessageAck();

				ack.AckType = (byte) type;
				ack.ConsumerId = this.info.ConsumerId;
				ack.Destination = dispatch.Destination;
				ack.LastMessageId = dispatch.Message.MessageId;
				ack.MessageCount = this.dispatchedMessages.Count;
				ack.FirstMessageId = this.dispatchedMessages.Last.Value.Message.MessageId;

				return ack;
			}
		}

		private void AckLater(MessageDispatch dispatch, AckType type)
		{
			// Don't acknowledge now, but we may need to let the broker know the
			// consumer got the message to expand the pre-fetch window
			if(this.session.IsTransacted)
			{
				this.session.DoStartTransaction();

				if(!synchronizationRegistered)
				{
                    Tracer.DebugFormat("Consumer {0} Registering new MessageConsumerSynchronization",
                                       this.info.ConsumerId);
					this.synchronizationRegistered = true;
					this.session.TransactionContext.AddSynchronization(new MessageConsumerSynchronization(this));
				}
			}

			this.deliveredCounter++;

			MessageAck oldPendingAck = pendingAck;

			pendingAck = new MessageAck();
			pendingAck.AckType = (byte) type;
			pendingAck.ConsumerId = this.info.ConsumerId;
			pendingAck.Destination = dispatch.Destination;
			pendingAck.LastMessageId = dispatch.Message.MessageId;
			pendingAck.MessageCount = deliveredCounter;

			if(this.session.IsTransacted && this.session.TransactionContext.InTransaction)
			{
				pendingAck.TransactionId = this.session.TransactionContext.TransactionId;
			}

			if(oldPendingAck == null)
			{
				pendingAck.FirstMessageId = pendingAck.LastMessageId;
			}
			else if(oldPendingAck.AckType == pendingAck.AckType)
			{
				pendingAck.FirstMessageId = oldPendingAck.FirstMessageId;
			}
			else
			{
				// old pending ack being superseded by ack of another type, if is is not a delivered
				// ack and hence important, send it now so it is not lost.
				if(oldPendingAck.AckType != (byte) AckType.DeliveredAck)
				{
					if(Tracer.IsDebugEnabled)
					{
						Tracer.Debug("Sending old pending ack " + oldPendingAck + ", new pending: " + pendingAck);
					}

					this.session.Connection.Oneway(oldPendingAck);
				}
				else
				{
					if(Tracer.IsDebugEnabled)
					{
						Tracer.Debug("dropping old pending ack " + oldPendingAck + ", new pending: " + pendingAck);
					}
				}
			}

			if((0.5 * this.info.PrefetchSize) <= (this.deliveredCounter - this.additionalWindowSize))
			{
				this.session.Connection.Oneway(pendingAck);
				this.pendingAck = null;
				this.deliveredCounter = 0;
				this.additionalWindowSize = 0;
			}
		}

		internal void Acknowledge()
		{
			lock(this.dispatchedMessages)
			{
				// Acknowledge all messages so far.
				MessageAck ack = MakeAckForAllDeliveredMessages(AckType.ConsumedAck);

				if(ack == null)
				{
					return; // no msgs
				}

				if(this.session.IsTransacted)
				{
					this.session.DoStartTransaction();
					ack.TransactionId = this.session.TransactionContext.TransactionId;
				}

				this.session.SendAck(ack);
				this.pendingAck = null;

				// Adjust the counters
				this.deliveredCounter = Math.Max(0, this.deliveredCounter - this.dispatchedMessages.Count);
				this.additionalWindowSize = Math.Max(0, this.additionalWindowSize - this.dispatchedMessages.Count);

				if(!this.session.IsTransacted)
				{
					this.dispatchedMessages.Clear();
				}
			}
		}

		private void Commit()
		{
			lock(this.dispatchedMessages)
			{
				this.dispatchedMessages.Clear();
			}

			this.redeliveryDelay = 0;
		}

		private void Rollback()
		{
			lock(this.unconsumedMessages.SyncRoot)
			{
				lock(this.dispatchedMessages)
				{
					if(this.dispatchedMessages.Count == 0)
					{
                        Tracer.DebugFormat("Consumer {0} Rolled Back, no dispatched Messages",
                                           this.info.ConsumerId);
						return;
					}

					// Only increase the redelivery delay after the first redelivery..
					MessageDispatch lastMd = this.dispatchedMessages.First.Value;
					int currentRedeliveryCount = lastMd.Message.RedeliveryCounter;

					redeliveryDelay = this.redeliveryPolicy.RedeliveryDelay(currentRedeliveryCount);

					MessageId firstMsgId = this.dispatchedMessages.Last.Value.Message.MessageId;

					foreach(MessageDispatch dispatch in this.dispatchedMessages)
					{
						// Allow the message to update its internal to reflect a Rollback.
						dispatch.Message.OnMessageRollback();
					}

					if(this.redeliveryPolicy.MaximumRedeliveries >= 0 &&
					   lastMd.Message.RedeliveryCounter > this.redeliveryPolicy.MaximumRedeliveries)
					{
						// We need to NACK the messages so that they get sent to the DLQ.
						MessageAck ack = new MessageAck();

						ack.AckType = (byte) AckType.PoisonAck;
						ack.ConsumerId = this.info.ConsumerId;
						ack.Destination = lastMd.Destination;
						ack.LastMessageId = lastMd.Message.MessageId;
						ack.MessageCount = this.dispatchedMessages.Count;
						ack.FirstMessageId = firstMsgId;

						this.session.SendAck(ack);

						// Adjust the window size.
						additionalWindowSize = Math.Max(0, this.additionalWindowSize - this.dispatchedMessages.Count);

						this.redeliveryDelay = 0;
					}
					else
					{
						// We only send a RedeliveryAck after the first redelivery
						if(currentRedeliveryCount > 0)
						{
							MessageAck ack = new MessageAck();

							ack.AckType = (byte) AckType.RedeliveredAck;
							ack.ConsumerId = this.info.ConsumerId;
							ack.Destination = lastMd.Destination;
							ack.LastMessageId = lastMd.Message.MessageId;
							ack.MessageCount = this.dispatchedMessages.Count;
							ack.FirstMessageId = firstMsgId;

							this.session.SendAck(ack);
						}

						// stop the delivery of messages.
						this.unconsumedMessages.Stop();

                        if(Tracer.IsDebugEnabled)
                        {
                            Tracer.DebugFormat("Consumer {0} Rolled Back, Re-enque {1} messages",
                                               this.info.ConsumerId, this.dispatchedMessages.Count);
                        }

						foreach(MessageDispatch dispatch in this.dispatchedMessages)
						{
                            this.unconsumedMessages.EnqueueFirst(dispatch);
						}

						if(redeliveryDelay > 0 && !this.unconsumedMessages.Closed)
						{
							DateTime deadline = DateTime.Now.AddMilliseconds(redeliveryDelay);
							ThreadPool.QueueUserWorkItem(this.RollbackHelper, deadline);
						}
						else
						{
							Start();
						}
					}

					this.deliveredCounter -= this.dispatchedMessages.Count;
					this.dispatchedMessages.Clear();
				}
			}

			// Only redispatch if there's an async listener otherwise a synchronous
			// consumer will pull them from the local queue.
			if(this.listener != null)
			{
				this.session.Redispatch(this.unconsumedMessages);
			}
		}

		private void RollbackHelper(Object arg)
		{
			try
			{
				TimeSpan waitTime = (DateTime) arg - DateTime.Now;

				if(waitTime.CompareTo(TimeSpan.Zero) > 0)
				{
					Thread.Sleep(waitTime);
				}

				this.Start();
			}
			catch(Exception e)
			{
				if(!this.unconsumedMessages.Closed)
				{
					this.session.Connection.OnSessionException(this.session, e);
				}
			}
		}

		private ActiveMQMessage CreateActiveMQMessage(MessageDispatch dispatch)
		{
			ActiveMQMessage message = dispatch.Message.Clone() as ActiveMQMessage;

			if(this.ConsumerTransformer != null)
			{
				IMessage newMessage = ConsumerTransformer(this.session, this, message);
				if(newMessage != null)
				{
					message = this.messageTransformation.TransformMessage<ActiveMQMessage>(newMessage);
				}
			}

			message.Connection = this.session.Connection;

			if(IsClientAcknowledge)
			{
				message.Acknowledger += new AcknowledgeHandler(DoClientAcknowledge);
			}
			else if(IsIndividualAcknowledge)
			{
				message.Acknowledger += new AcknowledgeHandler(DoIndividualAcknowledge);
			}
			else
			{
				message.Acknowledger += new AcknowledgeHandler(DoNothingAcknowledge);
			}

			return message;
		}

		private void CheckClosed()
		{
			if(this.unconsumedMessages.Closed)
			{
				throw new NMSException("The Consumer has been Closed");
			}
		}

		private void CheckMessageListener()
		{
			if(this.listener != null)
			{
				throw new NMSException("Cannot set Async listeners on Consumers with a prefetch limit of zero");
			}
		}

		private bool IsAutoAcknowledgeEach
		{
			get
			{
				return this.session.IsAutoAcknowledge ||
					   (this.session.IsDupsOkAcknowledge && this.info.Destination.IsQueue);
			}
		}

		private bool IsAutoAcknowledgeBatch
		{
			get { return this.session.IsDupsOkAcknowledge && !this.info.Destination.IsQueue; }
		}

		private bool IsIndividualAcknowledge
		{
			get { return this.session.IsIndividualAcknowledge; }
		}

		private bool IsClientAcknowledge
		{
			get { return this.session.IsClientAcknowledge; }
		}

		#region Nested ISyncronization Types

		class MessageConsumerSynchronization : ISynchronization
		{
			private readonly MessageConsumer consumer;

			public MessageConsumerSynchronization(MessageConsumer consumer)
			{
				this.consumer = consumer;
			}

			public void BeforeEnd()
			{
                Tracer.DebugFormat("MessageConsumerSynchronization - BeforeEnd Called for Consumer {0}.",
                                   this.consumer.ConsumerId);
				this.consumer.Acknowledge();
				this.consumer.synchronizationRegistered = false;
			}

			public void AfterCommit()
			{
                Tracer.DebugFormat("MessageConsumerSynchronization - AfterCommit Called for Consumer {0}.",
                                   this.consumer.ConsumerId);
				this.consumer.Commit();
				this.consumer.synchronizationRegistered = false;
			}

			public void AfterRollback()
			{
                Tracer.DebugFormat("MessageConsumerSynchronization - AfterRollback Called for Consumer {0}.",
                                   this.consumer.ConsumerId);
				this.consumer.Rollback();
				this.consumer.synchronizationRegistered = false;
			}
		}

		class ConsumerCloseSynchronization : ISynchronization
		{
			private readonly MessageConsumer consumer;

			public ConsumerCloseSynchronization(MessageConsumer consumer)
			{
				this.consumer = consumer;
			}

			public void BeforeEnd()
			{
			}

			public void AfterCommit()
			{
				this.consumer.DoClose();
			}

			public void AfterRollback()
			{
				this.consumer.DoClose();
			}
		}

		#endregion
	}
}
