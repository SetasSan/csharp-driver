using System;
using System.Collections.Generic;
using System.Net;

namespace Cassandra
{
    /// <summary>
    ///  The Cassandra trace for a query. <p> Such trace is generated by Cassandra
    ///  when query tracing is enabled for the query. The trace itself is stored in
    ///  Cassandra in the <code>sessions</code> and <code>events</code> table in the
    ///  <code>system_traces</code> keyspace and can be retrieve manually using the
    ///  trace identifier (the one returned by <link>#getTraceId</link>). <p> This
    ///  class provides facilities to fetch the traces from Cassandra. Please note
    ///  that the writting of the trace is done asynchronously in Cassandra. So
    ///  accessing the trace too soon after the query may result in the trace being
    ///  incomplete.
    /// </summary>
    public class QueryTrace
    {
        private readonly Logger _logger = new Logger(typeof(QueryTrace));
        private const string SelectSessionsFormat = "SELECT * FROM system_traces.sessions WHERE session_id = {0}";

        private const string SelectEventsFormat = "SELECT * FROM system_traces.events WHERE session_id = {0}";

        private readonly Guid _traceId;

        private string _requestType;
        // We use the duration to figure out if the trace is complete, because
        // that's the last event that is written (and it is written asynchronously'
        // so it's possible that a fetch gets all the trace except the duration).'
        private int _duration = int.MinValue;
        private IPAddress _coordinator;
        private IDictionary<string, string> _parameters;
        private long _startedAt;
        private List<Event> _events;

        private readonly Session _session;

        public QueryTrace(Guid traceId, Session session)
        {
            this._traceId = traceId;
            this._session = session;
        }

        /// <summary>
        ///  The identifier of this trace.
        /// </summary>
        /// 
        /// <returns>the identifier of this trace.</returns>
        public Guid TraceId
        {
            get { return _traceId; }
        }

        /// <summary>
        ///  The type of request.
        /// </summary>
        /// 
        /// <returns>the type of request. This method returns <code>null</code> if the
        ///  request type is not yet available.</returns>
        public string RequestType
        {
            get
            {
                MaybeFetchTrace();
                return _requestType;
            }
        }

        /// <summary>
        ///  The (server side) duration of the query in microseconds.
        /// </summary>
        /// 
        /// <returns>the (server side) duration of the query in microseconds. This method
        ///  will return <code>Integer.MIN_VALUE</code> if the duration is not yet
        ///  available.</returns>
        public int DurationMicros
        {
            get
            {
                MaybeFetchTrace();
                return _duration;
            }
        }

        /// <summary>
        ///  The coordinator host of the query.
        /// </summary>
        /// 
        /// <returns>the coordinator host of the query. This method returns
        ///  <code>null</code> if the coordinator is not yet available.</returns>
        public IPAddress Coordinator
        {
            get
            {
                MaybeFetchTrace();
                return _coordinator;
            }
        }

        /// <summary>
        ///  The parameters attached to this trace.
        /// </summary>
        /// 
        /// <returns>the parameters attached to this trace. This method returns
        ///  <code>null</code> if the coordinator is not yet available.</returns>
        public IDictionary<string, string> Parameters
        {
            get
            {
                MaybeFetchTrace();
                return _parameters;
            }
        }

        /// <summary>
        ///  The server side timestamp of the start of this query.
        /// </summary>
        /// 
        /// <returns>the server side timestamp of the start of this query. This method
        ///  returns 0 if the start timestamp is not available.</returns>
        public long StartedAt
        {
            get
            {
                MaybeFetchTrace();
                return _startedAt;
            }
        }

        /// <summary>
        ///  The events contained in this trace.
        /// </summary>
        /// 
        /// <returns>the events contained in this trace.</returns>
        public List<Event> Events
        {
            get
            {
                MaybeFetchTrace();
                return _events;
            }
        }

        public override string ToString()
        {
            MaybeFetchTrace();
            return string.Format("{0} [{1}] - {2}µs", _requestType, _traceId, _duration);
        }

        private readonly object _fetchLock = new object();

