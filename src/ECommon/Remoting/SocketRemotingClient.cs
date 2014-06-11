﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using ECommon.Components;
using ECommon.Extensions;
using ECommon.Logging;
using ECommon.Remoting.Exceptions;
using ECommon.Scheduling;
using ECommon.Socketing;

namespace ECommon.Remoting
{
    public class SocketRemotingClient
    {
        private ClientSocket _clientSocket;
        private readonly string _address;
        private readonly int _port;
        private readonly ConcurrentDictionary<long, ResponseFuture> _responseFutureDict;
        private readonly BlockingCollection<byte[]> _responseMessageQueue;
        private readonly IScheduleService _scheduleService;
        private readonly ILogger _logger;
        private readonly ISocketEventListener _socketEventListener;
        private readonly Worker _processResponseMessageWorker;
        private int _scanTimeoutRequestTaskId;
        private int _reconnectServerTaskId;

        public event Action<bool> ClientSocketConnectionChanged;

        public SocketRemotingClient() : this(SocketUtils.GetLocalIPV4().ToString(), 5000) { }
        public SocketRemotingClient(string address, int port, ISocketEventListener socketEventListener = null)
        {
            _address = address;
            _port = port;
            _socketEventListener = socketEventListener;
            _clientSocket = new ClientSocket(new RemotingClientSocketEventListener(this));
            _responseFutureDict = new ConcurrentDictionary<long, ResponseFuture>();
            _responseMessageQueue = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>());
            _scheduleService = ObjectContainer.Resolve<IScheduleService>();
            _processResponseMessageWorker = new Worker("SocketRemotingClient.ProcessResponseMessage", ProcessResponseMessage);
            _logger = ObjectContainer.Resolve<ILoggerFactory>().Create(GetType().FullName);
        }

        public void Connect()
        {
            _clientSocket.Connect(_address, _port);
        }
        public void Start()
        {
            _clientSocket.Start(responseMessage => _responseMessageQueue.Add(responseMessage));
            _processResponseMessageWorker.Start();
            _scanTimeoutRequestTaskId = _scheduleService.ScheduleTask("SocketRemotingClient.ScanTimeoutRequest", ScanTimeoutRequest, 1000, 1000);
        }
        public void Shutdown()
        {
            StopReconnectServerTask();
            _processResponseMessageWorker.Stop();
            _scheduleService.ShutdownTask(_scanTimeoutRequestTaskId);
            _clientSocket.Shutdown();
        }
        public RemotingResponse InvokeSync(RemotingRequest request, int timeoutMillis)
        {
            EnsureServerAvailable();

            var message = RemotingUtil.BuildRequestMessage(request);
            var taskCompletionSource = new TaskCompletionSource<RemotingResponse>();
            var responseFuture = new ResponseFuture(request, timeoutMillis, taskCompletionSource);

            if (!_responseFutureDict.TryAdd(request.Sequence, responseFuture))
            {
                throw new Exception(string.Format("Try to add response future failed. request sequence:{0}", request.Sequence));
            }

            _clientSocket.SendMessage(message, sendResult => SendMessageCallback(responseFuture, request, _address, sendResult));

            var task = taskCompletionSource.Task;
            var response = task.WaitResult<RemotingResponse>(timeoutMillis);
            if (response == null)
            {
                if (!task.IsCompleted)
                {
                    throw new RemotingTimeoutException(_address, request, timeoutMillis);
                }
                else if (task.IsFaulted)
                {
                    throw new RemotingRequestException(_address, request, task.Exception);
                }
                else
                {
                    throw new RemotingRequestException(_address, request, "Send remoting request successfully, but the remoting response is null.");
                }
            }
            return response;
        }
        public Task<RemotingResponse> InvokeAsync(RemotingRequest request, int timeoutMillis)
        {
            EnsureServerAvailable();

            var message = RemotingUtil.BuildRequestMessage(request);
            var taskCompletionSource = new TaskCompletionSource<RemotingResponse>();
            var responseFuture = new ResponseFuture(request, timeoutMillis, taskCompletionSource);

            if (!_responseFutureDict.TryAdd(request.Sequence, responseFuture))
            {
                throw new Exception(string.Format("Try to add response future failed. request sequence:{0}", request.Sequence));
            }

            _clientSocket.SendMessage(message, sendResult => SendMessageCallback(responseFuture, request, _address, sendResult));

            return taskCompletionSource.Task;
        }
        public void InvokeOneway(RemotingRequest request, int timeoutMillis)
        {
            EnsureServerAvailable();

            request.IsOneway = true;
            _clientSocket.SendMessage(RemotingUtil.BuildRequestMessage(request), sendResult =>
            {
                if (!sendResult.Success)
                {
                    _logger.ErrorFormat("Send request {0} to channel <{1}> failed, exception:{2}", request, _address, sendResult.Exception);
                }
            });
        }

