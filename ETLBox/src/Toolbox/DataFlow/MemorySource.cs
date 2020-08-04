﻿using ETLBox.ControlFlow;
using ETLBox.Exceptions;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace ETLBox.DataFlow.Connectors
{
    /// <summary>
    /// Reads data from a memory source. While reading the data from the list, data is also asnychronously posted into the targets.
    /// Data is read a as string from the source and dynamically converted into the corresponding data format.
    /// </summary>
    public class MemorySource<TOutput> : DataFlowSource<TOutput>
    {
        #region Public properties
        public override string TaskName => $"Read data from memory";
        public IEnumerable<TOutput> Data { get; set; }
        public IList<TOutput> DataAsList
        {
            get
            {
                return Data as IList<TOutput>;
            }
            set
            {
                Data = value;
            }
        }

        #endregion

        #region Constructors

        public MemorySource()
        {
            Data = new List<TOutput>();
        }

        public MemorySource(IEnumerable<TOutput> data)
        {
            Data = data;
        }

        #endregion

        #region Implement abstract methods

        protected override void OnExecutionDoSynchronousWork() { }

        protected override void OnExecutionDoAsyncWork()
        {
            NLogStartOnce();
            ReadAllRecords();
            Buffer.Complete();
        }

        protected override void InitComponent() { }

        protected override void CleanUpOnSuccess()
        {
            NLogFinishOnce();
        }

        protected override void CleanUpOnFaulted(Exception e) { }

        #endregion

        #region Implementation

        private void ReadAllRecords()
        {
            foreach (TOutput record in Data)
            {
                if (!Buffer.SendAsync(record).Result)
                    throw Exception;
                LogProgress();
            }
        }

        #endregion
    }

    /// <summary>
    /// Reads data from a memory source. While reading the data from the file, data is also asnychronously posted into the targets.
    /// MemorySource as a nongeneric type always return a dynamic object as output. If you need typed output, use
    /// the MemorySource&lt;TOutput&gt; object instead.
    /// </summary>
    /// <see cref="MemorySource{TOutput}"/>
    public class MemorySource : MemorySource<ExpandoObject>
    {
        public MemorySource() : base() { }
        public MemorySource(IList<ExpandoObject> data) : base(data) { }
    }
}