        private void MaybeFetchTrace()
        {
            if (_duration != int.MinValue)
                return;

            lock (_fetchLock)
            {
                // If by the time we grab the lock we've fetch the events, it's
                // fine, move on. Otherwise, fetch them.
                if (_duration == int.MinValue)
                {
                    DoFetchTrace();
                }
            }
        }

        private void DoFetchTrace()
        {
            try
            {
                using (var sessRows = _session.Execute(string.Format(SelectSessionsFormat, _traceId)))
                {
                    foreach (var sessRow in sessRows.GetRows())
                    {
                        _requestType = sessRow.GetValue<string>("request");
                        if (!sessRow.IsNull("duration"))
                            _duration = sessRow.GetValue<int>("duration");
                        _coordinator = sessRow.GetValue<IPEndPoint>("coordinator").Address;
                        if (!sessRow.IsNull("parameters"))
                            _parameters = sessRow.GetValue<IDictionary<string, string>>("parameters");
                        _startedAt = sessRow.GetValue<DateTimeOffset>("started_at").ToFileTime(); //.getTime();

                        break;
                    }
                }

                _events = new List<Event>();
                
                using (var evRows = _session.Execute(string.Format(SelectEventsFormat, _traceId)))
                {
                    foreach (var evRow in evRows.GetRows())
                    {
                        _events.Add(new Event(evRow.GetValue<string>("activity"),
                                             GuidGenerator.GetDateTimeOffset(evRow.GetValue<Guid>("event_id")),
                                             evRow.GetValue<IPEndPoint>("source").Address,
                                             evRow.GetValue<int>("source_elapsed"),
                                                evRow.GetValue<string>("thread")));
                    }
                }

            }
            catch (Exception ex)
            {
                _logger.Error("Unexpected exception while fetching query trace", ex);
            }
        }

        /// <summary>
        ///  A trace event. <p> A query trace is composed of a list of trace events.</p>
        /// </summary>

        public class Event
        {
            private readonly string _name;
            private readonly DateTimeOffset _timestamp;
            private readonly IPAddress _source;
            private readonly int _sourceElapsed;
            private readonly string _threadName;

            internal Event(string name, DateTimeOffset timestamp, IPAddress source, int sourceElapsed, string threadName)
            {
                this._name = name;
                // Convert the UUID timestamp to an epoch timestamp; I stole this seemingly random value from cqlsh, hopefully it's correct.'
//                this._timestamp = (timestamp - 0x01b21dd213814000L)/10000;
                this._timestamp = timestamp;
                this._source = source;
                this._sourceElapsed = sourceElapsed;
                this._threadName = threadName;
            }

            /// <summary>
            ///  The event description, i.e. which activity this event correspond to.
            /// </summary>
            /// 
            /// <returns>the event description.</returns>
            public string Description
            {
                get { return _name; }
            }

            /// <summary>
            ///  The server side timestamp of the event.
            /// </summary>
            /// 
            /// <returns>the server side timestamp of the event.</returns>
            public DateTimeOffset Timestamp
            {
                get { return _timestamp; }
            }

            /// <summary>
            ///  The address of the host having generated this event.
            /// </summary>
            /// 
            /// <returns>the address of the host having generated this event.</returns>
            public IPAddress Source
            {
                get { return _source; }
            }

            /// <summary>
            ///  The number of microseconds elapsed on the source when this event occurred
            ///  since when the source started handling the query.
            /// </summary>
            /// 
            /// <returns>the elapsed time on the source host when that event happened in
            ///  microseconds.</returns>
            public int SourceElapsedMicros
            {
                get { return _sourceElapsed; }
            }

            /// <summary>
            ///  The name of the thread on which this event occured.
            /// </summary>
            /// 
            /// <returns>the name of the thread on which this event occured.</returns>
            public string ThreadName
            {
                get { return _threadName; }
            }

            public override string ToString()
            {
                return string.Format("{0} on {1}[{2}] at {3}", _name, _source, _threadName,_timestamp);
            }
        }
    }
}

// end namespace