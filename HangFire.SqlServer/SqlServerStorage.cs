using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Threading;
using Dapper;
using HangFire.Common;
using HangFire.Server;
using HangFire.SqlServer.Entities;
using HangFire.Storage;
using HangFire.Storage.Monitoring;

namespace HangFire.SqlServer
{
    public class SqlServerFetcher : IJobFetcher
    {
        private readonly SqlConnection _connection;
        private readonly IEnumerable<string> _queues;

        public SqlServerFetcher(SqlConnection connection, IEnumerable<string> queues)
        {
            _connection = connection;
            _queues = queues;
        }

        public void Dispose()
        {
            _connection.Dispose();
        }

        public QueuedJob DequeueJob(CancellationToken cancellationToken)
        {
            Job job = null;

            do
            {
                var jobId = _connection.Query<Guid?>(
                    @"update top (1) HangFire.JobQueue set FetchedAt = GETUTCDATE() "
                    + @"output INSERTED.JobId "
                    + @"where FetchedAt < DATEADD(minute, 15, GETUTCDATE()) ")
                    .SingleOrDefault();

                if (jobId != null)
                {
                    job = _connection.Query<Job>(
                        @"select * from HangFire.Job where Id = @id",
                        new { id = jobId })
                        .SingleOrDefault();
                }
                if (job == null)
                {
                    if (cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)))
                    {
                        return null;
                    }
                }
            } while (job == null);

            var invocationData = JobHelper.FromJson<InvocationData>(job.InvocationData);
            var jobDictionary = new Dictionary<string, string>();

            jobDictionary.Add("Type", invocationData.Type);
            jobDictionary.Add("Method", invocationData.Method);
            jobDictionary.Add("ParameterTypes", invocationData.ParameterTypes);
            jobDictionary.Add("Arguments", job.Arguments);

            return new QueuedJob(new JobPayload(
                job.Id.ToString(), 
                "default", 
                jobDictionary));
        }
    }

    public class SqlServerStorage : JobStorage
    {
        private readonly string _connectionString;

        public SqlServerStorage(string connectionString)
        {
            _connectionString = connectionString;
        }

        public override IMonitoringApi Monitoring
        {
            get { throw new System.NotImplementedException(); }
        }

        public override IStorageConnection CreateConnection()
        {
            return CreatePooledConnection();
        }

        public override IStorageConnection CreatePooledConnection()
        {
            return new SqlStorageConnection(new SqlConnection(_connectionString));
        }

        public override IJobFetcher CreateFetcher(IEnumerable<string> queues, int workersCount)
        {
            return new SqlServerFetcher(new SqlConnection(_connectionString), queues);
        }

        public override IEnumerable<IThreadWrappable> GetComponents()
        {
            return Enumerable.Empty<IThreadWrappable>();
        }
    }
}