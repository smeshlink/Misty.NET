/*
 * SmeshLink.Misty.Service.Channel.IServiceChannel.cs
 * 
 * Copyright (c) 2009-2014 SmeshLink Technology Corporation.
 * All rights reserved.
 * 
 * Authors:
 *  Longxiang He
 * 
 * This file is part of the Misty, a sensor cloud for IoT.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Mina.Core.Buffer;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SmeshLink.Misty.Formatter;
using SmeshLink.Misty.Util;

namespace SmeshLink.Misty.Service.Channel
{
    /// <summary>
    /// TCP channel.
    /// </summary>
    public class TcpChannel : IServiceChannel, IDisposable
    {
        private String _host;
        private Int32 _port = 9011;
        private Int32 _timeout = 30000;
        private Int32 _retryInterval = 10000;
        private Int32 _maxWorkers = 1;
        private readonly ConcurrentDictionary<String, WaitFuture<IServiceRequest, IServiceResponse>> _waitingRequests
            = new ConcurrentDictionary<String, WaitFuture<IServiceRequest, IServiceResponse>>();
        private readonly List<Worker> _workerPool = new List<Worker>();
        private Boolean _disposed;

        /// <inheritdoc/>
        public event EventHandler<RequestEventArgs> RequestReceived;

        /// <summary>
        /// </summary>
        public TcpChannel(String host)
        {
            _host = host;
        }

        /// <summary>
        /// Gets or set the host.
        /// </summary>
        public String Host
        {
            get { return _host; }
            set { _host = value; }
        }

        /// <summary>
        /// Gets or set the port.
        /// </summary>
        public Int32 Port
        {
            get { return _port; }
            set { _port = value; }
        }

        /// <inheritdoc/>
        public Int32 Timeout
        {
            get { return _timeout; }
            set { _timeout = value; }
        }

        /// <summary>
        /// Gets or set interval for reconnecting.
        /// </summary>
        public Int32 RetryInterval
        {
            get { return _retryInterval; }
            set { _retryInterval = value; }
        }

        /// <summary>
        /// Gets or set max number of workers.
        /// </summary>
        public Int32 MaxWorkers
        {
            get { return _maxWorkers; }
            set { _maxWorkers = value; }
        }

        /// <inheritdoc/>
        public IServiceResponse Execute(IServiceRequest request)
        {
            Worker worker = GetWorker();
            
            if (worker == null)
                return null;

            request.Headers["Content-Type"] = MistyService.GetContentType(request.Format);
            request.Headers["User-Agent"] = MistyService.Version;
            WaitFuture<IServiceRequest, IServiceResponse> f = worker.Send(request);
            worker.Free();
            f.Wait(_timeout);
            return f.Response;
        }

        /// <summary>
        /// Disposes resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Disposes resources.
        /// </summary>
        protected virtual void Dispose(Boolean disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                _workerPool.ForEach(w => w.Dispose());
            }

            _disposed = true;
        }

        private Worker GetWorker()
        {
            lock (_workerPool)
            {
                for (; ; )
                {
                    foreach (Worker w in _workerPool)
                    {
                        if (w.Available)
                        {
                            w.Take();
                            return w;
                        }
                    }

                    if (_workerPool.Count < _maxWorkers)
                    {
                        Worker w = new Worker(this);
                        _workerPool.Add(w);
                        ThreadPool.QueueUserWorkItem(o => ((Worker)o).Run(), w);
                    }

                    try
                    {
                        Monitor.Wait(_workerPool, 3000);
                    }
                    catch (ThreadInterruptedException)
                    {
                        break;
                    }
                }
            }
            return null;
        }

        private void RemoveWorker(Worker w, Exception e)
        {
            Console.WriteLine("Removing worker " + e.Message);

            lock (_workerPool)
            {
                _workerPool.Remove(w);
            }

            try
            {
                w.Dispose();
            }
            catch
            { /* ignore */ }
        }

        private void Wakeup()
        {
            lock (_workerPool)
            {
                Monitor.PulseAll(_workerPool);
            }
        }

        private void HandleResponse(IServiceResponse response)
        {
            if (response.Token != null)
            {
                WaitFuture<IServiceRequest, IServiceResponse> f;
                if (_waitingRequests.TryRemove(response.Token, out f))
                    f.Response = response;
            }
        }

        private IServiceResponse FireRequestReceived(IServiceRequest request)
        {
            EventHandler<RequestEventArgs> h = RequestReceived;
            if (h != null)
            {
                RequestEventArgs e = new RequestEventArgs(request);
                IServiceResponse response = new JsonResponse(new JObject());
                response.StatusCode = 200;
                e.Response = response;
                h(this, e);
                return e.Response;
            }
            return null;
        }

        class Worker : IDisposable
        {
            readonly TcpChannel _channel;
            readonly Byte[] _byteBuffer = new Byte[1024];
            readonly IoBuffer _ioBuffer = IoBuffer.Allocate(2048);
            Int32 _counter;
            TcpClient _client;
            Boolean _free = true;

            public Worker(TcpChannel channel)
            {
                _channel = channel;
                _ioBuffer.AutoExpand = true;
            }

            public void Run()
            {
                for (; ; )
                {
                    try
                    {
                        _client = new TcpClient(_channel.Host, _channel.Port);
                    }
                    catch (SocketException)
                    {
                        if (_client != null)
                            ((IDisposable)_client).Dispose();
                    }

                    if (_client == null && _channel.RetryInterval > 0)
                    {
                        try
                        {
                            Thread.Sleep(_channel.RetryInterval);
                        }
                        catch (ThreadInterruptedException)
                        {
                            break;
                        }
                        continue;
                    }

                    break;
                }

                if (_client == null)
                    return;

                _channel.Wakeup();
                Free();

                while (_client.Connected)
                {
                    try
                    {
                        Int32 bytesRead = Math.Min(_client.Available, _byteBuffer.Length);
                        bytesRead = _client.Client.Receive(_byteBuffer, 0, bytesRead, SocketFlags.None);
                        for (Int32 i = 0; i < bytesRead; i++)
                        {
                            _ioBuffer.Put(_byteBuffer[i]);

                            if ('{' == _byteBuffer[i])
                            {
                                _counter++;
                            }
                            else if ('}' == _byteBuffer[i])
                            {
                                _counter--;

                                if (_counter == 0)
                                {
                                    _ioBuffer.Flip();
                                    ArraySegment<Byte> array = _ioBuffer.GetRemaining();
                                    String json = Encoding.UTF8.GetString(array.Array, array.Offset, array.Count);
                                    JObject jObj = ToJsonObject(json);

                                    if (jObj["status"] != null)
                                        ProcessResponse(new JsonResponse(jObj));
                                    else if (jObj["method"] != null)
                                        ProcessRequest(new JsonRequest(jObj));

                                    _ioBuffer.Clear();
                                }
                            }
                        }
                    }
                    catch (SocketException e)
                    {
                        _channel.RemoveWorker(this, e);
                    }
                    catch (Exception e)
                    {
                        // TODO log exception
                        Console.WriteLine(e.Message);
                    }
                }
            }

            public WaitFuture<IServiceRequest, IServiceResponse> Send(IServiceRequest request)
            {
                WaitFuture<IServiceRequest, IServiceResponse> f = new WaitFuture<IServiceRequest, IServiceResponse>(request);
                _channel._waitingRequests[request.Token] = f;

                try
                {
                    JsonFormatter.Instance.Format(_client.GetStream(), request);
                }
                catch (Exception e)
                {
                    _channel.RemoveWorker(this, e);
                    WaitFuture<IServiceRequest, IServiceResponse> wf;
                    _channel._waitingRequests.TryRemove(request.Token, out wf);
                    f.Response = null;
                }

                return f;
            }

            private void Send(IServiceResponse response)
            {
                try
                {
                    JsonFormatter.Instance.Format(_client.GetStream(), response);
                }
                catch (Exception e)
                {
                    _channel.RemoveWorker(this, e);
                }
            }

            public void Take()
            {
                _free = false;
            }

            public void Free()
            {
                _free = true;
                _channel.Wakeup();
            }

            public Boolean Available
            {
                get { return _free && _client != null && _client.Connected; }
            }

            private void ProcessResponse(IServiceResponse response)
            {
                _channel.HandleResponse(response);
                //_channel.FireResponseReceived(response);
            }

            private void ProcessRequest(IServiceRequest request)
            {
                IServiceResponse response = _channel.FireRequestReceived(request);
                if (response != null)
                {
                    if (response.Token == null)
                        response.Token = request.Token;
                    Send(response);
                }
            }

            public void Dispose()
            {
                if (_client != null)
                    ((IDisposable)_client).Dispose();
            }
        }

        private static JObject ToJsonObject(String value)
        {
            JsonSerializerSettings set = new JsonSerializerSettings();
            set.DateFormatHandling = DateFormatHandling.IsoDateFormat;
            set.DateTimeZoneHandling = DateTimeZoneHandling.Utc;
            return (JObject)JsonConvert.DeserializeObject(value);
        }
    }
}
