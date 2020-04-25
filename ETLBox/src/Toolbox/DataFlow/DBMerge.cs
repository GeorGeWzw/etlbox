﻿using ALE.ETLBox.ConnectionManager;
using ALE.ETLBox.ControlFlow;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace ALE.ETLBox.DataFlow
{
    /// <summary>
    /// Inserts, updates and (optionally) deletes data in db target.
    /// </summary>
    /// <typeparam name="TInput">Type of input data.</typeparam>
    /// <example>
    /// <code>
    /// </code>
    /// </example>
    public class DbMerge<TInput> : DataFlowTransformation<TInput, TInput>,
        ITask,
        IDataFlowTransformation<TInput, TInput>,
        IDataFlowBatchDestination<TInput>
        where TInput : IMergeableRow, new()
    {
        /* ITask Interface */
        public override string TaskName { get; set; } = "Insert, Upsert or delete in destination";

        public async Task ExecuteAsync() => await OutputSource.ExecuteAsync();
        public void Execute() => OutputSource.Execute();

        /* Public Properties */
        public override ISourceBlock<TInput> SourceBlock => OutputSource.SourceBlock;
        public override ITargetBlock<TInput> TargetBlock => Lookup.TargetBlock;
        public DeltaMode DeltaMode { get; set; }
        public TableDefinition DestinationTableDefinition { get; set; }
        public string TableName { get; set; }
        public override IConnectionManager ConnectionManager
        {
            get => base.ConnectionManager;
            set
            {
                base.ConnectionManager = value;
                DestinationTableAsSource.ConnectionManager = value;
                DestinationTable.ConnectionManager = value;
                //Init();
            }
        }
        public List<TInput> DeltaTable { get; set; } = new List<TInput>();
        public bool UseTruncateMethod
        {
            get
            {
                if (TypeInfo?.IdColumnNames == null || TypeInfo?.IdColumnNames?.Count == 0) return true;
                return _useTruncateMethod;
            }
            set
            {
                _useTruncateMethod = value;
            }
        }

        public int BatchSize
        {
            get => DestinationTable.BatchSize;
            set => DestinationTable.BatchSize = value;
        }

        /* Private stuff */
        bool _useTruncateMethod;

        ObjectNameDescriptor TN => new ObjectNameDescriptor(TableName, QB, QE);
        LookupTransformation<TInput, TInput> Lookup { get; set; }
        DbSource<TInput> DestinationTableAsSource { get; set; }
        DbDestination<TInput> DestinationTable { get; set; }
        List<TInput> InputData => Lookup.LookupData;
        Dictionary<string, TInput> InputDataDict { get; set; }
        CustomSource<TInput> OutputSource { get; set; }
        bool WasTruncationExecuted { get; set; }
        DBMergeTypeInfo TypeInfo { get; set; }

        public DbMerge(string tableName)
        {
            TableName = tableName;
            Init();
        }

        public DbMerge(IConnectionManager connectionManager, string tableName) : this(tableName)
        {
            ConnectionManager = connectionManager;
        }

        public DbMerge(string tableName, int batchSize) : this(tableName)
        {
            TableName = tableName;
            Init(batchSize);
        }

        public DbMerge(IConnectionManager connectionManager, string tableName, int batchSize) : this(tableName, batchSize)
        {
            ConnectionManager = connectionManager;
        }

        private void Init(int batchSize = DbDestination.DEFAULT_BATCH_SIZE)
        {
            TypeInfo = new DBMergeTypeInfo(typeof(TInput));
            DestinationTableAsSource = new DbSource<TInput>(ConnectionManager, TableName);
            DestinationTable = new DbDestination<TInput>(ConnectionManager, TableName, batchSize);
            InitInternalFlow();
            InitOutputFlow();
        }

        private void InitInternalFlow()
        {
            Lookup = new LookupTransformation<TInput, TInput>(
                DestinationTableAsSource,
                row => UpdateRowWithDeltaInfo(row)
            );

            DestinationTable.BeforeBatchWrite = batch =>
            {
                if (DeltaMode == DeltaMode.Delta)
                    DeltaTable.AddRange(batch.Where(row => row.ChangeAction != ChangeAction.Delete));
                else
                    DeltaTable.AddRange(batch);

                if (!UseTruncateMethod)
                {
                    SqlDeleteIds(batch.Where(row => row.ChangeAction != ChangeAction.Insert && row.ChangeAction != ChangeAction.Exists));
                    return batch.Where(row => row.ChangeAction == ChangeAction.Insert || row.ChangeAction == ChangeAction.Update).ToArray();
                }
                else
                {
                    TruncateDestinationOnce();
                    return batch.Where(row => row.ChangeAction == ChangeAction.Insert || row.ChangeAction == ChangeAction.Update || row.ChangeAction == ChangeAction.Exists).ToArray();
                }
            };

            Lookup.LinkTo(DestinationTable);
        }

        private void InitOutputFlow()
        {
            int x = 0;
            OutputSource = new CustomSource<TInput>(() =>
            {
                return DeltaTable.ElementAt(x++);
            }, () => x >= DeltaTable.Count);

            DestinationTable.OnCompletion = () =>
            {
                IdentifyAndDeleteMissingEntries();
                OutputSource.Execute();
            };
        }

        private TInput UpdateRowWithDeltaInfo(TInput row)
        {
            if (InputDataDict == null) InitInputDataDictionary();
            row.ChangeDate = DateTime.Now;
            TInput find = default(TInput);
            InputDataDict.TryGetValue(row.UniqueId, out find);
            if (DeltaMode == DeltaMode.Delta && row.IsDeletion)
            {
                if (find != null)
                {
                    find.ChangeAction = ChangeAction.Delete;
                    row.ChangeAction = ChangeAction.Delete;
                }
            }
            else
            {
                row.ChangeAction = ChangeAction.Insert;
                //TInput find = InputData.Where(d => d.UniqueId == row.UniqueId).FirstOrDefault();
                if (find != null)
                {
                    if (row.Equals(find))
                    {
                        row.ChangeAction = ChangeAction.Exists;
                        find.ChangeAction = ChangeAction.Exists;
                    }
                    else
                    {
                        row.ChangeAction = ChangeAction.Update;
                        find.ChangeAction = ChangeAction.Update;
                    }
                }
            }
            return row;
        }

        private void InitInputDataDictionary()
        {
            InputDataDict = new Dictionary<string, TInput>();
            foreach (var d in InputData)
                InputDataDict.Add(d.UniqueId, d);
        }

        void TruncateDestinationOnce()
        {
            if (WasTruncationExecuted == true) return;
            WasTruncationExecuted = true;
            if (DeltaMode == DeltaMode.NoDeletions == true) return;
            TruncateTableTask.Truncate(this.ConnectionManager, TableName);
        }

        void IdentifyAndDeleteMissingEntries()
        {
            if (DeltaMode == DeltaMode.NoDeletions) return;
            IEnumerable<TInput> deletions = null;
            if (DeltaMode == DeltaMode.Delta)
                deletions = InputData.Where(row => row.ChangeAction == ChangeAction.Delete).ToList();
            else
                deletions = InputData.Where(row => row.ChangeAction == null).ToList();
            if (!UseTruncateMethod)
                SqlDeleteIds(deletions);
            foreach (var row in deletions) //.ForEach(row =>
            {
                row.ChangeAction = ChangeAction.Delete;
                row.ChangeDate = DateTime.Now;
            };
            DeltaTable.AddRange(deletions);
        }

        private void SqlDeleteIds(IEnumerable<TInput> rowsToDelete)
        {
            var idsToDelete = rowsToDelete.Select(row => $"'{row.UniqueId}'");
            if (idsToDelete.Count() > 0)
            {
                string idNames = $"{QB}{TypeInfo.IdColumnNames.First()}{QE}";
                if (TypeInfo.IdColumnNames.Count > 1)
                    idNames = CreateConcatSqlForNames();
                new SqlTask(this, $@"
            DELETE FROM {TN.QuotatedFullName} 
            WHERE {idNames} IN (
            {String.Join(",", idsToDelete)}
            )")
                {
                    DisableLogging = true,
                }.ExecuteNonQuery();
            }
        }

        private string CreateConcatSqlForNames()
        {
            string result = $"CONCAT( {string.Join(",", TypeInfo?.IdColumnNames.Select(cn => $"{QB}{cn}{QE}"))} )";
            if (this.ConnectionType == ConnectionManagerType.SQLite)
                result = $" {string.Join("||", TypeInfo?.IdColumnNames.Select(cn => $"{QB}{cn}{QE}"))} ";
            return result;
        }

        public void Wait() => DestinationTable.Wait();
        public Task Completion => DestinationTable.Completion;
    }

    public enum DeltaMode
    {
        Full = 0,
        NoDeletions = 1,
        Delta = 2,
    }
}