        private void ProcessResponseMessage()
        {
            var responseMessage = _responseMessageQueue.Take();
            var remotingResponse = RemotingUtil.ParseResponse(responseMessage);

            ResponseFuture responseFuture;
            if (_responseFutureDict.TryRemove(remotingResponse.Sequence, out responseFuture))
            {
                responseFuture.SetResponse(remotingResponse);
            }
        }
        private void ScanTimeoutRequest()
        {
            var timeoutKeyList = new List<long>();
            foreach (var entry in _responseFutureDict)
            {
                if (entry.Value.IsTimeout())
                {
                    timeoutKeyList.Add(entry.Key);
                }
            }
            foreach (var key in timeoutKeyList)
            {
                ResponseFuture responseFuture;
                if (_responseFutureDict.TryRemove(key, out responseFuture))
                {
                    responseFuture.SetException(new RemotingTimeoutException(_address, responseFuture.Request, responseFuture.TimeoutMillis));
                    _logger.DebugFormat("Removed timeout request:{0}", responseFuture.Request);
                }
            }
        }
        private void SendMessageCallback(ResponseFuture responseFuture, RemotingRequest request, string address, SendResult sendResult)
        {
            if (!sendResult.Success)
            {
                _logger.ErrorFormat("Send request {0} to channel <{1}> failed, exception:{2}", request, address, sendResult.Exception);
                responseFuture.SetException(new RemotingRequestException(address, request, sendResult.Exception));
                _responseFutureDict.Remove(request.Sequence);
            }
        }
        private void ReconnectServer()
        {
            var success = false;
            try
            {
                _clientSocket.Shutdown();
                _clientSocket = new ClientSocket(new RemotingClientSocketEventListener(this));
                _clientSocket.Connect(_address, _port);
                _clientSocket.Start(responseMessage => _responseMessageQueue.Add(responseMessage));
                success = true;
            }
            catch { }

            if (success)
            {
                StopReconnectServerTask();
                _logger.InfoFormat("Server[address={0}] reconnected.", _clientSocket.SocketInfo.SocketRemotingEndpointAddress);
                if (ClientSocketConnectionChanged != null)
                {
                    ClientSocketConnectionChanged(true);
                }
            }
        }
        private void StartReconnectServerTask()
        {
            if (_reconnectServerTaskId == 0)
            {
                _reconnectServerTaskId = _scheduleService.ScheduleTask("SocketRemotingClient.ReconnectServer", ReconnectServer, 1000, 1000);
            }
        }
        private void StopReconnectServerTask()
        {
            if (_reconnectServerTaskId > 0)
            {
                _scheduleService.ShutdownTask(_reconnectServerTaskId);
                _reconnectServerTaskId = 0;
            }
        }
        private void EnsureServerAvailable()
        {
            if (!_clientSocket.IsConnected)
            {
                throw new RemotingServerUnAvailableException(_address, _port);
            }
        }

        class RemotingClientSocketEventListener : ISocketEventListener
        {
            private SocketRemotingClient _socketRemotingClient;

            public RemotingClientSocketEventListener(SocketRemotingClient socketRemotingClient)
            {
                _socketRemotingClient = socketRemotingClient;
            }

            public void OnNewSocketAccepted(SocketInfo socketInfo)
            {
                if (_socketRemotingClient._socketEventListener != null)
                {
                    _socketRemotingClient._socketEventListener.OnNewSocketAccepted(socketInfo);
                }
            }

            public void OnSocketException(SocketInfo socketInfo, SocketException socketException)
            {
                if (SocketUtils.IsSocketDisconnectedException(socketException))
                {
                    if (_socketRemotingClient.ClientSocketConnectionChanged != null)
                    {
                        _socketRemotingClient.ClientSocketConnectionChanged(false);
                    }
                    _socketRemotingClient._logger.ErrorFormat("Server[address={0}] disconnected, start task to reconnect.", socketInfo.SocketRemotingEndpointAddress);
                    _socketRemotingClient.StartReconnectServerTask();
                }
                if (_socketRemotingClient._socketEventListener != null)
                {
                    _socketRemotingClient._socketEventListener.OnSocketException(socketInfo, socketException);
                }
            }
        }
    }
}
