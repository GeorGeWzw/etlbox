﻿using ETLBox.ControlFlow;
using ETLBox.Exceptions;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using TSQL.Clauses;

namespace ETLBox.DataFlow
{
    public abstract class DataFlowTask : GenericTask, ITask
    {
        #region Component properties

        public int MaxBufferSize
        {
            get
            {
                return _maxBufferSize > 0 ? _maxBufferSize : DataFlow.MaxBufferSize;
            }
            set
            {
                _maxBufferSize = value;
            }
        }

        protected int _maxBufferSize = -1;

        #endregion

        #region Linking

        public List<DataFlowTask> Predecessors { get; set; } = new List<DataFlowTask>();
        public List<DataFlowTask> Successors { get; set; } = new List<DataFlowTask>();

        public Task Completion { get; protected set; }
        protected virtual Task BufferCompletion { get; }
        protected Task PredecessorCompletion { get; set; }

        protected bool WereBufferInitialized;
        protected bool ReadyForProcessing;
        protected Dictionary<DataFlowTask, bool> WasLinked = new Dictionary<DataFlowTask, bool>();
        internal Dictionary<DataFlowTask, LinkPredicates> LinkPredicates = new Dictionary<DataFlowTask, LinkPredicates>();

        protected IDataFlowSource<T> InternalLinkTo<T>(IDataFlowDestination target, object predicate = null, object voidPredicate = null)
        {
            var t = target as DataFlowTask;
            LinkPredicates.Add(t, new LinkPredicates(predicate, voidPredicate));
            this.Successors.Add(t);
            t.Predecessors.Add(this);
            var res = target as IDataFlowSource<T>;
            return res;
        }

        protected void LinkBuffersRecursively()
        {
            foreach (DataFlowTask predecessor in Predecessors)
            {
                if (!predecessor.WasLinked.ContainsKey(this))
                {
                    LinkPredicates predicate = null;
                    LinkPredicates.TryGetValue(this, out predicate);
                    predecessor.LinkBuffers(this, predicate);
                    predecessor.WasLinked.Add(this, true);
                    predecessor.LinkBuffersRecursively();
                }
            }
            foreach (DataFlowTask successor in Successors)
            {
                if (!WasLinked.ContainsKey(successor))
                {
                    LinkPredicates predicate = null;
                    LinkPredicates.TryGetValue(successor, out predicate);
                    LinkBuffers(successor, predicate);
                    WasLinked.Add(successor, true);
                    successor.LinkBuffersRecursively();
                }
            }
        }
        internal virtual void LinkBuffers(DataFlowTask successor, LinkPredicates predicate)
        {
            //A destination doesn't implement this
            throw new NotImplementedException("This component can't be used to link to something");
        }

        #endregion

        #region Network initialization

        protected void InitNetworkRecursively()
        {
            InitBufferRecursively();
            LinkBuffersRecursively();
            SetCompletionTaskRecursively();
            RunComponentInitializationRecursively();
        }


        protected void InitBufferRecursively()
        {
            foreach (DataFlowTask predecessor in Predecessors)
                if (!predecessor.WereBufferInitialized)
                    predecessor.InitBufferRecursively();

            if (!WereBufferInitialized)
            {
                InitBufferObjects();
                WereBufferInitialized = true;
            }

            foreach (DataFlowTask successor in Successors)
                if (!successor.WereBufferInitialized)
                    successor.InitBufferRecursively();
        }

        protected virtual void InitBufferObjects() { } //abstract

        protected void RunComponentInitializationRecursively()
        {
            foreach (DataFlowTask predecessor in Predecessors)
                if (!predecessor.ReadyForProcessing)
                    predecessor.RunComponentInitializationRecursively();

            if (!ReadyForProcessing)
            {
                LetErrorSourceWaitForInput();
                InitComponent();
                ReadyForProcessing = true;
            }

            foreach (DataFlowTask successor in Successors)
                if (!successor.ReadyForProcessing)
                    successor.RunComponentInitializationRecursively();
        }


        protected virtual void InitComponent() { } //abstract

        #endregion

        #region Completion tasks handling

        protected void SetCompletionTaskRecursively()
        {
            foreach (DataFlowTask predecessor in Predecessors)
                if (predecessor.Completion == null)
                    predecessor.SetCompletionTaskRecursively();

            if (Completion == null)
            {
                List<Task> PredecessorCompletionTasks = CollectCompletionFromPredecessors();
                if (PredecessorCompletionTasks.Count > 0)
                {
                    PredecessorCompletion = Task.WhenAll(PredecessorCompletionTasks).ContinueWith(CompleteOrFaultOnPredecessorCompletion);
                    Completion = Task.WhenAll(PredecessorCompletion, BufferCompletion).ContinueWith(CompleteOrFaultCompletion);
                }
            }

            foreach (DataFlowTask successor in Successors)
                if (successor.Completion == null)
                    successor.SetCompletionTaskRecursively();
        }

        private List<Task> CollectCompletionFromPredecessors()
        {
            List<Task> CompletionTasks = new List<Task>();
            foreach (DataFlowTask pre in Predecessors)
            {
                CompletionTasks.Add(pre.Completion);
                CompletionTasks.Add(pre.BufferCompletion);
            }
            return CompletionTasks;
        }

