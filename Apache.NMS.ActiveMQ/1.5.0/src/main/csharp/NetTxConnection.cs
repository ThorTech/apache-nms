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
using Apache.NMS.ActiveMQ.Transport;
using Apache.NMS.ActiveMQ.Util;

namespace Apache.NMS.ActiveMQ
{
    /// <summary>
    /// Extends the basic Connection class to provide a transacted Connection
    /// instance that operates within the bounds of a .NET Scoped Transaction.
    ///
    /// The default Session creation methods of Connection are overriden here
    /// to always return a TX capable session instance.
    /// </summary>
    public class NetTxConnection : Connection, INetTxConnection
    {
        private NetTxRecoveryPolicy recoveryPolicy = new NetTxRecoveryPolicy();

        public NetTxConnection(Uri connectionUri, ITransport transport, IdGenerator clientIdGenerator)
            : base(connectionUri, transport, clientIdGenerator)
        {
        }

        public INetTxSession CreateNetTxSession()
        {
            return (INetTxSession) CreateSession(AcknowledgementMode.Transactional);
        }

        protected override Session CreateAtiveMQSession(AcknowledgementMode ackMode)
        {
            CheckConnected();
            return new NetTxSession(this, NextSessionId);
        }

        public NetTxRecoveryPolicy RecoveryPolicy
        {
            get { return this.recoveryPolicy; }
            set { this.recoveryPolicy = value; }
        }

        internal Guid ResourceManagerGuid
        {
            get { return GuidFromId(this.ResourceManagerId); }
        }

        private static Guid GuidFromId(string id)
        {
            // Remove the ID: prefix, that's non-unique to be sure
            string resId = id.TrimStart("ID:".ToCharArray());

            // Remaing parts should be host-port-timestamp-instance:sequence
            string[] parts = resId.Split(":-".ToCharArray());

            // We don't use the hostname here, just the remaining bits.
            int a = Int32.Parse(parts[1]);
            short b = Int16.Parse(parts[3]);
            short c = Int16.Parse(parts[4]);
            byte[] d = System.BitConverter.GetBytes(Int64.Parse(parts[2]));

            return new Guid(a, b, c, d);
        }
    }
}

