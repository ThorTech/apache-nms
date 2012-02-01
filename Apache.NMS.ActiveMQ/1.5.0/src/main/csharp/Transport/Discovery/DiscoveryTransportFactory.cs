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
using Apache.NMS.ActiveMQ.Transport.Discovery.Multicast;
using Apache.NMS.ActiveMQ.Transport.Tcp;
using Apache.NMS.Util;

namespace Apache.NMS.ActiveMQ.Transport.Discovery
{
    [ActiveMQTransportFactory("discovery")]
	public class DiscoveryTransportFactory : ITransportFactory
	{
		private const int TIMEOUT_IN_SECONDS = 20;

		private static Uri discoveredUri;
		private static readonly MulticastDiscoveryAgent agent;
		private static string currentServiceName;
		private static readonly object uriLock = new object();
		private static readonly AutoResetEvent discoveredUriEvent = new AutoResetEvent(false);
		private static event ExceptionListener OnException;

		static DiscoveryTransportFactory()
		{
			DiscoveryTransportFactory.OnException += TransportFactory.HandleException;
			agent = new MulticastDiscoveryAgent();
			agent.OnNewServiceFound += agent_OnNewServiceFound;
			agent.OnServiceRemoved += agent_OnServiceRemoved;
		}

		public DiscoveryTransportFactory()
		{
			lock(uriLock)
			{
				currentServiceName = String.Empty;
			}
		}

		public static Uri DiscoveredUri
		{
			get { lock(uriLock) { return discoveredUri; } }
			set { lock(uriLock) { discoveredUri = value; } }
		}

		private static void agent_OnNewServiceFound(string brokerName, string serviceName)
		{
			lock(uriLock)
			{
				if(discoveredUri == null)
				{
					currentServiceName = serviceName;
					discoveredUri = new Uri(currentServiceName);
				}
			}

			// This will end the wait in the CreateTransport method.
			discoveredUriEvent.Set();
		}

		private static void agent_OnServiceRemoved(string brokerName, string serviceName)
		{
			lock(uriLock)
			{
				if(serviceName == currentServiceName)
				{
					DiscoveredUri = null;
					DiscoveryTransportFactory.OnException(new Exception("Broker connection is no longer valid."));
				}
			}
		}

		#region Overloaded ITransportFactory Members

		public ITransport CreateTransport(Uri location)
		{
			URISupport.CompositeData cd = URISupport.ParseComposite(location);

			if(cd.Components.Length > 0)
			{
				agent.DiscoveryURI = cd.Components[0];
			}

			if(!agent.IsStarted)
			{
				agent.Start();
			}

			Uri hostUri = DiscoveredUri;

			if(null == hostUri)
			{
				// If a new broker is found the agent will fire an event which will result in discoveredUri being set.
				discoveredUriEvent.WaitOne(TIMEOUT_IN_SECONDS * 1000, true);
				hostUri = DiscoveredUri;
				if(null == hostUri)
				{
					throw new NMSConnectionException(String.Format("Unable to find a connection to {0} before the timeout period expired.", location.ToString()));
				}
			}

			TcpTransportFactory tcpTransFactory = new TcpTransportFactory();
			return tcpTransFactory.CompositeConnect(new Uri(hostUri + location.Query));
		}

		public ITransport CompositeConnect(Uri location)
		{
			return CreateTransport(location);
		}


		public ITransport CompositeConnect(Uri location, SetTransport setTransport)
		{
			return CreateTransport(location);
		}

		#endregion
	}
}