        /// <summary>
        /// Predecessor completion task (Buffer of predecessors and Completion of predecessors) ran to completion or are faulted.
        /// Now complete or fault the current buffer.
        /// </summary>
        /// <param name="t">t is the continuation of Task.WhenAll of the predecessors buffer and predecessor completion tasks</param>
        protected void CompleteOrFaultOnPredecessorCompletion(Task t)
        {
            if (t.IsFaulted)
            {
                FaultBuffer(t.Exception.Flatten());
                throw t.Exception.Flatten();
            }
            else
            {
                CompleteBuffer();
            }
        }

        protected virtual void CompleteBuffer() { } //abstract
        protected virtual void FaultBuffer(Exception e) { } //abstract

        protected void CompleteOrFaultCompletion(Task t)
        {
            LetErrorSourceFinishUp();
            if (t.IsFaulted)
            {
                CleanUpOnFaulted(t.Exception.Flatten());
                throw t.Exception.Flatten(); //Will fault Completion task
            }
            else
            {
                CleanUpOnSuccess();
            }
        }

        protected virtual void CleanUpOnSuccess() { }

        protected virtual void CleanUpOnFaulted(Exception e) {  }

        protected void FaultPredecessorsRecursively(Exception e)
        {
            Exception = e;
            FaultBuffer(e);
            foreach (DataFlowTask pre in Predecessors)
                pre.FaultPredecessorsRecursively(e);
        }

        #endregion

        #region Error Handling

        public Exception Exception { get; private set; }
        public ErrorSource ErrorSource { get; set; }

        protected IDataFlowSource<ETLBoxError> InternalLinkErrorTo(IDataFlowDestination<ETLBoxError> target)
        {
            if (ErrorSource == null)
                ErrorSource = new ErrorSource();
            ErrorSource.LinkTo(target);
            return target as IDataFlowSource<ETLBoxError>;
        }

        protected void ThrowOrRedirectError(Exception e, string message)
        {
            if (ErrorSource == null)
            {
                FaultPredecessorsRecursively(e);
                throw e;
            }
            ErrorSource.Send(e, message);
        }

        private void LetErrorSourceWaitForInput() =>
            ErrorSource?.ExecuteAsync().Wait();

        private void LetErrorSourceFinishUp() =>
             ErrorSource?.SourceBlock.Complete();

        #endregion

        #region Logging

        protected int? _loggingThresholdRows;
        public virtual int? LoggingThresholdRows
        {
            get
            {
                if ((DataFlow.LoggingThresholdRows ?? 0) > 0)
                    return DataFlow.LoggingThresholdRows;
                else
                    return _loggingThresholdRows;
            }
            set
            {
                _loggingThresholdRows = value;
            }
        }

        public int ProgressCount { get; set; }
        protected bool HasLoggingThresholdRows => LoggingThresholdRows != null && LoggingThresholdRows > 0;
        protected int ThresholdCount { get; set; } = 1;
        protected bool WasLoggingStarted;
        protected bool WasLoggingFinished;
        protected void NLogStartOnce()
        {
            if (!WasLoggingStarted)
                NLogStart();
            WasLoggingStarted = true;
        }
        protected void NLogFinishOnce()
        {
            if (WasLoggingStarted && !WasLoggingFinished)
                NLogFinish();
            WasLoggingFinished = true;
        }
        protected void NLogStart() //private
        {
            if (!DisableLogging)
                NLogger.Info(TaskName, TaskType, "START", TaskHash, ControlFlow.ControlFlow.STAGE, ControlFlow.ControlFlow.CurrentLoadProcess?.Id);
        }

        protected void NLogFinish() //private
        {
            if (!DisableLogging && HasLoggingThresholdRows)
                NLogger.Info(TaskName + $" processed {ProgressCount} records in total.", TaskType, "LOG", TaskHash, ControlFlow.ControlFlow.STAGE, ControlFlow.ControlFlow.CurrentLoadProcess?.Id);
            if (!DisableLogging)
                NLogger.Info(TaskName, TaskType, "END", TaskHash, ControlFlow.ControlFlow.STAGE, ControlFlow.ControlFlow.CurrentLoadProcess?.Id);
        }

        protected void LogProgressBatch(int rowsProcessed)
        {
            ProgressCount += rowsProcessed;
            if (!DisableLogging && HasLoggingThresholdRows && ProgressCount >= (LoggingThresholdRows * ThresholdCount))
            {
                NLogger.Info(TaskName + $" processed {ProgressCount} records.", TaskType, "LOG", TaskHash, ControlFlow.ControlFlow.STAGE, ControlFlow.ControlFlow.CurrentLoadProcess?.Id);
                ThresholdCount++;
            }
        }

        protected void LogProgress()
        {
            ProgressCount += 1;
            if (!DisableLogging && HasLoggingThresholdRows && (ProgressCount % LoggingThresholdRows == 0))
                NLogger.Info(TaskName + $" processed {ProgressCount} records.", TaskType, "LOG", TaskHash, ControlFlow.ControlFlow.STAGE, ControlFlow.ControlFlow.CurrentLoadProcess?.Id);
        }
        #endregion
    }
}
