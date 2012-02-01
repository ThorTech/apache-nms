/*
 * Licensed to the Apache Software Foundation (ASF) under one or more
 * contributor license agreements.  See the NOTICE file distributed with
 * this work for additional information regarding copyright ownership.
 * The ASF licenses this file to You under the Apache License, Version 2.0
 * (the "License"); you may not use this file except in compliance with
 * the License.  You may obtain a copy of the License at
 *
 *      http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using Apache.NMS.ActiveMQ.Commands;
using Apache.NMS.Util;

namespace Apache.NMS.ActiveMQ.State
{
	public class TransactionState
	{

		private readonly List<Command> commands = new List<Command>();
		private readonly TransactionId id;
		private readonly Atomic<bool> _shutdown = new Atomic<bool>(false);
		private bool prepared;
		private int preparedResult;

		public TransactionState(TransactionId id)
		{
			this.id = id;
		}

		public override String ToString()
		{
			return id.ToString();
		}

		public void addCommand(Command operation)
		{
			checkShutdown();
			commands.Add(operation);
		}

		public List<Command> Commands
		{
			get
			{
				return commands;
			}
		}

		private void checkShutdown()
		{
			if(_shutdown.Value)
			{
				throw new ApplicationException("Disposed");
			}
		}

		public void shutdown()
		{
			_shutdown.Value = true;
		}

		public TransactionId getId()
		{
			return id;
		}

		public bool Prepared
		{
			get
			{
				return prepared;
			}
			set
			{
				prepared = value;
			}
		}

		public int PreparedResult
		{
			get
			{
				return preparedResult;
			}
			set
			{
				preparedResult = value;
			}
		}
	}
}
